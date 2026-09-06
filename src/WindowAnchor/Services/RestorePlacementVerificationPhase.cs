using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WindowAnchor.Services;

/// <summary>Re-observes assigned HWNDs and applies the existing bounded placement retry policy.</summary>
internal sealed class RestorePlacementVerificationPhase
{
    private readonly IWindowMutation _windowMutation;
    private readonly IRestoreClock _clock;
    private readonly IWindowPlacementProbe _placementProbe;
    private readonly WindowPlacementVerificationStrategyRegistry _placementStrategies;
    private readonly RestoreWindowRevalidator _revalidator;

    internal RestorePlacementVerificationPhase(
        IWindowMutation windowMutation,
        IRestoreClock clock,
        IWindowPlacementProbe placementProbe,
        WindowPlacementVerificationStrategyRegistry placementStrategies,
        RestoreWindowRevalidator revalidator)
    {
        _windowMutation = windowMutation;
        _clock = clock;
        _placementProbe = placementProbe;
        _placementStrategies = placementStrategies;
        _revalidator = revalidator;
    }

    internal async Task<bool> ExecuteAsync(
        RestoreExecutionContext context,
        CancellationToken cancellationToken,
        IProgress<RestoreProgressReport>? progress)
    {
        var pending = context.Entries.Values
            .Where(state => state.Status == RestoreExecutionEntryStatus.Restored &&
                            state.AssignedWindowHandle is not null &&
                            state.PlacementActionIndex is not null)
            .Select(state =>
            {
                (string name, WindowPlacementVerificationPolicy policy) =
                    _placementStrategies.Resolve(state.PlanEntry);
                return new PlacementVerificationSession(state, name, policy);
            })
            .ToDictionary(session => session.State.PlanEntry.EntryIndex);
        if (pending.Count == 0)
            return true;

        int totalPlacements = pending.Count;
        progress?.Report(new RestoreProgressReport(
            RestoreProgressStage.VerifyingPlacements,
            $"Verifying {totalPlacements} window placement{(totalPlacements == 1 ? "" : "s")}",
            "Checking final bounds and window state.",
            0,
            totalPlacements));

        // Record whether the mutation was ever visibly accepted so later app-driven movement can
        // remain distinguishable from an outright rejected placement.
        foreach (PlacementVerificationSession session in pending.Values)
            ObserveImmediateAcceptance(session);

        TimeSpan initialDelay = pending.Values.Max(session => session.Policy.InitialDelay);
        if (initialDelay > TimeSpan.Zero &&
            !await DelayOrCancelAsync(initialDelay, cancellationToken).ConfigureAwait(false))
        {
            CancelPendingPlacementVerification(pending.Values, context.Results);
            return false;
        }

        while (pending.Count > 0)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                CancelPendingPlacementVerification(pending.Values, context.Results);
                return false;
            }

            foreach (PlacementVerificationSession session in pending.Values.ToArray())
            {
                RestoreEntryExecutionState state = session.State;
                IntPtr hwnd = new(state.AssignedWindowHandle!.Value);
                WindowPlacementObservation observation = _placementProbe.Observe(hwnd);
                bool finalObservation = session.RetryCount >= session.Policy.MaxRetries;
                WindowPlacementEvaluation evaluation = WindowPlacementVerifier.Evaluate(
                    state.PlanEntry.TargetPlacement,
                    observation,
                    session.Policy,
                    finalObservation,
                    session.WasApplied);

                if (evaluation.State == WindowPlacementVerificationState.Applied ||
                    evaluation.State == WindowPlacementVerificationState.WindowGone ||
                    finalObservation)
                {
                    FinalizePlacementVerification(session, evaluation, context.Results);
                    pending.Remove(state.PlanEntry.EntryIndex);
                    ReportProgress(progress, state, totalPlacements, pending.Count);
                    continue;
                }

                uint expectedPid = state.PlanEntry.SelectedMatch?.ProcessId ?? 0;
                RestorePlanStaleReason? stale = _revalidator.Revalidate(
                    state.PlanEntry,
                    hwnd,
                    expectedPid);
                if (stale is not null)
                {
                    var staleEvaluation = new WindowPlacementEvaluation(
                        stale == RestorePlanStaleReason.WindowClosed
                            ? WindowPlacementVerificationState.WindowGone
                            : WindowPlacementVerificationState.Rejected,
                        evaluation.TolerancePixels,
                        stale == RestorePlanStaleReason.WindowClosed
                            ? "The assigned window closed before a placement retry."
                            : "The assigned HWND became stale before a placement retry.");
                    FinalizePlacementVerification(session, staleEvaluation, context.Results);
                    pending.Remove(state.PlanEntry.EntryIndex);
                    ReportProgress(progress, state, totalPlacements, pending.Count);
                    continue;
                }

                _windowMutation.RestoreSingleWindow(
                    hwnd,
                    RestoreExecutionSupport.ToWindowRecord(state.PlanEntry.TargetPlacement));
                session.RetryCount++;
                ObserveImmediateAcceptance(session);
            }

            if (pending.Count == 0)
                break;

            TimeSpan retryDelay = pending.Values.Max(session => session.Policy.RetryDelay);
            if (retryDelay > TimeSpan.Zero &&
                !await DelayOrCancelAsync(retryDelay, cancellationToken).ConfigureAwait(false))
            {
                CancelPendingPlacementVerification(pending.Values, context.Results);
                return false;
            }
        }

        return true;
    }

    private void ObserveImmediateAcceptance(PlacementVerificationSession session)
    {
        IntPtr hwnd = new(session.State.AssignedWindowHandle!.Value);
        WindowPlacementEvaluation immediate = WindowPlacementVerifier.Evaluate(
            session.State.PlanEntry.TargetPlacement,
            _placementProbe.Observe(hwnd),
            session.Policy,
            finalObservation: false,
            session.WasApplied);
        session.WasApplied |= immediate.State == WindowPlacementVerificationState.Applied;
    }

    private static void FinalizePlacementVerification(
        PlacementVerificationSession session,
        WindowPlacementEvaluation evaluation,
        IDictionary<int, RestoreExecutionActionResult> results)
    {
        RestoreEntryExecutionState state = session.State;
        bool succeeded = evaluation.State == WindowPlacementVerificationState.Applied;
        state.PlacementVerification = evaluation.State;
        state.PlacementRetryCount = session.RetryCount;
        state.PlacementVerificationStrategy = session.Strategy;
        state.PlacementTolerancePixels = evaluation.TolerancePixels;
        state.Explanation = evaluation.Explanation;
        if (!succeeded)
            state.Status = RestoreExecutionEntryStatus.Failed;

        int actionIndex = state.PlacementActionIndex!.Value;
        if (results.TryGetValue(actionIndex, out RestoreExecutionActionResult? action))
        {
            results[actionIndex] = action with
            {
                Status = succeeded
                    ? RestoreExecutionActionStatus.Succeeded
                    : RestoreExecutionActionStatus.Failed,
                Explanation = evaluation.Explanation,
                PlacementVerification = evaluation.State,
                PlacementRetryCount = session.RetryCount,
                PlacementVerificationStrategy = session.Strategy,
                PlacementTolerancePixels = evaluation.TolerancePixels
            };
        }

        Action<string, string, LogField[]> log = succeeded ? AppLogger.Info : AppLogger.Warn;
        log(
            succeeded
                ? "restore.entry.placement_verified"
                : "restore.entry.placement_verification_failed",
            succeeded
                ? "Verified the final window placement"
                : "Window placement did not verify within the bounded retry policy",
            [
                LogField.Identifier("entryId", state.PlanEntry.EntryId),
                LogField.Public("entryIndex", state.PlanEntry.EntryIndex),
                LogField.Public("state", evaluation.State),
                LogField.Public("retryCount", session.RetryCount),
                LogField.Public("strategy", session.Strategy),
                LogField.Public("tolerancePixels", evaluation.TolerancePixels)
            ]);
    }

    private static void CancelPendingPlacementVerification(
        IEnumerable<PlacementVerificationSession> pending,
        IDictionary<int, RestoreExecutionActionResult> results)
    {
        foreach (PlacementVerificationSession session in pending)
        {
            RestoreEntryExecutionState state = session.State;
            state.Status = RestoreExecutionEntryStatus.Cancelled;
            state.Explanation = "Cancellation interrupted post-restore placement verification.";
            if (state.PlacementActionIndex is int actionIndex &&
                results.TryGetValue(actionIndex, out RestoreExecutionActionResult? action))
            {
                results[actionIndex] = action with
                {
                    Status = RestoreExecutionActionStatus.Cancelled,
                    Explanation = state.Explanation,
                    PlacementRetryCount = session.RetryCount,
                    PlacementVerificationStrategy = session.Strategy
                };
            }
        }
    }

    private static void ReportProgress(
        IProgress<RestoreProgressReport>? progress,
        RestoreEntryExecutionState state,
        int totalPlacements,
        int pendingCount) =>
        progress?.Report(new RestoreProgressReport(
            RestoreProgressStage.VerifyingPlacements,
            "Verifying window placements",
            RestoreExecutionSupport.EntryDisplayName(state.PlanEntry),
            totalPlacements - pendingCount,
            totalPlacements));

    private async Task<bool> DelayOrCancelAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        try
        {
            await _clock.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
            return !cancellationToken.IsCancellationRequested;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private sealed class PlacementVerificationSession
    {
        internal PlacementVerificationSession(
            RestoreEntryExecutionState state,
            string strategy,
            WindowPlacementVerificationPolicy policy)
        {
            State = state;
            Strategy = strategy;
            Policy = policy;
        }

        internal RestoreEntryExecutionState State { get; }
        internal string Strategy { get; }
        internal WindowPlacementVerificationPolicy Policy { get; }
        internal int RetryCount { get; set; }
        internal bool WasApplied { get; set; }
    }
}
