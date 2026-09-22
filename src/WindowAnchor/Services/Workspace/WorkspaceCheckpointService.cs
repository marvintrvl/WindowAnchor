using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Owns the serialized checkpoint gate and bounded pre-restore recovery capture.</summary>
internal sealed class WorkspaceCheckpointService : IAsyncDisposable
{
    private readonly StorageService _storage;
    private readonly SettingsService? _settings;
    private readonly WorkspaceCaptureBuilder _captureBuilder;
    private readonly WorkspaceSnapshotBuilder _snapshotBuilder;
    private readonly RestoreTransactionCoordinator _transactions = new();

    internal WorkspaceCheckpointService(
        StorageService storage,
        SettingsService? settings,
        WorkspaceCaptureBuilder captureBuilder,
        WorkspaceSnapshotBuilder snapshotBuilder)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _settings = settings;
        _captureBuilder = captureBuilder ?? throw new ArgumentNullException(nameof(captureBuilder));
        _snapshotBuilder = snapshotBuilder ?? throw new ArgumentNullException(nameof(snapshotBuilder));
    }

    internal bool RoutineCheckpointsEnabled => _settings?.Settings.CreateRestoreCheckpoints ?? false;

    internal bool ShouldCreate(WorkspaceCheckpointTrigger trigger) =>
        trigger is WorkspaceCheckpointTrigger.WorkspaceSwitch or WorkspaceCheckpointTrigger.Undo ||
        RoutineCheckpointsEnabled;

    internal WorkspaceSnapshot? GetLatest() => _storage.Checkpoints.GetLatest();

    internal async Task<CheckpointedOperationResult<T>> ExecuteAsync<T>(
        WorkspaceSnapshot targetSnapshot,
        WorkspaceCheckpointTrigger trigger,
        Func<RestoreCheckpointOutcome, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken,
        IProgress<RestoreProgressReport>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(targetSnapshot);
        ArgumentNullException.ThrowIfNull(operation);
        return await _transactions.ExecuteAsync(
            trigger,
            (token, operationProgress) => ShouldCreate(trigger)
                ? CreatePreRestoreCheckpointAsync(targetSnapshot, trigger, token, operationProgress)
                : SkipPreRestoreCheckpointAsync(trigger),
            operation,
            cancellationToken,
            progress).ConfigureAwait(false);
    }

    internal RestoreExecutionResult CreateAbortedResult(
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
                cancelled ? RestoreExecutionActionStatus.Cancelled : RestoreExecutionActionStatus.Skipped,
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

    public ValueTask DisposeAsync() => _transactions.DisposeAsync();

    private async Task<RestoreCheckpointOutcome> CreatePreRestoreCheckpointAsync(
        WorkspaceSnapshot targetSnapshot,
        WorkspaceCheckpointTrigger trigger,
        CancellationToken cancellationToken,
        IProgress<RestoreProgressReport>? progress)
    {
        Stopwatch timer = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new RestoreProgressReport(
                RestoreProgressStage.PreparingCheckpoint,
                "Creating recovery checkpoint",
                "Capturing the current desktop before making changes.",
                Elapsed: timer.Elapsed));
            IProgress<SaveProgressReport>? captureProgress = progress is null
                ? null
                : new CallbackProgress<SaveProgressReport>(report =>
                    progress.Report(MapProgress(report, timer.Elapsed)));
            WorkspaceCaptureResult capture = await _captureBuilder.CaptureAsync(
                new WorkspaceCaptureRequest(
                    $"Recovery before {trigger}: {targetSnapshot.Name}",
                    SaveFiles: true,
                    MonitorIds: null,
                    Progress: captureProgress,
                    SelectedWindows: null,
                    CaptureBrowserSessions: true,
                    SearchCommonFolders: false,
                    CommonFolderSearchBudget: null,
                    CancellationToken: cancellationToken,
                    BuildFullJumpListCache: false,
                    BrowserCaptureBudget: TimeSpan.FromMilliseconds(500)),
                _snapshotBuilder.Build).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            string targetWorkspaceId = Guid.TryParse(targetSnapshot.WorkspaceId, out _)
                ? targetSnapshot.WorkspaceId
                : "";
            progress?.Report(new RestoreProgressReport(
                RestoreProgressStage.SavingCheckpoint,
                "Saving recovery checkpoint",
                "Writing the checkpoint atomically.",
                capture.Snapshot.Entries.Count,
                capture.Snapshot.Entries.Count,
                timer.Elapsed));
            CheckpointSaveReceipt receipt = _storage.Checkpoints.Save(
                capture.Snapshot,
                trigger,
                targetWorkspaceId);
            timer.Stop();
            AppLogger.Info(
                "checkpoint.pre_restore_created",
                "Created a durable pre-restore checkpoint",
                LogField.Identifier("checkpointId", receipt.CheckpointId),
                LogField.Identifier("targetWorkspaceId", targetWorkspaceId),
                LogField.Public("trigger", trigger),
                LogField.Public("entryCount", capture.Snapshot.Entries.Count),
                LogField.Public("durationMs", timer.Elapsed.TotalMilliseconds),
                LogField.Public("recursiveFileSearch", false),
                LogField.Public("jumpListSearch", false));
            return new RestoreCheckpointOutcome(
                RestoreCheckpointStatus.Created,
                trigger,
                receipt.CheckpointId,
                receipt.CreatedAtUtc,
                "A recovery checkpoint was created before desktop mutation.",
                timer.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            timer.Stop();
            return RestoreTransactionCoordinator.CancelledCheckpoint(trigger) with { Duration = timer.Elapsed };
        }
        catch (Exception exception)
        {
            timer.Stop();
            AppLogger.Error(
                "checkpoint.pre_restore_failed",
                "Could not create the required pre-restore checkpoint; desktop mutation was blocked",
                exception,
                LogField.Identifier("targetWorkspaceId", targetSnapshot.WorkspaceId),
                LogField.Public("trigger", trigger),
                LogField.Public("durationMs", timer.Elapsed.TotalMilliseconds),
                LogField.Public("errorCategory", "checkpoint_creation"));
            return new RestoreCheckpointOutcome(
                RestoreCheckpointStatus.Failed,
                trigger,
                null,
                null,
                "The recovery checkpoint could not be saved, so no restore action was applied.",
                timer.Elapsed);
        }
    }

    private static Task<RestoreCheckpointOutcome> SkipPreRestoreCheckpointAsync(
        WorkspaceCheckpointTrigger trigger)
    {
        AppLogger.Info(
            "checkpoint.pre_restore_disabled",
            "Skipped the pre-restore checkpoint because it is disabled in settings",
            LogField.Public("trigger", trigger));
        return Task.FromResult(RestoreTransactionCoordinator.DisabledCheckpoint(trigger));
    }

    private static RestoreProgressReport MapProgress(SaveProgressReport report, TimeSpan elapsed)
    {
        RestoreProgressStage stage = report.Stage switch
        {
            WorkspaceCaptureProgressStage.DetectingResources or
            WorkspaceCaptureProgressStage.SearchingCommonFolders => RestoreProgressStage.DetectingResources,
            WorkspaceCaptureProgressStage.CapturingBrowserSession => RestoreProgressStage.CapturingBrowserSession,
            WorkspaceCaptureProgressStage.Finalizing => RestoreProgressStage.SavingCheckpoint,
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
            stage, message, report.Detail, report.Current, report.Total, elapsed);
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        private readonly Action<T> _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        public void Report(T value) => _callback(value);
    }
}
