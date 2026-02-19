using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

public class LayoutCoordinator
{
    private readonly MonitorService  _monitorService;
    private readonly WorkspaceService _workspaceService;
    private CancellationTokenSource? _displayChangeCts;

    public LayoutCoordinator(
        MonitorService  monitorService,
        WindowService   _,                // kept for call-site compat; no longer used internally
        WorkspaceService workspaceService)
    {
        _monitorService  = monitorService;
        _workspaceService = workspaceService;
    }

    public async void HandleDisplayChangeAsync()
    {
        _displayChangeCts?.Cancel();
        _displayChangeCts = new CancellationTokenSource();
        var token = _displayChangeCts.Token;

        try
        {
            // Debounce: wait for display resolution to stabilize (1 s)
            await Task.Delay(1000, token);

            string fingerprint = _monitorService.GetCurrentMonitorFingerprint();
            AppLogger.Info($"Display change detected. New fingerprint: {fingerprint}");

            var matchedWorkspace = _workspaceService.FindWorkspaceByFingerprint(fingerprint);
            bool isReconnect = matchedWorkspace != null;

            if (!isReconnect)
            {
                AppLogger.Info($"No workspace saved for fingerprint {fingerprint} — no auto-restore");
                return;
            }

            await Task.Delay(500, token);
            if (token.IsCancellationRequested) return;

            AppLogger.Info($"Reconnect detected — auto-restoring \u2018{matchedWorkspace!.Name}\u2019");
            await _workspaceService.RestoreWorkspaceAsync(matchedWorkspace, token);
            if (token.IsCancellationRequested) return;

            _workspaceService.SetLastKnownFingerprint(fingerprint);
            NotifyBalloon("Workspace Restored",
                $"\u201c{matchedWorkspace.Name}\u201d \u2014 {matchedWorkspace.Entries.Count} windows repositioned.");
        }
        catch (TaskCanceledException) { }
    }

    /// <summary>Restores a workspace and shows a completion balloon.</summary>
    public async Task RestoreWorkspaceAsync(WorkspaceSnapshot snapshot, CancellationToken token = default)
    {
        AppLogger.Info($"RestoreWorkspaceAsync: \u2018{snapshot.Name}\u2019");
        NotifyBalloon("Restoring\u2026", $"\u201c{snapshot.Name}\u201d \u2014 launching apps and repositioning windows.");
        await _workspaceService.RestoreWorkspaceAsync(snapshot, token);
        if (!token.IsCancellationRequested)
            NotifyBalloon("Workspace Restored",
                $"\u201c{snapshot.Name}\u201d \u2014 {snapshot.Entries.Count} windows repositioned.");
    }

    /// <summary>
    /// Restores only entries on the specified monitors and shows a completion balloon.
    /// When <paramref name="monitorIds"/> is <c>null</c> all monitors are restored.
    /// </summary>
    public async Task RestoreWorkspaceSelectiveAsync(
        WorkspaceSnapshot snapshot,
        HashSet<string>? monitorIds,
        CancellationToken token = default)
    {
        string desc = monitorIds == null ? "all monitors" : $"{monitorIds.Count} monitor(s)";
        AppLogger.Info($"RestoreWorkspaceSelectiveAsync: \u2018{snapshot.Name}\u2019 \u2014 {desc}");
        NotifyBalloon("Restoring\u2026", $"\u201c{snapshot.Name}\u201d ({desc}) \u2014 launching apps.");
        await _workspaceService.RestoreWorkspaceSelectiveAsync(snapshot, monitorIds, token);
        if (!token.IsCancellationRequested)
            NotifyBalloon("Workspace Restored",
                $"\u201c{snapshot.Name}\u201d ({desc}) \u2014 restored successfully.");
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
