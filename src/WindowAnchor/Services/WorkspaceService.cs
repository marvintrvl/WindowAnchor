using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Progress update emitted while a workspace snapshot is assembled.</summary>
/// <param name="Current">1-based index of the window currently being processed (0 = pre-loop setup).</param>
/// <param name="Total">Total number of windows to process.</param>
/// <param name="AppName">Process name of the window being processed (or a stage description).</param>
/// <param name="Detail">Window title snippet or a short stage description.</param>
public record struct SaveProgressReport(int Current, int Total, string AppName, string Detail);

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
    private readonly StorageService    _storageService;
    private readonly IWindowInventory  _windowInventory;
    private readonly IWindowMutation   _windowMutation;
    private readonly IMonitorInventory _monitorInventory;
    private readonly JumpListService   _jumpListService;
    private readonly WebAppService     _webAppService;
    private readonly IBrowserSessionConnector? _browserSessionConnector;

    /// <summary>Creates the production workspace service using the native window service.</summary>
    public WorkspaceService(
        StorageService  storageService,
        WindowService   windowService,
        MonitorService  monitorService,
        JumpListService jumpListService,
        WebAppService?   webAppService = null,
        IBrowserSessionConnector? browserSessionConnector = null)
        : this(
            storageService,
            windowService,
            windowService,
            monitorService,
            jumpListService,
            webAppService,
            browserSessionConnector)
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
        IBrowserSessionConnector? browserSessionConnector = null)
    {
        _storageService   = storageService;
        _windowInventory  = windowInventory;
        _windowMutation   = windowMutation;
        _monitorInventory = monitorInventory;
        _jumpListService  = jumpListService;
        _webAppService    = webAppService ?? new WebAppService();
        _browserSessionConnector = browserSessionConnector;
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
        CancellationToken cancellationToken = default)
    {
        WorkspaceSnapshot snapshot = await Task.Run(
            () => TakeSnapshot(name, saveFiles, monitorIds, progress, selectedWindows),
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
        List<WindowRecord>? selectedWindows = null)
    {
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
            if (saveFiles)
            {
                progress?.Report(new SaveProgressReport(0, windows.Count, "Building file detection cache\u2026", ""));
                _jumpListService.BuildSnapshotCache();
            }

            int selProgressIdx = 0;
            try
            {
                foreach (var w in windows)
                {
                    progress?.Report(new SaveProgressReport(++selProgressIdx, windows.Count, w.ProcessName, w.TitleSnippet));
                    selEntries.Add(BuildEntryForWindow(w, saveFiles));
                }
            }
            finally
            {
                if (saveFiles) _jumpListService.ClearSnapshotCache();
            }

            progress?.Report(new SaveProgressReport(windows.Count, windows.Count, "Assembling workspace snapshot\u2026", ""));

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
                LogField.Public("captureMode", "selective"));
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
        if (saveFiles)
        {
            progress?.Report(new SaveProgressReport(0, windows.Count, "Building file detection cache\u2026", ""));
            _jumpListService.BuildSnapshotCache();
        }

        int progressIdx = 0;
        try
        {
        foreach (var w in windows)
        {
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
                if (confidence == 40 && !string.IsNullOrEmpty(filePath) && !Path.IsPathRooted(filePath))
                {
                    try
                    {
                        var jlPool = _jumpListService.GetRecentFilesForApp(w.ExecutablePath, maxFiles: 50);
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
                if (confidence < 80 && !string.IsNullOrEmpty(w.ExecutablePath))
                {
                    try
                    {
                        var jlFiles = _jumpListService.GetRecentFilesForApp(w.ExecutablePath, maxFiles: 30);
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
                if (confidence < 80 && !string.IsNullOrEmpty(filePath) && !Path.IsPathRooted(filePath))
                {
                    AppLogger.Debug(
                        "file_detection.folder_search_started",
                        "Searching common folders for a filename",
                        LogField.Path("fileName", filePath));
                    try
                    {
                        string? found = SearchFileInCommonLocations(filePath);
                        if (found != null)
                        {
                            filePath = found; confidence = 85; source = "FILE_SEARCH";
                            AppLogger.Debug(
                                "file_detection.folder_search_match",
                                "Found a matching file in a common folder",
                                LogField.Path("filePath", found));
                        }
                        else
                        {
                            AppLogger.Debug(
                                "file_detection.folder_search_no_match",
                                "Common-folder search found zero or ambiguous matches");
                        }
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
                AppLogger.Debug(
                    "file_detection.completed",
                    "Completed file detection for a window",
                    LogField.Public("source", source),
                    LogField.Public("confidence", confidence),
                    LogField.Path("launchArgument", launchArg));

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
            if (saveFiles)
                _jumpListService.ClearSnapshotCache();
        }

        progress?.Report(new SaveProgressReport(windows.Count, windows.Count, "Assembling workspace snapshot\u2026", ""));

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
            LogField.Public("captureMode", "all_windows"));
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

    /// <summary>
    /// Builds a <see cref="WorkspaceEntry"/> for a single window, running file-detection
    /// tiers when <paramref name="saveFiles"/> is <c>true</c>.  Assumes the jump-list
    /// snapshot cache is already populated.
    /// </summary>
    private WorkspaceEntry BuildEntryForWindow(WindowRecord w, bool saveFiles)
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
            if (confidence == 40 && !string.IsNullOrEmpty(filePath) && !Path.IsPathRooted(filePath))
            {
                try
                {
                    var jlPool = _jumpListService.GetRecentFilesForApp(w.ExecutablePath, maxFiles: 50);
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
            if (confidence < 80 && !string.IsNullOrEmpty(w.ExecutablePath))
            {
                try
                {
                    var jlFiles = _jumpListService.GetRecentFilesForApp(w.ExecutablePath, maxFiles: 30);
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
            if (confidence < 80 && !string.IsNullOrEmpty(filePath) && !Path.IsPathRooted(filePath))
            {
                try
                {
                    string? found = SearchFileInCommonLocations(filePath);
                    if (found != null)
                    {
                        filePath = found; confidence = 85; source = "FILE_SEARCH";
                    }
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

            AppLogger.Debug(
                "file_detection.completed",
                "Completed file detection for a window",
                LogField.Public("source", source),
                LogField.Public("confidence", confidence),
                LogField.Path("launchArgument", launchArg));
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

        var filtered = new WorkspaceSnapshot
        {
            SchemaVersion      = snapshot.SchemaVersion,
            WorkspaceId       = snapshot.WorkspaceId,
            Name               = snapshot.Name,
            SavedAt            = snapshot.SavedAt,
            MonitorFingerprint = snapshot.MonitorFingerprint,
            SavedWithFiles     = snapshot.SavedWithFiles,
            Monitors           = snapshot.Monitors.Where(m => monitorIds.Contains(m.MonitorId)).ToList(),
            Entries            = snapshot.Entries.Where(e => monitorIds.Contains(e.MonitorId)).ToList(),
            BrowserSessions    = snapshot.BrowserSessions,
        };
        return RestoreWorkspaceAsync(filtered, ct);
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
        _ = await RestoreCoreAsync(snapshot, minimizeOthers: false, ct);
    }

    /// <summary>
    /// Same as <see cref="RestoreWorkspaceAsync"/>, but after repositioning the workspace's own
    /// windows it minimizes every other open window (nothing is closed). Use this to bring a
    /// workspace to the foreground and clear away unrelated windows without the destructive
    /// close-everything behaviour of <c>SwitchWorkspaceAsync</c>.
    /// </summary>
    public async Task RestoreWorkspaceAlignAndMinimizeAsync(WorkspaceSnapshot snapshot, CancellationToken ct = default)
    {
        _ = await RestoreCoreAsync(snapshot, minimizeOthers: true, ct);
    }

    /// <summary>
    /// Restores a workspace and returns the structured state accumulated by that restore session.
    /// Kept internal until a UI or automation contract needs to expose the detailed result.
    /// </summary>
    internal Task<RestoreSessionResult> RestoreWorkspaceWithResultAsync(
        WorkspaceSnapshot snapshot,
        bool minimizeOthers = false,
        CancellationToken ct = default) => RestoreCoreAsync(snapshot, minimizeOthers, ct);

    private async Task<RestoreSessionResult> RestoreCoreAsync(
        WorkspaceSnapshot snapshot,
        bool minimizeOthers,
        CancellationToken ct)
    {
        AppLogger.Info(
            "restore.session_started",
            "Started a workspace restore session",
            LogField.Identifier("workspaceId", snapshot.WorkspaceId),
            LogField.Workspace("workspaceName", snapshot.Name),
            LogField.Public("entryCount", snapshot.Entries.Count),
            LogField.Public("minimizeOthers", minimizeOthers));
        var session = new RestoreSessionContext(snapshot, ct);

        bool browserSessionsRestored = false;
        if (_browserSessionConnector != null && snapshot.BrowserSessions.Count > 0)
        {
            browserSessionsRestored = await _browserSessionConnector.RestoreAsync(snapshot.Name, snapshot.BrowserSessions, ct);
            session.RecordBrowserRestore(browserSessionsRestored);
        }

        // ── Phase 1: reposition already-running windows ───────────────────
        // The session owns entry and HWND assignment across all phases. A match proposed in a
        // later pass cannot claim a still-live HWND committed during an earlier pass.
        var liveWindows = _windowInventory.GetWindowsWithPids(
            WindowCandidatePolicy.RestoreMatchCandidate);
        MatchAndRestore(session, liveWindows);

        if (ct.IsCancellationRequested) return session.Complete();

        // ── Phase 2: open documents and launch missing apps ───────────────
        // Document entries (have a LaunchArg): open the file unless it was already matched
        // with the correct title in Phase 1. Shell-executing the file works whether the
        // app is already running (opens in the existing instance via DDE/COM) or not.
        //
        // Plain app entries (no LaunchArg): only launch when the exe is not already running
        // AND when no document entry for the same exe is pending in this pass.
        // If we launch the bare exe first (e.g. WINWORD.EXE with no file) and a document
        // entry for the same exe follows, Windows DDE will route the document into the
        // already-running bare instance instead of spawning a new window. That consumes
        // the bare instance's slot while leaving zero windows for Praktikumsbericht/etc.
        // Skipping the bare launch lets the document entry start the exe properly.
        bool anyLaunched = false;
        // Web-app windows must not count as "the browser is already running": a Chrome window
        // that is really the Insilico Terminal PWA should not suppress launching Chrome itself.
        // Dedicated single-site windows are excluded for the same reason: a Brave window that is
        // really the trading site must not suppress launching the user's normal Brave window.
        var runningExes = liveWindows.Values
            .Where(v => !IsWebAppWindow(v.Record))
            .Where(v => string.IsNullOrEmpty(v.Record.BrowserUrl))
            .Select(v => v.Record.ExecutablePath.ToLowerInvariant())
            .ToHashSet();

        // Pre-scan: collect exe paths that will be started by a document entry this pass.
        var exesWithPendingDocLaunch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in session.Entries)
        {
            if (!string.IsNullOrEmpty(e.LaunchArg) && !string.IsNullOrEmpty(e.ExecutablePath))
                exesWithPendingDocLaunch.Add(e.ExecutablePath.ToLowerInvariant());
        }

        for (int i = 0; i < session.Entries.Count; i++)
        {
            if (ct.IsCancellationRequested) return session.Complete();

            var entry = session.Entries[i];

            if (browserSessionsRestored && IsBrowserProcess(entry.ProcessName))
                continue;

            // ── Installed browser web app (PWA) ───────────────────────────
            // Always relaunch via its own shortcut / --app-id command line unless Phase 1
            // already matched a live window with the same AppUserModelID. Never fall through
            // to the generic browser launch, which would just open a normal browser window.
            if (entry.IsWebApp)
            {
                if (session.IsEntryAssigned(i)) continue;

                try
                {
                    Process.Start(BuildProcessStartInfo(entry));
                    anyLaunched = true;
                    session.RecordLaunch(
                        i,
                        RestoreSessionActionKind.ProcessLaunch,
                        succeeded: true,
                        "Launched installed browser web app");
                    AppLogger.Info(
                        "restore.entry_launch_succeeded",
                        "Launched an installed browser web app",
                        LogField.Identifier("entryId", entry.EntryId),
                        LogField.Title("webAppName", entry.WebAppName),
                        LogField.Identifier("appUserModelId", entry.AppUserModelId),
                        LogField.Public("launchKind", "web_app"));
                }
                catch (Exception ex)
                {
                    session.RecordLaunch(
                        i,
                        RestoreSessionActionKind.ProcessLaunch,
                        succeeded: false,
                        "Failed to launch installed browser web app");
                    AppLogger.Warn(
                        "restore.entry_launch_failed",
                        "Failed to launch an installed browser web app",
                        ex,
                        LogField.Identifier("entryId", entry.EntryId),
                        LogField.Title("webAppName", entry.WebAppName),
                        LogField.Identifier("appUserModelId", entry.AppUserModelId),
                        LogField.Public("errorCategory", "web_app_launch"));
                }
                continue;
            }

            // ── Dedicated browser window ──────────────────────────────────
            // Open the site in its own window. Unlike --restore-last-session this targets one
            // specific window, so it can coexist with the user's normal multi-tab window.
            if (entry.IsDedicatedBrowserWindow)
            {
                if (session.IsEntryAssigned(i)) continue;

                try
                {
                    Process.Start(BuildProcessStartInfo(entry));
                    anyLaunched = true;
                    session.RecordLaunch(
                        i,
                        RestoreSessionActionKind.ResourceOpen,
                        succeeded: true,
                        "Opened dedicated browser resource");
                    AppLogger.Info(
                        "restore.entry_launch_succeeded",
                        "Opened a dedicated browser window",
                        LogField.Identifier("entryId", entry.EntryId),
                        LogField.Url("url", entry.BrowserUrl),
                        LogField.Public("launchKind", "dedicated_browser"));
                }
                catch (Exception ex)
                {
                    session.RecordLaunch(
                        i,
                        RestoreSessionActionKind.ResourceOpen,
                        succeeded: false,
                        "Failed to open dedicated browser resource");
                    AppLogger.Warn(
                        "restore.entry_launch_failed",
                        "Failed to open a dedicated browser window",
                        ex,
                        LogField.Identifier("entryId", entry.EntryId),
                        LogField.Url("url", entry.BrowserUrl),
                        LogField.Public("errorCategory", "dedicated_browser_launch"));
                }
                continue;
            }

            // A plain application already assigned by the identity engine is present even when
            // an app update changed its executable path (packaged AUMID evidence survives that
            // change). Document entries still continue so a wrong open document can be replaced.
            if (session.IsEntryAssigned(i) && string.IsNullOrEmpty(entry.LaunchArg))
                continue;

            if (string.IsNullOrEmpty(entry.ExecutablePath)) continue;

            if (!string.IsNullOrEmpty(entry.LaunchArg))
            {
                // Document entry: skip only when the right document is already open.
                if (session.CorrectlyMatchedEntries.Contains(i)) continue;

                try
                {
                    // Shell-executing the file opens it in the registered handler.
                    // If the app is already running, it uses DDE/COM to open in the
                    // existing instance instead of spawning a second process.
                    var psi = BuildProcessStartInfo(entry);
                    Process.Start(psi);
                    anyLaunched = true;
                    session.RecordLaunch(
                        i,
                        RestoreSessionActionKind.ResourceOpen,
                        succeeded: true,
                        "Opened saved resource");
                    AppLogger.Info(
                        "restore.entry_launch_succeeded",
                        "Opened a saved resource",
                        LogField.Identifier("entryId", entry.EntryId),
                        LogField.Path("resourcePath", entry.LaunchArg),
                        LogField.Public("launchKind", "resource"));
                }
                catch (Exception ex)
                {
                    session.RecordLaunch(
                        i,
                        RestoreSessionActionKind.ResourceOpen,
                        succeeded: false,
                        "Failed to open saved resource");
                    AppLogger.Warn(
                        "restore.entry_launch_failed",
                        "Failed to open a saved resource",
                        ex,
                        LogField.Identifier("entryId", entry.EntryId),
                        LogField.Path("resourcePath", entry.LaunchArg),
                        LogField.Public("errorCategory", "resource_launch"));
                }
            }
            else
            {
                // Plain app entry: only launch when not already running.
                string exeLower = entry.ExecutablePath.ToLowerInvariant();
                if (runningExes.Contains(exeLower)) continue;

                // Skip if a document entry for the same exe is pending in this pass.
                // Shell-executing that document will start the app; a bare launch here
                // would open the start screen and steal the DDE slot.
                if (exesWithPendingDocLaunch.Contains(exeLower))
                {
                    AppLogger.Debug(
                        "restore.bare_launch_skipped",
                        "Skipped a bare application launch because a resource entry is pending",
                        LogField.Identifier("entryId", entry.EntryId),
                        LogField.Path("executablePath", entry.ExecutablePath));
                    continue;
                }

                try
                {
                    var psi = BuildProcessStartInfo(entry);
                    Process.Start(psi);
                    anyLaunched = true;
                    session.RecordLaunch(
                        i,
                        RestoreSessionActionKind.ProcessLaunch,
                        succeeded: true,
                        "Launched saved application");
                    // Log what actually launched: shell:AppsFolder for Store apps prints the
                    // FileName+Arguments, everything else the executable path.
                    string how = IsStoreApp(entry) ? $"{psi.FileName} {psi.Arguments}".Trim()
                                                   : entry.ExecutablePath;
                    AppLogger.Info(
                        "restore.entry_launch_succeeded",
                        "Launched a saved application",
                        LogField.Identifier("entryId", entry.EntryId),
                        LogField.CommandLine("launchCommand", how),
                        LogField.Public("launchKind", "application"));
                }
                catch (Exception ex)
                {
                    session.RecordLaunch(
                        i,
                        RestoreSessionActionKind.ProcessLaunch,
                        succeeded: false,
                        "Failed to launch saved application");
                    AppLogger.Warn(
                        "restore.entry_launch_failed",
                        "Failed to launch a saved application",
                        ex,
                        LogField.Identifier("entryId", entry.EntryId),
                        LogField.Path("executablePath", entry.ExecutablePath),
                        LogField.Public("errorCategory", "application_launch"));
                }
            }
        }

        if (!anyLaunched)
        {
            // Nothing new to launch — every workspace window was already open and repositioned in
            // Phase 1. Still honour the minimize request before returning, otherwise "align &
            // minimize others" would do nothing when the workspace is already fully open.
            if (minimizeOthers && !ct.IsCancellationRequested)
            {
                _windowMutation.MinimizeUserWindowsExcept(
                    WindowCandidatePolicy.MinimizeCandidate,
                    new HashSet<IntPtr>(session.AssignedHwnds));
                session.RecordMinimizeOthers(succeeded: true);
            }
            return session.Complete();
        }

        // ── Phase 3: wait for app initialisation ─────────────────────────
        await Task.Delay(3000, ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return session.Complete();

        // ── Phase 4: reposition newly appeared windows ────────────────────
        liveWindows = _windowInventory.GetWindowsWithPids(
            WindowCandidatePolicy.RestoreMatchCandidate);
        MatchAndRestore(session, liveWindows);

        if (ct.IsCancellationRequested) return session.Complete();

        // ── Phase 5: second pass for slow launchers ────────────────────────
        await Task.Delay(2000, ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return session.Complete();

        liveWindows = _windowInventory.GetWindowsWithPids(
            WindowCandidatePolicy.RestoreMatchCandidate);
        MatchAndRestore(session, liveWindows);

        // ── Optional: minimize everything that is not part of the workspace ──
        if (minimizeOthers && !ct.IsCancellationRequested)
        {
            _windowMutation.MinimizeUserWindowsExcept(
                WindowCandidatePolicy.MinimizeCandidate,
                new HashSet<IntPtr>(session.AssignedHwnds));
            session.RecordMinimizeOthers(succeeded: true);
        }

        AppLogger.Info(
            "restore.session_completed",
            "Completed a workspace restore session",
            LogField.Identifier("workspaceId", snapshot.WorkspaceId));
        return session.Complete();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Matches live windows to snapshot entries and calls
    /// <see cref="WindowService.RestoreSingleWindow"/> for each match.
    /// <para>
    /// Matching priority (highest → lowest):
    /// <list type="number">
    ///   <item>For document entries (have a <see cref="WorkspaceEntry.LaunchArg"/>):
    ///     exe path + live title contains the saved document filename.
    ///     A match here is recorded in the session so that Phase 2 knows the right document is
    ///     already on screen.</item>
    ///   <item>exe path + window class name.</item>
    ///   <item>exe path + first 10 chars of saved title snippet.</item>
    /// </list>
    /// </para>
    /// The planner only proposes matches. <see cref="RestoreSessionContext"/> centrally commits
    /// each proposal and owns the one-to-one assignment across Phase 1 / 4 / 5 calls.
    /// </summary>
    private void MatchAndRestore(
        RestoreSessionContext session,
        Dictionary<IntPtr, (uint Pid, WindowRecord Record)> liveWindows)
    {
        // An assignment is released only after this inventory proves its HWND disappeared.
        // The affected entry then becomes eligible for an explicit rematch in the same pass.
        session.ReconcileLiveWindows(liveWindows, _windowInventory.IsWindowAlive);

        foreach (var match in WindowRestorePlanner.PlanMatches(
                     session.Entries,
                     liveWindows,
                     session.AssignedEntryIndices,
                     session.AssignedHwnds))
        {
            int i = match.EntryIndex;
            var entry = session.Entries[i];

            // The session is authoritative. A proposal that races with or duplicates an earlier
            // assignment is rejected before any live window mutation occurs.
            if (!session.TryCommitAssignment(match))
                continue;

            if (match.TitleSimilarityScore is double score)
                AppLogger.Info(
                    "restore.entry_disambiguated",
                    "Disambiguated a restore entry by title similarity",
                    LogField.Identifier("entryId", entry.EntryId),
                    LogField.Public("processName", entry.ProcessName),
                    LogField.Public("score", score),
                    LogField.Public("hwnd", match.Hwnd));

            _windowMutation.RestoreSingleWindow(match.Hwnd, entry.Position);
            session.RecordWindowRestored(i, match.Hwnd);
            AppLogger.Info(
                "restore.entry_positioned",
                "Restored a workspace entry to its saved position",
                LogField.Identifier("entryId", entry.EntryId),
                LogField.Public("entryIndex", i),
                LogField.Public("processName", entry.ProcessName),
                LogField.Public("hwnd", match.Hwnd),
                LogField.Public("titleMatched", match.TitleMatched),
                LogField.Public("matchScore", match.Candidate?.Score),
                LogField.Public("matchConfidence", match.Candidate?.Confidence),
                LogField.Public(
                    "matchEvidence",
                    string.Join(',', match.Candidate?.Evidence
                        .Where(evidence => evidence.Matched)
                        .Select(evidence => evidence.Kind) ?? [])));
        }
    }

    /// <summary>
    /// Searches common user-accessible locations for a file with the given <paramref name="filename"/>.
    /// Returns the full path when it is found at <em>exactly one</em> location, or <c>null</c>
    /// when zero or multiple matches are found (multiple matches are ambiguous — don't guess).
    /// Searched roots: Documents, Desktop, Downloads, OneDrive (if present).
    /// </summary>
    private static string? SearchFileInCommonLocations(string filename)
    {
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

        foreach (var root in searchRoots.Where(Directory.Exists)
                                        .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            SearchDirectoryRecursive(root, filename, matches);
            if (matches.Count > 1) return null;   // ambiguous — don't guess
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
    private static void SearchDirectoryRecursive(string directory, string filename, List<string> matches)
    {
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
            SearchDirectoryRecursive(sub, filename, matches);
            if (matches.Count > 1) return;
        }
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

    /// <summary>
    /// True when a live window is an installed browser web app rather than a normal
    /// browser window, judged by the shape of its <c>AppUserModelID</c>.
    /// </summary>
    private static bool IsWebAppWindow(WindowRecord rec) =>
        WebAppService.IsChromiumBrowser(rec.ProcessName) &&
        WebAppService.LooksLikeWebAppAumid(rec.AppUserModelId);


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

