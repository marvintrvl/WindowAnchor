using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

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

    // ── Profile proxies ──────────────────────────────────────────────────────

    public void SaveProfile(MonitorProfile profile)       => _storageService.SaveProfile(profile);
    public MonitorProfile? LoadProfile(string fp)         => _storageService.LoadProfile(fp);
    public bool HasProfile(string fp)                     => _storageService.HasProfile(fp);
    public string GetLastKnownFingerprint()               => _storageService.GetLastKnownFingerprint();
    public void SetLastKnownFingerprint(string fp)        => _storageService.SetLastKnownFingerprint(fp);
    public void SaveWorkspace(WorkspaceSnapshot snapshot) => _storageService.SaveWorkspace(snapshot);
    public List<WorkspaceSnapshot> GetAllWorkspaces()     => _storageService.LoadAllWorkspaces();

    // ── Snapshot ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Captures all visible windows and builds a <see cref="WorkspaceSnapshot"/>
    /// using Tier 1 (title parsing) then Tier 2 (jump-list) file detection.
    /// </summary>
    public WorkspaceSnapshot TakeSnapshot(string name)
    {
        string fingerprint = _monitorService.GetCurrentMonitorFingerprint();
        var windows = _windowService.SnapshotAllWindows();

        var entries = new List<WorkspaceEntry>();

        // Build the jump-list index once for the whole snapshot pass (avoids re-parsing
        // every .automaticDestinations-ms file once per window, which caused ~20s delays).
        _jumpListService.BuildSnapshotCache();

        try
        {
        foreach (var w in windows)
        {
            // ── Tier 1: parse file path from window title ──────────────────
            var (titlePath, titleConf) = TitleParser.ExtractFilePath(w.ProcessName, w.TitleSnippet);

            string? filePath   = titlePath;
            int     confidence = titleConf;
            string  source     = titleConf > 0 ? "TITLE_PARSE" : "NONE";

            // ── Tier 2: jump-list lookup if Tier 1 gave nothing confident ──
            // Only attempt for apps that produce jump lists (need an exe path).
            if (confidence < 80 && !string.IsNullOrEmpty(w.ExecutablePath))
            {
                try
                {
                    var jlFiles = _jumpListService.GetRecentFilesForApp(w.ExecutablePath, maxFiles: 5);
                    if (jlFiles.Count > 0)
                    {
                        // Only accept a jump-list result when a filename actually appears in the
                        // current window title. The old fallback of "?? jlFiles[0]" blindly used
                        // the most-recently-opened file regardless of which document is currently
                        // open, causing wrong filenames in the UI and wrong files being relaunched
                        // on restore (e.g. "Relevant code.docx" shown instead of "Diplomarbeit.docx").
                        string titleLower = w.TitleSnippet.ToLowerInvariant();
                        string? jlBest = jlFiles.FirstOrDefault(p =>
                            titleLower.Contains(Path.GetFileNameWithoutExtension(p).ToLowerInvariant()));

                        if (jlBest != null)
                        {
                            filePath   = jlBest;
                            confidence = 80;
                            source     = "JUMPLIST";
                        }
                        // No title match → leave filePath/confidence from Tier 1 unchanged so the
                        // correctly parsed filename is still shown in the UI and launchArg stays
                        // null rather than pointing at the wrong file.
                    }
                }
                catch { /* Jump list failures must not stop snapshot */ }
            }

            // Build launch arg: only include high-confidence paths
            string? launchArg = confidence >= 80 ? filePath : null;

            // VS Code: launch arg is the folder, not the file
            if (w.ProcessName.Equals("Code", StringComparison.OrdinalIgnoreCase) &&
                launchArg != null && File.Exists(launchArg))
            {
                launchArg = Path.GetDirectoryName(launchArg);
            }

            entries.Add(new WorkspaceEntry
            {
                ExecutablePath   = w.ExecutablePath,
                ProcessName      = w.ProcessName,
                WindowClassName  = w.ClassName,
                FilePath         = filePath,
                FileConfidence   = confidence,
                FileSource       = source,
                LaunchArg        = launchArg,
                Position         = w,
            });
        }
        }
        finally
        {
            _jumpListService.ClearSnapshotCache();
        }

        var snapshot = new WorkspaceSnapshot
        {
            Name               = name,
            MonitorFingerprint = fingerprint,
            SavedAt            = DateTime.UtcNow,
            Entries            = entries,
        };

        _storageService.SaveWorkspace(snapshot);
        AppLogger.Info($"TakeSnapshot saved '{name}' — {entries.Count} entries");
        return snapshot;
    }

    // ── Restore ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Restores a workspace snapshot using a 5-phase approach:
    /// <list type="number">
    ///   <item>Immediately reposition already-running windows.</item>
    ///   <item>Launch missing apps.</item>
    ///   <item>3-second wait for app initialisation.</item>
    ///   <item>Reposition newly appeared windows.</item>
    ///   <item>2-second wait + second pass for slow launchers (Office, IDEs).</item>
    /// </list>
    /// </summary>
    public async Task RestoreWorkspaceAsync(WorkspaceSnapshot snapshot, CancellationToken ct = default)
    {
        AppLogger.Info($"RestoreWorkspaceAsync '{snapshot.Name}' — {snapshot.Entries.Count} entries");

        // ── Phase 1: reposition already-running windows ───────────────────
        var liveWindows = _windowService.GetAllWindowsWithPids();
        var restoredEntries = new HashSet<int>();

        MatchAndRestore(snapshot.Entries, liveWindows, restoredEntries);

        if (ct.IsCancellationRequested) return;

        // ── Phase 2: launch missing apps ──────────────────────────────────
        bool anyLaunched = false;
        var runningExes = liveWindows.Values
            .Select(v => v.Record.ExecutablePath.ToLowerInvariant())
            .ToHashSet();

        foreach (var entry in snapshot.Entries)
        {
            if (ct.IsCancellationRequested) return;

            string exeLower = entry.ExecutablePath.ToLowerInvariant();
            if (runningExes.Contains(exeLower)) continue;  // already running
            if (string.IsNullOrEmpty(entry.ExecutablePath)) continue;

            try
            {
                var psi = BuildProcessStartInfo(entry);
                Process.Start(psi);
                anyLaunched = true;
                AppLogger.Info($"Launched: {entry.ExecutablePath} arg={entry.LaunchArg ?? "(none)"}");
            }
            catch (Exception ex)
            {
                // App may be uninstalled — skip and continue
                AppLogger.Warn($"Failed to launch '{entry.ExecutablePath}': {ex.Message}");
            }
        }

        if (!anyLaunched) return;

        // ── Phase 3: wait for app initialisation ─────────────────────────
        await Task.Delay(3000, ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return;

        // ── Phase 4: reposition newly appeared windows ────────────────────
        liveWindows = _windowService.GetAllWindowsWithPids();
        MatchAndRestore(snapshot.Entries, liveWindows, restoredEntries);

        if (ct.IsCancellationRequested) return;

        // ── Phase 5: second pass for slow launchers ────────────────────────
        await Task.Delay(2000, ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return;

        liveWindows = _windowService.GetAllWindowsWithPids();
        MatchAndRestore(snapshot.Entries, liveWindows, restoredEntries);

        AppLogger.Info($"RestoreWorkspaceAsync complete");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Matches live windows to snapshot entries by exe path + class name, then
    /// falls back to exe path + title prefix. Calls <see cref="WindowService.RestoreSingleWindow"/>
    /// for each match, and marks the entry index in <paramref name="restoredEntries"/> so
    /// entries are not repositioned twice (important because Phase 4 and 5 re-use the same set).
    /// </summary>
    private void MatchAndRestore(
        List<WorkspaceEntry> entries,
        Dictionary<IntPtr, (uint Pid, WindowRecord Record)> liveWindows,
        HashSet<int> restoredEntries)
    {
        // Build a consumed-hwnd set so each window only gets one entry applied.
        var consumedHwnds = new HashSet<IntPtr>();

        for (int i = 0; i < entries.Count; i++)
        {
            if (restoredEntries.Contains(i)) continue;

            var entry = entries[i];
            if (string.IsNullOrEmpty(entry.ExecutablePath)) continue;

            IntPtr bestHwnd = IntPtr.Zero;

            // Primary: exe + class
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

            // Fallback: exe + title prefix (10 chars)
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
            entry.WasRestored = true;

            _windowService.RestoreSingleWindow(bestHwnd, entry.Position);
            AppLogger.Info($"Restored entry[{i}] {entry.ProcessName} → hwnd {bestHwnd}");
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

