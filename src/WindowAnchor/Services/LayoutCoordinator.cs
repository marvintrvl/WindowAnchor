using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly WorkspaceService _workspaceService;
    private readonly WorkspaceSwitchEngine _switchEngine;
    private readonly object _switchRequestSync = new();
    private CancellationTokenSource? _activeSwitchRequest;
    private CancellationTokenSource? _displayChangeCts;

    public LayoutCoordinator(
        MonitorService  monitorService,
        WindowService   windowService,
        WorkspaceService workspaceService)
        : this(
            monitorService,
            workspaceService,
            new WorkspaceSwitchEngine(windowService))
    {
    }

    internal LayoutCoordinator(
        MonitorService monitorService,
        WorkspaceService workspaceService,
        WorkspaceSwitchEngine switchEngine)
    {
        _monitorService  = monitorService;
        _workspaceService = workspaceService;
        _switchEngine = switchEngine;
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
            RestoreExecutionResult autoRestore = await _workspaceService
                .RestoreWorkspaceWithExecutionResultAsync(
                    matchedWorkspace,
                    RestoreMode.Standard,
                    WorkspaceCheckpointTrigger.AutomaticDisplayRestore,
                    token);
            if (token.IsCancellationRequested || autoRestore.WasCancelled) return;
            if (NotifyCheckpointFailure(autoRestore)) return;
            if (autoRestore.Status != RestoreExecutionStatus.Completed)
            {
                NotifyBalloon(
                    "Automatic Restore Needs Attention",
                    "The matching workspace could not be restored cleanly. Review it manually for details.",
                    H.NotifyIcon.Core.NotificationIcon.Warning);
                return;
            }

            _workspaceService.SetLastKnownFingerprint(fingerprint);
            NotifyBalloon("Workspace Restored",
                $"\u201c{matchedWorkspace.Name}\u201d \u2014 {matchedWorkspace.Entries.Count} windows repositioned.");
        }
        catch (TaskCanceledException) { }
    }

    /// <summary>Restores a workspace and shows a completion balloon.</summary>
    public async Task RestoreWorkspaceAsync(
        WorkspaceSnapshot snapshot,
        CancellationToken token = default,
        IProgress<RestoreProgressReport>? progress = null)
    {
        AppLogger.Info(
            "layout.restore_requested",
            "Requested a workspace restore",
            LogField.Identifier("workspaceId", snapshot.WorkspaceId),
            LogField.Workspace("workspaceName", snapshot.Name),
            LogField.Public("mode", "restore"));
        NotifyBalloon("Restoring\u2026", $"\u201c{snapshot.Name}\u201d \u2014 creating a recovery checkpoint first.");
        RestoreExecutionResult result = await _workspaceService.RestoreWorkspaceWithExecutionResultAsync(
            snapshot,
            RestoreMode.Standard,
            token,
            progress);
        if (token.IsCancellationRequested || result.WasCancelled || NotifyCheckpointFailure(result))
            return;
        if (result.Status == RestoreExecutionStatus.Completed)
            NotifyBalloon("Workspace Restored",
                $"\u201c{snapshot.Name}\u201d restored \u2014 other open windows were left untouched. " +
                $"Use \u201cSwitch to Workspace\u201d to close everything else first.");
        else
            NotifyBalloon(
                "Restore Needs Attention",
                "The workspace restore did not complete cleanly. Open the preview for details.",
                H.NotifyIcon.Core.NotificationIcon.Warning);
    }

    /// <summary>
    /// Restores a workspace and then minimizes every window that is not part of it (nothing is
    /// closed). This brings the workspace's own windows to their saved positions and clears away
    /// unrelated windows — a non-destructive middle ground between Restore (leaves other windows
    /// untouched) and Switch (closes everything else first).
    /// </summary>
    public async Task AlignAndMinimizeOthersAsync(
        WorkspaceSnapshot snapshot,
        CancellationToken token = default,
        IProgress<RestoreProgressReport>? progress = null)
    {
        AppLogger.Info(
            "layout.restore_requested",
            "Requested workspace alignment and minimization",
            LogField.Identifier("workspaceId", snapshot.WorkspaceId),
            LogField.Workspace("workspaceName", snapshot.Name),
            LogField.Public("mode", "align_and_minimize"));
        NotifyBalloon("Aligning\u2026",
            $"\u201c{snapshot.Name}\u201d \u2014 creating a recovery checkpoint before positioning windows.");
        RestoreExecutionResult result = await _workspaceService.RestoreWorkspaceWithExecutionResultAsync(
            snapshot,
            RestoreMode.AlignAndMinimize,
            token,
            progress);
        if (token.IsCancellationRequested || result.WasCancelled || NotifyCheckpointFailure(result))
            return;
        if (result.Status == RestoreExecutionStatus.Completed)
            NotifyBalloon("Workspace Aligned",
                $"\u201c{snapshot.Name}\u201d \u2014 other windows were minimized, not closed.");
        else
            NotifyBalloon(
                "Alignment Needs Attention",
                "The workspace could not be fully aligned. Open the preview for details.",
                H.NotifyIcon.Core.NotificationIcon.Warning);
    }

    /// <summary>
    /// Restores only entries on the specified monitors and shows a completion balloon.
    /// When <paramref name="monitorIds"/> is <c>null</c> all monitors are restored.
    /// </summary>
    public async Task RestoreWorkspaceSelectiveAsync(
        WorkspaceSnapshot snapshot,
        HashSet<string>? monitorIds,
        CancellationToken token = default,
        IProgress<RestoreProgressReport>? progress = null)
    {
        string desc = monitorIds == null ? "all monitors" : $"{monitorIds.Count} monitor(s)";
        AppLogger.Info(
            "layout.restore_requested",
            "Requested a selective workspace restore",
            LogField.Identifier("workspaceId", snapshot.WorkspaceId),
            LogField.Workspace("workspaceName", snapshot.Name),
            LogField.Public("mode", "selective"),
            LogField.Public("monitorCount", monitorIds?.Count));
        NotifyBalloon("Restoring\u2026", $"\u201c{snapshot.Name}\u201d ({desc}) \u2014 creating a recovery checkpoint first.");
        RestoreMode mode = monitorIds is null
            ? RestoreMode.Standard
            : RestoreMode.Selective(monitorIds.ToArray());
        RestoreExecutionResult result = await _workspaceService.RestoreWorkspaceWithExecutionResultAsync(
            snapshot,
            mode,
            token,
            progress);
        if (token.IsCancellationRequested || result.WasCancelled || NotifyCheckpointFailure(result))
            return;
        if (result.Status == RestoreExecutionStatus.Completed)
            NotifyBalloon("Workspace Restored",
                $"\u201c{snapshot.Name}\u201d ({desc}) \u2014 restored successfully.");
        else
            NotifyBalloon(
                "Restore Needs Attention",
                "The selective restore did not complete cleanly. Open the preview for details.",
                H.NotifyIcon.Core.NotificationIcon.Warning);
    }

    /// <summary>Builds the immutable plan shown by the manual restore preview.</summary>
    public RestorePlan CreateRestorePlan(WorkspaceSnapshot snapshot, RestoreMode mode) =>
        _workspaceService.CreateRestorePlan(snapshot, mode);

    /// <summary>Persists one user-confirmed match after its approved plan executes safely.</summary>
    public void RememberWindowMatch(
        string workspaceId,
        string entryId,
        WindowIdentityHint identity) =>
        _workspaceService.RememberWindowMatch(workspaceId, entryId, identity);

    /// <summary>Executes the exact plan approved by the preview without rebuilding its matches.</summary>
    public async Task<RestoreExecutionResult> RestoreApprovedPlanAsync(
        WorkspaceSnapshot snapshot,
        RestorePlan approvedPlan,
        CancellationToken token = default,
        IProgress<RestoreProgressReport>? progress = null)
    {
        NotifyBalloon(
            "Restoring…",
            $"“{snapshot.Name}” — creating a recovery checkpoint, then executing the reviewed plan.");
        RestoreExecutionResult result = await _workspaceService.ExecuteApprovedRestorePlanAsync(
            snapshot,
            approvedPlan,
            token,
            progress);
        if (token.IsCancellationRequested || result.WasCancelled)
            return result;

        if (NotifyCheckpointFailure(result))
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
            int placementFailures = result.PlacementFailures.Count;
            NotifyBalloon(
                "Restore Needs Attention",
                placementFailures > 0
                    ? $"{placementFailures} window placement{(placementFailures == 1 ? "" : "s")} " +
                      "could not be verified after bounded retries."
                    : "The reviewed plan could not be completed. Open the preview for details.",
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
    /// Creates an immediate plan for compatibility callers, then performs a single-flight switch.
    /// Interactive entry points should pass the exact plan approved in the preview overload.
    /// </summary>
    public Task<RestoreExecutionResult?> SwitchWorkspaceAsync(
        WorkspaceSnapshot snapshot,
        CancellationToken token = default,
        IProgress<RestoreProgressReport>? progress = null) =>
        SwitchWorkspaceAsync(
            snapshot,
            CreateRestorePlan(snapshot, RestoreMode.Standard),
            token,
            progress);

    /// <summary>
    /// Switches to the exact reviewed plan. Approved target-workspace HWNDs stay open; only
    /// unrelated close candidates receive WM_CLOSE and only those handles are polled.
    /// </summary>
    public async Task<RestoreExecutionResult?> SwitchWorkspaceAsync(
        WorkspaceSnapshot snapshot,
        RestorePlan approvedPlan,
        CancellationToken token = default,
        IProgress<RestoreProgressReport>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(approvedPlan);
        if (!string.Equals(snapshot.WorkspaceId, approvedPlan.WorkspaceId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The approved switch plan belongs to another workspace.", nameof(approvedPlan));

        using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (_switchRequestSync)
        {
            _activeSwitchRequest?.Cancel();
            _activeSwitchRequest = request;
        }

        try
        {
            return await SwitchWorkspaceCoreAsync(snapshot, approvedPlan, request.Token, progress);
        }
        finally
        {
            lock (_switchRequestSync)
            {
                if (ReferenceEquals(_activeSwitchRequest, request))
                    _activeSwitchRequest = null;
            }
        }
    }

    private async Task<RestoreExecutionResult?> SwitchWorkspaceCoreAsync(
        WorkspaceSnapshot snapshot,
        RestorePlan approvedPlan,
        CancellationToken token,
        IProgress<RestoreProgressReport>? progress)
    {
        if (!approvedPlan.CanExecute || approvedPlan.Actions.Count == 0)
        {
            NotifyBalloon(
                "Switch Needs Review",
                "Resolve blocking entries in the restore preview before switching.",
                H.NotifyIcon.Core.NotificationIcon.Warning);
            return null;
        }

        AppLogger.Info(
            "layout.switch_requested",
            "Requested a workspace switch",
            LogField.Identifier("workspaceId", snapshot.WorkspaceId),
            LogField.Workspace("workspaceName", snapshot.Name),
            LogField.Public("approvedEntryCount", approvedPlan.Entries.Count - approvedPlan.DisabledEntryIndexes.Count));
        NotifyBalloon("Switching\u2026",
            $"Creating a recovery checkpoint for \u201c{snapshot.Name}\u201d before closing unrelated windows.");

        HashSet<IntPtr> keep = approvedPlan.Actions
            .Where(action => action.Kind == RestoreActionKind.RestoreExistingWindow &&
                action.WindowHandle.HasValue)
            .Select(action => new IntPtr(action.WindowHandle!.Value))
            .ToHashSet();

        CheckpointedOperationResult<WorkspaceSwitchResult> transaction =
            await _workspaceService.ExecuteCheckpointedOperationAsync(
                snapshot,
                WorkspaceCheckpointTrigger.WorkspaceSwitch,
                (checkpoint, transactionToken) => _switchEngine.ExecuteAsync(
                    keep,
                    cancellationToken => _workspaceService.ExecuteApprovedRestorePlanAfterCheckpointAsync(
                        snapshot,
                        approvedPlan,
                        checkpoint,
                        cancellationToken,
                        progress),
                    switchProgress => ReportSwitchProgress(switchProgress, progress),
                    transactionToken),
                token,
                progress);

        if (!transaction.OperationStarted || transaction.Value is null)
        {
            RestoreExecutionResult rejected = _workspaceService.CreateCheckpointAbortedResult(
                approvedPlan,
                transaction.Checkpoint);
            NotifyCheckpointFailure(rejected);
            return rejected;
        }

        WorkspaceSwitchResult switchResult = transaction.Value;

        if (switchResult.Status == WorkspaceSwitchStatus.Cancelled)
            return null;

        if (switchResult.Status == WorkspaceSwitchStatus.TimedOut)
        {
            int remaining = switchResult.RemainingWindowHandles.Count;
            AppLogger.Warn(
                "layout.switch_timed_out",
                "Workspace switch reached its wall-clock timeout while waiting for requested windows",
                LogField.Public("remainingWindowCount", remaining),
                LogField.Public("errorCategory", "window_close_timeout"));
            NotifyBalloon("Switch Cancelled",
                $"{remaining} window{(remaining == 1 ? " is" : "s are")} still open. Workspace switch aborted.",
                H.NotifyIcon.Core.NotificationIcon.Warning);
            return null;
        }

        RestoreExecutionResult? restoreResult = switchResult.RestoreResult;
        if (restoreResult is null || restoreResult.WasCancelled)
            return restoreResult;
        if (restoreResult.HasStalePlan)
        {
            NotifyBalloon(
                "Switch Preview Is Stale",
                "The desktop changed after review. Reopen the switch preview and try again.",
                H.NotifyIcon.Core.NotificationIcon.Warning);
        }
        else if (restoreResult.Status is RestoreExecutionStatus.CompletedWithFailures or
                 RestoreExecutionStatus.Rejected)
        {
            int placementFailures = restoreResult.PlacementFailures.Count;
            NotifyBalloon(
                "Switch Needs Attention",
                placementFailures > 0
                    ? $"{placementFailures} window placement{(placementFailures == 1 ? "" : "s")} " +
                      "could not be verified after bounded retries."
                    : "The reviewed workspace plan could not be completed.",
                H.NotifyIcon.Core.NotificationIcon.Warning);
        }
        else
        {
            NotifyBalloon("Workspace Switched",
                $"Switched to \u201c{snapshot.Name}\u201d \u2014 {snapshot.Entries.Count} entries processed.");
        }
        return restoreResult;
    }

    /// <summary>True when a healthy recovery checkpoint makes undo available.</summary>
    public bool CanUndoLastRestore => _workspaceService.CanUndoLastRestore;

    /// <summary>Restores the latest checkpoint through the normal planner and transaction gate.</summary>
    public async Task<RestoreExecutionResult?> UndoLastRestoreAsync(
        CancellationToken token = default,
        IProgress<RestoreProgressReport>? progress = null)
    {
        if (!CanUndoLastRestore)
        {
            NotifyBalloon(
                "Nothing to Undo",
                "No recent recovery checkpoint is available.",
                H.NotifyIcon.Core.NotificationIcon.Warning);
            return null;
        }

        NotifyBalloon("Undoing Restore…", "Creating a new safety checkpoint before restoring the previous desktop.");
        RestoreExecutionResult? result = await _workspaceService.UndoLastRestoreAsync(token, progress);
        if (result is null || token.IsCancellationRequested || result.WasCancelled)
            return result;
        if (NotifyCheckpointFailure(result))
            return result;

        if (result.Status == RestoreExecutionStatus.Completed)
        {
            NotifyBalloon(
                "Restore Undone",
                "The previous desktop state was restored. Undo is available for this operation too.");
        }
        else
        {
            NotifyBalloon(
                "Undo Needs Attention",
                "The previous desktop state could not be fully restored.",
                H.NotifyIcon.Core.NotificationIcon.Warning);
        }
        return result;
    }

    private static bool NotifyCheckpointFailure(RestoreExecutionResult result)
    {
        if (result.Checkpoint?.Status != RestoreCheckpointStatus.Failed)
            return false;

        NotifyBalloon(
            "Restore Safely Blocked",
            "WindowAnchor could not save a recovery checkpoint, so it made no desktop changes.",
            H.NotifyIcon.Core.NotificationIcon.Warning);
        return true;
    }

    private static void ReportSwitchProgress(
        WorkspaceSwitchProgress progress,
        IProgress<RestoreProgressReport>? operationProgress)
    {
        switch (progress.Kind)
        {
            case WorkspaceSwitchProgressKind.PreflightCompleted:
                operationProgress?.Report(new RestoreProgressReport(
                    RestoreProgressStage.ClosingWindows,
                    "Checking open windows",
                    $"{progress.WindowCount} user window{(progress.WindowCount == 1 ? "" : "s")} inspected."));
                AppLogger.Info(
                    "layout.switch_preflight_completed",
                    "Completed the workspace-switch risk preflight",
                    LogField.Public("riskWindowCount", progress.WindowCount));
                break;
            case WorkspaceSwitchProgressKind.CloseRequested:
                operationProgress?.Report(new RestoreProgressReport(
                    RestoreProgressStage.ClosingWindows,
                    progress.WindowCount == 0
                        ? "No unrelated windows need to close"
                        : $"Closing {progress.WindowCount} unrelated window{(progress.WindowCount == 1 ? "" : "s")}",
                    progress.WindowCount == 0
                        ? "Continuing with the restore."
                        : "Save any work if an application prompts you."));
                AppLogger.Info(
                    "layout.switch_close_requested",
                    "Sent close requests to unrelated user windows",
                    LogField.Public("windowCount", progress.WindowCount));
                break;
            case WorkspaceSwitchProgressKind.WaitingForClose:
                operationProgress?.Report(new RestoreProgressReport(
                    RestoreProgressStage.ClosingWindows,
                    $"Waiting for {progress.WindowCount} window{(progress.WindowCount == 1 ? "" : "s")} to close",
                    "Complete any visible save prompts to continue.",
                    Elapsed: progress.Elapsed,
                    Timeout: progress.Timeout));
                if (progress.ShouldNotifyUser)
                {
                    AppLogger.Info(
                        "layout.switch_waiting_for_close",
                        "Waiting for requested user windows to close",
                        LogField.Public("remainingWindowCount", progress.WindowCount),
                        LogField.Public("elapsedMs", progress.Elapsed?.TotalMilliseconds));
                }
                if (progress.ShouldNotifyUser)
                {
                    NotifyBalloon("Waiting\u2026",
                        $"{progress.WindowCount} window{(progress.WindowCount == 1 ? "" : "s")} still open \u2014 save your work to continue.");
                }
                break;
        }
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
