using System;
using System.Threading;
using System.Threading.Tasks;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>
/// Owns the single-flight durability gate around a restore-related operation.
/// Checkpoint construction stays injectable so WorkspaceService retains capture-specific policy,
/// while this type owns admission, cancellation, and guaranteed gate release.
/// </summary>
internal sealed class RestoreTransactionCoordinator : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _lifetimeSync = new();
    private TaskCompletionSource? _drained;
    private TaskCompletionSource? _disposeCompletion;
    private int _activeCalls;
    private bool _disposed;

    internal async Task<CheckpointedOperationResult<T>> ExecuteAsync<T>(
        WorkspaceCheckpointTrigger trigger,
        Func<CancellationToken, IProgress<RestoreProgressReport>?, Task<RestoreCheckpointOutcome>> createCheckpoint,
        Func<RestoreCheckpointOutcome, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken,
        IProgress<RestoreProgressReport>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(createCheckpoint);
        ArgumentNullException.ThrowIfNull(operation);

        CancellationTokenSource operationCancellation;
        lock (_lifetimeSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCancellation.Token);
            _activeCalls++;
        }

        try
        {
            try
            {
                await _gate.WaitAsync(operationCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
            {
                return new CheckpointedOperationResult<T>(
                    CancelledCheckpoint(trigger),
                    false,
                    default);
            }

            try
            {
                RestoreCheckpointOutcome checkpoint = await createCheckpoint(
                    operationCancellation.Token,
                    progress).ConfigureAwait(false);
                if (!checkpoint.AllowsOperation)
                    return new CheckpointedOperationResult<T>(checkpoint, false, default);

                T value = await operation(
                    checkpoint,
                    operationCancellation.Token).ConfigureAwait(false);
                return new CheckpointedOperationResult<T>(checkpoint, true, value);
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            operationCancellation.Dispose();
            lock (_lifetimeSync)
            {
                _activeCalls--;
                if (_activeCalls == 0)
                    _drained?.TrySetResult();
            }
        }
    }

    internal static RestoreCheckpointOutcome CancelledCheckpoint(
        WorkspaceCheckpointTrigger trigger) =>
        new(
            RestoreCheckpointStatus.Cancelled,
            trigger,
            null,
            null,
            "Checkpoint creation was cancelled before desktop mutation began.");

    internal static RestoreCheckpointOutcome DisabledCheckpoint(
        WorkspaceCheckpointTrigger trigger) =>
        new(
            RestoreCheckpointStatus.Disabled,
            trigger,
            null,
            null,
            "Recovery checkpoint creation is disabled in Settings.");

    public async ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        Task drained;
        bool ownsDisposal;
        lock (_lifetimeSync)
        {
            if (_disposeCompletion is not null)
            {
                completion = _disposeCompletion;
                drained = Task.CompletedTask;
                ownsDisposal = false;
            }
            else
            {
                _disposed = true;
                completion = _disposeCompletion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                drained = _activeCalls == 0
                    ? Task.CompletedTask
                    : (_drained = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously)).Task;
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
            await _lifetimeCancellation.CancelAsync().ConfigureAwait(false);
            await drained.ConfigureAwait(false);
            _gate.Dispose();
            _lifetimeCancellation.Dispose();
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
            throw;
        }
    }
}
