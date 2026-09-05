using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Stages emitted while a workspace snapshot is assembled.</summary>
public enum WorkspaceCaptureProgressStage
{
    Preparing,
    DetectingResources,
    SearchingCommonFolders,
    CapturingBrowserSession,
    Finalizing
}

/// <summary>Progress update emitted while a workspace snapshot is assembled.</summary>
/// <param name="Current">1-based index of the window currently being processed (0 = pre-loop setup).</param>
/// <param name="Total">Total number of windows to process.</param>
/// <param name="AppName">Process name of the window being processed (or a stage description).</param>
/// <param name="Detail">Window title snippet or a short stage description.</param>
public record struct SaveProgressReport(
    int Current,
    int Total,
    string AppName,
    string Detail,
    WorkspaceCaptureProgressStage Stage = WorkspaceCaptureProgressStage.DetectingResources);

/// <summary>
/// Orchestrates the save and restore pipeline for workspaces.
/// Coordinates <see cref="MonitorService"/>, <see cref="WindowService"/>,
/// <see cref="StorageService"/>, and <see cref="JumpListService"/>.
/// </summary>
/// <remarks>
/// This is the primary service called by both <see cref="LayoutCoordinator"/>
/// and UI code. Storage and tray operations are dispatched to the correct contexts internally.
/// </remarks>
public class WorkspaceService
{
    private static readonly TimeSpan DefaultCommonFolderSearchBudget = TimeSpan.FromSeconds(5);
    private readonly StorageService    _storageService;
    private readonly IWindowInventory  _windowInventory;
    private readonly IWindowMutation   _windowMutation;
    private readonly IMonitorInventory _monitorInventory;
    private readonly JumpListService   _jumpListService;
    private readonly WebAppService     _webAppService;
    private readonly IPackagedAppResolver _packagedAppResolver;
    private readonly IBrowserSessionConnector? _browserSessionConnector;
    private readonly IRestoreResourceBoundary _restoreResources;
    private readonly RestoreExecutor _restoreExecutor;
    private readonly SettingsService? _settingsService;
    private readonly SemaphoreSlim _restoreTransactionGate = new(1, 1);

    /// <summary>Creates the production workspace service using the native window service.</summary>
    public WorkspaceService(
        StorageService  storageService,
        WindowService   windowService,
        MonitorService  monitorService,
        JumpListService jumpListService,
        WebAppService?   webAppService = null,
        IBrowserSessionConnector? browserSessionConnector = null,
        IRestoreProcessLauncher? restoreProcessLauncher = null,
        IRestoreClock? restoreClock = null,
        IRestoreResourceBoundary? restoreResources = null,
        SettingsService? settingsService = null,
        IAppReadinessProbe? appReadinessProbe = null,
        AppReadinessPolicy? appReadinessPolicy = null,
        IEnumerable<IAppReadinessStrategy>? appReadinessStrategies = null,
        IPackagedAppResolver? packagedAppResolver = null,
        IWindowPlacementProbe? placementProbe = null,
        WindowPlacementVerificationPolicy? placementPolicy = null,
        IEnumerable<IWindowPlacementVerificationStrategy>? placementStrategies = null)
        : this(
            storageService,
            windowService,
            windowService,
            monitorService,
            jumpListService,
            webAppService,
            browserSessionConnector,
            restoreProcessLauncher,
            restoreClock,
            restoreResources,
            settingsService,
            appReadinessProbe,
            appReadinessPolicy,
            appReadinessStrategies,
            packagedAppResolver,
            placementProbe,
            placementPolicy,
            placementStrategies)
    {
    }

    /// <summary>
    /// Creates a workspace service with explicit inventory and mutation boundaries.
    /// This overload supports deterministic service-layer tests without native HWNDs.
    /// </summary>
    public WorkspaceService(
        StorageService     storageService,
        IWindowInventory   windowInventory,
        IWindowMutation    windowMutation,
        IMonitorInventory  monitorInventory,
        JumpListService    jumpListService,
        WebAppService?     webAppService = null,
        IBrowserSessionConnector? browserSessionConnector = null,
        IRestoreProcessLauncher? restoreProcessLauncher = null,
        IRestoreClock? restoreClock = null,
        IRestoreResourceBoundary? restoreResources = null,
        SettingsService? settingsService = null,
        IAppReadinessProbe? appReadinessProbe = null,
        AppReadinessPolicy? appReadinessPolicy = null,
        IEnumerable<IAppReadinessStrategy>? appReadinessStrategies = null,
        IPackagedAppResolver? packagedAppResolver = null,
        IWindowPlacementProbe? placementProbe = null,
        WindowPlacementVerificationPolicy? placementPolicy = null,
        IEnumerable<IWindowPlacementVerificationStrategy>? placementStrategies = null)
    {
        _storageService   = storageService;
        _windowInventory  = windowInventory;
        _windowMutation   = windowMutation;
        _monitorInventory = monitorInventory;
        _jumpListService  = jumpListService;
        _webAppService    = webAppService ?? new WebAppService();
        _packagedAppResolver = packagedAppResolver ?? new PackagedAppResolver();
        _browserSessionConnector = browserSessionConnector;
        _settingsService = settingsService;
        _restoreResources = restoreResources ?? new FileSystemRestoreResourceBoundary();
        _restoreExecutor = new RestoreExecutor(
            _windowInventory,
            _windowMutation,
            restoreProcessLauncher ?? new SystemRestoreProcessLauncher(),
            restoreClock ?? new SystemRestoreClock(),
            _restoreResources,
            _browserSessionConnector,
            appReadinessProbe ?? new SystemAppReadinessProbe(_windowInventory),
            appReadinessPolicy,
            appReadinessStrategies,
            placementProbe ?? (_windowInventory is WindowService
                ? new SystemWindowPlacementProbe()
                : new InventoryWindowPlacementProbe(_windowInventory)),
            placementPolicy,
            placementStrategies);
    }

    // ── Storage proxies ────────────────────────────────────────────

    public string GetLastKnownFingerprint()               => _storageService.GetLastKnownFingerprint();
    public void SetLastKnownFingerprint(string fp)        => _storageService.SetLastKnownFingerprint(fp);
    public void SaveWorkspace(WorkspaceSnapshot snapshot) => _storageService.SaveWorkspace(snapshot);
    public List<WorkspaceSnapshot> GetAllWorkspaces()     => _storageService.LoadAllWorkspaces();

    /// <summary>
    /// Returns the most-recently saved workspace whose
    /// <see cref="WorkspaceSnapshot.MonitorFingerprint"/> matches <paramref name="fingerprint"/>,
    /// or <c>null</c> when no match exists.
    /// </summary>
    public WorkspaceSnapshot? FindWorkspaceByFingerprint(string fingerprint)
        => _storageService.LoadAllWorkspaces()
            .Where(w => w.MonitorFingerprint == fingerprint)
            .OrderByDescending(w => w.SavedAt)
            .FirstOrDefault();

    /// <summary>Returns the current monitor fingerprint (hash of the connected display configuration).</summary>
    public string GetCurrentMonitorFingerprint() => _monitorInventory.GetCurrentMonitorFingerprint();

    /// <summary>
    /// Enumerates the current monitors and counts live windows per monitor.
    /// Used by the Save Workspace dialog to populate the monitor checkbox list.
    /// </summary>
    public List<(MonitorInfo Monitor, int WindowCount)> GetMonitorDataForDialog()
    {
        var monitors = _monitorInventory.GetCurrentMonitors();
        var windows  = _windowInventory.SnapshotWindows(
            WindowCandidatePolicy.CaptureCandidate,
            monitors);
        return monitors
            .Select(m => (m, windows.Count(w => w.MonitorId == m.MonitorId)))
            .ToList();
    }

    /// <summary>
    /// Returns current monitors paired with all visible windows on each monitor.
    /// Used by the Save Workspace dialog to show a per-window checkbox list.
    /// </summary>
    public List<(MonitorInfo Monitor, List<WindowRecord> Windows)> GetWindowPreviewForDialog()
    {
        var monitors = _monitorInventory.GetCurrentMonitors();
        var windows  = _windowInventory.SnapshotWindows(
                WindowCandidatePolicy.CaptureCandidate,
                monitors)
            .Where(w => !w.ProcessName.Equals("WindowAnchor", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Resolve a friendly display name so the dialog shows "Insilico Terminal" rather than
        // "brave" for installed web apps. Everything else keeps its process name.
        foreach (var w in windows)
            w.DisplayName = _webAppService.ResolveWebAppName(w.ProcessName, w.AppUserModelId)
                            ?? w.ProcessName;

        return monitors
            .Select(m => (m, windows.Where(w => w.MonitorId == m.MonitorId).ToList()))
            .ToList();
    }

    // ── Capture ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Assembles window and optional browser data into one complete result without writing it.
    /// Call <see cref="PersistCapture"/> once after applying an explicit partial-capture policy.
    /// </summary>
    public async Task<WorkspaceCaptureResult> CaptureWorkspaceAsync(
        string name,
        bool saveFiles = true,
        HashSet<string>? monitorIds = null,
        IProgress<SaveProgressReport>? progress = null,
        List<WindowRecord>? selectedWindows = null,
        bool captureBrowserSessions = true,
        CancellationToken cancellationToken = default,
        bool searchCommonFolders = true,
        TimeSpan? commonFolderSearchBudget = null,
        bool buildFullJumpListCache = true)
    {
        WorkspaceSnapshot snapshot = await Task.Run(
            () => TakeSnapshot(
                name,
                saveFiles,
                monitorIds,
                progress,
                selectedWindows,
                searchCommonFolders,
                commonFolderSearchBudget,
                cancellationToken,
                buildFullJumpListCache),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        BrowserCaptureResult browserCapture;
        if (!captureBrowserSessions)
        {
            browserCapture = BrowserCaptureResult.Empty(
                BrowserCaptureStatus.Skipped,
                "Browser capture was disabled by the caller.");
        }
        else
        {
            List<string> browserTitles = snapshot.Entries
                .Where(entry => IsBrowserProcess(entry.ProcessName))
                .Select(entry => entry.Position.TitleSnippet)
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (browserTitles.Count == 0)
            {
                browserCapture = BrowserCaptureResult.Empty(
                    BrowserCaptureStatus.Skipped,
                    "No selected browser windows required session capture.");
            }
            else if (_browserSessionConnector == null)
            {
                browserCapture = BrowserCaptureResult.Empty(
                    BrowserCaptureStatus.Unavailable,
                    "No browser session connector is configured.");
            }
            else
            {
                try
                {
                    progress?.Report(new SaveProgressReport(
                        snapshot.Entries.Count,
                        snapshot.Entries.Count,
                        "Capturing browser session…",
                        $"{browserTitles.Count} browser window{(browserTitles.Count == 1 ? "" : "s")}",
                        WorkspaceCaptureProgressStage.CapturingBrowserSession));
                    browserCapture = await _browserSessionConnector.CaptureAsync(
                        name,
                        browserTitles,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppLogger.Warn(
                        "workspace.browser_capture_failed",
                        "Browser session capture failed",
                        ex,
                        LogField.Workspace("workspaceName", name),
                        LogField.Public("errorCategory", "browser_capture"));
                    browserCapture = BrowserCaptureResult.Empty(
                        BrowserCaptureStatus.Failed,
                        ex.Message);
                }
            }
        }

        snapshot.BrowserSessions = browserCapture.Sessions.ToList();
        progress?.Report(new SaveProgressReport(
            snapshot.Entries.Count,
            snapshot.Entries.Count,
            "Finalizing workspace…",
            "",
            WorkspaceCaptureProgressStage.Finalizing));
        return new WorkspaceCaptureResult(snapshot, browserCapture);
    }

    /// <summary>Persists one already-complete capture into the requested typed repository.</summary>
    public void PersistCapture(
        WorkspaceCaptureResult capture,
        WorkspaceArtifactKind destination,
        IncompleteBrowserCapturePolicy incompleteBrowserPolicy)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (!capture.BrowserCapture.IsComplete &&
            incompleteBrowserPolicy == IncompleteBrowserCapturePolicy.RequireCompleteBrowserCapture)
        {
            throw new InvalidOperationException(
                $"Browser capture did not complete ({capture.BrowserCapture.Status}); persistence was not allowed by policy.");
        }

        switch (destination)
        {
            case WorkspaceArtifactKind.NamedWorkspace:
                _storageService.NamedWorkspaces.Save(capture.Snapshot);
                break;
            case WorkspaceArtifactKind.Checkpoint:
                _storageService.Checkpoints.Save(capture.Snapshot);
                break;
            case WorkspaceArtifactKind.TemporaryCapture:
                _storageService.TemporaryCaptures.Save(capture.Snapshot);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(destination), destination, null);
        }

        AppLogger.Info(
            "workspace.capture_persisted",
            "Persisted a workspace capture",
            LogField.Workspace("workspaceName", capture.Snapshot.Name),
            LogField.Public("artifactKind", destination),
            LogField.Public("browserCaptureStatus", capture.BrowserCapture.Status));
    }

    // ── Side-effect-free snapshot construction ───────────────────────────────

    /// <summary>
    /// Captures visible windows and builds a <see cref="WorkspaceSnapshot"/>.
    /// </summary>
    /// <param name="name">Workspace name shown to the user.</param>
    /// <param name="saveFiles">
    ///   When <c>true</c> (default), run Tier 1/2/3 file detection and populate
    ///   <see cref="WorkspaceEntry.FilePath"/> / <see cref="WorkspaceEntry.LaunchArg"/>.
    ///   When <c>false</c>, only window positions are saved.
    /// </param>
    /// <param name="monitorIds">
    ///   Restrict the snapshot to windows on specific monitors (by
    ///   <see cref="MonitorInfo.MonitorId"/>).  Pass <c>null</c> (default) to include all.
    /// </param>
    /// <param name="selectedWindows">
    ///   When non-null, use this pre-filtered window list instead of enumerating
    ///   from scratch.  This is fed from the selective-save dialog.
    /// </param>
    public WorkspaceSnapshot TakeSnapshot(
        string name,
        bool saveFiles = true,
        HashSet<string>? monitorIds = null,
        IProgress<SaveProgressReport>? progress = null,
        List<WindowRecord>? selectedWindows = null,
        bool searchCommonFolders = true,
        TimeSpan? commonFolderSearchBudget = null,
        CancellationToken cancellationToken = default,
        bool buildFullJumpListCache = true)
    {
        Stopwatch snapshotTimer = Stopwatch.StartNew();
        var folderSearchBudget = new CommonFolderSearchBudget(
            saveFiles && searchCommonFolders,
            commonFolderSearchBudget ?? DefaultCommonFolderSearchBudget,
            cancellationToken);
        string fingerprint = _monitorInventory.GetCurrentMonitorFingerprint();

        // Enumerate monitors first so every WindowRecord is tagged with monitor info
        var allMonitors = _monitorInventory.GetCurrentMonitors();

        List<WindowRecord> windows;

        if (selectedWindows != null)
        {
            // Use the pre-filtered list from the selective-save dialog
            windows = selectedWindows;
            // Determine monitors to save from the windows that were selected
            var usedMonitorIds = new HashSet<string>(windows.Select(w => w.MonitorId));
            var monitorsToSaveFromSelection = allMonitors.Where(m => usedMonitorIds.Contains(m.MonitorId)).ToList();

            var selEntries = new List<WorkspaceEntry>();
            if (saveFiles && buildFullJumpListCache)
            {
                progress?.Report(new SaveProgressReport(
                    0,
                    windows.Count,
                    "Building file detection cache\u2026",
                    "",
                    WorkspaceCaptureProgressStage.Preparing));
                _jumpListService.BuildSnapshotCache();
            }

            int selProgressIdx = 0;
            try
            {
                foreach (var w in windows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new SaveProgressReport(++selProgressIdx, windows.Count, w.ProcessName, w.TitleSnippet));
                    selEntries.Add(BuildEntryForWindow(
                        w,
                        saveFiles,
                        folderSearchBudget,
                        progress,
                        buildFullJumpListCache));
                }
            }
            finally
            {
                if (saveFiles && buildFullJumpListCache) _jumpListService.ClearSnapshotCache();
            }

            progress?.Report(new SaveProgressReport(
                windows.Count,
                windows.Count,
                "Assembling workspace snapshot\u2026",
                "",
                WorkspaceCaptureProgressStage.Finalizing));

            var selSnapshot = new WorkspaceSnapshot
            {
                Name               = name,
                MonitorFingerprint = fingerprint,
                SavedAt            = DateTime.UtcNow,
                SavedWithFiles     = saveFiles,
                Monitors           = monitorsToSaveFromSelection,
                Entries            = selEntries,
            };

            AppLogger.Info(
                "workspace.snapshot_built",
                "Built a selective workspace snapshot",
                LogField.Workspace("workspaceName", name),
                LogField.Public("entryCount", selEntries.Count),
                LogField.Public("saveFiles", saveFiles),
                LogField.Public("captureMode", "selective"),
                LogField.Public("durationMs", snapshotTimer.Elapsed.TotalMilliseconds),
                LogField.Public("recursiveFileSearch", searchCommonFolders),
                LogField.Public("fullJumpListIndex", buildFullJumpListCache));
            return selSnapshot;
        }

        // ── Original path: enumerate windows from scratch ─────────────────

        // Determine which monitors to include (null = all)
        var monitorsToSave = monitorIds == null
            ? allMonitors
            : allMonitors.Where(m => monitorIds.Contains(m.MonitorId)).ToList();

        windows = _windowInventory.SnapshotWindows(
            WindowCandidatePolicy.CaptureCandidate,
            allMonitors);

        // Filter windows to only include those on the selected monitors
        var selectedMonitorIdSet = new HashSet<string>(monitorsToSave.Select(m => m.MonitorId));
        if (monitorIds != null)
            windows = windows.Where(w => selectedMonitorIdSet.Contains(w.MonitorId)).ToList();

        var entries = new List<WorkspaceEntry>();

        // Build the jump-list index once (only needed when saving files)
        if (saveFiles && buildFullJumpListCache)
        {
            progress?.Report(new SaveProgressReport(
                0,
                windows.Count,
                "Building file detection cache\u2026",
                "",
                WorkspaceCaptureProgressStage.Preparing));
            _jumpListService.BuildSnapshotCache();
        }

        int progressIdx = 0;
        try
        {
        foreach (var w in windows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Report progress for this window before processing it
            progress?.Report(new SaveProgressReport(++progressIdx, windows.Count, w.ProcessName, w.TitleSnippet));
            // ── Self-exclusion: never save WindowAnchor's own windows ──────
            if (w.ProcessName.Equals("WindowAnchor", StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Info("TakeSnapshot: skipping WindowAnchor's own window");
                continue;
            }

            // ── Browser web app (PWA) special case ─────────────────────────
            // Installed web apps (Insilico Terminal, aggr.trade, …) run inside chrome.exe /
            // brave.exe. Record how to relaunch the app itself instead of the browser.
            var webAppEntry = TryBuildWebAppEntry(w);
            if (webAppEntry != null)
            {
                entries.Add(webAppEntry);
                continue;
            }

            // ── Dedicated browser window (site kept in its own window) ─────
            var urlEntry = TryBuildDedicatedBrowserEntry(w);
            if (urlEntry != null)
            {
                entries.Add(urlEntry);
                continue;
            }

            // ── Explorer special case ──────────────────────────────────────
            if (w.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(w.FolderPath))
            {
                entries.Add(new WorkspaceEntry
                {
                    ExecutablePath  = w.ExecutablePath,
                    ProcessName     = w.ProcessName,
                    WindowClassName = w.ClassName,
                    AppUserModelId  = w.AppUserModelId,
                    FilePath        = saveFiles ? w.FolderPath : null,
                    FileConfidence  = saveFiles ? 95 : 0,
                    FileSource      = saveFiles ? "EXPLORER_FOLDER" : "NONE",
                    LaunchArg       = saveFiles ? w.FolderPath : null,
                    Position        = w,
                    MonitorId       = w.MonitorId,
                    MonitorIndex    = w.MonitorIndex,
                    MonitorName     = w.MonitorName,
                });
                continue;
            }

            string? filePath   = null;
            int     confidence = 0;
            string  source     = "NONE";
            string? launchArg  = null;

            if (saveFiles)
            {
                Stopwatch detectionTimer = Stopwatch.StartNew();
                AppLogger.Debug(
                    "file_detection.started",
                    "Started file detection for a window",
                    LogField.Public("processName", w.ProcessName),
                    LogField.Title("windowTitle", w.TitleSnippet),
                    LogField.Path("executablePath", w.ExecutablePath));

                // ── Tier 1: parse file path from window title ──────────────
                var (titlePath, titleConf) = TitleParser.ExtractFilePath(w.ProcessName, w.TitleSnippet);
                filePath   = titlePath;
                confidence = titleConf;
                source     = titleConf > 0 ? "TITLE_PARSE" : "NONE";

                if (titleConf > 0)
                    AppLogger.Debug(
                        "file_detection.title_match",
                        "Matched a file from the window title",
                        LogField.Public("confidence", titleConf),
                        LogField.Path("filePath", titlePath));
                else
                    AppLogger.Debug(
                        "file_detection.title_no_match",
                        "Window title did not produce a file match");

                // ── Tier 1.5: exact jump-list filename match for bare T1 names ──
                // When T1 extracted only a bare filename (conf=40, no directory separator),
                // search a larger jump-list pool for the exact same filename.
                // This handles files that are open but have scrolled past position 10 in the
                // jump list, making them invisible to T2's default candidate window.
                if (buildFullJumpListCache &&
                    confidence == 40 &&
                    !string.IsNullOrEmpty(filePath) &&
                    !Path.IsPathRooted(filePath))
                {
                    try
                    {
                        var jlPool = GetRecentFilesForCapture(
                            w.ExecutablePath,
                            maxFiles: 50,
                            buildFullJumpListCache);
                        AppLogger.Debug(
                            "file_detection.jump_list_exact_started",
                            "Searching the jump list for an exact filename",
                            LogField.Public("candidateCount", jlPool.Count));
                        string? exact = jlPool.FirstOrDefault(p =>
                            Path.GetFileName(p).Equals(filePath, StringComparison.OrdinalIgnoreCase));
                        if (exact != null)
                        {
                            filePath   = exact;
                            confidence = 90;
                            source     = "JUMPLIST_EXACT";
                            AppLogger.Debug(
                                "file_detection.jump_list_exact_match",
                                "Matched an exact filename in the jump list",
                                LogField.Path("filePath", exact));
                        }
                        else
                        {
                            AppLogger.Debug(
                                "file_detection.jump_list_exact_no_match",
                                "No exact filename match was found in the jump list");
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn(
                            "file_detection.jump_list_exact_failed",
                            "Exact jump-list matching failed",
                            ex,
                            LogField.Public("errorCategory", "jump_list_exact"));
                    }
                }

                // ── Tier 2: jump-list lookup ───────────────────────────────
                if (buildFullJumpListCache &&
                    confidence < 80 &&
                    !string.IsNullOrEmpty(w.ExecutablePath))
                {
                    try
                    {
                        var jlFiles = GetRecentFilesForCapture(
                            w.ExecutablePath,
                            maxFiles: 30,
                            buildFullJumpListCache);
                        AppLogger.Debug(
                            "file_detection.jump_list_loaded",
                            "Loaded jump-list candidates",
                            LogField.Public("candidateCount", jlFiles.Count));
                        foreach (var jf in jlFiles)
                            AppLogger.Debug(
                                "file_detection.jump_list_candidate",
                                "Found a jump-list candidate",
                                LogField.Path("filePath", jf));

                        if (jlFiles.Count > 0)
                        {
                            string titleLower = w.TitleSnippet.ToLowerInvariant();

                            // Match using the full filename (including extension) to avoid
                            // false positives from short or common filename stems.
                            // Sort by filename length descending so the most specific
                            // (longest) match wins when multiple candidates qualify.
                            string? jlBest = jlFiles
                                .Where(p =>
                                {
                                    string name = Path.GetFileName(p);
                                    string stem = Path.GetFileNameWithoutExtension(p);
                                    if (stem.Length < 3) return false;
                                    return titleLower.Contains(name.ToLowerInvariant()) ||
                                           titleLower.Contains(stem.ToLowerInvariant());
                                })
                                .OrderByDescending(p => Path.GetFileNameWithoutExtension(p).Length)
                                .FirstOrDefault();

                            if (jlBest != null)
                            {
                                filePath   = jlBest;
                                confidence = 80;
                                source     = "JUMPLIST";
                                AppLogger.Debug(
                                    "file_detection.jump_list_match",
                                    "Matched a jump-list candidate",
                                    LogField.Path("filePath", jlBest));
                            }
                            else
                            {
                                // Log why none matched: show what the title contains vs what candidates had
                                AppLogger.Debug(
                                    "file_detection.jump_list_no_match",
                                    "No jump-list candidate matched the window title",
                                    LogField.Title("windowTitle", w.TitleSnippet));
                                foreach (var jf in jlFiles)
                                    AppLogger.Debug(
                                        "file_detection.jump_list_no_match_detail",
                                        "Recorded jump-list comparison detail",
                                        LogField.Title("candidateStem", Path.GetFileNameWithoutExtension(jf)),
                                        LogField.Public("titleContainsCandidate", titleLower.Contains(Path.GetFileNameWithoutExtension(jf).ToLowerInvariant())));
                            }
                        }
                        else
                        {
                            AppLogger.Debug(
                                "file_detection.jump_list_empty",
                                "No jump-list candidates were available");
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn(
                            "file_detection.jump_list_failed",
                            "Jump-list file detection failed",
                            ex,
                            LogField.Public("errorCategory", "jump_list"));
                    }
                }

                // ── Tier 3: search common user folders for bare filename ───
                if (confidence < 80 &&
                    IsPlausibleBareFileName(filePath) &&
                    folderSearchBudget.CanSearch)
                {
                    progress?.Report(new SaveProgressReport(
                        progressIdx,
                        windows.Count,
                        $"Searching files for {w.ProcessName}…",
                        filePath!,
                        WorkspaceCaptureProgressStage.SearchingCommonFolders));
                    AppLogger.Debug(
                        "file_detection.folder_search_started",
                        "Searching common folders for a filename",
                        LogField.Path("fileName", filePath));
                    try
                    {
                        Stopwatch searchTimer = Stopwatch.StartNew();
                        string? found = SearchFileInCommonLocations(
                            filePath!,
                            folderSearchBudget,
                            out bool timedOut);
                        searchTimer.Stop();
                        if (found != null)
                        {
                            filePath = found; confidence = 85; source = "FILE_SEARCH";
                            AppLogger.Debug(
                                "file_detection.folder_search_match",
                                "Found a matching file in a common folder",
                                LogField.Path("filePath", found),
                                LogField.Public("durationMs", searchTimer.Elapsed.TotalMilliseconds));
                        }
                        else if (timedOut)
                        {
                            AppLogger.Warn(
                                "file_detection.folder_search_timed_out",
                                "Common-folder search reached the global capture budget",
                                LogField.Public("durationMs", searchTimer.Elapsed.TotalMilliseconds),
                                LogField.Public("budgetMs", folderSearchBudget.Limit.TotalMilliseconds));
                        }
                        else
                        {
                            AppLogger.Debug(
                                "file_detection.folder_search_no_match",
                                "Common-folder search found zero or ambiguous matches",
                                LogField.Public("durationMs", searchTimer.Elapsed.TotalMilliseconds));
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn(
                            "file_detection.folder_search_failed",
                            "Common-folder file detection failed",
                            ex,
                            LogField.Public("errorCategory", "folder_search"));
                    }
                }

                launchArg = confidence >= 80 ? filePath : null;
                detectionTimer.Stop();
                AppLogger.Debug(
                    "file_detection.completed",
                    "Completed file detection for a window",
                    LogField.Public("source", source),
                    LogField.Public("confidence", confidence),
                    LogField.Path("launchArgument", launchArg),
                    LogField.Public("durationMs", detectionTimer.Elapsed.TotalMilliseconds));

                // VS Code / Cursor (Electron-based editors): launch arg must be a folder or
                // a .code-workspace file, never a bare source file.
                bool isVsCodeLike = w.ProcessName.Equals("Code",   StringComparison.OrdinalIgnoreCase)
                                 || w.ProcessName.Equals("Cursor", StringComparison.OrdinalIgnoreCase);
                if (isVsCodeLike && launchArg != null)
                {
                    if (Directory.Exists(launchArg))
                    {
                        // Jump-list returned a workspace folder directly — use as-is.
                    }
                    else if (File.Exists(launchArg) &&
                             !launchArg.EndsWith(".code-workspace", StringComparison.OrdinalIgnoreCase))
                    {
                        // Tier 1/3 returned a source file — promote to the containing folder.
                        launchArg = Path.GetDirectoryName(launchArg);
                    }
                    // .code-workspace files are kept as-is (VS Code accepts them directly).
                }
            }

            entries.Add(new WorkspaceEntry
            {
                ExecutablePath  = w.ExecutablePath,
                ProcessName     = w.ProcessName,
                WindowClassName = w.ClassName,
                AppUserModelId  = w.AppUserModelId,
                FilePath        = filePath,
                FileConfidence  = confidence,
                FileSource      = source,
                LaunchArg       = launchArg,
                Position        = w,
                MonitorId       = w.MonitorId,
                MonitorIndex    = w.MonitorIndex,
                MonitorName     = w.MonitorName,
            });
        }
        }
        finally
        {
            if (saveFiles && buildFullJumpListCache)
                _jumpListService.ClearSnapshotCache();
        }

        progress?.Report(new SaveProgressReport(
            windows.Count,
            windows.Count,
            "Assembling workspace snapshot\u2026",
            "",
            WorkspaceCaptureProgressStage.Finalizing));

        var snapshot = new WorkspaceSnapshot
        {
            Name               = name,
            MonitorFingerprint = fingerprint,
            SavedAt            = DateTime.UtcNow,
            SavedWithFiles     = saveFiles,
            Monitors           = monitorsToSave,
            Entries            = entries,
        };

        AppLogger.Info(
            "workspace.snapshot_built",
            "Built a workspace snapshot",
            LogField.Workspace("workspaceName", name),
            LogField.Public("entryCount", entries.Count),
            LogField.Public("monitorCount", monitorsToSave.Count),
            LogField.Public("saveFiles", saveFiles),
            LogField.Public("captureMode", "all_windows"),
            LogField.Public("durationMs", snapshotTimer.Elapsed.TotalMilliseconds),
            LogField.Public("recursiveFileSearch", searchCommonFolders),
            LogField.Public("fullJumpListIndex", buildFullJumpListCache));
        return snapshot;
    }

    // ── Browser web apps (PWAs) ───────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="WorkspaceEntry"/> for an installed browser web app (PWA), or returns
    /// <c>null</c> when <paramref name="w"/> is not a web-app window.
    /// <para>
    /// A window qualifies when it belongs to a Chromium-based browser <em>and</em> carries an
    /// explicit <c>AppUserModelID</c> that either matches a Start-Menu shortcut created by the
    /// browser (<c>--app-id=…</c>) or has the shape of a Chromium web-app AUMID. Ordinary browser
    /// windows share the browser's own AUMID and are left to the normal code path.
    /// </para>
    /// </summary>
    private WorkspaceEntry? TryBuildWebAppEntry(WindowRecord w)
    {
        if (!WebAppService.IsChromiumBrowser(w.ProcessName)) return null;
        if (string.IsNullOrEmpty(w.AppUserModelId))          return null;

        var info = _webAppService.FindByAumid(w.AppUserModelId);

        if (info == null && !WebAppService.LooksLikeWebAppAumid(w.AppUserModelId))
            return null;   // plain browser window

        string? shortcutPath;
        string? target;
        string? args;
        string  name;
        string  source;

        if (info != null)
        {
            shortcutPath = info.ShortcutPath;
            target       = info.TargetPath;
            args         = info.Arguments;
            name         = info.DisplayName;
            source       = "WEB_APP_SHORTCUT";
        }
        else
        {
            // No shortcut on disk (app installed without one, or shortcut deleted).
            // Rebuild a command line from the AUMID: chrome.exe --app-id=<32-char id>.
            string? appId = WebAppService.ExtractAppIdFromAumid(w.AppUserModelId);
            if (appId == null) return null;

            shortcutPath = null;
            target       = w.ExecutablePath;
            args         = $"--app-id={appId}";
            name         = w.TitleSnippet;
            source       = "WEB_APP_AUMID";
            AppLogger.Warn(
                "web_app.shortcut_not_found",
                "No web-app shortcut was found; using an AUMID-derived launch command",
                LogField.Identifier("appUserModelId", w.AppUserModelId),
                LogField.Path("launchTarget", target),
                LogField.CommandLine("launchArguments", args));
        }

        AppLogger.Info(
            "web_app.detected",
            "Detected an installed browser web app",
            LogField.Title("webAppName", name),
            LogField.Identifier("appUserModelId", w.AppUserModelId),
            LogField.Public("source", source));

        return new WorkspaceEntry
        {
            ExecutablePath        = w.ExecutablePath,
            ProcessName           = w.ProcessName,
            WindowClassName       = w.ClassName,
            FilePath              = null,
            FileConfidence        = 0,
            FileSource            = source,
            LaunchArg             = null,          // must stay null: not a document entry
            AppUserModelId        = w.AppUserModelId,
            IsWebApp              = true,
            WebAppName            = name,
            WebAppShortcutPath    = shortcutPath,
            WebAppLaunchTarget    = target,
            WebAppLaunchArguments = args,
            Position              = w,
            MonitorId             = w.MonitorId,
            MonitorIndex          = w.MonitorIndex,
            MonitorName           = w.MonitorName,
        };
    }

    /// <summary>
    /// Builds a <see cref="WorkspaceEntry"/> for a browser window that the user keeps at a
    /// specific site in its own window, or <c>null</c> when the window is not one of those.
    /// <para>
    /// A window qualifies when <see cref="WindowRecord.BrowserUrl"/> was populated during capture,
    /// which only happens for a Chromium window whose address bar matched a configured
    /// <see cref="Models.AppSettings.DedicatedBrowserUrlPatterns"/> entry.
    /// </para>
    /// </summary>
    private WorkspaceEntry? TryBuildDedicatedBrowserEntry(WindowRecord w)
    {
        if (string.IsNullOrEmpty(w.BrowserUrl)) return null;

        AppLogger.Info(
            "browser_url.dedicated_window_captured",
            "Captured a dedicated browser window",
            LogField.Url("url", w.BrowserUrl),
            LogField.Public("processName", w.ProcessName));

        return new WorkspaceEntry
        {
            ExecutablePath           = w.ExecutablePath,
            ProcessName              = w.ProcessName,
            WindowClassName          = w.ClassName,
            AppUserModelId           = w.AppUserModelId,
            FilePath                 = null,
            FileConfidence           = 0,
            FileSource               = "BROWSER_URL",
            LaunchArg                = null,   // must stay null: not a document entry
            IsDedicatedBrowserWindow = true,
            BrowserUrl               = w.BrowserUrl,
            Position                 = w,
            MonitorId                = w.MonitorId,
            MonitorIndex             = w.MonitorIndex,
            MonitorName              = w.MonitorName,
        };
    }

    // ── Per-window entry builder (shared by both snapshot paths) ──────────

    private List<string> GetRecentFilesForCapture(
        string executablePath,
        int maxFiles,
        bool buildFullJumpListCache) =>
        buildFullJumpListCache
            ? _jumpListService.GetRecentFilesForApp(executablePath, maxFiles)
            : new List<string>();

    /// <summary>
    /// Builds a <see cref="WorkspaceEntry"/> for a single window, running file-detection
    /// tiers when <paramref name="saveFiles"/> is <c>true</c>.  Assumes the jump-list
    /// snapshot cache is already populated.
    /// </summary>
    private WorkspaceEntry BuildEntryForWindow(
        WindowRecord w,
        bool saveFiles,
        CommonFolderSearchBudget folderSearchBudget,
        IProgress<SaveProgressReport>? progress,
        bool buildFullJumpListCache)
    {
        // Browser web app (PWA) special case — must run before file detection, otherwise a
        // web-app window is treated as a generic browser window.
        var webAppEntry = TryBuildWebAppEntry(w);
        if (webAppEntry != null) return webAppEntry;

        // Dedicated browser window (site kept in its own window)
        var urlEntry = TryBuildDedicatedBrowserEntry(w);
        if (urlEntry != null) return urlEntry;

        // Explorer special case
        if (w.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(w.FolderPath))
        {
            return new WorkspaceEntry
            {
                ExecutablePath  = w.ExecutablePath,
                ProcessName     = w.ProcessName,
                WindowClassName = w.ClassName,
                AppUserModelId  = w.AppUserModelId,
                FilePath        = saveFiles ? w.FolderPath : null,
                FileConfidence  = saveFiles ? 95 : 0,
                FileSource      = saveFiles ? "EXPLORER_FOLDER" : "NONE",
                LaunchArg       = saveFiles ? w.FolderPath : null,
                Position        = w,
                MonitorId       = w.MonitorId,
                MonitorIndex    = w.MonitorIndex,
                MonitorName     = w.MonitorName,
            };
        }

        string? filePath   = null;
        int     confidence = 0;
        string  source     = "NONE";
        string? launchArg  = null;

        if (saveFiles)
        {
            Stopwatch detectionTimer = Stopwatch.StartNew();
            AppLogger.Debug(
                "file_detection.started",
                "Started file detection for a window",
                LogField.Public("processName", w.ProcessName),
                LogField.Title("windowTitle", w.TitleSnippet),
                LogField.Path("executablePath", w.ExecutablePath));

            var (titlePath, titleConf) = TitleParser.ExtractFilePath(w.ProcessName, w.TitleSnippet);
            filePath   = titlePath;
            confidence = titleConf;
            source     = titleConf > 0 ? "TITLE_PARSE" : "NONE";

            if (titleConf > 0)
                AppLogger.Debug(
                    "file_detection.title_match",
                    "Matched a file from the window title",
                    LogField.Public("confidence", titleConf),
                    LogField.Path("filePath", titlePath));
            else
                AppLogger.Debug(
                    "file_detection.title_no_match",
                    "Window title did not produce a file match");

            // Tier 1.5
            if (buildFullJumpListCache &&
                confidence == 40 &&
                !string.IsNullOrEmpty(filePath) &&
                !Path.IsPathRooted(filePath))
            {
                try
                {
                    var jlPool = GetRecentFilesForCapture(
                        w.ExecutablePath,
                        maxFiles: 50,
                        buildFullJumpListCache);
                    string? exact = jlPool.FirstOrDefault(p =>
                        Path.GetFileName(p).Equals(filePath, StringComparison.OrdinalIgnoreCase));
                    if (exact != null)
                    {
                        filePath   = exact;
                        confidence = 90;
                        source     = "JUMPLIST_EXACT";
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn(
                        "file_detection.jump_list_exact_failed",
                        "Exact jump-list matching failed",
                        ex,
                        LogField.Public("errorCategory", "jump_list_exact"));
                }
            }

            // Tier 2
            if (buildFullJumpListCache &&
                confidence < 80 &&
                !string.IsNullOrEmpty(w.ExecutablePath))
            {
                try
                {
                    var jlFiles = GetRecentFilesForCapture(
                        w.ExecutablePath,
                        maxFiles: 30,
                        buildFullJumpListCache);
                    if (jlFiles.Count > 0)
                    {
                        string titleLower = w.TitleSnippet.ToLowerInvariant();
                        string? jlBest = jlFiles
                            .Where(p =>
                            {
                                string name = Path.GetFileName(p);
                                string stem = Path.GetFileNameWithoutExtension(p);
                                if (stem.Length < 3) return false;
                                return titleLower.Contains(name.ToLowerInvariant()) ||
                                       titleLower.Contains(stem.ToLowerInvariant());
                            })
                            .OrderByDescending(p => Path.GetFileNameWithoutExtension(p).Length)
                            .FirstOrDefault();

                        if (jlBest != null)
                        {
                            filePath   = jlBest;
                            confidence = 80;
                            source     = "JUMPLIST";
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn(
                        "file_detection.jump_list_failed",
                        "Jump-list file detection failed",
                        ex,
                        LogField.Public("errorCategory", "jump_list"));
                }
            }

            // Tier 3
            if (confidence < 80 &&
                IsPlausibleBareFileName(filePath) &&
                folderSearchBudget.CanSearch)
            {
                progress?.Report(new SaveProgressReport(
                    0,
                    0,
                    $"Searching files for {w.ProcessName}…",
                    filePath!,
                    WorkspaceCaptureProgressStage.SearchingCommonFolders));
                try
                {
                    Stopwatch searchTimer = Stopwatch.StartNew();
                    string? found = SearchFileInCommonLocations(
                        filePath!,
                        folderSearchBudget,
                        out bool timedOut);
                    searchTimer.Stop();
                    if (found != null)
                    {
                        filePath = found; confidence = 85; source = "FILE_SEARCH";
                    }
                    else if (timedOut)
                    {
                        AppLogger.Warn(
                            "file_detection.folder_search_timed_out",
                            "Common-folder search reached the global capture budget",
                            LogField.Public("durationMs", searchTimer.Elapsed.TotalMilliseconds),
                            LogField.Public("budgetMs", folderSearchBudget.Limit.TotalMilliseconds));
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppLogger.Warn(
                        "file_detection.folder_search_failed",
                        "Common-folder file detection failed",
                        ex,
                        LogField.Public("errorCategory", "folder_search"));
                }
            }

            launchArg = confidence >= 80 ? filePath : null;

            bool isVsCodeLike = w.ProcessName.Equals("Code",   StringComparison.OrdinalIgnoreCase)
                             || w.ProcessName.Equals("Cursor", StringComparison.OrdinalIgnoreCase);
            if (isVsCodeLike && launchArg != null)
            {
                if (Directory.Exists(launchArg)) { /* folder — keep as-is */ }
                else if (File.Exists(launchArg) &&
                         !launchArg.EndsWith(".code-workspace", StringComparison.OrdinalIgnoreCase))
                    launchArg = Path.GetDirectoryName(launchArg);
            }

            detectionTimer.Stop();
            AppLogger.Debug(
                "file_detection.completed",
                "Completed file detection for a window",
                LogField.Public("source", source),
                LogField.Public("confidence", confidence),
                LogField.Path("launchArgument", launchArg),
                LogField.Public("durationMs", detectionTimer.Elapsed.TotalMilliseconds));
        }

        return new WorkspaceEntry
        {
            ExecutablePath  = w.ExecutablePath,
            ProcessName     = w.ProcessName,
            WindowClassName = w.ClassName,
            AppUserModelId  = w.AppUserModelId,
            FilePath        = filePath,
            FileConfidence  = confidence,
            FileSource      = source,
            LaunchArg       = launchArg,
            Position        = w,
            MonitorId       = w.MonitorId,
            MonitorIndex    = w.MonitorIndex,
            MonitorName     = w.MonitorName,
        };
    }

    // ── Selective restore ─────────────────────────────────────────────────────

    /// <summary>
    /// Restores only the entries that belong to the specified monitors.
    /// When <paramref name="monitorIds"/> is <c>null</c> all entries are restored (same as
    /// <see cref="RestoreWorkspaceAsync"/>).
    /// </summary>
    public Task RestoreWorkspaceSelectiveAsync(
        WorkspaceSnapshot snapshot,
        HashSet<string>? monitorIds,
        CancellationToken ct = default)
    {
        if (monitorIds == null)
            return RestoreWorkspaceAsync(snapshot, ct);
        return RestoreWorkspaceUsingPlanAsync(snapshot, RestoreMode.Selective(monitorIds.ToArray()), ct);
    }

    // ── Restore ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Restores a workspace snapshot using a 5-phase approach:
    /// <list type="number">
    ///   <item>Immediately reposition already-running windows.</item>
    ///   <item>Launch missing apps; open saved documents even if the app exe is already running.</item>
    ///   <item>3-second wait for app initialisation.</item>
    ///   <item>Reposition newly appeared windows.</item>
    ///   <item>2-second wait + second pass for slow launchers (Office, IDEs).</item>
    /// </list>
    /// </summary>
    public async Task RestoreWorkspaceAsync(WorkspaceSnapshot snapshot, CancellationToken ct = default)
    {
        _ = await RestoreWorkspaceWithExecutionResultAsync(snapshot, RestoreMode.Standard, ct);
    }

    /// <summary>
    /// Same as <see cref="RestoreWorkspaceAsync"/>, but after repositioning the workspace's own
    /// windows it minimizes every other open window (nothing is closed). Use this to bring a
    /// workspace to the foreground and clear away unrelated windows without the destructive
    /// close-everything behaviour of <c>SwitchWorkspaceAsync</c>.
    /// </summary>
    public async Task RestoreWorkspaceAlignAndMinimizeAsync(WorkspaceSnapshot snapshot, CancellationToken ct = default)
    {
        _ = await RestoreWorkspaceWithExecutionResultAsync(
            snapshot,
            RestoreMode.AlignAndMinimize,
            ct);
    }

    /// <summary>
    /// Restores a workspace and returns the structured state accumulated by that restore session.
    /// Kept internal until a UI or automation contract needs to expose the detailed result.
    /// </summary>
    internal Task<RestoreSessionResult> RestoreWorkspaceWithResultAsync(
        WorkspaceSnapshot snapshot,
        bool minimizeOthers = false,
        CancellationToken ct = default) => RestoreWorkspaceWithLegacyResultAsync(
            snapshot,
            minimizeOthers ? RestoreMode.AlignAndMinimize : RestoreMode.Standard,
            ct);

    /// <summary>
    /// Observes the current environment and builds a reviewable plan. Discovery is read-only;
    /// process, browser, persistence, and native window mutation APIs are not called.
    /// </summary>
    public RestorePlan CreateRestorePlan(WorkspaceSnapshot snapshot, RestoreMode mode)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(mode);
        Dictionary<IntPtr, (uint Pid, WindowRecord Record)> liveWindows =
            _windowInventory.GetWindowsWithPids(WindowCandidatePolicy.RestoreMatchCandidate);
        return CreateRestorePlan(snapshot, mode, liveWindows);
    }

    /// <summary>
    /// Executes exactly the actions in an already-approved plan. This is intentionally internal:
    /// user-facing restore paths require the source snapshot so they can create a checkpoint.
    /// </summary>
    internal Task<RestoreExecutionResult> ExecuteRestorePlanAsync(
        RestorePlan approvedPlan,
        CancellationToken ct = default,
        IProgress<RestoreProgressReport>? progress = null) =>
        _restoreExecutor.ExecuteAsync(approvedPlan, ct, progress);

    /// <summary>Builds and executes a plan while returning structured stale-plan outcomes.</summary>
    public async Task<RestoreExecutionResult> RestoreWorkspaceWithExecutionResultAsync(
        WorkspaceSnapshot snapshot,
        RestoreMode mode,
        CancellationToken ct = default,
        IProgress<RestoreProgressReport>? progress = null)
    {
        RestorePlan plan = CreateRestorePlan(snapshot, mode);
        return await ExecuteApprovedRestorePlanAsync(snapshot, plan, ct, progress).ConfigureAwait(false);
    }

    /// <summary>Runs an automatic restore with an explicit checkpoint trigger classification.</summary>
    internal async Task<RestoreExecutionResult> RestoreWorkspaceWithExecutionResultAsync(
        WorkspaceSnapshot snapshot,
        RestoreMode mode,
        WorkspaceCheckpointTrigger checkpointTrigger,
        CancellationToken ct)
    {
        RestorePlan plan = CreateRestorePlan(snapshot, mode);
        return await ExecuteApprovedRestorePlanTransactionalAsync(
            snapshot,
            plan,
            checkpointTrigger,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes an already-reviewed plan for its source snapshot without rebuilding or silently
    /// replacing the approved matches and actions.
    /// </summary>
    public async Task<RestoreExecutionResult> ExecuteApprovedRestorePlanAsync(
        WorkspaceSnapshot snapshot,
        RestorePlan approvedPlan,
        CancellationToken ct = default,
        IProgress<RestoreProgressReport>? progress = null) => await ExecuteApprovedRestorePlanTransactionalAsync(
            snapshot,
            approvedPlan,
            DetermineCheckpointTrigger(approvedPlan),
            ct,
            progress).ConfigureAwait(false);

    /// <summary>True when at least one healthy, non-expired checkpoint can be restored.</summary>
    public bool CanUndoLastRestore => _storageService.Checkpoints.GetLatest() is not null;

    /// <summary>
    /// Restores the newest healthy checkpoint through the normal planner. The transactional
    /// execution creates a new safety checkpoint first, making an undo itself undoable.
    /// </summary>
    public async Task<RestoreExecutionResult?> UndoLastRestoreAsync(
        CancellationToken ct = default,
        IProgress<RestoreProgressReport>? progress = null)
    {
        WorkspaceSnapshot? checkpoint = _storageService.Checkpoints.GetLatest();
        if (checkpoint is null)
            return null;

        RestorePlan plan = CreateRestorePlan(checkpoint, RestoreMode.Standard);
        return await ExecuteApprovedRestorePlanTransactionalAsync(
            checkpoint,
            plan,
            WorkspaceCheckpointTrigger.Undo,
            ct,
            progress).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs an operation behind the single restore transaction gate. The callback is not invoked
    /// unless the pre-restore snapshot has been committed atomically.
    /// </summary>
    internal async Task<CheckpointedOperationResult<T>> ExecuteCheckpointedOperationAsync<T>(
        WorkspaceSnapshot targetSnapshot,
        WorkspaceCheckpointTrigger trigger,
        Func<RestoreCheckpointOutcome, CancellationToken, Task<T>> operation,
        CancellationToken ct,
        IProgress<RestoreProgressReport>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(targetSnapshot);
        ArgumentNullException.ThrowIfNull(operation);

        try
        {
            await _restoreTransactionGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new CheckpointedOperationResult<T>(
                CancelledCheckpoint(trigger),
                false,
                default);
        }

        try
        {
            RestoreCheckpointOutcome checkpoint = await CreatePreRestoreCheckpointAsync(
                targetSnapshot,
                trigger,
                ct,
                progress).ConfigureAwait(false);
            if (!checkpoint.IsCreated)
                return new CheckpointedOperationResult<T>(checkpoint, false, default);

            T value = await operation(checkpoint, ct).ConfigureAwait(false);
            return new CheckpointedOperationResult<T>(checkpoint, true, value);
        }
        finally
        {
            _restoreTransactionGate.Release();
        }
    }

    internal RestoreExecutionResult CreateCheckpointAbortedResult(
        RestorePlan plan,
        RestoreCheckpointOutcome checkpoint)
    {
        bool cancelled = checkpoint.Status == RestoreCheckpointStatus.Cancelled;
        RestoreExecutionEntryResult[] entries = plan.Entries.Select(entry =>
            new RestoreExecutionEntryResult(
                entry.EntryIndex,
                entry.EntryId,
                entry.Outcome == RestorePlanEntryOutcome.Excluded
                    ? RestoreExecutionEntryStatus.Excluded
                    : entry.Outcome == RestorePlanEntryOutcome.Blocked
                        ? RestoreExecutionEntryStatus.Blocked
                        : cancelled
                            ? RestoreExecutionEntryStatus.Cancelled
                            : RestoreExecutionEntryStatus.Failed,
                null,
                checkpoint.Explanation)).ToArray();
        RestoreExecutionActionResult[] actions = plan.Actions.Select((action, index) =>
            new RestoreExecutionActionResult(
                index,
                action.EntryIndex,
                action.Kind,
                cancelled
                    ? RestoreExecutionActionStatus.Cancelled
                    : RestoreExecutionActionStatus.Skipped,
                null,
                action.WindowHandle,
                checkpoint.Explanation)).ToArray();
        return new RestoreExecutionResult(
            plan.WorkspaceId,
            cancelled ? RestoreExecutionStatus.Cancelled : RestoreExecutionStatus.Rejected,
            cancelled,
            entries,
            actions,
            new HashSet<long>())
        {
            Checkpoint = checkpoint
        };
    }

    private async Task<RestoreExecutionResult> ExecuteApprovedRestorePlanTransactionalAsync(
        WorkspaceSnapshot snapshot,
        RestorePlan approvedPlan,
        WorkspaceCheckpointTrigger trigger,
        CancellationToken ct,
        IProgress<RestoreProgressReport>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(approvedPlan);
        if (!string.Equals(
                snapshot.WorkspaceId,
                approvedPlan.WorkspaceId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The approved restore plan belongs to a different workspace snapshot.",
                nameof(approvedPlan));
        }

        // A blocked, cancelled, or empty plan cannot mutate the desktop and therefore should not
        // consume checkpoint history. The executor still owns its structured rejection result.
        if (!approvedPlan.CanExecute || approvedPlan.Actions.Count == 0)
            return await ExecuteApprovedRestorePlanCoreAsync(snapshot, approvedPlan, ct, progress)
                .ConfigureAwait(false);

        CheckpointedOperationResult<RestoreExecutionResult> transaction =
            await ExecuteCheckpointedOperationAsync(
                snapshot,
                trigger,
                async (checkpoint, token) =>
                {
                    RestoreExecutionResult execution = await ExecuteApprovedRestorePlanCoreAsync(
                        snapshot,
                        approvedPlan,
                        token,
                        progress).ConfigureAwait(false);
                    return execution with { Checkpoint = checkpoint };
                },
                ct,
                progress).ConfigureAwait(false);

        return transaction.OperationStarted && transaction.Value is not null
            ? transaction.Value
            : CreateCheckpointAbortedResult(approvedPlan, transaction.Checkpoint);
    }

    internal async Task<RestoreExecutionResult> ExecuteApprovedRestorePlanAfterCheckpointAsync(
        WorkspaceSnapshot snapshot,
        RestorePlan approvedPlan,
        RestoreCheckpointOutcome checkpoint,
        CancellationToken ct,
        IProgress<RestoreProgressReport>? progress = null)
    {
        if (!checkpoint.IsCreated)
            return CreateCheckpointAbortedResult(approvedPlan, checkpoint);
        RestoreExecutionResult result = await ExecuteApprovedRestorePlanCoreAsync(
            snapshot,
            approvedPlan,
            ct,
            progress).ConfigureAwait(false);
        return result with { Checkpoint = checkpoint };
    }

    private async Task<RestoreExecutionResult> ExecuteApprovedRestorePlanCoreAsync(
        WorkspaceSnapshot snapshot,
        RestorePlan approvedPlan,
        CancellationToken ct,
        IProgress<RestoreProgressReport>? progress)
    {
        Stopwatch restoreTimer = Stopwatch.StartNew();
        AppLogger.Info(
            "restore.session_started",
            "Started an approved workspace restore session",
            LogField.Identifier("workspaceId", snapshot.WorkspaceId),
            LogField.Workspace("workspaceName", snapshot.Name),
            LogField.Public("entryCount", snapshot.Entries.Count),
            LogField.Public("mode", approvedPlan.Mode),
            LogField.Public("disabledEntryCount", approvedPlan.DisabledEntryIndexes.Count));

        RestoreExecutionResult result = await ExecuteRestorePlanAsync(approvedPlan, ct, progress).ConfigureAwait(false);
        ApplyExecutionState(snapshot, result);
        restoreTimer.Stop();

        AppLogger.Info(
            "restore.session_completed",
            "Completed an approved workspace restore session",
            LogField.Identifier("workspaceId", snapshot.WorkspaceId),
            LogField.Public("status", result.Status),
            LogField.Public("stalePlan", result.HasStalePlan),
            LogField.Public("assignedWindowCount", result.AssignedWindowHandles.Count),
            LogField.Public("durationMs", restoreTimer.Elapsed.TotalMilliseconds));
        return result;
    }

    private async Task<RestoreCheckpointOutcome> CreatePreRestoreCheckpointAsync(
        WorkspaceSnapshot targetSnapshot,
        WorkspaceCheckpointTrigger trigger,
        CancellationToken ct,
        IProgress<RestoreProgressReport>? progress)
    {
        Stopwatch checkpointTimer = Stopwatch.StartNew();
        try
        {
            ct.ThrowIfCancellationRequested();
            string name = $"Recovery before {trigger}: {targetSnapshot.Name}";
            progress?.Report(new RestoreProgressReport(
                RestoreProgressStage.PreparingCheckpoint,
                "Creating recovery checkpoint",
                "Capturing the current desktop before making changes.",
                Elapsed: checkpointTimer.Elapsed));
            IProgress<SaveProgressReport>? captureProgress = progress is null
                ? null
                : new CallbackProgress<SaveProgressReport>(report =>
                    progress.Report(MapCheckpointProgress(report, checkpointTimer.Elapsed)));
            WorkspaceCaptureResult capture = await CaptureWorkspaceAsync(
                name,
                saveFiles: true,
                captureBrowserSessions: true,
                progress: captureProgress,
                cancellationToken: ct,
                searchCommonFolders: false,
                buildFullJumpListCache: false).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            string targetWorkspaceId = Guid.TryParse(targetSnapshot.WorkspaceId, out _)
                ? targetSnapshot.WorkspaceId
                : "";
            progress?.Report(new RestoreProgressReport(
                RestoreProgressStage.SavingCheckpoint,
                "Saving recovery checkpoint",
                "Writing the checkpoint atomically.",
                capture.Snapshot.Entries.Count,
                capture.Snapshot.Entries.Count,
                checkpointTimer.Elapsed));
            CheckpointSaveReceipt receipt = _storageService.Checkpoints.Save(
                capture.Snapshot,
                trigger,
                targetWorkspaceId);
            checkpointTimer.Stop();
            AppLogger.Info(
                "checkpoint.pre_restore_created",
                "Created a durable pre-restore checkpoint",
                LogField.Identifier("checkpointId", receipt.CheckpointId),
                LogField.Identifier("targetWorkspaceId", targetWorkspaceId),
                LogField.Public("trigger", trigger),
                LogField.Public("entryCount", capture.Snapshot.Entries.Count),
                LogField.Public("durationMs", checkpointTimer.Elapsed.TotalMilliseconds),
                LogField.Public("recursiveFileSearch", false),
                LogField.Public("jumpListSearch", false));
            return new RestoreCheckpointOutcome(
                RestoreCheckpointStatus.Created,
                trigger,
                receipt.CheckpointId,
                receipt.CreatedAtUtc,
                "A recovery checkpoint was created before desktop mutation.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            checkpointTimer.Stop();
            return CancelledCheckpoint(trigger);
        }
        catch (Exception ex)
        {
            checkpointTimer.Stop();
            AppLogger.Error(
                "checkpoint.pre_restore_failed",
                "Could not create the required pre-restore checkpoint; desktop mutation was blocked",
                ex,
                LogField.Identifier("targetWorkspaceId", targetSnapshot.WorkspaceId),
                LogField.Public("trigger", trigger),
                LogField.Public("durationMs", checkpointTimer.Elapsed.TotalMilliseconds),
                LogField.Public("errorCategory", "checkpoint_creation"));
            return new RestoreCheckpointOutcome(
                RestoreCheckpointStatus.Failed,
                trigger,
                null,
                null,
                "The recovery checkpoint could not be saved, so no restore action was applied.");
        }
    }

    private static WorkspaceCheckpointTrigger DetermineCheckpointTrigger(RestorePlan plan)
    {
        if (plan.Mode == RestoreModeKind.AlignAndMinimize)
            return WorkspaceCheckpointTrigger.AlignAndMinimize;
        if (plan.Mode == RestoreModeKind.Selective)
            return WorkspaceCheckpointTrigger.SelectiveRestore;
        if (plan.Actions.Any(action => action.TargetPlacement is { Strategy: not RestorePlacementStrategy.ExactPixels }))
            return WorkspaceCheckpointTrigger.AdaptiveRestore;
        return WorkspaceCheckpointTrigger.Restore;
    }

    private static RestoreProgressReport MapCheckpointProgress(
        SaveProgressReport report,
        TimeSpan elapsed)
    {
        RestoreProgressStage stage = report.Stage switch
        {
            WorkspaceCaptureProgressStage.DetectingResources or
            WorkspaceCaptureProgressStage.SearchingCommonFolders =>
                RestoreProgressStage.DetectingResources,
            WorkspaceCaptureProgressStage.CapturingBrowserSession =>
                RestoreProgressStage.CapturingBrowserSession,
            WorkspaceCaptureProgressStage.Finalizing =>
                RestoreProgressStage.SavingCheckpoint,
            _ => RestoreProgressStage.PreparingCheckpoint
        };
        string message = report.Stage switch
        {
            WorkspaceCaptureProgressStage.DetectingResources =>
                $"Identifying recovery resources for {report.AppName}",
            WorkspaceCaptureProgressStage.SearchingCommonFolders => report.AppName,
            WorkspaceCaptureProgressStage.CapturingBrowserSession => report.AppName,
            WorkspaceCaptureProgressStage.Finalizing => "Finalizing recovery checkpoint",
            _ => "Preparing recovery checkpoint"
        };
        return new RestoreProgressReport(
            stage,
            message,
            report.Detail,
            report.Current,
            report.Total,
            elapsed);
    }

    private static RestoreCheckpointOutcome CancelledCheckpoint(WorkspaceCheckpointTrigger trigger) =>
        new(
            RestoreCheckpointStatus.Cancelled,
            trigger,
            null,
            null,
            "Checkpoint creation was cancelled before desktop mutation began.");

    private async Task RestoreWorkspaceUsingPlanAsync(
        WorkspaceSnapshot snapshot,
        RestoreMode mode,
        CancellationToken ct)
    {
        _ = await RestoreWorkspaceWithExecutionResultAsync(snapshot, mode, ct).ConfigureAwait(false);
    }

    private async Task<RestoreSessionResult> RestoreWorkspaceWithLegacyResultAsync(
        WorkspaceSnapshot snapshot,
        RestoreMode mode,
        CancellationToken ct)
    {
        RestorePlan plan = CreateRestorePlan(snapshot, mode);
        RestoreExecutionResult execution = await ExecuteApprovedRestorePlanAsync(
            snapshot,
            plan,
            ct).ConfigureAwait(false);
        return ToLegacyResult(plan, execution);
    }

    private RestorePlan CreateRestorePlan(
        WorkspaceSnapshot snapshot,
        RestoreMode mode,
        IReadOnlyDictionary<IntPtr, (uint Pid, WindowRecord Record)> liveWindows)
    {
        LiveWindowIdentity[] identities = liveWindows
            .Select(window => WindowIdentityExtractor.FromLive(
                window.Key,
                window.Value.Pid,
                window.Value.Record))
            .ToArray();
        var inventory = new RestoreLiveInventory
        {
            Windows = identities,
            Resources = ObserveRestoreResources(snapshot),
            RunningApplications = _windowInventory.GetRunningApplications(),
            MatchHints = _settingsService?.GetWindowMatchHints(snapshot.WorkspaceId) ??
                Array.Empty<WindowMatchHint>(),
            BrowserSessionRestore = snapshot.BrowserSessions.Count == 0
                ? BrowserSessionRestoreAvailability.NotAvailable
                : _browserSessionConnector is null
                    ? BrowserSessionRestoreAvailability.Unavailable
                    : BrowserSessionRestoreAvailability.Available
        };
        RestoreMonitorTopology topology = BuildRestoreTopology(snapshot, liveWindows);
        return RestorePlanner.Build(snapshot, inventory, topology, mode);
    }

    /// <summary>Persists a user-confirmed composite identity for a stable workspace entry.</summary>
    public void RememberWindowMatch(
        string workspaceId,
        string entryId,
        WindowIdentityHint identity) =>
        (_settingsService ?? throw new InvalidOperationException(
            "Learned window matching is unavailable without application settings."))
        .RememberWindowMatch(workspaceId, entryId, identity);

    /// <summary>Clears every persisted learned window match.</summary>
    public int ClearAllWindowMatches() => _settingsService?.ClearAllWindowMatches() ?? 0;

    private IReadOnlyList<RestoreResourceObservation> ObserveRestoreResources(
        WorkspaceSnapshot snapshot)
    {
        var observations = new List<RestoreResourceObservation>();
        for (int entryIndex = 0; entryIndex < snapshot.Entries.Count; entryIndex++)
        {
            WorkspaceEntry entry = snapshot.Entries[entryIndex];
            if (!string.IsNullOrWhiteSpace(entry.LaunchArg))
            {
                observations.Add(_restoreResources.Observe(
                    entryIndex,
                    RestoreResourceKind.LaunchTarget,
                    entry.LaunchArg));
            }

            string executableTarget = entry.IsWebApp
                ? entry.WebAppLaunchTarget ?? entry.ExecutablePath
                : entry.ExecutablePath;
            if (!string.IsNullOrWhiteSpace(executableTarget))
            {
                observations.Add(_restoreResources.Observe(
                    entryIndex,
                    RestoreResourceKind.Executable,
                    executableTarget));
            }

            PackagedAppResolution? packaged = _packagedAppResolver.Resolve(
                entry.ExecutablePath,
                entry.AppUserModelId);
            if (packaged is not null)
            {
                observations.Add(new RestoreResourceObservation(
                    entryIndex,
                    RestoreResourceKind.PackagedApplication,
                    RestoreResourceAvailability.Available,
                    packaged.AppUserModelId));
            }

            if (!entry.IsWebApp) continue;
            RestoreResourceObservation shortcut = _restoreResources.Observe(
                entryIndex,
                RestoreResourceKind.WebAppShortcut,
                entry.WebAppShortcutPath ?? "");
            if (shortcut.Availability != RestoreResourceAvailability.Available &&
                !string.IsNullOrWhiteSpace(entry.AppUserModelId))
            {
                WebAppInfo? resolved = _webAppService.FindByAumid(entry.AppUserModelId);
                if (resolved is not null)
                {
                    shortcut = _restoreResources.Observe(
                        entryIndex,
                        RestoreResourceKind.WebAppShortcut,
                        resolved.ShortcutPath);
                }
            }
            observations.Add(shortcut);
        }
        return observations;
    }

    private RestoreMonitorTopology BuildRestoreTopology(
        WorkspaceSnapshot snapshot,
        IReadOnlyDictionary<IntPtr, (uint Pid, WindowRecord Record)> liveWindows)
    {
        List<MonitorInfo> currentMonitors = _monitorInventory.GetCurrentMonitors();
        var result = new List<RestoreMonitor>(currentMonitors.Count);
        int nextLeft = 0;
        foreach (MonitorInfo monitor in currentMonitors
                     .OrderBy(item => item.Index)
                     .ThenBy(item => item.MonitorId, StringComparer.OrdinalIgnoreCase))
        {
            uint dpi = monitor.Dpi > 0 ? monitor.Dpi : liveWindows.Values
                .Select(item => item.Record)
                .Where(record => string.Equals(
                    record.MonitorId,
                    monitor.MonitorId,
                    StringComparison.OrdinalIgnoreCase))
                .Select(record => record.SavedDpi)
                .FirstOrDefault(value => value > 0);
            if (dpi == 0)
            {
                dpi = snapshot.Entries
                    .Where(entry => string.Equals(
                        entry.MonitorId,
                        monitor.MonitorId,
                        StringComparison.OrdinalIgnoreCase))
                    .Select(entry => entry.Position?.SavedDpi ?? 0)
                    .FirstOrDefault(value => value > 0);
            }
            if (dpi == 0) dpi = 96;

            int width = monitor.WidthPixels > 0 ? monitor.WidthPixels : 1920;
            int height = monitor.HeightPixels > 0 ? monitor.HeightPixels : 1080;
            int left = monitor.HasValidBounds ? monitor.BoundsLeft : nextLeft;
            int top = monitor.HasValidBounds ? monitor.BoundsTop : 0;
            int right = monitor.HasValidBounds ? monitor.BoundsRight : left + width;
            int bottom = monitor.HasValidBounds ? monitor.BoundsBottom : top + height;
            int workLeft = monitor.HasValidWorkArea ? monitor.WorkAreaLeft : left;
            int workTop = monitor.HasValidWorkArea ? monitor.WorkAreaTop : top;
            int workRight = monitor.HasValidWorkArea ? monitor.WorkAreaRight : right;
            int workBottom = monitor.HasValidWorkArea ? monitor.WorkAreaBottom : bottom;
            result.Add(new RestoreMonitor(
                monitor.MonitorId,
                monitor.Index,
                left,
                top,
                right,
                bottom,
                dpi,
                monitor.IsPrimary,
                workLeft,
                workTop,
                workRight,
                workBottom));
            nextLeft = Math.Max(nextLeft, right);
        }
        return new RestoreMonitorTopology
        {
            Monitors = result,
            IsExactMatch = MonitorTopologiesMatchExactly(snapshot.Monitors, currentMonitors)
        };
    }

    internal static bool MonitorTopologiesMatchExactly(
        IReadOnlyList<MonitorInfo> saved,
        IReadOnlyList<MonitorInfo> current)
    {
        if (saved.Count == 0 || saved.Count != current.Count)
            return false;

        MonitorInfo[] savedOrdered = saved.OrderBy(monitor => monitor.Index).ToArray();
        MonitorInfo[] currentOrdered = current.OrderBy(monitor => monitor.Index).ToArray();
        for (int index = 0; index < savedOrdered.Length; index++)
        {
            MonitorInfo source = savedOrdered[index];
            MonitorInfo target = currentOrdered[index];
            if (!source.HasValidBounds || !source.HasValidWorkArea ||
                !target.HasValidBounds || !target.HasValidWorkArea ||
                !string.Equals(source.MonitorId, target.MonitorId, StringComparison.OrdinalIgnoreCase) ||
                source.BoundsLeft != target.BoundsLeft ||
                source.BoundsTop != target.BoundsTop ||
                source.BoundsRight != target.BoundsRight ||
                source.BoundsBottom != target.BoundsBottom ||
                source.WorkAreaLeft != target.WorkAreaLeft ||
                source.WorkAreaTop != target.WorkAreaTop ||
                source.WorkAreaRight != target.WorkAreaRight ||
                source.WorkAreaBottom != target.WorkAreaBottom ||
                (source.Dpi > 0 ? source.Dpi : 96) != (target.Dpi > 0 ? target.Dpi : 96))
            {
                return false;
            }
        }
        return true;
    }

    private static void ApplyExecutionState(
        WorkspaceSnapshot snapshot,
        RestoreExecutionResult result)
    {
        foreach (WorkspaceEntry entry in snapshot.Entries)
            entry.WasRestored = false;
        foreach (RestoreExecutionEntryResult entry in result.Entries)
        {
            if (entry.EntryIndex >= 0 && entry.EntryIndex < snapshot.Entries.Count)
                snapshot.Entries[entry.EntryIndex].WasRestored =
                    entry.Status == RestoreExecutionEntryStatus.Restored;
        }
    }

    private static RestoreSessionResult ToLegacyResult(
        RestorePlan plan,
        RestoreExecutionResult execution)
    {
        RestoreEntryResult[] entries = execution.Entries
            .Select(entry =>
            {
                RestorePlanEntry planned = plan.Entries[entry.EntryIndex];
                bool titleMatched = planned.SelectedMatch?.Evidence.Any(evidence =>
                    evidence.Matched && evidence.Kind is
                        WindowMatchEvidenceKind.PwaIdentityExact or
                        WindowMatchEvidenceKind.DedicatedBrowserSiteExact or
                        WindowMatchEvidenceKind.DocumentNameInTitle) == true;
                RestoreEntryStatus status = entry.Status switch
                {
                    RestoreExecutionEntryStatus.Restored => RestoreEntryStatus.Assigned,
                    RestoreExecutionEntryStatus.LaunchRequested or
                        RestoreExecutionEntryStatus.AwaitingWindow => RestoreEntryStatus.LaunchRequested,
                    RestoreExecutionEntryStatus.Failed => RestoreEntryStatus.LaunchFailed,
                    _ => RestoreEntryStatus.Pending
                };
                return new RestoreEntryResult(
                    entry.EntryIndex,
                    entry.EntryId,
                    status,
                    entry.AssignedWindowHandle is long hwnd ? new IntPtr(hwnd) : null,
                    titleMatched);
            })
            .ToArray();
        var actions = new List<RestoreSessionAction>();
        foreach (RestoreExecutionActionResult action in execution.Actions)
        {
            bool succeeded = action.Status == RestoreExecutionActionStatus.Succeeded;
            IntPtr? hwnd = action.WindowHandle is long handle ? new IntPtr(handle) : null;
            switch (action.Kind)
            {
                case RestoreActionKind.RestoreBrowserSession:
                    actions.Add(new RestoreSessionAction(
                        RestoreSessionActionKind.BrowserRestore,
                        null,
                        null,
                        succeeded,
                        action.Explanation));
                    break;
                case RestoreActionKind.LaunchApplication:
                case RestoreActionKind.LaunchWebApp:
                case RestoreActionKind.ActivatePackagedApplication:
                    actions.Add(new RestoreSessionAction(
                        RestoreSessionActionKind.ProcessLaunch,
                        action.EntryIndex,
                        null,
                        succeeded,
                        action.Explanation));
                    break;
                case RestoreActionKind.OpenResource:
                case RestoreActionKind.LaunchDedicatedBrowser:
                    actions.Add(new RestoreSessionAction(
                        RestoreSessionActionKind.ResourceOpen,
                        action.EntryIndex,
                        null,
                        succeeded,
                        action.Explanation));
                    break;
                case RestoreActionKind.RestoreExistingWindow:
                case RestoreActionKind.AwaitWindowAppearance when succeeded:
                    actions.Add(new RestoreSessionAction(
                        RestoreSessionActionKind.WindowAssigned,
                        action.EntryIndex,
                        hwnd,
                        succeeded,
                        action.Explanation));
                    actions.Add(new RestoreSessionAction(
                        RestoreSessionActionKind.WindowRestored,
                        action.EntryIndex,
                        hwnd,
                        succeeded,
                        action.Explanation));
                    break;
                case RestoreActionKind.MinimizeOtherWindows:
                    actions.Add(new RestoreSessionAction(
                        RestoreSessionActionKind.MinimizeOtherWindows,
                        null,
                        null,
                        succeeded,
                        action.Explanation));
                    break;
            }
        }
        return new RestoreSessionResult(
            DateTimeOffset.UtcNow,
            TimeSpan.Zero,
            execution.WasCancelled,
            entries,
            actions,
            execution.AssignedWindowHandles.Select(hwnd => new IntPtr(hwnd)).ToHashSet());
    }


    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Searches common user-accessible locations for a file with the given <paramref name="filename"/>.
    /// Returns the full path when it is found at <em>exactly one</em> location, or <c>null</c>
    /// when zero or multiple matches are found (multiple matches are ambiguous — don't guess).
    /// Searched roots: Documents, Desktop, Downloads, OneDrive (if present).
    /// </summary>
    private static string? SearchFileInCommonLocations(
        string filename,
        CommonFolderSearchBudget budget,
        out bool timedOut)
    {
        timedOut = false;
        var searchRoots = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        };

        // Include all OneDrive variants: personal (%OneDrive%), consumer (%OneDriveConsumer%),
        // and business/commercial (%OneDriveCommercial%). Any of these may be set depending on
        // whether the user has a personal, work, or both OneDrive accounts configured.
        foreach (var envVar in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            string value = Environment.GetEnvironmentVariable(envVar) ?? "";
            if (!string.IsNullOrEmpty(value) && Directory.Exists(value))
                searchRoots.Add(value);
        }

        var matches = new List<string>();

        budget.StartMeasuring();
        try
        {
            foreach (var root in searchRoots.Where(Directory.Exists)
                                            .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                SearchDirectoryRecursive(root, filename, matches, budget, ref timedOut);
                if (matches.Count > 1 || timedOut) return null;   // ambiguous or out of budget
            }
        }
        finally
        {
            budget.StopMeasuring();
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>
    /// Recursive file search that isolates failures at the individual directory level.
    /// <para>
    /// <c>Directory.EnumerateFiles(..., SearchOption.AllDirectories)</c> throws as soon as it
    /// encounters a cloud-only OneDrive placeholder directory, abandoning the rest of the tree.
    /// This helper catches the exception per directory so that sibling folders continue to be
    /// searched even when one subtree is online-only or access-denied.
    /// </para>
    /// </summary>
    private static void SearchDirectoryRecursive(
        string directory,
        string filename,
        List<string> matches,
        CommonFolderSearchBudget budget,
        ref bool timedOut)
    {
        if (!budget.CanContinue)
        {
            timedOut = true;
            return;
        }

        // Enumerate files in this exact directory (no recursion flag — errors are per-folder)
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, filename))
            {
                if (!matches.Contains(file, StringComparer.OrdinalIgnoreCase))
                    matches.Add(file);
                if (matches.Count > 1) return;  // already ambiguous — stop early
            }
        }
        catch { /* online-only placeholder, access-denied, etc. — skip files in this dir */ }

        if (matches.Count > 1) return;

        // Enumerate subdirectories; each gets its own try/catch when recursed into
        IEnumerable<string> subDirs;
        try { subDirs = Directory.EnumerateDirectories(directory).ToList(); }
        catch { return; }  // can't list subdirs of this folder — just stop here

        foreach (var sub in subDirs)
        {
            SearchDirectoryRecursive(sub, filename, matches, budget, ref timedOut);
            if (matches.Count > 1 || timedOut) return;
        }
    }

    private static bool IsPlausibleBareFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
            return false;
        if (!string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal))
            return false;
        if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;

        string extension = Path.GetExtension(value);
        return extension.Length is >= 2 and <= 20 &&
               extension.Skip(1).All(character =>
                   char.IsLetterOrDigit(character) || character is '-' or '_');
    }

    /// <summary>
    /// One cumulative budget shared by every Tier-3 search in a capture. The stopwatch runs only
    /// while traversing folders, so jump-list and window enumeration time do not consume it.
    /// </summary>
    private sealed class CommonFolderSearchBudget
    {
        private readonly bool _enabled;
        private readonly CancellationToken _cancellationToken;
        private readonly Stopwatch _stopwatch = new();

        internal CommonFolderSearchBudget(
            bool enabled,
            TimeSpan limit,
            CancellationToken cancellationToken)
        {
            _enabled = enabled;
            Limit = limit > TimeSpan.Zero ? limit : TimeSpan.Zero;
            _cancellationToken = cancellationToken;
        }

        internal TimeSpan Limit { get; }

        internal bool CanSearch => CanContinue;

        internal bool CanContinue
        {
            get
            {
                _cancellationToken.ThrowIfCancellationRequested();
                return _enabled && _stopwatch.Elapsed < Limit;
            }
        }

        internal void StartMeasuring()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_enabled && !_stopwatch.IsRunning)
                _stopwatch.Start();
        }

        internal void StopMeasuring()
        {
            if (_stopwatch.IsRunning)
                _stopwatch.Stop();
        }
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        private readonly Action<T> _callback = callback ?? throw new ArgumentNullException(nameof(callback));

        public void Report(T value) => _callback(value);
    }

    /// <summary>
    /// Builds a <see cref="ProcessStartInfo"/> for launching an app to restore a workspace entry.
    /// <c>UseShellExecute = true</c> is mandatory so file associations are honoured.
    /// VS Code is special-cased to use <c>code.exe &lt;folder&gt;</c>.
    /// Browsers are special-cased to use <c>--restore-last-session</c> (or equivalent).
    /// </summary>
    public ProcessStartInfo BuildProcessStartInfo(WorkspaceEntry entry)
    {
        // ── Installed browser web app (PWA) ───────────────────────────────
        // Launching the shortcut reproduces exactly what happens when the user starts the app
        // from the Start Menu, including profile directory and app id.
        if (entry.IsWebApp)
        {
            string? shortcut = entry.WebAppShortcutPath;

            // Shortcut may have been moved or the app reinstalled — re-resolve by AUMID.
            if ((string.IsNullOrEmpty(shortcut) || !File.Exists(shortcut)) &&
                !string.IsNullOrEmpty(entry.AppUserModelId))
            {
                shortcut = _webAppService.FindByAumid(entry.AppUserModelId)?.ShortcutPath;
            }

            if (!string.IsNullOrEmpty(shortcut) && File.Exists(shortcut))
            {
                return new ProcessStartInfo
                {
                    FileName        = shortcut,
                    UseShellExecute = true,
                };
            }

            string target = entry.WebAppLaunchTarget ?? entry.ExecutablePath;
            if (!string.IsNullOrEmpty(target))
            {
                AppLogger.Info(
                    "restore.launch_command_built",
                    "Built a web-app launch command",
                    LogField.Identifier("entryId", entry.EntryId),
                    LogField.Title("webAppName", entry.WebAppName),
                    LogField.CommandLine("commandLine", $"{target} {entry.WebAppLaunchArguments}"),
                    LogField.Public("launchKind", "web_app"));
                return new ProcessStartInfo
                {
                    FileName        = target,
                    Arguments       = entry.WebAppLaunchArguments ?? "",
                    UseShellExecute = false,
                };
            }
        }

        // ── Dedicated browser window ──────────────────────────────────────
        // --new-window forces a separate window instead of a tab in an existing one, which is
        // what makes this coexist with the user's regular multi-tab browser window.
        if (entry.IsDedicatedBrowserWindow && !string.IsNullOrEmpty(entry.BrowserUrl))
        {
            AppLogger.Info(
                "restore.launch_command_built",
                "Built a dedicated-browser launch command",
                LogField.Identifier("entryId", entry.EntryId),
                LogField.Public("processName", entry.ProcessName),
                LogField.Url("url", entry.BrowserUrl),
                LogField.Public("launchKind", "dedicated_browser"));
            return new ProcessStartInfo
            {
                FileName        = entry.ExecutablePath,
                Arguments       = $"--new-window \"{entry.BrowserUrl}\"",
                UseShellExecute = false,
            };
        }

        // ── Store / MSIX app (TradingView, Notepad, Store-installed apps) ─
        // Their executables live under C:\Program Files\WindowsApps and must NOT be started
        // by path: the process then runs without package identity, so the app cannot reach its
        // packaged AppData container and comes back with default settings (e.g. light theme
        // instead of dark). Launching through shell:AppsFolder\<AUMID> activates the package
        // properly, exactly like clicking the Start-Menu tile.
        // Entries that open a document keep the file association path below.
        if (string.IsNullOrEmpty(entry.LaunchArg) && IsStoreApp(entry))
        {
            AppLogger.Info(
                "restore.launch_command_built",
                "Built a packaged-application launch command",
                LogField.Identifier("entryId", entry.EntryId),
                LogField.Public("processName", entry.ProcessName),
                LogField.Identifier("appUserModelId", entry.AppUserModelId),
                LogField.Public("launchKind", "packaged_application"));
            return new ProcessStartInfo
            {
                FileName        = "explorer.exe",
                Arguments       = $"shell:AppsFolder\\{entry.AppUserModelId}",
                UseShellExecute = true,
            };
        }

        // VS Code special case: open as folder via CLI
        if (entry.ProcessName.Equals("Code", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(entry.LaunchArg))
        {
            return new ProcessStartInfo
            {
                FileName        = entry.ExecutablePath,
                Arguments       = $"\"{entry.LaunchArg}\"",
                UseShellExecute = false,
            };
        }

        // All other apps: shell-execute with optional file argument
        if (!string.IsNullOrEmpty(entry.LaunchArg))
        {
            return new ProcessStartInfo
            {
                FileName        = entry.LaunchArg,  // shell-execute on file → opens in registered handler
                UseShellExecute = true,
            };
        }

        // ── Browser restore-session support ───────────────────────────────
        // When launching a browser that has no specific LaunchArg (no URL), pass
        // the restore-session flag so the browser reopens whatever tabs the user
        // had open when the workspace was saved.
        if (IsBrowserProcess(entry.ProcessName))
        {
            string flag = GetBrowserRestoreFlag(entry.ProcessName);
            AppLogger.Info(
                "restore.launch_command_built",
                "Built a browser session launch command",
                LogField.Identifier("entryId", entry.EntryId),
                LogField.Public("processName", entry.ProcessName),
                LogField.CommandLine("arguments", flag),
                LogField.Public("launchKind", "browser_session"));
            return new ProcessStartInfo
            {
                FileName        = entry.ExecutablePath,
                Arguments       = flag,
                UseShellExecute = false,
            };
        }

        return new ProcessStartInfo
        {
            FileName        = entry.ExecutablePath,
            UseShellExecute = true,
        };
    }

    // ── Browser detection helpers ─────────────────────────────────────────

    /// <summary>
    /// True when the entry belongs to a packaged (MSIX/Store) app that must be started through
    /// its AppUserModelID rather than by executable path. Requires both a packaged install
    /// location and an explicit AUMID (packaged AUMIDs have the form
    /// <c>PackageFamilyName!AppId</c>).
    /// </summary>
    private static bool IsStoreApp(WorkspaceEntry entry) =>
        !string.IsNullOrEmpty(entry.AppUserModelId) &&
        entry.AppUserModelId.Contains('!') &&
        entry.ExecutablePath.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> BrowserProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "opera", "brave",
    };

    /// <summary>Returns <c>true</c> if the process name is a known browser.</summary>
    public static bool IsBrowserProcess(string processName) =>
        BrowserProcessNames.Contains(processName);

    /// <summary>
    /// Returns the command-line flag that tells the browser to restore its
    /// previous session. Chromium-based browsers all use the same flag.
    /// </summary>
    private static string GetBrowserRestoreFlag(string processName)
    {
        // Chrome, Edge, Opera, Brave (all Chromium-based)
        return "--restore-last-session";
    }
}

