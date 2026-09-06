using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WindowAnchor.Services;

/// <summary>Native-window operations required by the safe switch close phase.</summary>
public interface IWorkspaceSwitchWindowController
{
    IReadOnlyList<ObservedWindow> InspectUserWindows(WindowCandidatePolicy policy);
    IReadOnlySet<IntPtr> RequestCloseUserWindowsExcept(
        WindowCandidatePolicy policy,
        IReadOnlySet<IntPtr> keep);
    bool IsWindowAlive(IntPtr hWnd);
}

internal enum WorkspaceSwitchProgressKind
{
    PreflightCompleted,
    CloseRequested,
    WaitingForClose
}

internal sealed record WorkspaceSwitchProgress(
    WorkspaceSwitchProgressKind Kind,
    int WindowCount,
    bool ShouldNotifyUser = false,
    TimeSpan? Elapsed = null,
    TimeSpan? Timeout = null);

internal enum WorkspaceSwitchStatus
{
    Completed,
    Cancelled,
    TimedOut
}

internal sealed record WorkspaceSwitchResult(
    WorkspaceSwitchStatus Status,
    int RiskWindowCount,
    int RequestedCloseCount,
    IReadOnlyList<IntPtr> RemainingWindowHandles,
    RestoreExecutionResult? RestoreResult = null);

/// <summary>
/// Owns one switch close session at a time. A newer request cancels and then waits for the
/// previous request, polls only HWNDs that actually received WM_CLOSE, and measures a real
/// wall-clock timeout.
/// </summary>
internal sealed class WorkspaceSwitchEngine : IAsyncDisposable
{
    private readonly IWorkspaceSwitchWindowController _windows;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _notificationInterval;
    private readonly SemaphoreSlim _singleFlight = new(1, 1);
    private readonly object _sync = new();
    private CancellationTokenSource? _activeSwitch;
    private TaskCompletionSource? _disposeCompletion;
    private bool _disposed;

    internal WorkspaceSwitchEngine(
        IWorkspaceSwitchWindowController windows,
        TimeSpan? pollInterval = null,
        TimeSpan? timeout = null,
        TimeSpan? notificationInterval = null)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);
        _timeout = timeout ?? TimeSpan.FromMinutes(2);
        _notificationInterval = notificationInterval ?? TimeSpan.FromSeconds(30);
    }

    internal async Task<WorkspaceSwitchResult> ExecuteAsync(
        IReadOnlySet<IntPtr> keep,
        Func<CancellationToken, Task<RestoreExecutionResult>> restore,
        Action<WorkspaceSwitchProgress>? reportProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keep);
        ArgumentNullException.ThrowIfNull(restore);

        var switchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? previous;
        lock (_sync)
        {
            if (_disposed)
            {
                switchCancellation.Dispose();
                throw new ObjectDisposedException(nameof(WorkspaceSwitchEngine));
            }
            previous = _activeSwitch;
            _activeSwitch = switchCancellation;
        }
        if (previous is not null)
            await TryCancelAsync(previous).ConfigureAwait(false);

        bool entered = false;
        try
        {
            await _singleFlight.WaitAsync(switchCancellation.Token);
            entered = true;
            CancellationToken token = switchCancellation.Token;

            int riskCount = _windows.InspectUserWindows(
                WindowCandidatePolicy.SwitchRiskCandidate).Count;
            reportProgress?.Invoke(new WorkspaceSwitchProgress(
                WorkspaceSwitchProgressKind.PreflightCompleted,
                riskCount));

            IReadOnlySet<IntPtr> requested = _windows.RequestCloseUserWindowsExcept(
                WindowCandidatePolicy.SwitchCloseCandidate,
                keep);
            reportProgress?.Invoke(new WorkspaceSwitchProgress(
                WorkspaceSwitchProgressKind.CloseRequested,
                requested.Count));

            if (requested.Count > 0)
            {
                var stopwatch = Stopwatch.StartNew();
                TimeSpan lastNotification = -_notificationInterval;
                TimeSpan lastProgress = TimeSpan.FromSeconds(-1);
                int lastRemaining = -1;

                while (stopwatch.Elapsed < _timeout)
                {
                    await Task.Delay(_pollInterval, token);
                    IntPtr[] remaining = requested
                        .Where(_windows.IsWindowAlive)
                        .OrderBy(handle => handle.ToInt64())
                        .ToArray();
                    if (remaining.Length == 0)
                        break;

                    bool shouldReport = remaining.Length != lastRemaining ||
                        stopwatch.Elapsed - lastProgress >= TimeSpan.FromSeconds(1);
                    if (shouldReport)
                    {
                        bool notify = stopwatch.Elapsed - lastNotification >= _notificationInterval;
                        if (notify)
                            lastNotification = stopwatch.Elapsed;
                        reportProgress?.Invoke(new WorkspaceSwitchProgress(
                            WorkspaceSwitchProgressKind.WaitingForClose,
                            remaining.Length,
                            notify,
                            stopwatch.Elapsed,
                            _timeout));
                        lastRemaining = remaining.Length;
                        lastProgress = stopwatch.Elapsed;
                    }
                }

                IntPtr[] finalRemaining = requested
                    .Where(_windows.IsWindowAlive)
                    .OrderBy(handle => handle.ToInt64())
                    .ToArray();
                if (finalRemaining.Length > 0)
                {
                    return new WorkspaceSwitchResult(
                        WorkspaceSwitchStatus.TimedOut,
                        riskCount,
                        requested.Count,
                        finalRemaining);
                }
            }

            RestoreExecutionResult restoreResult = await restore(token);
            return new WorkspaceSwitchResult(
                WorkspaceSwitchStatus.Completed,
                riskCount,
                requested.Count,
                Array.Empty<IntPtr>(),
                restoreResult);
        }
        catch (OperationCanceledException) when (switchCancellation.IsCancellationRequested)
        {
            return new WorkspaceSwitchResult(
                WorkspaceSwitchStatus.Cancelled,
                0,
                0,
                Array.Empty<IntPtr>());
        }
        finally
        {
            if (entered)
                _singleFlight.Release();
            lock (_sync)
            {
                if (ReferenceEquals(_activeSwitch, switchCancellation))
                    _activeSwitch = null;
            }
            switchCancellation.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? active = null;
        TaskCompletionSource completion;
        bool ownsDisposal;
        lock (_sync)
        {
            if (_disposeCompletion is not null)
            {
                completion = _disposeCompletion;
                ownsDisposal = false;
            }
            else
            {
                _disposed = true;
                active = _activeSwitch;
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
            if (active is not null)
            {
                try { await active.CancelAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { }
            }
            await _singleFlight.WaitAsync().ConfigureAwait(false);
            _singleFlight.Release();
            _singleFlight.Dispose();
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
}
