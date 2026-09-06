using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class Phase4ExtractionTests
{
    [Fact]
    public void Workspace_order_policy_keeps_preferred_ids_then_appends_newest_unlisted()
    {
        WorkspaceSnapshot older = Snapshot("older", DateTimeOffset.UtcNow.AddMinutes(-10));
        WorkspaceSnapshot preferred = Snapshot("preferred", DateTimeOffset.UtcNow.AddMinutes(-20));
        WorkspaceSnapshot newest = Snapshot("newest", DateTimeOffset.UtcNow);

        List<WorkspaceSnapshot> ordered = WorkspaceOrderPolicy.Order(
            [older, preferred, newest],
            [preferred.WorkspaceId]);

        Assert.Equal(
            [preferred.WorkspaceId, newest.WorkspaceId, older.WorkspaceId],
            ordered.Select(workspace => workspace.WorkspaceId));
    }

    [Fact]
    public async Task Transaction_coordinator_blocks_operation_when_checkpoint_fails()
    {
        await using var coordinator = new RestoreTransactionCoordinator();
        bool operationStarted = false;

        CheckpointedOperationResult<string> result = await coordinator.ExecuteAsync(
            WorkspaceCheckpointTrigger.Restore,
            (_, _) => Task.FromResult(new RestoreCheckpointOutcome(
                RestoreCheckpointStatus.Failed,
                WorkspaceCheckpointTrigger.Restore,
                null,
                null,
                "checkpoint failed")),
            (_, _) =>
            {
                operationStarted = true;
                return Task.FromResult("unexpected");
            },
            CancellationToken.None);

        Assert.False(result.OperationStarted);
        Assert.False(operationStarted);
        Assert.Equal(RestoreCheckpointStatus.Failed, result.Checkpoint.Status);
    }

    [Fact]
    public async Task Transaction_coordinator_runs_serialized_operation_when_checkpoint_is_disabled()
    {
        await using var coordinator = new RestoreTransactionCoordinator();

        CheckpointedOperationResult<string> result = await coordinator.ExecuteAsync(
            WorkspaceCheckpointTrigger.Restore,
            (_, _) => Task.FromResult(
                RestoreTransactionCoordinator.DisabledCheckpoint(
                    WorkspaceCheckpointTrigger.Restore)),
            (checkpoint, _) => Task.FromResult(
                checkpoint.AllowsOperation ? "executed" : "blocked"),
            CancellationToken.None);

        Assert.True(result.OperationStarted);
        Assert.Equal("executed", result.Value);
        Assert.Equal(RestoreCheckpointStatus.Disabled, result.Checkpoint.Status);
        Assert.False(result.Checkpoint.IsCreated);
    }

    [Fact]
    public async Task Transaction_coordinator_disposal_cancels_and_drains_active_operation()
    {
        var coordinator = new RestoreTransactionCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<CheckpointedOperationResult<string>> execution = coordinator.ExecuteAsync(
            WorkspaceCheckpointTrigger.Restore,
            (_, _) => Task.FromResult(new RestoreCheckpointOutcome(
                RestoreCheckpointStatus.Created,
                WorkspaceCheckpointTrigger.Restore,
                "checkpoint",
                DateTime.UtcNow,
                "created")),
            async (_, token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "unexpected";
            },
            CancellationToken.None);

        await started.Task;
        await coordinator.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => coordinator.ExecuteAsync<string>(
            WorkspaceCheckpointTrigger.Restore,
            (_, _) => throw new InvalidOperationException(),
            (_, _) => throw new InvalidOperationException(),
            CancellationToken.None));
    }

    [Fact]
    public async Task Transaction_coordinator_cancels_a_queued_call_before_its_checkpoint_starts()
    {
        await using var coordinator = new RestoreTransactionCoordinator();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<CheckpointedOperationResult<string>> first = coordinator.ExecuteAsync(
            WorkspaceCheckpointTrigger.Restore,
            (_, _) => Task.FromResult(CreatedCheckpoint()),
            async (_, token) =>
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(token);
                return "first";
            },
            CancellationToken.None);
        await firstStarted.Task;

        bool secondCheckpointStarted = false;
        using var queuedCancellation = new CancellationTokenSource();
        Task<CheckpointedOperationResult<string>> second = coordinator.ExecuteAsync(
            WorkspaceCheckpointTrigger.WorkspaceSwitch,
            (_, _) =>
            {
                secondCheckpointStarted = true;
                return Task.FromResult(CreatedCheckpoint(WorkspaceCheckpointTrigger.WorkspaceSwitch));
            },
            (_, _) => Task.FromResult("second"),
            queuedCancellation.Token);
        queuedCancellation.Cancel();

        CheckpointedOperationResult<string> cancelled = await second;
        Assert.False(cancelled.OperationStarted);
        Assert.Equal(RestoreCheckpointStatus.Cancelled, cancelled.Checkpoint.Status);
        Assert.False(secondCheckpointStarted);

        releaseFirst.TrySetResult();
        CheckpointedOperationResult<string> completed = await first;
        Assert.True(completed.OperationStarted);
        Assert.Equal("first", completed.Value);
    }

    [Fact]
    public async Task Transaction_coordinator_shutdown_during_checkpoint_cancels_and_drains_it()
    {
        var coordinator = new RestoreTransactionCoordinator();
        var checkpointStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool operationStarted = false;

        Task<CheckpointedOperationResult<string>> execution = coordinator.ExecuteAsync(
            WorkspaceCheckpointTrigger.Restore,
            async (token, _) =>
            {
                checkpointStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return CreatedCheckpoint();
            },
            (_, _) =>
            {
                operationStarted = true;
                return Task.FromResult("unexpected");
            },
            CancellationToken.None);
        await checkpointStarted.Task;

        await coordinator.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.False(operationStarted);
    }

    [Fact]
    public async Task Transaction_coordinator_allows_concurrent_disposal_while_execution_is_active()
    {
        var coordinator = new RestoreTransactionCoordinator();
        var operationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CheckpointedOperationResult<string>> execution = coordinator.ExecuteAsync(
            WorkspaceCheckpointTrigger.Restore,
            (_, _) => Task.FromResult(CreatedCheckpoint()),
            async (_, token) =>
            {
                operationStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "unexpected";
            },
            CancellationToken.None);
        await operationStarted.Task;

        Task firstDisposal = coordinator.DisposeAsync().AsTask();
        Task secondDisposal = coordinator.DisposeAsync().AsTask();
        await Task.WhenAll(firstDisposal, secondDisposal);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => coordinator.ExecuteAsync<string>(
            WorkspaceCheckpointTrigger.Restore,
            (_, _) => throw new InvalidOperationException(),
            (_, _) => throw new InvalidOperationException(),
            CancellationToken.None));
    }

    [Fact]
    public async Task Capture_builder_preserves_disabled_browser_capture_contract()
    {
        var builder = new WorkspaceCaptureBuilder(browserSessionConnector: null);
        WorkspaceSnapshot snapshot = Snapshot("capture", DateTimeOffset.UtcNow);

        WorkspaceCaptureResult result = await builder.CaptureAsync(
            new WorkspaceCaptureRequest(
                snapshot.Name,
                SaveFiles: false,
                MonitorIds: null,
                Progress: null,
                SelectedWindows: null,
                CaptureBrowserSessions: false,
                SearchCommonFolders: false,
                CommonFolderSearchBudget: null,
                CancellationToken: CancellationToken.None,
                BuildFullJumpListCache: false),
            _ => snapshot);

        Assert.Equal(BrowserCaptureStatus.Skipped, result.BrowserCapture.Status);
        Assert.Empty(result.Snapshot.BrowserSessions);
    }

    private static WorkspaceSnapshot Snapshot(string id, DateTimeOffset savedAt) => new()
    {
        WorkspaceId = id,
        Name = id,
        SavedAt = savedAt.UtcDateTime
    };

    private static RestoreCheckpointOutcome CreatedCheckpoint(
        WorkspaceCheckpointTrigger trigger = WorkspaceCheckpointTrigger.Restore) => new(
            RestoreCheckpointStatus.Created,
            trigger,
            "checkpoint",
            DateTime.UtcNow,
            "created");
}
