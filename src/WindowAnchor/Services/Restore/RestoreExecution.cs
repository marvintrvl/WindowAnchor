using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WindowAnchor.Services;

/// <summary>Overall outcome of executing one approved restore plan.</summary>
public enum RestoreExecutionStatus
{
    Completed,
    CompletedWithFailures,
    StalePlan,
    Cancelled,
    Rejected
}

/// <summary>Execution state of one action from the approved plan.</summary>
public enum RestoreExecutionActionStatus
{
    Succeeded,
    Failed,
    Skipped,
    Stale,
    Cancelled
}

/// <summary>Execution state of one saved entry after approved actions were processed.</summary>
public enum RestoreExecutionEntryStatus
{
    Pending,
    Excluded,
    Blocked,
    Cancelled,
    Restored,
    LaunchRequested,
    AwaitingWindow,
    Stale,
    Failed
}

/// <summary>Stable stages surfaced while a restore or workspace switch is executing.</summary>
public enum RestoreProgressStage
{
    PreparingCheckpoint,
    DetectingResources,
    CapturingBrowserSession,
    SavingCheckpoint,
    ClosingWindows,
    LaunchingApplications,
    WaitingForApplications,
    VerifyingPlacements,
    ObservingAndPlanning,
    PreparingPreview
}

/// <summary>
/// User-facing progress for one restore transaction. Titles and application names are intended
/// for the local UI; structured diagnostics continue to apply their normal sensitivity policy.
/// </summary>
public sealed record RestoreProgressReport(
    RestoreProgressStage Stage,
    string Message,
    string Detail = "",
    int Current = 0,
    int Total = 0,
    TimeSpan? Elapsed = null,
    TimeSpan? Timeout = null);

/// <summary>Measured duration for one executor stage. Recording is observational only.</summary>
public sealed record RestoreExecutionStageTiming(
    RestoreProgressStage Stage,
    TimeSpan Duration);

/// <summary>Timing facts captured while an approved plan is executed.</summary>
public sealed record RestoreExecutionTiming(
    TimeSpan TotalDuration,
    IReadOnlyList<RestoreExecutionStageTiming> Stages)
{
    public static RestoreExecutionTiming Empty { get; } = new(
        TimeSpan.Zero,
        Array.Empty<RestoreExecutionStageTiming>());
}

/// <summary>Why an approved action no longer matches current external state.</summary>
public enum RestorePlanStaleReason
{
    WindowClosed,
    WindowReplaced,
    WindowNoLongerEligible,
    WindowInventoryChanged,
    ResourceMissing,
    ResourceChanged,
    BrowserSessionUnavailable
}

/// <summary>Structured result for exactly one action in the approved plan.</summary>
public sealed record RestoreExecutionActionResult(
    int ActionIndex,
    int? EntryIndex,
    RestoreActionKind Kind,
    RestoreExecutionActionStatus Status,
    RestorePlanStaleReason? StaleReason,
    long? WindowHandle,
    string Explanation,
    AppReadinessState? ReadinessState = null,
    string? ReadinessStrategy = null,
    WindowPlacementVerificationState? PlacementVerification = null,
    int PlacementRetryCount = 0,
    string? PlacementVerificationStrategy = null,
    int? PlacementTolerancePixels = null);

/// <summary>Structured execution result for one saved entry.</summary>
public sealed record RestoreExecutionEntryResult(
    int EntryIndex,
    string EntryId,
    RestoreExecutionEntryStatus Status,
    long? AssignedWindowHandle,
    string Explanation,
    AppReadinessState? ReadinessState = null,
    string? ReadinessStrategy = null,
    WindowPlacementVerificationState? PlacementVerification = null,
    int PlacementRetryCount = 0,
    string? PlacementVerificationStrategy = null,
    int? PlacementTolerancePixels = null);

/// <summary>Structured outcome of executing an immutable restore plan.</summary>
public sealed record RestoreExecutionResult(
    string WorkspaceId,
    RestoreExecutionStatus Status,
    bool WasCancelled,
    IReadOnlyList<RestoreExecutionEntryResult> Entries,
    IReadOnlyList<RestoreExecutionActionResult> Actions,
    IReadOnlySet<long> AssignedWindowHandles)
{
    /// <summary>
    /// Durable pre-mutation checkpoint outcome. Null only for deliberately non-transactional
    /// low-level executor use or a plan that contained no executable actions.
    /// </summary>
    public RestoreCheckpointOutcome? Checkpoint { get; init; }

    /// <summary>Observed executor timings; they never add an execution wait or retry.</summary>
    public RestoreExecutionTiming Timing { get; init; } = RestoreExecutionTiming.Empty;

    public bool HasStalePlan => Status == RestoreExecutionStatus.StalePlan;

    public IReadOnlyList<RestoreExecutionEntryResult> PlacementFailures => Entries
        .Where(entry => entry.PlacementVerification is not null and not
            WindowPlacementVerificationState.Applied)
        .ToArray();
}

/// <summary>Accumulates executor timings without participating in restore control flow.</summary>
internal sealed class RestoreExecutionTimingCollector
{
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private readonly Dictionary<RestoreProgressStage, TimeSpan> _stages = new();
    private TimeSpan _priorDuration;

    internal IDisposable Measure(RestoreProgressStage stage) => new Scope(this, stage);

    internal void AddPriorStage(RestoreProgressStage stage, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return;

        _priorDuration += duration;
        Add(stage, duration);
    }

    internal RestoreExecutionTiming Snapshot() => new(
        _total.Elapsed + _priorDuration,
        _stages
            .OrderBy(item => item.Key)
            .Select(item => new RestoreExecutionStageTiming(item.Key, item.Value))
            .ToArray());

    private void Add(RestoreProgressStage stage, TimeSpan elapsed)
    {
        _stages[stage] = _stages.TryGetValue(stage, out TimeSpan existing)
            ? existing + elapsed
            : elapsed;
    }

    private sealed class Scope : IDisposable
    {
        private readonly RestoreExecutionTimingCollector _owner;
        private readonly RestoreProgressStage _stage;
        private readonly Stopwatch _timer = Stopwatch.StartNew();
        private bool _disposed;

        internal Scope(RestoreExecutionTimingCollector owner, RestoreProgressStage stage)
        {
            _owner = owner;
            _stage = stage;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _timer.Stop();
            _owner.Add(_stage, _timer.Elapsed);
        }
    }
}

/// <summary>Result of immediately revalidating a launch target.</summary>
public sealed record RestoreResourceValidation(
    RestoreResourceAvailability Availability,
    string Explanation)
{
    public bool IsAvailable => Availability == RestoreResourceAvailability.Available;
}

/// <summary>Process launch boundary used only by <see cref="RestoreExecutor"/>.</summary>
public interface IRestoreProcessLauncher
{
    void Launch(RestoreAction action);
}

/// <summary>Cancellation-aware delay boundary used by deterministic readiness polling.</summary>
public interface IRestoreClock
{
    long GetTimestamp();
    TimeSpan GetElapsedTime(long startingTimestamp);
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>
/// Read-only resource observation and immediate pre-launch revalidation boundary.
/// </summary>
public interface IRestoreResourceBoundary
{
    RestoreResourceObservation Observe(
        int entryIndex,
        RestoreResourceKind kind,
        string target);

    RestoreResourceValidation Revalidate(RestoreAction action);
}

/// <summary>Production process launcher backed by <see cref="Process.Start(ProcessStartInfo)"/>.</summary>
public sealed class SystemRestoreProcessLauncher : IRestoreProcessLauncher
{
    public void Launch(RestoreAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Process.Start(new ProcessStartInfo
        {
            FileName = action.Target,
            Arguments = action.Arguments,
            UseShellExecute = action.UseShellExecute
        });
    }
}

/// <summary>Production readiness clock backed by <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</summary>
public sealed class SystemRestoreClock : IRestoreClock
{
    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public TimeSpan GetElapsedTime(long startingTimestamp) =>
        Stopwatch.GetElapsedTime(startingTimestamp);

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

/// <summary>Production read-only validation for executable, document, folder, and URL targets.</summary>
public sealed class FileSystemRestoreResourceBoundary : IRestoreResourceBoundary
{
    public RestoreResourceObservation Observe(
        int entryIndex,
        RestoreResourceKind kind,
        string target)
    {
        RestoreResourceValidation validation = ValidateTarget(target);
        string resolvedTarget = validation.IsAvailable ? target : "";
        if (!validation.IsAvailable &&
            kind == RestoreResourceKind.Executable &&
            VersionedExecutableResolver.TryResolve(target, out string currentExecutable))
        {
            validation = Available(
                "The saved Squirrel app version changed; the newest installed version was resolved safely.");
            resolvedTarget = currentExecutable;
            AppLogger.Info(
                "restore.executable_version_resolved",
                "Resolved a missing versioned executable to the newest installed sibling",
                LogField.Path("savedExecutablePath", target),
                LogField.Path("resolvedExecutablePath", currentExecutable));
        }
        return new RestoreResourceObservation(
            entryIndex,
            kind,
            validation.Availability,
            resolvedTarget);
    }

    public RestoreResourceValidation Revalidate(RestoreAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.Kind == RestoreActionKind.ActivatePackagedApplication)
        {
            return action.Arguments.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase) &&
                   action.Arguments.Length > "shell:AppsFolder\\".Length
                ? Available("The packaged application identity is present.")
                : Missing("The packaged application identity is missing.");
        }

        RestoreResourceValidation target = ValidateTarget(action.Target);
        if (!target.IsAvailable) return target;

        // VS Code/project actions carry the resource path as a quoted argument while the target
        // is the executable. Validate both preconditions immediately before launching.
        if (action.Kind == RestoreActionKind.OpenResource && !action.UseShellExecute)
        {
            string argumentTarget = action.Arguments.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(argumentTarget))
                return ValidateTarget(argumentTarget);
        }

        return target;
    }

    private static RestoreResourceValidation ValidateTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return Missing("The approved launch target is empty.");

        if (Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) &&
            uri.Scheme is "http" or "https")
        {
            return Available("The approved URL is syntactically valid.");
        }

        try
        {
            return File.Exists(target) || Directory.Exists(target)
                ? Available("The approved file-system target still exists.")
                : Missing("The approved file-system target no longer exists.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new RestoreResourceValidation(
                RestoreResourceAvailability.Stale,
                "The approved file-system target can no longer be validated.");
        }
    }

    private static RestoreResourceValidation Available(string explanation) =>
        new(RestoreResourceAvailability.Available, explanation);

    private static RestoreResourceValidation Missing(string explanation) =>
        new(RestoreResourceAvailability.Missing, explanation);
}
