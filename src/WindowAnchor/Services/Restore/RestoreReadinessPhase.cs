using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WindowAnchor.Services;

/// <summary>Correlates successful launches with newly responsive, stable, eligible windows.</summary>
internal sealed class RestoreReadinessPhase
{
    private readonly IWindowMutation _windowMutation;
    private readonly IRestoreClock _clock;
    private readonly IAppReadinessProbe _readinessProbe;
    private readonly AppReadinessEngine _readinessEngine;
    private readonly RestoreWindowRevalidator _revalidator;

    internal RestoreReadinessPhase(
        IWindowMutation windowMutation,
        IRestoreClock clock,
        IAppReadinessProbe readinessProbe,
        AppReadinessEngine readinessEngine,
        RestoreWindowRevalidator revalidator)
    {
        _windowMutation = windowMutation;
        _clock = clock;
        _readinessProbe = readinessProbe;
        _readinessEngine = readinessEngine;
        _revalidator = revalidator;
    }

    internal async Task<bool> ExecuteAsync(
        RestoreExecutionContext context,
        CancellationToken cancellationToken,
        IProgress<RestoreProgressReport>? progress)
    {
        IndexedRestoreAction[] awaitActions = context.IndexedActions
            .Where(item => item.Action.Kind == RestoreActionKind.AwaitWindowAppearance)
            .ToArray();
        IndexedRestoreAction[] relatedAwaitActions = awaitActions
            .Where(item => HasSuccessfulRelatedActivity(context, item))
            .ToArray();

        if (relatedAwaitActions.Length > 0 &&
            !await ReconcileAwaitingWindowsAsync(
                context,
                relatedAwaitActions,
                cancellationToken,
                progress).ConfigureAwait(false))
        {
            return false;
        }

        foreach (IndexedRestoreAction item in awaitActions.Where(item =>
                     !context.Results.ContainsKey(item.Index)))
        {
            context.Results[item.Index] = RestoreExecutionSupport.Result(
                item,
                RestoreExecutionActionStatus.Skipped,
                staleReason: null,
                "No successful launch or browser action related to this entry required a reconciliation wait.");
            if (item.Action.EntryIndex is int entryIndex &&
                context.Entries.TryGetValue(entryIndex, out RestoreEntryExecutionState? state) &&
                state.Status is RestoreExecutionEntryStatus.Pending or
                    RestoreExecutionEntryStatus.LaunchRequested)
            {
                state.Status = RestoreExecutionEntryStatus.AwaitingWindow;
                state.Explanation = "No successful action related to this entry could create an eligible live window.";
            }
        }

        return true;
    }

    private async Task<bool> ReconcileAwaitingWindowsAsync(
        RestoreExecutionContext context,
        IReadOnlyList<IndexedRestoreAction> awaitActions,
        CancellationToken cancellationToken,
        IProgress<RestoreProgressReport>? progress)
    {
        var pending = new Dictionary<int, (IndexedRestoreAction Action, AppReadinessTracker Tracker)>();
        foreach (IndexedRestoreAction item in awaitActions)
        {
            if (context.Results.ContainsKey(item.Index) ||
                item.Action.EntryIndex is not int entryIndex ||
                !context.Entries.TryGetValue(entryIndex, out RestoreEntryExecutionState? state))
            {
                continue;
            }
            if (state.AssignedWindowHandle is not null)
            {
                context.Results[item.Index] = RestoreExecutionSupport.Result(
                    item,
                    RestoreExecutionActionStatus.Skipped,
                    staleReason: null,
                    "The entry already owns a revalidated live window.");
                continue;
            }
            if (state.Status is RestoreExecutionEntryStatus.Stale or RestoreExecutionEntryStatus.Failed)
            {
                context.Results[item.Index] = RestoreExecutionSupport.Result(
                    item,
                    RestoreExecutionActionStatus.Skipped,
                    staleReason: null,
                    "Readiness polling was skipped because an earlier action did not succeed.",
                    readinessState: AppReadinessState.Failed);
                continue;
            }

            pending[item.Index] = (item, new AppReadinessTracker());
        }

        int totalPending = pending.Count;
        int lastReportedCount = -1;
        int lastReportedSecond = -1;
        long startedAt = _clock.GetTimestamp();
        while (pending.Count > 0)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                CancelPendingReadiness(context, pending.Values);
                return false;
            }

            AppReadinessObservation observation = _readinessProbe.Observe();
            TimeSpan elapsed = _clock.GetElapsedTime(startedAt);
            int elapsedSecond = Math.Max(0, (int)elapsed.TotalSeconds);
            if (pending.Count != lastReportedCount || elapsedSecond != lastReportedSecond)
            {
                string[] waitingFor = pending.Values
                    .Select(value => RestoreExecutionSupport.EntryDisplayName(
                        context.Entries[value.Action.Action.EntryIndex!.Value].PlanEntry))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .ToArray();
                string message = pending.Count == 1
                    ? $"Waiting for {waitingFor[0]}"
                    : $"Waiting for {pending.Count} applications";
                string detail = string.Join(", ", waitingFor);
                if (pending.Count > waitingFor.Length)
                    detail += $" and {pending.Count - waitingFor.Length} more";
                progress?.Report(new RestoreProgressReport(
                    RestoreProgressStage.WaitingForApplications,
                    message,
                    detail,
                    totalPending - pending.Count,
                    totalPending,
                    elapsed,
                    _readinessEngine.Policy.Timeout));
                lastReportedCount = pending.Count;
                lastReportedSecond = elapsedSecond;
            }

            foreach ((IndexedRestoreAction item, AppReadinessTracker tracker) in
                     pending.Values.ToArray())
            {
                int entryIndex = item.Action.EntryIndex!.Value;
                RestoreEntryExecutionState state = context.Entries[entryIndex];
                HashSet<IntPtr> unavailableHandles = context.AssignedHwnds
                    .Concat(state.PlanEntry.ReadinessExcludedWindowHandles.Select(handle =>
                        new IntPtr(handle)))
                    .ToHashSet();
                AppReadinessEvaluation evaluation = _readinessEngine.Evaluate(
                    state.PlanEntry,
                    observation,
                    unavailableHandles,
                    tracker,
                    elapsed);
                state.ReadinessState = evaluation.State;
                state.ReadinessStrategy = evaluation.Strategy;
                state.Explanation = evaluation.Explanation;

                if (evaluation.State == AppReadinessState.Ready &&
                    evaluation.Candidate is { } selected)
                {
                    RestorePlanStaleReason? stale = _revalidator.Revalidate(
                        state.PlanEntry,
                        selected.Hwnd,
                        selected.ProcessId);
                    if (stale is not null)
                    {
                        RestoreExecutionSupport.MarkStale(
                            item,
                            state,
                            context.Results,
                            stale.Value);
                        context.Results[item.Index] = context.Results[item.Index] with
                        {
                            ReadinessState = AppReadinessState.Ready,
                            ReadinessStrategy = evaluation.Strategy
                        };
                    }
                    else
                    {
                        RestoreTargetPlacement placement =
                            item.Action.TargetPlacement ?? state.PlanEntry.TargetPlacement;
                        _windowMutation.RestoreSingleWindow(
                            selected.Hwnd,
                            RestoreExecutionSupport.ToWindowRecord(placement));
                        context.AssignedHwnds.Add(selected.Hwnd);
                        state.Status = RestoreExecutionEntryStatus.Restored;
                        state.AssignedWindowHandle = selected.Hwnd.ToInt64();
                        state.PlacementActionIndex = item.Index;
                        state.Explanation =
                            "Assigned and positioned a responsive, stable eligible window.";
                        context.Results[item.Index] = RestoreExecutionSupport.Result(
                            item,
                            RestoreExecutionActionStatus.Succeeded,
                            staleReason: null,
                            state.Explanation,
                            selected.Hwnd.ToInt64(),
                            evaluation.State,
                            evaluation.Strategy);
                    }
                    pending.Remove(item.Index);
                    continue;
                }

                if (evaluation.State is AppReadinessState.TimedOut or AppReadinessState.Failed)
                {
                    state.Status = RestoreExecutionEntryStatus.Failed;
                    context.Results[item.Index] = RestoreExecutionSupport.Result(
                        item,
                        RestoreExecutionActionStatus.Failed,
                        staleReason: null,
                        evaluation.Explanation,
                        readinessState: evaluation.State,
                        readinessStrategy: evaluation.Strategy);
                    LogReadinessFailure(entryIndex, state, evaluation, elapsed);
                    pending.Remove(item.Index);
                    continue;
                }

                state.Status = RestoreExecutionEntryStatus.AwaitingWindow;
            }

            if (pending.Count == 0)
                break;
            elapsed = _clock.GetElapsedTime(startedAt);
            TimeSpan remaining = _readinessEngine.Policy.Timeout - elapsed;
            if (remaining <= TimeSpan.Zero)
                continue;
            if (!await DelayOrCancelAsync(
                    remaining < _readinessEngine.Policy.PollInterval
                        ? remaining
                        : _readinessEngine.Policy.PollInterval,
                    cancellationToken).ConfigureAwait(false))
            {
                CancelPendingReadiness(context, pending.Values);
                return false;
            }
        }

        return true;
    }

    private bool HasSuccessfulRelatedActivity(
        RestoreExecutionContext context,
        IndexedRestoreAction awaitAction)
    {
        if (awaitAction.Action.EntryIndex is not int entryIndex)
            return false;
        RestorePlanEntry? entry = context.Plan.Entries.FirstOrDefault(item =>
            item.EntryIndex == entryIndex);
        if (entry is null)
            return false;

        if (entry.Outcome == RestorePlanEntryOutcome.AwaitingBrowserSession &&
            context.BrowserSessionSucceeded == true)
        {
            return true;
        }

        bool SuccessfulLaunch(IndexedRestoreAction item) =>
            RestoreExecutionSupport.IsLaunch(item.Action.Kind) &&
            context.Results.TryGetValue(item.Index, out RestoreExecutionActionResult? result) &&
            result.Status == RestoreExecutionActionStatus.Succeeded;

        if (context.IndexedActions.Any(item =>
                item.Action.EntryIndex == entryIndex && SuccessfulLaunch(item)))
        {
            return true;
        }

        if (entry.Outcome != RestorePlanEntryOutcome.AwaitingRunningApplication)
            return false;

        string expectedExecutable = WindowIdentityExtractor.NormalizePath(
            entry.SavedIdentity.ExecutablePath);
        string expectedProcess = ProcessIdentityNormalizer.Normalize(entry.SavedIdentity.ProcessName);
        return context.IndexedActions.Any(item =>
        {
            if (!SuccessfulLaunch(item) || item.Action.EntryIndex is not int sourceEntryIndex)
                return false;
            RestorePlanEntry? source = context.Plan.Entries.FirstOrDefault(candidate =>
                candidate.EntryIndex == sourceEntryIndex);
            if (source is null)
                return false;
            string sourceExecutable = WindowIdentityExtractor.NormalizePath(
                source.SavedIdentity.ExecutablePath);
            string sourceProcess = ProcessIdentityNormalizer.Normalize(source.SavedIdentity.ProcessName);
            return (expectedExecutable.Length > 0 && string.Equals(
                       expectedExecutable,
                       sourceExecutable,
                       StringComparison.OrdinalIgnoreCase)) ||
                   (expectedProcess.Length > 0 && string.Equals(
                       expectedProcess,
                       sourceProcess,
                       StringComparison.OrdinalIgnoreCase));
        });
    }

    private static void CancelPendingReadiness(
        RestoreExecutionContext context,
        IEnumerable<(IndexedRestoreAction Action, AppReadinessTracker Tracker)> pending)
    {
        foreach ((IndexedRestoreAction item, _) in pending)
        {
            if (item.Action.EntryIndex is int entryIndex &&
                context.Entries.TryGetValue(entryIndex, out RestoreEntryExecutionState? state))
            {
                state.Status = RestoreExecutionEntryStatus.Cancelled;
                state.Explanation = "Cancellation interrupted application readiness polling.";
                context.Results[item.Index] = RestoreExecutionSupport.Result(
                    item,
                    RestoreExecutionActionStatus.Cancelled,
                    staleReason: null,
                    state.Explanation,
                    readinessState: state.ReadinessState,
                    readinessStrategy: state.ReadinessStrategy);
            }
        }
    }

    private void LogReadinessFailure(
        int entryIndex,
        RestoreEntryExecutionState state,
        AppReadinessEvaluation evaluation,
        TimeSpan elapsed)
    {
        if (evaluation.State == AppReadinessState.TimedOut)
        {
            AppLogger.Warn(
                "restore.entry.readiness_timeout",
                "Application readiness timed out",
                LogField.Public("entryIndex", entryIndex),
                LogField.Identifier("entryId", state.PlanEntry.EntryId),
                LogField.Public("processName", state.PlanEntry.SavedIdentity.ProcessName),
                LogField.Public("timeoutMs", _readinessEngine.Policy.Timeout.TotalMilliseconds),
                LogField.Public("elapsedMs", elapsed.TotalMilliseconds),
                LogField.Public("strategy", evaluation.Strategy),
                LogField.Public("explanation", evaluation.Explanation));
        }
        else
        {
            AppLogger.Warn(
                "restore.entry.readiness_failed",
                "Application readiness evaluation failed",
                LogField.Public("entryIndex", entryIndex),
                LogField.Identifier("entryId", state.PlanEntry.EntryId),
                LogField.Public("strategy", evaluation.Strategy));
        }
    }

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
}
