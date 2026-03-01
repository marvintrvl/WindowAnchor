using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>
/// Bridges display-change events from the UI layer to <see cref="WorkspaceService"/>.
/// Owns the debounce timer for <c>WM_DISPLAYCHANGE</c>, the auto-restore logic,
/// and all system-tray notification balloons.
/// </summary>
public class LayoutCoordinator
{
    private readonly MonitorService  _monitorService;
    private readonly WindowService   _windowService;
    private readonly WorkspaceService _workspaceService;
    private CancellationTokenSource? _displayChangeCts;

    public LayoutCoordinator(
        MonitorService  monitorService,
        WindowService   windowService,
        WorkspaceService workspaceService)
    {
        _monitorService  = monitorService;
        _windowService   = windowService;
        _workspaceService = workspaceService;
    }

    /// <summary>
    /// Called by <c>App.xaml.cs</c> whenever a <c>WM_DISPLAYCHANGE</c> message is received.
    /// Debounces the event by 1 second, computes the new monitor fingerprint, and
    /// auto-restores the matching workspace (if any). Cancels any in-flight invocation
    /// so that rapid display changes do not trigger multiple concurrent restores.
    /// </summary>
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
                $"\u201c{snapshot.Name}\u201d restored \u2014 other open windows were left untouched. " +
                $"Use \u201cSwitch to Workspace\u201d to close everything else first.");
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

    /// <summary>
    /// Instant context switch: gracefully closes all current user windows, waits
    /// for them to finish (giving users time to respond to save-confirmation
    /// dialogs), then restores the target workspace.
    /// </summary>
    /// <remarks>
    /// <list type="number">
    ///   <item>Post <c>WM_CLOSE</c> to every user window (triggers save prompts).</item>
    ///   <item>Poll every 500 ms until all windows are gone (up to 2 minutes).</item>
    ///   <item>If windows remain after the timeout the switch is aborted.</item>
    ///   <item>Once the desktop is clear, restore the target workspace.</item>
    /// </list>
    /// </remarks>
    public async Task SwitchWorkspaceAsync(WorkspaceSnapshot snapshot, CancellationToken token = default)
    {
        AppLogger.Info($"SwitchWorkspaceAsync: \u2018{snapshot.Name}\u2019");
        NotifyBalloon("Switching\u2026",
            $"Closing all windows\u2026 save any unsaved work, then they will close automatically.");

        // Phase 1: send WM_CLOSE to every user window
        int closed = _windowService.CloseAllUserWindows();
        AppLogger.Info($"SwitchWorkspaceAsync: sent WM_CLOSE to {closed} windows");

        if (closed == 0)
        {
            // Nothing to close — go straight to restore
            await RestoreAfterSwitch(snapshot, token);
            return;
        }

        // Phase 2: poll until all user windows are gone
        //   Generous timeout (120 s) — users may need to respond to multiple
        //   save-confirmation dialogs across several apps.
        const int pollIntervalMs = 500;
        const int timeoutMs      = 120_000;
        int elapsed = 0;
        int lastRemaining = -1;

        while (elapsed < timeoutMs)
        {
            if (token.IsCancellationRequested) return;

            await Task.Delay(pollIntervalMs, token).ConfigureAwait(false);
            elapsed += pollIntervalMs;

            int remaining = _windowService.CountUserWindows();

            if (remaining == 0)
            {
                AppLogger.Info("SwitchWorkspaceAsync: all windows closed");
                break;
            }

            // Notify the user how many windows are still open (only when count changes)
            if (remaining != lastRemaining)
            {
                AppLogger.Info($"SwitchWorkspaceAsync: waiting for {remaining} window(s) to close");
                NotifyBalloon("Waiting\u2026",
                    $"{remaining} window{(remaining == 1 ? "" : "s")} still open \u2014 save your work to continue.");
                lastRemaining = remaining;
            }
        }

        if (token.IsCancellationRequested) return;

        // Phase 3: check result
        int finalRemaining = _windowService.CountUserWindows();
        if (finalRemaining > 0)
        {
            AppLogger.Warn($"SwitchWorkspaceAsync: timed out with {finalRemaining} window(s) still open — aborting");
            NotifyBalloon("Switch Cancelled",
                $"{finalRemaining} window{(finalRemaining == 1 ? " is" : "s are")} still open. Workspace switch aborted.",
                H.NotifyIcon.Core.NotificationIcon.Warning);
            return;
        }

        // Phase 4: restore
        await RestoreAfterSwitch(snapshot, token);
    }

    private async Task RestoreAfterSwitch(WorkspaceSnapshot snapshot, CancellationToken token)
    {
        await _workspaceService.RestoreWorkspaceAsync(snapshot, token);
        if (!token.IsCancellationRequested)
            NotifyBalloon("Workspace Switched",
                $"Switched to \u201c{snapshot.Name}\u201d \u2014 {snapshot.Entries.Count} windows restored.");
    }

    // ── Balloon helper ─────────────────────────────────────────────────────

    /// <summary>
    /// Dispatches a system-tray balloon notification to the UI thread.
    /// Safe to call from any thread.
    /// </summary>
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
