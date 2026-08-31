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
            AppLogger.Info(
                "display.change_detected",
                "Detected a stabilized display change",
                LogField.Identifier("monitorFingerprint", fingerprint));

            var matchedWorkspace = _workspaceService.FindWorkspaceByFingerprint(fingerprint);
            bool isReconnect = matchedWorkspace != null;

            if (!isReconnect)
            {
                AppLogger.Info(
                    "display.auto_restore_not_found",
                    "No workspace matched the current display topology",
                    LogField.Identifier("monitorFingerprint", fingerprint));
                return;
            }

            await Task.Delay(500, token);
            if (token.IsCancellationRequested) return;

            AppLogger.Info(
                "display.auto_restore_started",
                "A display reconnect matched a saved workspace",
                LogField.Identifier("workspaceId", matchedWorkspace!.WorkspaceId),
                LogField.Workspace("workspaceName", matchedWorkspace.Name));
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
        AppLogger.Info(
            "layout.restore_requested",
            "Requested a workspace restore",
            LogField.Identifier("workspaceId", snapshot.WorkspaceId),
            LogField.Workspace("workspaceName", snapshot.Name),
            LogField.Public("mode", "restore"));
        NotifyBalloon("Restoring\u2026", $"\u201c{snapshot.Name}\u201d \u2014 launching apps and repositioning windows.");
        await _workspaceService.RestoreWorkspaceAsync(snapshot, token);
        if (!token.IsCancellationRequested)
            NotifyBalloon("Workspace Restored",
                $"\u201c{snapshot.Name}\u201d restored \u2014 other open windows were left untouched. " +
                $"Use \u201cSwitch to Workspace\u201d to close everything else first.");
    }

    /// <summary>
    /// Restores a workspace and then minimizes every window that is not part of it (nothing is
    /// closed). This brings the workspace's own windows to their saved positions and clears away
    /// unrelated windows — a non-destructive middle ground between Restore (leaves other windows
    /// untouched) and Switch (closes everything else first).
    /// </summary>
    public async Task AlignAndMinimizeOthersAsync(WorkspaceSnapshot snapshot, CancellationToken token = default)
    {
        AppLogger.Info(
            "layout.restore_requested",
            "Requested workspace alignment and minimization",
            LogField.Identifier("workspaceId", snapshot.WorkspaceId),
            LogField.Workspace("workspaceName", snapshot.Name),
            LogField.Public("mode", "align_and_minimize"));
        NotifyBalloon("Aligning\u2026",
            $"\u201c{snapshot.Name}\u201d \u2014 positioning its windows and minimizing everything else.");
        await _workspaceService.RestoreWorkspaceAlignAndMinimizeAsync(snapshot, token);
        if (!token.IsCancellationRequested)
            NotifyBalloon("Workspace Aligned",
                $"\u201c{snapshot.Name}\u201d \u2014 other windows were minimized, not closed.");
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
        AppLogger.Info(
            "layout.restore_requested",
            "Requested a selective workspace restore",
            LogField.Identifier("workspaceId", snapshot.WorkspaceId),
            LogField.Workspace("workspaceName", snapshot.Name),
            LogField.Public("mode", "selective"),
            LogField.Public("monitorCount", monitorIds?.Count));
        NotifyBalloon("Restoring\u2026", $"\u201c{snapshot.Name}\u201d ({desc}) \u2014 launching apps.");
        await _workspaceService.RestoreWorkspaceSelectiveAsync(snapshot, monitorIds, token);
        if (!token.IsCancellationRequested)
            NotifyBalloon("Workspace Restored",
                $"\u201c{snapshot.Name}\u201d ({desc}) \u2014 restored successfully.");
    }

    /// <summary>Builds the immutable plan shown by the manual restore preview.</summary>
    public RestorePlan CreateRestorePlan(WorkspaceSnapshot snapshot, RestoreMode mode) =>
        _workspaceService.CreateRestorePlan(snapshot, mode);

    /// <summary>Executes the exact plan approved by the preview without rebuilding its matches.</summary>
    public async Task<RestoreExecutionResult> RestoreApprovedPlanAsync(
        WorkspaceSnapshot snapshot,
        RestorePlan approvedPlan,
        CancellationToken token = default)
    {
        NotifyBalloon(
            "Restoring…",
            $"“{snapshot.Name}” — executing the reviewed restore plan.");
        RestoreExecutionResult result = await _workspaceService.ExecuteApprovedRestorePlanAsync(
            snapshot,
            approvedPlan,
            token);
        if (token.IsCancellationRequested || result.WasCancelled)
            return result;

        if (result.HasStalePlan)
        {
            NotifyBalloon(
                "Restore Preview Is Stale",
                "The desktop changed after preview. No stale action was applied; review a fresh plan.",
                H.NotifyIcon.Core.NotificationIcon.Warning);
        }
        else if (result.Status is RestoreExecutionStatus.CompletedWithFailures or
                 RestoreExecutionStatus.Rejected)
        {
            NotifyBalloon(
                "Restore Needs Attention",
                "The reviewed plan could not be completed. Open the preview for details.",
                H.NotifyIcon.Core.NotificationIcon.Warning);
        }
        else
        {
            NotifyBalloon(
                "Workspace Restored",
                $"“{snapshot.Name}” restored from the reviewed plan.");
        }
        return result;
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
        AppLogger.Info(
            "layout.switch_requested",
            "Requested a workspace switch",
            LogField.Identifier("workspaceId", snapshot.WorkspaceId),
            LogField.Workspace("workspaceName", snapshot.Name));
        NotifyBalloon("Switching\u2026",
            $"Closing all windows\u2026 save any unsaved work, then they will close automatically.");

        // Safe-switch preflight deliberately uses the broader risk policy. This inventory can
        // see owned save dialogs and transient windows that normal capture must never persist.
        var switchRisks = _windowService.InspectUserWindows(
            WindowCandidatePolicy.SwitchRiskCandidate);
        AppLogger.Info(
            "layout.switch_preflight_completed",
            "Completed the workspace-switch risk preflight",
            LogField.Public("riskWindowCount", switchRisks.Count));

        // Phase 1: send WM_CLOSE to every user window
        int closed = _windowService.CloseAllUserWindows(
            WindowCandidatePolicy.SwitchCloseCandidate);
        AppLogger.Info(
            "layout.switch_close_requested",
            "Sent close requests to user windows",
            LogField.Public("windowCount", closed));

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

            int remaining = _windowService.CountUserWindows(
                WindowCandidatePolicy.SwitchRiskCandidate);

            if (remaining == 0)
            {
                AppLogger.Info("SwitchWorkspaceAsync: all windows closed");
                break;
            }

            // Notify the user how many windows are still open (only when count changes)
            if (remaining != lastRemaining)
            {
                AppLogger.Info(
                    "layout.switch_waiting_for_close",
                    "Waiting for user windows to close",
                    LogField.Public("remainingWindowCount", remaining));
                NotifyBalloon("Waiting\u2026",
                    $"{remaining} window{(remaining == 1 ? "" : "s")} still open \u2014 save your work to continue.");
                lastRemaining = remaining;
            }
        }

        if (token.IsCancellationRequested) return;

        // Phase 3: check result
        int finalRemaining = _windowService.CountUserWindows(
            WindowCandidatePolicy.SwitchRiskCandidate);
        if (finalRemaining > 0)
        {
            AppLogger.Warn(
                "layout.switch_timed_out",
                "Workspace switch timed out while waiting for windows to close",
                LogField.Public("remainingWindowCount", finalRemaining),
                LogField.Public("errorCategory", "window_close_timeout"));
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
