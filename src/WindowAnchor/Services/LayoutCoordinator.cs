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
public class LayoutCoordinator : IAsyncDisposable
{
    private readonly MonitorService  _monitorService;
    private readonly WorkspaceService _workspaceService;
    private readonly WorkspaceSwitchEngine _switchEngine;
    private readonly object _switchRequestSync = new();
    private CancellationTokenSource? _activeSwitchRequest;
    private CancellationTokenSource? _displayChangeCts;
    private Task _activeDisplayChange = Task.CompletedTask;
    private TaskCompletionSource? _disposeCompletion;
    private bool _disposed;

    public LayoutCoordinator(
        MonitorService  monitorService,
        WindowService   windowService,
        WorkspaceService workspaceService)
#pragma warning disable CA2000 // The delegated constructor transfers ownership to this coordinator.
        : this(
            monitorService,
            workspaceService,
            new WorkspaceSwitchEngine(windowService))
#pragma warning restore CA2000
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
    public async Task HandleDisplayChangeAsync()
    {
        var invocation = new CancellationTokenSource();
        CancellationTokenSource? previous;
        Task operation;
        lock (_switchRequestSync)
        {
            if (_disposed)
            {
                invocation.Dispose();
                return;
            }
            previous = _displayChangeCts;
            _displayChangeCts = invocation;
            operation = HandleDisplayChangeCoreAsync(invocation);
            _activeDisplayChange = operation;
        }
        if (previous is not null)
            await TryCancelAsync(previous).ConfigureAwait(false);
        await operation.ConfigureAwait(false);
    }

    private async Task HandleDisplayChangeCoreAsync(CancellationTokenSource invocation)
    {
        var token = invocation.Token;

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
        finally
        {
            lock (_switchRequestSync)
            {
                if (ReferenceEquals(_displayChangeCts, invocation))
                {
                    _displayChangeCts = null;
                    _activeDisplayChange = Task.CompletedTask;
                }
            }
            invocation.Dispose();
        }
    }

    /// <summary>Restores a workspace and shows a completion balloon.</summary>
    public Task RestoreWorkspaceAsync(
        WorkspaceSnapshot snapshot,
        CancellationToken token = default,
        IProgress<RestoreProgressReport>? progress = null) =>
        ExecuteRestoreAndNotifyAsync(
            snapshot,
            RestoreMode.Resume,
            "Requested a workspace restore",
            "restore",
            "Restoring…",
            RestoreStartMessage(snapshot.Name, "restoring windows"),
            "Workspace Restored",
            $"“{snapshot.Name}” restored — other open windows were left untouched. " +
            "Use “Switch to Workspace” to close everything else first.",
            "Restore Needs Attention",
            "The workspace restore did not complete cleanly. Open the preview for details.",
            token,
            progress);

    /// <summary>
    /// Restores a workspace and then minimizes every window that is not part of it (nothing is
    /// closed). This brings the workspace's own windows to their saved positions and clears away
    /// unrelated windows — a non-destructive middle ground between Restore (leaves other windows
    /// untouched) and Switch (closes everything else first).
    /// </summary>
    public Task AlignAndMinimizeOthersAsync(
        WorkspaceSnapshot snapshot,
        CancellationToken token = default,
        IProgress<RestoreProgressReport>? progress = null) =>
        ExecuteRestoreAndNotifyAsync(
            snapshot,
            RestoreMode.AlignAndMinimize,
            "Requested workspace alignment and minimization",
            "align_and_minimize",
            "Aligning…",
            RestoreStartMessage(snapshot.Name, "positioning windows"),
            "Workspace Aligned",
            $"“{snapshot.Name}” — other windows were minimized, not closed.",
            "Alignment Needs Attention",
            "The workspace could not be fully aligned. Open the preview for details.",
            token,
            progress);

    /// <summary>
    /// Restores only entries on the specified monitors and shows a completion balloon.
    /// When <paramref name="monitorIds"/> is <c>null</c> all monitors are restored.
    /// </summary>
    public Task RestoreWorkspaceSelectiveAsync(
        WorkspaceSnapshot snapshot,
        HashSet<string>? monitorIds,
        CancellationToken token = default,
        IProgress<RestoreProgressReport>? progress = null)
    {
        string desc = monitorIds == null ? "all monitors" : $"{monitorIds.Count} monitor(s)";
        RestoreMode mode = monitorIds is null
            ? RestoreMode.Standard
            : RestoreMode.Selective(monitorIds.ToArray());
        return ExecuteRestoreAndNotifyAsync(
            snapshot,
            mode,
            "Requested a selective workspace restore",
            "selective",
            "Restoring…",
            RestoreStartMessage($"{snapshot.Name} ({desc})", "restoring windows"),
            "Workspace Restored",
            $"“{snapshot.Name}” ({desc}) — restored successfully.",
            "Restore Needs Attention",
            "The selective restore did not complete cleanly. Open the preview for details.",
            token,
            progress,
            LogField.Public("monitorCount", monitorIds?.Count));
    }

    private async Task ExecuteRestoreAndNotifyAsync(
        WorkspaceSnapshot snapshot,
        RestoreMode mode,
        string requestMessage,
        string modeName,
        string startedTitle,
        string startedMessage,
        string successTitle,
        string successMessage,
        string failureTitle,
        string failureMessage,
        CancellationToken token,
        IProgress<RestoreProgressReport>? progress,
        params LogField[] additionalLogFields)
    {
        LogField[] logFields =
        [
            LogField.Identifier("workspaceId", snapshot.WorkspaceId),
            LogField.Workspace("workspaceName", snapshot.Name),
            LogField.Public("mode", modeName),
            .. additionalLogFields
        ];
        AppLogger.Info("layout.restore_requested", requestMessage, logFields);
        NotifyBalloon(startedTitle, startedMessage);

        RestoreExecutionResult result = await _workspaceService.RestoreWorkspaceWithExecutionResultAsync(
            snapshot,
            mode,
            token,
            progress);
        if (token.IsCancellationRequested || result.WasCancelled || NotifyCheckpointFailure(result))
            return;

        if (result.Status == RestoreExecutionStatus.Completed)
            NotifyBalloon(successTitle, successMessage);
        else
            NotifyBalloon(
                failureTitle,
                failureMessage,
                H.NotifyIcon.Core.NotificationIcon.Warning);
    }

    /// <summary>Builds the immutable plan shown by the manual restore preview.</summary>
    public RestorePlan CreateRestorePlan(WorkspaceSnapshot snapshot, RestoreMode mode) =>
        _workspaceService.CreateRestorePlan(snapshot, mode);

    /// <summary>Current user preference for routine interactive restore previews.</summary>
    public bool RestorePreviewEnabled => _workspaceService.RestorePreviewEnabled;

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
            RestoreStartMessage(snapshot.Name, "executing the reviewed plan"));
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
            CreateRestorePlan(snapshot, RestoreMode.ExactSwitch),
            token,
            progress);

    /// <summary>
    /// Switches to the exact reviewed plan. Approved target-workspace HWNDs stay open; only
    /// unrelated close candidates receive WM_CLOSE and only those handles are polled.
    /// </summary>
    public Task<RestoreExecutionResult?> SwitchWorkspaceAsync(
        WorkspaceSnapshot snapshot,
        RestorePlan approvedPlan,
        CancellationToken token = default,
        IProgress<RestoreProgressReport>? progress = null) =>
        SwitchWorkspaceAsync(
            snapshot,
            approvedPlan,
            WorkspaceCheckpointTrigger.WorkspaceSwitch,
            isUndo: false,
            token,
            progress);

    private async Task<RestoreExecutionResult?> SwitchWorkspaceAsync(
        WorkspaceSnapshot snapshot,
        RestorePlan approvedPlan,
        WorkspaceCheckpointTrigger checkpointTrigger,
        bool isUndo,
        CancellationToken token,
        IProgress<RestoreProgressReport>? progress)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(approvedPlan);
        if (!string.Equals(snapshot.WorkspaceId, approvedPlan.WorkspaceId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The approved switch plan belongs to another workspace.", nameof(approvedPlan));

        using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationTokenSource? previous;
        lock (_switchRequestSync)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(LayoutCoordinator));
            previous = _activeSwitchRequest;
            _activeSwitchRequest = request;
        }
        if (previous is not null)
            await TryCancelAsync(previous).ConfigureAwait(false);

        try
        {
            return await SwitchWorkspaceCoreAsync(
                snapshot,
                approvedPlan,
                checkpointTrigger,
                isUndo,
                request.Token,
                progress);
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
        WorkspaceCheckpointTrigger checkpointTrigger,
        bool isUndo,
        CancellationToken token,
        IProgress<RestoreProgressReport>? progress)
    {
        if (!approvedPlan.CanExecute || approvedPlan.Actions.Count == 0)
        {
            NotifyBalloon(
                isUndo ? "Undo Needs Review" : "Switch Needs Review",
                isUndo
                    ? "The recovery checkpoint contains entries that require review before it can be restored."
                    : "Resolve blocking entries in the restore preview before switching.",
                H.NotifyIcon.Core.NotificationIcon.Warning);
            return null;
        }

        AppLogger.Info(
            isUndo ? "layout.undo_requested" : "layout.switch_requested",
            isUndo ? "Requested recovery-checkpoint reconciliation" : "Requested a workspace switch",
            LogField.Identifier("workspaceId", snapshot.WorkspaceId),
            LogField.Workspace("workspaceName", snapshot.Name),
            LogField.Public("approvedEntryCount", approvedPlan.Entries.Count - approvedPlan.DisabledEntryIndexes.Count));
        NotifyBalloon(
            isUndo ? "Undoing Restore…" : "Switching…",
            RestoreStartMessage(
                snapshot.Name,
                isUndo ? "restoring the previous desktop" : "closing unrelated windows"));

        HashSet<IntPtr> keep = GetSwitchKeepHandles(approvedPlan);

        CheckpointedOperationResult<WorkspaceSwitchResult> transaction =
            await _workspaceService.ExecuteCheckpointedOperationAsync(
                snapshot,
                checkpointTrigger,
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
            NotifyBalloon(isUndo ? "Undo Cancelled" : "Switch Cancelled",
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
                isUndo ? "Undo State Is Stale" : "Switch Preview Is Stale",
                isUndo
                    ? "The desktop changed while restoring the recovery checkpoint. Try Undo Last Restore again."
                    : "The desktop changed after review. Reopen the switch preview and try again.",
                H.NotifyIcon.Core.NotificationIcon.Warning);
        }
        else if (restoreResult.Status is RestoreExecutionStatus.CompletedWithFailures or
                 RestoreExecutionStatus.Rejected)
        {
            int placementFailures = restoreResult.PlacementFailures.Count;
            NotifyBalloon(
                isUndo ? "Undo Needs Attention" : "Switch Needs Attention",
                placementFailures > 0
                    ? $"{placementFailures} window placement{(placementFailures == 1 ? "" : "s")} " +
                      "could not be verified after bounded retries."
                    : isUndo
                        ? "The previous desktop state could not be fully restored."
                        : "The reviewed workspace plan could not be completed.",
                H.NotifyIcon.Core.NotificationIcon.Warning);
        }
        else
        {
            NotifyBalloon(
                isUndo ? "Restore Undone" : "Workspace Switched",
                isUndo
                    ? "The previous desktop state was reconciled, including closing unrelated windows."
                    : $"Switched to \u201c{snapshot.Name}\u201d \u2014 {snapshot.Entries.Count} entries processed.");
        }
        return restoreResult;
    }

    internal static HashSet<IntPtr> GetSwitchKeepHandles(RestorePlan approvedPlan)
    {
        ArgumentNullException.ThrowIfNull(approvedPlan);
        HashSet<IntPtr> keep = approvedPlan.Actions
            .Where(action => action.Kind == RestoreActionKind.RestoreExistingWindow &&
                action.WindowHandle.HasValue)
            .Select(action => new IntPtr(action.WindowHandle!.Value))
            .ToHashSet();
        keep.UnionWith(approvedPlan.ProtectedWindowHandles.Select(handle => new IntPtr(handle)));
        return keep;
    }

    /// <summary>True when a healthy recovery checkpoint makes undo available.</summary>
    public bool CanUndoLastRestore => _workspaceService.CanUndoLastRestore;

    /// <summary>
    /// Reconciles the desktop to the latest checkpoint, including closing windows that were not
    /// present before the original restore. A new checkpoint is created first when enabled.
    /// </summary>
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

        WorkspaceSnapshot? checkpoint = _workspaceService.GetLatestRestoreCheckpoint();
        if (checkpoint is null) return null;
        RestorePlan plan = CreateRestorePlan(checkpoint, RestoreMode.ExactSwitch);
        return await SwitchWorkspaceAsync(
            checkpoint,
            plan,
            WorkspaceCheckpointTrigger.Undo,
            isUndo: true,
            token,
            progress);
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? switchRequest = null;
        CancellationTokenSource? displayChange = null;
        Task displayChangeTask = Task.CompletedTask;
        TaskCompletionSource completion;
        bool ownsDisposal;
        lock (_switchRequestSync)
        {
            if (_disposeCompletion is not null)
            {
                completion = _disposeCompletion;
                ownsDisposal = false;
            }
            else
            {
                _disposed = true;
                switchRequest = _activeSwitchRequest;
                _activeSwitchRequest = null;
                displayChange = _displayChangeCts;
                _displayChangeCts = null;
                displayChangeTask = _activeDisplayChange;
                _activeDisplayChange = Task.CompletedTask;
                completion = _disposeCompletion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                ownsDisposal = true;
            }
        }

        if (!ownsDisposal)
        {
            await completion.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            if (switchRequest is not null)
                await TryCancelAsync(switchRequest).ConfigureAwait(false);
            if (displayChange is not null)
                await TryCancelAsync(displayChange).ConfigureAwait(false);
            await displayChangeTask.ConfigureAwait(false);
            await _switchEngine.DisposeAsync().ConfigureAwait(false);
            // Each operation owns and disposes its token source after observing cancellation.
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
            throw;
        }
    }

    private static async ValueTask TryCancelAsync(CancellationTokenSource source)
    {
        try { await source.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
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

    private string RestoreStartMessage(string workspaceName, string nextAction) =>
        _workspaceService.RestoreCheckpointsEnabled
            ? $"“{workspaceName}” — creating a recovery checkpoint before {nextAction}."
            : $"“{workspaceName}” — {nextAction}; recovery checkpoints are disabled.";

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
