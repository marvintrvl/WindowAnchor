using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Progress update emitted by <see cref="WorkspaceService.TakeSnapshot"/> for each window processed.</summary>
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
    private readonly StorageService   _storageService;
    private readonly WindowService    _windowService;
    private readonly MonitorService   _monitorService;
    private readonly JumpListService  _jumpListService;

    public WorkspaceService(
        StorageService  storageService,
        WindowService   windowService,
        MonitorService  monitorService,
        JumpListService jumpListService)
    {
        _storageService  = storageService;
        _windowService   = windowService;
        _monitorService  = monitorService;
        _jumpListService = jumpListService;
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
    public string GetCurrentMonitorFingerprint() => _monitorService.GetCurrentMonitorFingerprint();

    /// <summary>
    /// Enumerates the current monitors and counts live windows per monitor.
    /// Used by the Save Workspace dialog to populate the monitor checkbox list.
    /// </summary>
    public List<(MonitorInfo Monitor, int WindowCount)> GetMonitorDataForDialog()
    {
        var monitors = _monitorService.GetCurrentMonitors();
        var windows  = _windowService.SnapshotAllWindows(monitors);
        return monitors
            .Select(m => (m, windows.Count(w => w.MonitorId == m.MonitorId)))
            .ToList();
    }

    // ── Snapshot ─────────────────────────────────────────────────────────────

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
    public WorkspaceSnapshot TakeSnapshot(
        string name,
        bool saveFiles = true,
        HashSet<string>? monitorIds = null,
        IProgress<SaveProgressReport>? progress = null)
    {
        string fingerprint = _monitorService.GetCurrentMonitorFingerprint();

        // Enumerate monitors first so every WindowRecord is tagged with monitor info
        var allMonitors = _monitorService.GetCurrentMonitors();

        // Determine which monitors to include (null = all)
        var monitorsToSave = monitorIds == null
            ? allMonitors
            : allMonitors.Where(m => monitorIds.Contains(m.MonitorId)).ToList();

        var windows = _windowService.SnapshotAllWindows(allMonitors);

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

            // ── Explorer special case ──────────────────────────────────────
            if (w.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(w.FolderPath))
            {
                entries.Add(new WorkspaceEntry
                {
                    ExecutablePath  = w.ExecutablePath,
                    ProcessName     = w.ProcessName,
                    WindowClassName = w.ClassName,
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
                AppLogger.Debug($"[FileDetect] ── {w.ProcessName} | title: \"{w.TitleSnippet}\" | exe: {w.ExecutablePath}");

                // ── Tier 1: parse file path from window title ──────────────
                var (titlePath, titleConf) = TitleParser.ExtractFilePath(w.ProcessName, w.TitleSnippet);
                filePath   = titlePath;
                confidence = titleConf;
                source     = titleConf > 0 ? "TITLE_PARSE" : "NONE";

                if (titleConf > 0)
                    AppLogger.Debug($"[FileDetect]   T1 TITLE_PARSE  conf={titleConf}  path=\"{titlePath}\"");
                else
                    AppLogger.Debug($"[FileDetect]   T1 no match (process not in TitleParser or title format mismatch)");

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
                        AppLogger.Debug($"[FileDetect]   T1.5 exact-filename search in pool of {jlPool.Count}");
                        string? exact = jlPool.FirstOrDefault(p =>
                            Path.GetFileName(p).Equals(filePath, StringComparison.OrdinalIgnoreCase));
                        if (exact != null)
                        {
                            filePath   = exact;
                            confidence = 90;
                            source     = "JUMPLIST_EXACT";
                            AppLogger.Debug($"[FileDetect]   T1.5 JUMPLIST_EXACT: \"{exact}\"");
                        }
                        else
                        {
                            AppLogger.Debug($"[FileDetect]   T1.5 no exact match found");
                        }
                    }
                    catch (Exception ex) { AppLogger.Warn($"[FileDetect]   T1.5 exception: {ex.Message}"); }
                }

                // ── Tier 2: jump-list lookup ───────────────────────────────
                if (confidence < 80 && !string.IsNullOrEmpty(w.ExecutablePath))
                {
                    try
                    {
                        var jlFiles = _jumpListService.GetRecentFilesForApp(w.ExecutablePath, maxFiles: 30);
                        AppLogger.Debug($"[FileDetect]   T2 jump-list returned {jlFiles.Count} candidate(s)");
                        foreach (var jf in jlFiles)
                            AppLogger.Debug($"[FileDetect]      JL candidate: \"{jf}\"");

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
                                AppLogger.Debug($"[FileDetect]   T2 JUMPLIST match: \"{jlBest}\"");
                            }
                            else
                            {
                                // Log why none matched: show what the title contains vs what candidates had
                                AppLogger.Debug($"[FileDetect]   T2 no JL candidate matched title \"{w.TitleSnippet}\"");
                                foreach (var jf in jlFiles)
                                    AppLogger.Debug($"[FileDetect]      no-match detail: stem=\"{Path.GetFileNameWithoutExtension(jf)}\"  titleContains={titleLower.Contains(Path.GetFileNameWithoutExtension(jf).ToLowerInvariant())}");
                            }
                        }
                        else
                        {
                            AppLogger.Debug($"[FileDetect]   T2 jump-list empty for this exe");
                        }
                    }
                    catch (Exception ex) { AppLogger.Warn($"[FileDetect]   T2 exception: {ex.Message}"); }
                }

                // ── Tier 3: search common user folders for bare filename ───
                if (confidence < 80 && !string.IsNullOrEmpty(filePath) && !Path.IsPathRooted(filePath))
                {
                    AppLogger.Debug($"[FileDetect]   T3 searching common folders for bare name \"{filePath}\"");
                    try
                    {
                        string? found = SearchFileInCommonLocations(filePath);
                        if (found != null)
                        {
                            filePath = found; confidence = 85; source = "FILE_SEARCH";
                            AppLogger.Debug($"[FileDetect]   T3 FILE_SEARCH found: \"{found}\"");
                        }
                        else
                        {
                            AppLogger.Debug($"[FileDetect]   T3 not found (zero or ambiguous matches)");
                        }
                    }
                    catch (Exception ex) { AppLogger.Warn($"[FileDetect]   T3 exception: {ex.Message}"); }
                }

                launchArg = confidence >= 80 ? filePath : null;
                AppLogger.Debug($"[FileDetect]   RESULT  source={source}  conf={confidence}  launchArg=\"{launchArg ?? "(null)"}\"");

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

        progress?.Report(new SaveProgressReport(windows.Count, windows.Count, "Saving workspace file\u2026", ""));

        var snapshot = new WorkspaceSnapshot
        {
            Name               = name,
            MonitorFingerprint = fingerprint,
            SavedAt            = DateTime.UtcNow,
            SavedWithFiles     = saveFiles,
            Monitors           = monitorsToSave,
            Entries            = entries,
        };

        _storageService.SaveWorkspace(snapshot);
        AppLogger.Info($"TakeSnapshot saved '{name}' — {entries.Count} entries across {monitorsToSave.Count} monitor(s), saveFiles={saveFiles}");
        return snapshot;
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
            Name               = snapshot.Name,
            SavedAt            = snapshot.SavedAt,
            MonitorFingerprint = snapshot.MonitorFingerprint,
            SavedWithFiles     = snapshot.SavedWithFiles,
            Monitors           = snapshot.Monitors.Where(m => monitorIds.Contains(m.MonitorId)).ToList(),
            Entries            = snapshot.Entries.Where(e => monitorIds.Contains(e.MonitorId)).ToList(),
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
        AppLogger.Info($"RestoreWorkspaceAsync '{snapshot.Name}' — {snapshot.Entries.Count} entries");

        // ── Phase 1: reposition already-running windows ───────────────────
        // correctlyMatchedEntries tracks entries whose live window already had the right
        // document open (title matched). Only those entries are skipped in Phase 2.
        var liveWindows = _windowService.GetAllWindowsWithPids();
        var restoredEntries = new HashSet<int>();
        var correctlyMatchedEntries = new HashSet<int>();

        MatchAndRestore(snapshot.Entries, liveWindows, restoredEntries, correctlyMatchedEntries);

        if (ct.IsCancellationRequested) return;

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
        var runningExes = liveWindows.Values
            .Select(v => v.Record.ExecutablePath.ToLowerInvariant())
            .ToHashSet();

        // Pre-scan: collect exe paths that will be started by a document entry this pass.
        var exesWithPendingDocLaunch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in snapshot.Entries)
        {
            if (!string.IsNullOrEmpty(e.LaunchArg) && !string.IsNullOrEmpty(e.ExecutablePath))
                exesWithPendingDocLaunch.Add(e.ExecutablePath.ToLowerInvariant());
        }

        for (int i = 0; i < snapshot.Entries.Count; i++)
        {
            if (ct.IsCancellationRequested) return;

            var entry = snapshot.Entries[i];
            if (string.IsNullOrEmpty(entry.ExecutablePath)) continue;

            if (!string.IsNullOrEmpty(entry.LaunchArg))
            {
                // Document entry: skip only when the right document is already open.
                if (correctlyMatchedEntries.Contains(i)) continue;

                try
                {
                    // Shell-executing the file opens it in the registered handler.
                    // If the app is already running, it uses DDE/COM to open in the
                    // existing instance instead of spawning a second process.
                    var psi = BuildProcessStartInfo(entry);
                    Process.Start(psi);
                    anyLaunched = true;
                    AppLogger.Info($"Opened document: {entry.LaunchArg}");
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"Failed to open document '{entry.LaunchArg}': {ex.Message}");
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
                    AppLogger.Debug($"Phase2: skipping bare launch of {Path.GetFileName(entry.ExecutablePath)} — document entry pending for same exe");
                    continue;
                }

                try
                {
                    var psi = BuildProcessStartInfo(entry);
                    Process.Start(psi);
                    anyLaunched = true;
                    AppLogger.Info($"Launched: {entry.ExecutablePath}");
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"Failed to launch '{entry.ExecutablePath}': {ex.Message}");
                }
            }
        }

        if (!anyLaunched) return;

        // ── Phase 3: wait for app initialisation ─────────────────────────
        await Task.Delay(3000, ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return;

        // ── Phase 4: reposition newly appeared windows ────────────────────
        liveWindows = _windowService.GetAllWindowsWithPids();
        MatchAndRestore(snapshot.Entries, liveWindows, restoredEntries, correctlyMatchedEntries);

        if (ct.IsCancellationRequested) return;

        // ── Phase 5: second pass for slow launchers ────────────────────────
        await Task.Delay(2000, ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return;

        liveWindows = _windowService.GetAllWindowsWithPids();
        MatchAndRestore(snapshot.Entries, liveWindows, restoredEntries, correctlyMatchedEntries);

        AppLogger.Info($"RestoreWorkspaceAsync complete");
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
    ///     A match here is recorded in <paramref name="correctlyMatchedEntries"/> so that
    ///     Phase 2 knows the right document is already on screen.</item>
    ///   <item>exe path + window class name.</item>
    ///   <item>exe path + first 10 chars of saved title snippet.</item>
    /// </list>
    /// </para>
    /// <paramref name="restoredEntries"/> prevents the same entry from being repositioned
    /// twice across Phase 1 / 4 / 5 calls.
    /// </summary>
    private void MatchAndRestore(
        List<WorkspaceEntry> entries,
        Dictionary<IntPtr, (uint Pid, WindowRecord Record)> liveWindows,
        HashSet<int> restoredEntries,
        HashSet<int>? correctlyMatchedEntries = null)
    {
        // Build a consumed-hwnd set so each window only gets one entry applied.
        var consumedHwnds = new HashSet<IntPtr>();

        for (int i = 0; i < entries.Count; i++)
        {
            if (restoredEntries.Contains(i)) continue;

            var entry = entries[i];
            if (string.IsNullOrEmpty(entry.ExecutablePath)) continue;

            IntPtr bestHwnd = IntPtr.Zero;
            bool titleMatched = false;

            // ── Tier 0: document-aware match (exe + title contains document name) ──
            // This is the highest-priority match for document entries: we want
            // "Diplomarbeit.docx - Word" to match only a Word window that actually
            // has Diplomarbeit.docx open, not just any Word window.
            if (!string.IsNullOrEmpty(entry.LaunchArg))
            {
                string expectedName = Path.GetFileNameWithoutExtension(entry.LaunchArg)
                                          .ToLowerInvariant();
                foreach (var (hwnd, (_, rec)) in liveWindows)
                {
                    if (consumedHwnds.Contains(hwnd)) continue;
                    if (rec.ExecutablePath.Equals(entry.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
                        rec.TitleSnippet.ToLowerInvariant().Contains(expectedName))
                    {
                        bestHwnd = hwnd;
                        titleMatched = true;
                        break;
                    }
                }
            }

            // ── Tier 1: exe + class ───────────────────────────────────────────────
            if (bestHwnd == IntPtr.Zero)
            {
                foreach (var (hwnd, (_, rec)) in liveWindows)
                {
                    if (consumedHwnds.Contains(hwnd)) continue;
                    if (rec.ExecutablePath.Equals(entry.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
                        rec.ClassName == entry.WindowClassName)
                    {
                        bestHwnd = hwnd;
                        break;
                    }
                }
            }

            // ── Tier 2: exe + title prefix (10 chars) ────────────────────────────
            if (bestHwnd == IntPtr.Zero)
            {
                string prefix = entry.Position.TitleSnippet.Length >= 10
                    ? entry.Position.TitleSnippet[..10]
                    : entry.Position.TitleSnippet;

                foreach (var (hwnd, (_, rec)) in liveWindows)
                {
                    if (consumedHwnds.Contains(hwnd)) continue;
                    if (rec.ExecutablePath.Equals(entry.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
                        rec.TitleSnippet.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        bestHwnd = hwnd;
                        break;
                    }
                }
            }

            if (bestHwnd == IntPtr.Zero) continue;

            consumedHwnds.Add(bestHwnd);
            restoredEntries.Add(i);
            if (titleMatched) correctlyMatchedEntries?.Add(i);
            entry.WasRestored = true;

            _windowService.RestoreSingleWindow(bestHwnd, entry.Position);
            AppLogger.Info($"Restored entry[{i}] {entry.ProcessName} → hwnd {bestHwnd} (titleMatched={titleMatched})");
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
    /// </summary>
    public ProcessStartInfo BuildProcessStartInfo(WorkspaceEntry entry)
    {
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

        return new ProcessStartInfo
        {
            FileName        = entry.ExecutablePath,
            UseShellExecute = true,
        };
    }
}

