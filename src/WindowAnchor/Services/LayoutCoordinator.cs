using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

public class LayoutCoordinator
{
    private readonly MonitorService _monitorService;
    private readonly WindowService _windowService;
    private readonly WorkspaceService _workspaceService;
    private CancellationTokenSource? _displayChangeCts;

    public LayoutCoordinator(
        MonitorService monitorService,
        WindowService windowService,
        WorkspaceService workspaceService)
    {
        _monitorService = monitorService;
        _windowService = windowService;
        _workspaceService = workspaceService;
    }

    public async void HandleDisplayChangeAsync()
    {
        _displayChangeCts?.Cancel();
        _displayChangeCts = new CancellationTokenSource();
        var token = _displayChangeCts.Token;

        try
        {
            // Debounce to wait for resolution to settle
            await Task.Delay(1000, token);

            string fingerprint = _monitorService.GetCurrentMonitorFingerprint();
            AppLogger.Info($"Display change detected. New fingerprint: {fingerprint}");

            bool isReconnect = _workspaceService.HasProfile(fingerprint);

            if (!isReconnect)
            {
                // Disconnect / new config — auto-save with default name
                AppLogger.Info($"Disconnect/new config — saving state for {fingerprint}");
                await Task.Delay(500, token);
                if (token.IsCancellationRequested) return;
                SaveCurrentLayoutInternal(fingerprint, $"Profile {DateTime.Now:HH:mm}");
                return;
            }

            // Reconnect
            AppLogger.Info($"Reconnect detected — restoring layout for {fingerprint}");
            await Task.Delay(500, token);
            if (token.IsCancellationRequested) return;

            var profile = _workspaceService.LoadProfile(fingerprint)!;
            await LaunchMissingAppsAsync(profile.Windows, token);
            if (token.IsCancellationRequested) return;

            // First-pass restore
            _windowService.RestoreWindows(profile);

            // Second pass for slow-rendering windows
            await Task.Delay(500, token);
            if (token.IsCancellationRequested) return;
            AppLogger.Info($"Second-pass restore (reconnect)");
            _windowService.RestoreWindows(profile);

            _workspaceService.SetLastKnownFingerprint(fingerprint);
            NotifyBalloon("Layout Restored", $"Windows repositioned for display configuration {fingerprint[..6]}…");
        }
        catch (TaskCanceledException) { }
    }

    public void SaveCurrentLayout()
    {
        string fingerprint = _monitorService.GetCurrentMonitorFingerprint();
        SaveCurrentLayoutInternal(fingerprint, $"Profile {DateTime.Now:HH:mm}");
    }

    public void SaveCurrentLayoutWithName(string displayName)
    {
        string fingerprint = _monitorService.GetCurrentMonitorFingerprint();
        SaveCurrentLayoutInternal(fingerprint, displayName);
    }

    private void SaveCurrentLayoutInternal(string fingerprint, string displayName)
    {
        var windows = _windowService.SnapshotAllWindows();

        var profile = new MonitorProfile
        {
            Fingerprint = fingerprint,
            DisplayName = displayName,
            LastSaved   = DateTime.Now,
            Windows     = windows
        };

        _workspaceService.SaveProfile(profile);
        _workspaceService.SetLastKnownFingerprint(fingerprint);
        AppLogger.Info($"Saved monitor profile for {fingerprint} ({windows.Count} windows)");
        NotifyBalloon("Layout Saved", $"{windows.Count} window positions saved.");
    }

    /// <summary>
    /// For any window in the saved profile whose exe is not currently running,
    /// launch it via ShellExecute and wait for it to start.
    /// Uses Process.GetProcessesByName() — not window enumeration — so it works
    /// even for elevated apps whose ExecutablePath cannot be read.
    /// Returns true if any app was launched (caller should do a second-pass restore).
    /// </summary>
    private async Task<bool> LaunchMissingAppsAsync(List<WindowRecord> savedWindows, CancellationToken token)
    {
        var launched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var saved in savedWindows)
        {
            if (string.IsNullOrEmpty(saved.ExecutablePath)) continue;
            if (launched.Contains(saved.ExecutablePath)) continue;

            // Check by process name — reliable even when ExecutablePath access is denied
            bool isRunning = false;
            if (!string.IsNullOrEmpty(saved.ProcessName))
            {
                var procs = Process.GetProcessesByName(saved.ProcessName);
                isRunning = procs.Length > 0;
                foreach (var p in procs) p.Dispose();
            }

            if (!isRunning)
            {
                launched.Add(saved.ExecutablePath);
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName        = saved.ExecutablePath,
                        UseShellExecute = true
                    });
                    AppLogger.Info($"Launched missing app: {Path.GetFileName(saved.ExecutablePath)}");
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"Failed to launch {saved.ExecutablePath}: {ex.Message}");
                }
            }
        }

        if (launched.Count > 0)
        {
            AppLogger.Info($"Waiting 3 s for {launched.Count} app(s) to initialise…");
            await Task.Delay(3000, token);
            return true;
        }

        return false;
    }

    public async void RestoreLayout()
    {
        string fingerprint = _monitorService.GetCurrentMonitorFingerprint();
        var profile = _workspaceService.LoadProfile(fingerprint);

        if (profile == null)
        {
            AppLogger.Warn($"RestoreLayout: no saved profile for {fingerprint}");
            NotifyBalloon("No Layout Found", "No saved window layout for current monitor configuration.", H.NotifyIcon.Core.NotificationIcon.Warning);
            return;
        }

        bool anyLaunched = await LaunchMissingAppsAsync(profile.Windows, CancellationToken.None);

        // First pass
        _windowService.RestoreWindows(profile);

        if (anyLaunched)
        {
            await Task.Delay(2000, CancellationToken.None);
            AppLogger.Info("Second-pass restore for slow-launching apps");
            _windowService.RestoreWindows(profile);
        }

        NotifyBalloon("Layout Restored", $"{profile.Windows.Count} windows repositioned.");
    }

    public void SaveWorkspaceSnapshot(string name)
    {
        _workspaceService.TakeSnapshot(name);
        AppLogger.Info($"SaveWorkspaceSnapshot: '{name}'");
        NotifyBalloon("Workspace Saved", $"\u201c{name}\u201d has been saved.");
    }

    public Task RestoreWorkspaceAsync(WorkspaceSnapshot snapshot, CancellationToken token = default)
    {
        AppLogger.Info($"RestoreWorkspaceAsync: '{snapshot.Name}'");
        NotifyBalloon("Restoring Workspace", $"Launching apps for \u201c{snapshot.Name}\u201d\u2026");
        return _workspaceService.RestoreWorkspaceAsync(snapshot, token);
    }

    // ── Balloon helper ─────────────────────────────────────────────────────

    private static void NotifyBalloon(string title, string message,
        H.NotifyIcon.Core.NotificationIcon icon = H.NotifyIcon.Core.NotificationIcon.Info)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (System.Windows.Application.Current is App app)
                app.ShowBalloon(title, message, icon);
        });
    }
}
