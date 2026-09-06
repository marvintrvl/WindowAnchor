using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using WindowAnchor.Native;

namespace WindowAnchor.Services;

/// <summary>Lifecycle states reported while an approved entry waits for its live window.</summary>
public enum AppReadinessState
{
    NotStarted,
    ProcessStarted,
    WindowFound,
    Ready,
    TimedOut,
    Failed
}

/// <summary>Deterministic limits for readiness polling.</summary>
public sealed record AppReadinessPolicy
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(45);
    public int RequiredStableObservations { get; init; } = 2;

    public static AppReadinessPolicy Default { get; } = new();

    internal void Validate()
    {
        if (PollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(PollInterval));
        if (Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(Timeout));
        if (RequiredStableObservations <= 0)
            throw new ArgumentOutOfRangeException(nameof(RequiredStableObservations));
    }
}

/// <summary>
/// One shared, read-only desktop observation used to evaluate every pending entry in a poll.
/// </summary>
public sealed record AppReadinessObservation
{
    public IReadOnlyList<LiveWindowIdentity> Windows { get; init; } =
        Array.Empty<LiveWindowIdentity>();
    public IReadOnlySet<string> RunningProcessNames { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<long> ResponsiveWindowHandles { get; init; } =
        new HashSet<long>();
    public string? Failure { get; init; }
}

/// <summary>Read-only boundary for process, window, and responsiveness readiness signals.</summary>
public interface IAppReadinessProbe
{
    AppReadinessObservation Observe();
}

/// <summary>
/// Production readiness probe. It does not use WaitForInputIdle, which is unreliable for
/// UWP/MSIX and multi-process browser applications.
/// </summary>
public sealed class SystemAppReadinessProbe : IAppReadinessProbe
{
    private readonly IWindowInventory _windowInventory;

    public SystemAppReadinessProbe(IWindowInventory windowInventory) =>
        _windowInventory = windowInventory ?? throw new ArgumentNullException(nameof(windowInventory));

    public AppReadinessObservation Observe()
    {
        try
        {
            Dictionary<IntPtr, (uint Pid, Models.WindowRecord Record)> records =
                _windowInventory.GetWindowsWithPids(WindowCandidatePolicy.RestoreMatchCandidate);
            LiveWindowIdentity[] windows = records
                .Select(item => WindowIdentityExtractor.FromLive(
                    item.Key,
                    item.Value.Pid,
                    item.Value.Record))
                .OrderBy(window => window.Hwnd.ToInt64())
                .ToArray();
            var processNames = new HashSet<string>(
                windows.Select(window => ProcessIdentityNormalizer.Normalize(window.ProcessName)),
                StringComparer.OrdinalIgnoreCase);
            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    try { processNames.Add(ProcessIdentityNormalizer.Normalize(process.ProcessName)); }
                    catch { /* Elevated and short-lived processes are not readiness failures. */ }
                }
            }

            var responsive = records.Keys
                .Where(hwnd => !NativeMethodsWindow.IsHungAppWindow(hwnd))
                .Select(hwnd => hwnd.ToInt64())
                .ToHashSet();
            return new AppReadinessObservation
            {
                Windows = windows,
                RunningProcessNames = processNames,
                ResponsiveWindowHandles = responsive
            };
        }
        catch (Exception ex)
        {
            return new AppReadinessObservation
            {
                Failure = $"Readiness observation failed ({ex.GetType().Name})."
            };
        }
    }

    internal static string ProcessName(SavedWindowIdentity identity)
    {
        if (!string.IsNullOrWhiteSpace(identity.ProcessName))
            return ProcessIdentityNormalizer.Normalize(identity.ProcessName);
        try { return ProcessIdentityNormalizer.Normalize(Path.GetFileNameWithoutExtension(identity.ExecutablePath)); }
        catch { return ""; }
    }
}

/// <summary>Facts supplied to one generic or adapter-specific readiness strategy.</summary>
public sealed record AppReadinessContext(
    RestorePlanEntry Entry,
    AppReadinessObservation Observation,
    WindowMatchResolution MatchResolution,
    bool ProcessExists,
    bool SelectedWindowResponsive,
    int ConsecutiveStableObservations,
    AppReadinessPolicy Policy);

/// <summary>Explained state returned by a readiness strategy.</summary>
public sealed record AppReadinessDecision(AppReadinessState State, string Explanation);

/// <summary>
/// Extensible readiness policy for an application family. Matching remains owned by
/// <see cref="WindowMatcher"/>; a strategy may only decide when its safely matched window is ready.
/// </summary>
public interface IAppReadinessStrategy
{
    string Name { get; }
    bool CanHandle(SavedWindowIdentity identity);
    AppReadinessDecision Evaluate(AppReadinessContext context);
}

/// <summary>Fallback readiness policy used when no app adapter claims an entry.</summary>
public sealed class GenericAppReadinessStrategy : IAppReadinessStrategy
{
    public string Name => "generic";
    public bool CanHandle(SavedWindowIdentity identity) => true;

    public AppReadinessDecision Evaluate(AppReadinessContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!string.IsNullOrWhiteSpace(context.Observation.Failure))
            return new(AppReadinessState.Failed, context.Observation.Failure);
        if (!context.ProcessExists)
            return new(AppReadinessState.NotStarted, "The application process has not started.");
        if (context.MatchResolution.SelectedCandidate is null)
        {
            bool eligibleWindowExists = context.MatchResolution.Candidates.Any(candidate =>
                candidate.IsEligible);
            return eligibleWindowExists
                ? new(AppReadinessState.WindowFound,
                    "Live windows exist, but none can be assigned safely yet.")
                : new(AppReadinessState.ProcessStarted,
                    "The process exists, but no eligible top-level window exists yet.");
        }
        if (!context.SelectedWindowResponsive)
            return new(AppReadinessState.WindowFound,
                "An eligible window exists but is not responsive yet.");
        if (context.ConsecutiveStableObservations < context.Policy.RequiredStableObservations)
            return new(AppReadinessState.WindowFound,
                "An eligible responsive window exists; its identity and bounds are still stabilizing.");

        return new(AppReadinessState.Ready,
            "The eligible window is responsive and its identity and bounds are stable.");
    }
}

/// <summary>Selects the first adapter strategy that claims an entry, then the generic fallback.</summary>
public sealed class AppReadinessStrategyRegistry
{
    private readonly IReadOnlyList<IAppReadinessStrategy> _strategies;
    private readonly IAppReadinessStrategy _fallback = new GenericAppReadinessStrategy();

    public AppReadinessStrategyRegistry(IEnumerable<IAppReadinessStrategy>? strategies = null) =>
        _strategies = (strategies ?? Array.Empty<IAppReadinessStrategy>()).ToArray();

    public IAppReadinessStrategy Resolve(SavedWindowIdentity identity) =>
        _strategies.FirstOrDefault(strategy => strategy.CanHandle(identity)) ?? _fallback;
}

/// <summary>Mutable per-entry stability memory owned only for one executor invocation.</summary>
public sealed class AppReadinessTracker
{
    private string? _lastSignature;

    public int ConsecutiveStableObservations { get; private set; }

    internal void Observe(WindowMatchCandidate? candidate)
    {
        if (candidate is null)
        {
            _lastSignature = null;
            ConsecutiveStableObservations = 0;
            return;
        }

        string signature = string.Join(
            '\u001f',
            candidate.Hwnd.ToInt64(),
            candidate.ProcessId,
            candidate.Title,
            candidate.WindowClassName,
            candidate.Bounds.Left,
            candidate.Bounds.Top,
            candidate.Bounds.Right,
            candidate.Bounds.Bottom);
        ConsecutiveStableObservations = string.Equals(
            signature,
            _lastSignature,
            StringComparison.Ordinal)
            ? ConsecutiveStableObservations + 1
            : 1;
        _lastSignature = signature;
    }
}

/// <summary>One readiness evaluation, including the safely matched candidate when available.</summary>
public sealed record AppReadinessEvaluation(
    AppReadinessState State,
    WindowMatchCandidate? Candidate,
    string Strategy,
    string Explanation,
    TimeSpan Elapsed);

/// <summary>
/// Side-effect-free readiness evaluator over an already-observed desktop snapshot. The executor
/// owns polling and mutation so completed entries can be placed while slower entries keep waiting.
/// </summary>
public sealed class AppReadinessEngine
{
    private readonly AppReadinessStrategyRegistry _strategies;

    public AppReadinessEngine(
        AppReadinessPolicy? policy = null,
        IEnumerable<IAppReadinessStrategy>? strategies = null)
    {
        Policy = policy ?? AppReadinessPolicy.Default;
        Policy.Validate();
        _strategies = new AppReadinessStrategyRegistry(strategies);
    }

    public AppReadinessPolicy Policy { get; }

    public AppReadinessEvaluation Evaluate(
        RestorePlanEntry entry,
        AppReadinessObservation observation,
        IReadOnlySet<IntPtr> assignedWindowHandles,
        AppReadinessTracker tracker,
        TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(assignedWindowHandles);
        ArgumentNullException.ThrowIfNull(tracker);

        WindowMatchResolution resolution = WindowMatcher.Resolve(
            entry.SavedIdentity,
            observation.Windows.Where(window => !assignedWindowHandles.Contains(window.Hwnd)));
        tracker.Observe(resolution.SelectedCandidate);
        bool processExists = ProcessExists(entry.SavedIdentity, observation);
        bool responsive = resolution.SelectedCandidate is { } selected &&
            observation.ResponsiveWindowHandles.Contains(selected.Hwnd.ToInt64());

        IAppReadinessStrategy strategy;
        AppReadinessDecision decision;
        try
        {
            strategy = _strategies.Resolve(entry.SavedIdentity);
            decision = strategy.Evaluate(new AppReadinessContext(
                entry,
                observation,
                resolution,
                processExists,
                responsive,
                tracker.ConsecutiveStableObservations,
                Policy));
        }
        catch (Exception ex)
        {
            return new AppReadinessEvaluation(
                AppReadinessState.Failed,
                null,
                "strategy-error",
                $"Readiness strategy failed ({ex.GetType().Name}).",
                elapsed);
        }

        if (decision.State == AppReadinessState.Ready && resolution.SelectedCandidate is null)
        {
            decision = new AppReadinessDecision(
                AppReadinessState.Failed,
                "The readiness strategy reported Ready without a safely matched window.");
        }
        if (decision.State is not (AppReadinessState.Ready or AppReadinessState.Failed) &&
            elapsed >= Policy.Timeout)
        {
            decision = new AppReadinessDecision(
                AppReadinessState.TimedOut,
                $"Readiness timed out after {Policy.Timeout.TotalMilliseconds:0} ms. " +
                $"Last state: {decision.State}. {decision.Explanation}");
        }

        return new AppReadinessEvaluation(
            decision.State,
            resolution.SelectedCandidate,
            strategy.Name,
            decision.Explanation,
            elapsed);
    }

    private static bool ProcessExists(
        SavedWindowIdentity identity,
        AppReadinessObservation observation)
    {
        string expected = SystemAppReadinessProbe.ProcessName(identity);
        if (!string.IsNullOrWhiteSpace(expected) &&
            observation.RunningProcessNames.Contains(expected))
        {
            return true;
        }

        return observation.Windows.Any(window =>
            (!string.IsNullOrWhiteSpace(identity.ExecutablePath) &&
             string.Equals(
                 identity.ExecutablePath,
                 window.ExecutablePath,
                 StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(identity.ProcessName) &&
             string.Equals(
                 identity.ProcessName,
                 window.ProcessName,
                 StringComparison.OrdinalIgnoreCase)));
    }
}
