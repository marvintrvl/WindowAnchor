using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>
/// Executes only actions present in an approved <see cref="RestorePlan"/>. Every HWND and launch
/// resource is revalidated immediately before mutation; changed preconditions become structured
/// stale-plan results rather than being applied to a replacement target.
/// </summary>
public sealed class RestoreExecutor
{
    private readonly IWindowInventory _windowInventory;
    private readonly IWindowMutation _windowMutation;
    private readonly IRestoreProcessLauncher _processLauncher;
    private readonly IRestoreClock _clock;
    private readonly IRestoreResourceBoundary _resources;
    private readonly IBrowserSessionConnector? _browserConnector;
    private readonly IAppReadinessProbe _readinessProbe;
    private readonly AppReadinessEngine _readinessEngine;
    private readonly IWindowPlacementProbe _placementProbe;
    private readonly WindowPlacementVerificationStrategyRegistry _placementStrategies;

    public RestoreExecutor(
        IWindowInventory windowInventory,
        IWindowMutation windowMutation,
        IRestoreProcessLauncher processLauncher,
        IRestoreClock clock,
        IRestoreResourceBoundary resources,
        IBrowserSessionConnector? browserConnector = null,
        IAppReadinessProbe? readinessProbe = null,
        AppReadinessPolicy? readinessPolicy = null,
        IEnumerable<IAppReadinessStrategy>? readinessStrategies = null,
        IWindowPlacementProbe? placementProbe = null,
        WindowPlacementVerificationPolicy? placementPolicy = null,
        IEnumerable<IWindowPlacementVerificationStrategy>? placementStrategies = null)
    {
        _windowInventory = windowInventory ?? throw new ArgumentNullException(nameof(windowInventory));
        _windowMutation = windowMutation ?? throw new ArgumentNullException(nameof(windowMutation));
        _processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _browserConnector = browserConnector;
        _readinessProbe = readinessProbe ?? new SystemAppReadinessProbe(windowInventory);
        _readinessEngine = new AppReadinessEngine(readinessPolicy, readinessStrategies);
        _placementProbe = placementProbe ?? new InventoryWindowPlacementProbe(windowInventory);
        _placementStrategies = new WindowPlacementVerificationStrategyRegistry(
            placementPolicy,
            placementStrategies);
    }

    /// <summary>Executes an approved plan through injectable process, browser, window, and clock boundaries.</summary>
    public async Task<RestoreExecutionResult> ExecuteAsync(
        RestorePlan plan,
        CancellationToken cancellationToken = default,
        IProgress<RestoreProgressReport>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var entries = plan.Entries.ToDictionary(
            entry => entry.EntryIndex,
            entry => new EntryExecutionState(entry));
        var results = new Dictionary<int, RestoreExecutionActionResult>();
        var assignedHwnds = new HashSet<IntPtr>();
        IndexedAction[] indexedActions = plan.Actions
            .Select((action, index) => new IndexedAction(index, action))
            .ToArray();

        if (plan.WasCancelled)
        {
            MarkRemaining(indexedActions, results, RestoreExecutionActionStatus.Cancelled,
                "The approved plan was already cancelled.");
            return Complete(
                plan,
                entries,
                results,
                assignedHwnds,
                RestoreExecutionStatus.Cancelled,
                wasCancelled: true);
        }

        if (plan.BlockingErrors.Count > 0)
        {
            MarkRemaining(indexedActions, results, RestoreExecutionActionStatus.Skipped,
                "The approved plan contains blocking errors.");
            return Complete(
                plan,
                entries,
                results,
                assignedHwnds,
                RestoreExecutionStatus.Rejected,
                wasCancelled: false);
        }

        RestoreExecutionResult? stalePreview = PreflightApprovedPlan(
            plan,
            indexedActions,
            entries,
            results,
            assignedHwnds);
        if (stalePreview is not null)
            return stalePreview;

        bool? browserSessionSucceeded = null;

        foreach (IndexedAction item in indexedActions.Where(item =>
                     item.Action.Kind == RestoreActionKind.RestoreBrowserSession))
        {
            progress?.Report(new RestoreProgressReport(
                RestoreProgressStage.CapturingBrowserSession,
                "Restoring browser session",
                "Waiting for the browser connector."));
            bool succeeded = await ExecuteBrowserActionAsync(
                plan,
                item,
                results,
                cancellationToken).ConfigureAwait(false);
            browserSessionSucceeded = succeeded;
        }

        // Preserve the current cancellation contract: already-running windows are reconciled in
        // phase one before cancellation prevents launch and wait phases.
        foreach (IndexedAction item in indexedActions.Where(item =>
                     item.Action.Kind == RestoreActionKind.RestoreExistingWindow))
        {
            ExecuteWindowAction(item, entries, results, assignedHwnds);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            MarkRemaining(indexedActions, results, RestoreExecutionActionStatus.Cancelled,
                "Cancellation was observed after initial window reconciliation.");
            return Complete(
                plan,
                entries,
                results,
                assignedHwnds,
                RestoreExecutionStatus.Cancelled,
                wasCancelled: true);
        }

        IndexedAction[] launchActions = indexedActions.Where(item => IsLaunch(item.Action.Kind)).ToArray();
        int launchIndex = 0;
        foreach (IndexedAction item in launchActions)
        {
            launchIndex++;
            RestorePlanEntry? launchEntry = item.Action.EntryIndex is int entryIndex
                ? plan.Entries.FirstOrDefault(entry => entry.EntryIndex == entryIndex)
                : null;
            progress?.Report(new RestoreProgressReport(
                RestoreProgressStage.LaunchingApplications,
                launchEntry is null
                    ? "Launching applications"
                    : $"Launching {EntryDisplayName(launchEntry)}",
                launchEntry?.SavedIdentity.Title ?? "",
                launchIndex,
                launchActions.Length));
            if (item.Action.Condition == RestoreActionCondition.BrowserSessionUnavailable &&
                browserSessionSucceeded == true)
            {
                results[item.Index] = Result(
                    item,
                    RestoreExecutionActionStatus.Skipped,
                    staleReason: null,
                    "The browser-session action succeeded, so its approved fallback was not needed.");
                continue;
            }

            ExecuteLaunchAction(item, entries, results);
        }

        IndexedAction[] awaitActions = indexedActions
            .Where(item => item.Action.Kind == RestoreActionKind.AwaitWindowAppearance)
            .ToArray();
        IndexedAction[] relatedAwaitActions = awaitActions
            .Where(item => HasSuccessfulRelatedActivity(
                plan,
                item,
                indexedActions,
                results,
                browserSessionSucceeded))
            .ToArray();

        if (relatedAwaitActions.Length > 0)
        {
            if (!await ReconcileAwaitingWindowsAsync(
                    relatedAwaitActions,
                    entries,
                    results,
                    assignedHwnds,
                    cancellationToken,
                    progress).ConfigureAwait(false))
            {
                MarkRemaining(indexedActions, results, RestoreExecutionActionStatus.Cancelled,
                    "Cancellation interrupted application readiness polling.");
                return Complete(
                    plan,
                    entries,
                    results,
                    assignedHwnds,
                    RestoreExecutionStatus.Cancelled,
                    wasCancelled: true);
            }
        }

        foreach (IndexedAction item in awaitActions.Where(item => !results.ContainsKey(item.Index)))
        {
            results[item.Index] = Result(
                item,
                RestoreExecutionActionStatus.Skipped,
                staleReason: null,
                "No successful launch or browser action related to this entry required a reconciliation wait.");
            if (item.Action.EntryIndex is int entryIndex &&
                entries.TryGetValue(entryIndex, out EntryExecutionState? state) &&
                state.Status is RestoreExecutionEntryStatus.Pending or
                    RestoreExecutionEntryStatus.LaunchRequested)
            {
                state.Status = RestoreExecutionEntryStatus.AwaitingWindow;
                state.Explanation = "No successful action related to this entry could create an eligible live window.";
            }
        }

        if (!await VerifyAssignedPlacementsAsync(
                entries,
                results,
                cancellationToken,
                progress).ConfigureAwait(false))
        {
            MarkRemaining(indexedActions, results, RestoreExecutionActionStatus.Cancelled,
                "Cancellation interrupted post-restore placement verification.");
            return Complete(
                plan,
                entries,
                results,
                assignedHwnds,
                RestoreExecutionStatus.Cancelled,
                wasCancelled: true);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            MarkRemaining(indexedActions, results, RestoreExecutionActionStatus.Cancelled,
                "Cancellation was observed before final minimization.");
            return Complete(
                plan,
                entries,
                results,
                assignedHwnds,
                RestoreExecutionStatus.Cancelled,
                wasCancelled: true);
        }

        foreach (IndexedAction item in indexedActions.Where(item =>
                     item.Action.Kind == RestoreActionKind.MinimizeOtherWindows))
        {
            var keep = new HashSet<IntPtr>(assignedHwnds);
            keep.UnionWith(plan.ProtectedWindowHandles.Select(handle => new IntPtr(handle)));
            _windowMutation.MinimizeUserWindowsExcept(
                WindowCandidatePolicy.MinimizeCandidate,
                keep);
            results[item.Index] = Result(
                item,
                RestoreExecutionActionStatus.Succeeded,
                staleReason: null,
                "Minimized windows outside the final approved assignment set.");
        }

        MarkRemaining(indexedActions, results, RestoreExecutionActionStatus.Skipped,
            "The action required no additional execution work.");
        RestoreExecutionStatus status = results.Values.Any(result =>
            result.Status == RestoreExecutionActionStatus.Stale)
            ? RestoreExecutionStatus.StalePlan
            : results.Values.Any(result => result.Status == RestoreExecutionActionStatus.Failed)
                ? RestoreExecutionStatus.CompletedWithFailures
                : RestoreExecutionStatus.Completed;
        return Complete(plan, entries, results, assignedHwnds, status, wasCancelled: false);
    }

    private async Task<bool> ExecuteBrowserActionAsync(
        RestorePlan plan,
        IndexedAction item,
        IDictionary<int, RestoreExecutionActionResult> results,
        CancellationToken cancellationToken)
    {
        if (_browserConnector is null)
        {
            results[item.Index] = Result(
                item,
                RestoreExecutionActionStatus.Stale,
                RestorePlanStaleReason.BrowserSessionUnavailable,
                "The approved browser-session connector is no longer available.");
            return false;
        }

        try
        {
            bool restored = await _browserConnector.RestoreAsync(
                plan.WorkspaceName,
                plan.BrowserSessions.Select(ToBrowserSession).ToList(),
                cancellationToken).ConfigureAwait(false);
            results[item.Index] = Result(
                item,
                restored ? RestoreExecutionActionStatus.Succeeded : RestoreExecutionActionStatus.Stale,
                restored ? null : RestorePlanStaleReason.BrowserSessionUnavailable,
                restored
                    ? "Requested restoration through the approved browser-session action."
                    : "Browser-session restoration became unavailable; approved fallbacks remain eligible.");
            return restored;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            results[item.Index] = Result(
                item,
                RestoreExecutionActionStatus.Cancelled,
                staleReason: null,
                "Browser-session restoration was cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            results[item.Index] = Result(
                item,
                RestoreExecutionActionStatus.Failed,
                staleReason: null,
                $"Browser-session restoration failed ({ex.GetType().Name}); approved fallbacks remain eligible.");
            return false;
        }
    }

    private RestoreExecutionResult? PreflightApprovedPlan(
        RestorePlan plan,
        IReadOnlyList<IndexedAction> indexedActions,
        Dictionary<int, EntryExecutionState> entries,
        Dictionary<int, RestoreExecutionActionResult> results,
        IReadOnlySet<IntPtr> assignedHwnds)
    {
        Dictionary<IntPtr, (uint Pid, WindowRecord Record)> current =
            _windowInventory.GetWindowsWithPids(WindowCandidatePolicy.RestoreMatchCandidate);
        LiveWindowIdentity[] identities = current
            .Select(window => WindowIdentityExtractor.FromLive(
                window.Key,
                window.Value.Pid,
                window.Value.Record))
            .OrderBy(window => window.Hwnd.ToInt64())
            .ToArray();
        var consumed = new HashSet<IntPtr>();

        foreach (RestorePlanEntry entry in plan.Entries.OrderBy(entry => entry.EntryIndex))
        {
            bool disabled = plan.DisabledEntryIndexes.Contains(entry.EntryIndex);
            if (entry.SelectedMatch is not null)
                consumed.Add(new IntPtr(entry.SelectedMatch.WindowHandle));
            if (disabled || entry.Outcome is RestorePlanEntryOutcome.Excluded or
                RestorePlanEntryOutcome.Cancelled or RestorePlanEntryOutcome.Blocked)
            {
                continue;
            }

            IndexedAction[] entryActions = indexedActions.Where(action =>
                action.Action.EntryIndex == entry.EntryIndex).ToArray();
            if (entryActions.Length == 0)
                continue;
            IndexedAction firstAction = entryActions[0];

            if (entry.SelectedMatch is not null)
            {
                RestorePlanStaleReason? staleWindow = RevalidateWindow(
                    entry,
                    new IntPtr(entry.SelectedMatch.WindowHandle),
                    entry.SelectedMatch.ProcessId,
                    current);
                if (staleWindow is not null)
                {
                    MarkStale(firstAction, entries[entry.EntryIndex], results, staleWindow.Value);
                    continue;
                }
            }

            long[] plannedEligible = entry.Candidates
                .Where(candidate => candidate.IsEligible &&
                    (!consumed.Contains(new IntPtr(candidate.WindowHandle)) ||
                     entry.SelectedMatch?.WindowHandle == candidate.WindowHandle))
                .Select(candidate => candidate.WindowHandle)
                .OrderBy(handle => handle)
                .ToArray();
            long[] currentEligible = WindowMatcher.FindCandidates(
                    entry.SavedIdentity,
                    identities.Where(identity => !consumed.Contains(identity.Hwnd) ||
                        entry.SelectedMatch?.WindowHandle == identity.Hwnd.ToInt64()))
                .Where(candidate => candidate.IsEligible)
                .Select(candidate => candidate.Hwnd.ToInt64())
                .OrderBy(handle => handle)
                .ToArray();
            // Windows that disappeared since preview are allowed here. During a workspace
            // switch those are normally unrelated candidates that this transaction deliberately
            // asked to close. A newly appearing eligible HWND is different: it was never
            // reviewed and could change assignment, so that still invalidates the plan.
            if (currentEligible.Except(plannedEligible).Any())
            {
                MarkStale(
                    firstAction,
                    entries[entry.EntryIndex],
                    results,
                    RestorePlanStaleReason.WindowInventoryChanged);
            }
        }

        foreach (IndexedAction item in indexedActions.Where(action =>
                     IsLaunch(action.Action.Kind) && !results.ContainsKey(action.Index)))
        {
            RestoreResourceValidation validation = _resources.Revalidate(item.Action);
            if (validation.IsAvailable) continue;
            RestorePlanStaleReason reason = validation.Availability == RestoreResourceAvailability.Missing
                ? RestorePlanStaleReason.ResourceMissing
                : RestorePlanStaleReason.ResourceChanged;
            results[item.Index] = Result(
                item,
                RestoreExecutionActionStatus.Stale,
                reason,
                validation.Explanation);
            if (item.Action.EntryIndex is int entryIndex &&
                entries.TryGetValue(entryIndex, out EntryExecutionState? state))
            {
                state.Status = RestoreExecutionEntryStatus.Stale;
                state.Explanation = validation.Explanation;
            }
        }

        foreach (IndexedAction item in indexedActions.Where(action =>
                     action.Action.Kind == RestoreActionKind.RestoreBrowserSession &&
                     !results.ContainsKey(action.Index)))
        {
            if (_browserConnector is not null) continue;
            results[item.Index] = Result(
                item,
                RestoreExecutionActionStatus.Stale,
                RestorePlanStaleReason.BrowserSessionUnavailable,
                "The browser-session connector became unavailable while the preview was open.");
        }

        if (!results.Values.Any(result => result.Status == RestoreExecutionActionStatus.Stale))
            return null;

        MarkRemaining(
            indexedActions,
            results,
            RestoreExecutionActionStatus.Skipped,
            "The approved preview became stale before execution; no plan mutation was started.");
        return Complete(
            plan,
            entries,
            results,
            assignedHwnds,
            RestoreExecutionStatus.StalePlan,
            wasCancelled: false);
    }

    private void ExecuteWindowAction(
        IndexedAction item,
        IDictionary<int, EntryExecutionState> entries,
        IDictionary<int, RestoreExecutionActionResult> results,
        ISet<IntPtr> assignedHwnds)
    {
        if (item.Action.EntryIndex is not int entryIndex ||
            !entries.TryGetValue(entryIndex, out EntryExecutionState? state) ||
            item.Action.WindowHandle is not long handle ||
            item.Action.TargetPlacement is null)
        {
            results[item.Index] = Result(
                item,
                RestoreExecutionActionStatus.Failed,
                staleReason: null,
                "The approved window action is incomplete.");
            return;
        }

        uint expectedPid = state.PlanEntry.SelectedMatch?.ProcessId ?? 0;
        RestorePlanStaleReason? stale = RevalidateWindow(
            state.PlanEntry,
            new IntPtr(handle),
            expectedPid);
        if (stale is not null)
        {
            MarkStale(item, state, results, stale.Value);
            return;
        }

        _windowMutation.RestoreSingleWindow(
            new IntPtr(handle),
            ToWindowRecord(item.Action.TargetPlacement));
        assignedHwnds.Add(new IntPtr(handle));
        state.Status = RestoreExecutionEntryStatus.Restored;
        state.AssignedWindowHandle = handle;
        state.PlacementActionIndex = item.Index;
        state.Explanation = "Applied the approved placement to the revalidated live window.";
        results[item.Index] = Result(
            item,
            RestoreExecutionActionStatus.Succeeded,
            staleReason: null,
            state.Explanation);
    }

    private bool ExecuteLaunchAction(
        IndexedAction item,
        IDictionary<int, EntryExecutionState> entries,
        IDictionary<int, RestoreExecutionActionResult> results)
    {
        RestoreResourceValidation validation = _resources.Revalidate(item.Action);
        if (!validation.IsAvailable)
        {
            RestorePlanStaleReason reason = validation.Availability == RestoreResourceAvailability.Missing
                ? RestorePlanStaleReason.ResourceMissing
                : RestorePlanStaleReason.ResourceChanged;
            results[item.Index] = Result(
                item,
                RestoreExecutionActionStatus.Stale,
                reason,
                validation.Explanation);
            if (item.Action.EntryIndex is int staleIndex &&
                entries.TryGetValue(staleIndex, out EntryExecutionState? staleEntry))
            {
                staleEntry.Status = RestoreExecutionEntryStatus.Stale;
                staleEntry.Explanation = validation.Explanation;
            }
            return false;
        }

        try
        {
            _processLauncher.Launch(item.Action);
            results[item.Index] = Result(
                item,
                RestoreExecutionActionStatus.Succeeded,
                staleReason: null,
                "Executed the approved launch action after resource revalidation.");
            if (item.Action.EntryIndex is int entryIndex &&
                entries.TryGetValue(entryIndex, out EntryExecutionState? state) &&
                state.Status != RestoreExecutionEntryStatus.Restored)
            {
                state.Status = RestoreExecutionEntryStatus.LaunchRequested;
                state.Explanation = "The approved launch action was requested successfully.";
            }
            return true;
        }
        catch (Exception ex)
        {
            results[item.Index] = Result(
                item,
                RestoreExecutionActionStatus.Failed,
                staleReason: null,
                $"The approved launch action failed ({ex.GetType().Name}).");
            if (item.Action.EntryIndex is int entryIndex &&
                entries.TryGetValue(entryIndex, out EntryExecutionState? state) &&
                state.Status != RestoreExecutionEntryStatus.Restored)
            {
                state.Status = RestoreExecutionEntryStatus.Failed;
                state.Explanation = "The approved launch action failed.";
            }
            return false;
        }
    }

    private async Task<bool> ReconcileAwaitingWindowsAsync(
        IReadOnlyList<IndexedAction> awaitActions,
        IDictionary<int, EntryExecutionState> entries,
        IDictionary<int, RestoreExecutionActionResult> results,
        HashSet<IntPtr> assignedHwnds,
        CancellationToken cancellationToken,
        IProgress<RestoreProgressReport>? progress)
    {
        var pending = new Dictionary<int, (IndexedAction Action, AppReadinessTracker Tracker)>();
        foreach (IndexedAction item in awaitActions)
        {
            if (results.ContainsKey(item.Index) || item.Action.EntryIndex is not int entryIndex ||
                !entries.TryGetValue(entryIndex, out EntryExecutionState? state))
            {
                continue;
            }
            if (state.AssignedWindowHandle is not null)
            {
                results[item.Index] = Result(
                    item,
                    RestoreExecutionActionStatus.Skipped,
                    staleReason: null,
                    "The entry already owns a revalidated live window.");
                continue;
            }
            if (state.Status is RestoreExecutionEntryStatus.Stale or RestoreExecutionEntryStatus.Failed)
            {
                results[item.Index] = Result(
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
                CancelPendingReadiness(pending.Values, entries, results);
                return false;
            }

            AppReadinessObservation observation = _readinessProbe.Observe();
            TimeSpan elapsed = _clock.GetElapsedTime(startedAt);
            int elapsedSecond = Math.Max(0, (int)elapsed.TotalSeconds);
            if (pending.Count != lastReportedCount || elapsedSecond != lastReportedSecond)
            {
                string[] waitingFor = pending.Values
                    .Select(value => EntryDisplayName(
                        entries[value.Action.Action.EntryIndex!.Value].PlanEntry))
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
            foreach ((IndexedAction item, AppReadinessTracker tracker) in
                     pending.Values.ToArray())
            {
                int entryIndex = item.Action.EntryIndex!.Value;
                EntryExecutionState state = entries[entryIndex];
                AppReadinessEvaluation evaluation = _readinessEngine.Evaluate(
                    state.PlanEntry,
                    observation,
                    assignedHwnds,
                    tracker,
                    elapsed);
                state.ReadinessState = evaluation.State;
                state.ReadinessStrategy = evaluation.Strategy;
                state.Explanation = evaluation.Explanation;

                if (evaluation.State == AppReadinessState.Ready &&
                    evaluation.Candidate is { } selected)
                {
                    RestorePlanStaleReason? stale = RevalidateWindow(
                        state.PlanEntry,
                        selected.Hwnd,
                        selected.ProcessId);
                    if (stale is not null)
                    {
                        MarkStale(item, state, results, stale.Value);
                        results[item.Index] = results[item.Index] with
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
                            ToWindowRecord(placement));
                        assignedHwnds.Add(selected.Hwnd);
                        state.Status = RestoreExecutionEntryStatus.Restored;
                        state.AssignedWindowHandle = selected.Hwnd.ToInt64();
                        state.PlacementActionIndex = item.Index;
                        state.Explanation =
                            "Assigned and positioned a responsive, stable eligible window.";
                        results[item.Index] = Result(
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
                    results[item.Index] = Result(
                        item,
                        RestoreExecutionActionStatus.Failed,
                        staleReason: null,
                        evaluation.Explanation,
                        readinessState: evaluation.State,
                        readinessStrategy: evaluation.Strategy);
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
                CancelPendingReadiness(pending.Values, entries, results);
                return false;
            }
        }

        return true;
    }

    private static bool HasSuccessfulRelatedActivity(
        RestorePlan plan,
        IndexedAction awaitAction,
        IReadOnlyList<IndexedAction> indexedActions,
        IReadOnlyDictionary<int, RestoreExecutionActionResult> results,
        bool? browserSessionSucceeded)
    {
        if (awaitAction.Action.EntryIndex is not int entryIndex)
            return false;
        RestorePlanEntry? entry = plan.Entries.FirstOrDefault(item =>
            item.EntryIndex == entryIndex);
        if (entry is null)
            return false;

        if (entry.Outcome == RestorePlanEntryOutcome.AwaitingBrowserSession &&
            browserSessionSucceeded == true)
        {
            return true;
        }

        bool SuccessfulLaunch(IndexedAction item) =>
            IsLaunch(item.Action.Kind) &&
            results.TryGetValue(item.Index, out RestoreExecutionActionResult? result) &&
            result.Status == RestoreExecutionActionStatus.Succeeded;

        if (indexedActions.Any(item =>
                item.Action.EntryIndex == entryIndex && SuccessfulLaunch(item)))
        {
            return true;
        }

        if (entry.Outcome != RestorePlanEntryOutcome.AwaitingRunningApplication)
            return false;

        string expectedExecutable = WindowIdentityExtractor.NormalizePath(
            entry.SavedIdentity.ExecutablePath);
        string expectedProcess = NormalizeProcessName(entry.SavedIdentity.ProcessName);
        return indexedActions.Any(item =>
        {
            if (!SuccessfulLaunch(item) || item.Action.EntryIndex is not int sourceEntryIndex)
                return false;
            RestorePlanEntry? source = plan.Entries.FirstOrDefault(candidate =>
                candidate.EntryIndex == sourceEntryIndex);
            if (source is null)
                return false;
            string sourceExecutable = WindowIdentityExtractor.NormalizePath(
                source.SavedIdentity.ExecutablePath);
            string sourceProcess = NormalizeProcessName(source.SavedIdentity.ProcessName);
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
        IEnumerable<(IndexedAction Action, AppReadinessTracker Tracker)> pending,
        IDictionary<int, EntryExecutionState> entries,
        IDictionary<int, RestoreExecutionActionResult> results)
    {
        foreach ((IndexedAction item, _) in pending)
        {
            if (item.Action.EntryIndex is int entryIndex &&
                entries.TryGetValue(entryIndex, out EntryExecutionState? state))
            {
                state.Status = RestoreExecutionEntryStatus.Cancelled;
                state.Explanation = "Cancellation interrupted application readiness polling.";
                results[item.Index] = Result(
                    item,
                    RestoreExecutionActionStatus.Cancelled,
                    staleReason: null,
                    state.Explanation,
                    readinessState: state.ReadinessState,
                    readinessStrategy: state.ReadinessStrategy);
            }
        }
    }

    private async Task<bool> VerifyAssignedPlacementsAsync(
        IDictionary<int, EntryExecutionState> entries,
        IDictionary<int, RestoreExecutionActionResult> results,
        CancellationToken cancellationToken,
        IProgress<RestoreProgressReport>? progress)
    {
        var pending = entries.Values
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

        // Capture whether SetWindowPlacement was ever visibly accepted. If the app later moves
        // away, the final report can distinguish MovedByApp from a mutation it rejected outright.
        foreach (PlacementVerificationSession session in pending.Values)
            ObserveImmediateAcceptance(session);

        TimeSpan initialDelay = pending.Values.Max(session => session.Policy.InitialDelay);
        if (initialDelay > TimeSpan.Zero &&
            !await DelayOrCancelAsync(initialDelay, cancellationToken).ConfigureAwait(false))
        {
            CancelPendingPlacementVerification(pending.Values, results);
            return false;
        }

        while (pending.Count > 0)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                CancelPendingPlacementVerification(pending.Values, results);
                return false;
            }

            foreach (PlacementVerificationSession session in pending.Values.ToArray())
            {
                EntryExecutionState state = session.State;
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
                    FinalizePlacementVerification(session, evaluation, results);
                    pending.Remove(state.PlanEntry.EntryIndex);
                    progress?.Report(new RestoreProgressReport(
                        RestoreProgressStage.VerifyingPlacements,
                        "Verifying window placements",
                        EntryDisplayName(state.PlanEntry),
                        totalPlacements - pending.Count,
                        totalPlacements));
                    continue;
                }

                uint expectedPid = state.PlanEntry.SelectedMatch?.ProcessId ?? 0;
                RestorePlanStaleReason? stale = RevalidateWindow(
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
                    FinalizePlacementVerification(session, staleEvaluation, results);
                    pending.Remove(state.PlanEntry.EntryIndex);
                    progress?.Report(new RestoreProgressReport(
                        RestoreProgressStage.VerifyingPlacements,
                        "Verifying window placements",
                        EntryDisplayName(state.PlanEntry),
                        totalPlacements - pending.Count,
                        totalPlacements));
                    continue;
                }

                _windowMutation.RestoreSingleWindow(
                    hwnd,
                    ToWindowRecord(state.PlanEntry.TargetPlacement));
                session.RetryCount++;
                ObserveImmediateAcceptance(session);
            }

            if (pending.Count == 0)
                break;

            TimeSpan retryDelay = pending.Values.Max(session => session.Policy.RetryDelay);
            if (retryDelay > TimeSpan.Zero &&
                !await DelayOrCancelAsync(retryDelay, cancellationToken).ConfigureAwait(false))
            {
                CancelPendingPlacementVerification(pending.Values, results);
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
        EntryExecutionState state = session.State;
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
            EntryExecutionState state = session.State;
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

    private RestorePlanStaleReason? RevalidateWindow(
        RestorePlanEntry entry,
        IntPtr hwnd,
        uint expectedPid)
    {
        Dictionary<IntPtr, (uint Pid, WindowRecord Record)> current =
            _windowInventory.GetWindowsWithPids(WindowCandidatePolicy.RestoreMatchCandidate);
        return RevalidateWindow(entry, hwnd, expectedPid, current);
    }

    private RestorePlanStaleReason? RevalidateWindow(
        RestorePlanEntry entry,
        IntPtr hwnd,
        uint expectedPid,
        IReadOnlyDictionary<IntPtr, (uint Pid, WindowRecord Record)> current)
    {
        if (!current.TryGetValue(hwnd, out (uint Pid, WindowRecord Record) observed))
        {
            return _windowInventory.IsWindowAlive(hwnd)
                ? RestorePlanStaleReason.WindowNoLongerEligible
                : RestorePlanStaleReason.WindowClosed;
        }
        if (expectedPid != 0 && observed.Pid != expectedPid)
            return RestorePlanStaleReason.WindowReplaced;

        LiveWindowIdentity live = WindowIdentityExtractor.FromLive(hwnd, observed.Pid, observed.Record);
        bool remainsEligible = WindowMatcher.FindCandidates(entry.SavedIdentity, [live])
            .Any(candidate => candidate.IsEligible);
        return remainsEligible ? null : RestorePlanStaleReason.WindowNoLongerEligible;
    }

    private static void MarkStale(
        IndexedAction item,
        EntryExecutionState state,
        IDictionary<int, RestoreExecutionActionResult> results,
        RestorePlanStaleReason reason)
    {
        string explanation = reason switch
        {
            RestorePlanStaleReason.WindowClosed =>
                "The approved HWND was closed before its placement mutation.",
            RestorePlanStaleReason.WindowReplaced =>
                "The approved HWND now belongs to a different process.",
            RestorePlanStaleReason.WindowInventoryChanged =>
                "Eligible live-window candidates changed while the preview was open.",
            _ => "The approved HWND no longer satisfies the saved identity."
        };
        state.Status = RestoreExecutionEntryStatus.Stale;
        state.Explanation = explanation;
        results[item.Index] = Result(
            item,
            RestoreExecutionActionStatus.Stale,
            reason,
            explanation);
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

    private static RestoreExecutionResult Complete(
        RestorePlan plan,
        IReadOnlyDictionary<int, EntryExecutionState> entries,
        IReadOnlyDictionary<int, RestoreExecutionActionResult> actions,
        IReadOnlySet<IntPtr> assignedHwnds,
        RestoreExecutionStatus status,
        bool wasCancelled)
    {
        RestoreExecutionEntryResult[] entryResults = entries.Values
            .OrderBy(entry => entry.PlanEntry.EntryIndex)
            .Select(entry => new RestoreExecutionEntryResult(
                entry.PlanEntry.EntryIndex,
                entry.PlanEntry.EntryId,
                entry.Status,
                entry.AssignedWindowHandle,
                entry.Explanation,
                entry.ReadinessState,
                entry.ReadinessStrategy,
                entry.PlacementVerification,
                entry.PlacementRetryCount,
                entry.PlacementVerificationStrategy,
                entry.PlacementTolerancePixels))
            .ToArray();
        return new RestoreExecutionResult(
            plan.WorkspaceId,
            status,
            wasCancelled,
            entryResults,
            actions.Values.OrderBy(action => action.ActionIndex).ToArray(),
            assignedHwnds.Select(hwnd => hwnd.ToInt64()).ToHashSet());
    }

    private static void MarkRemaining(
        IEnumerable<IndexedAction> actions,
        IDictionary<int, RestoreExecutionActionResult> results,
        RestoreExecutionActionStatus status,
        string explanation)
    {
        foreach (IndexedAction item in actions.Where(item => !results.ContainsKey(item.Index)))
            results[item.Index] = Result(item, status, staleReason: null, explanation);
    }

    private static RestoreExecutionActionResult Result(
        IndexedAction item,
        RestoreExecutionActionStatus status,
        RestorePlanStaleReason? staleReason,
        string explanation,
        long? windowHandle = null,
        AppReadinessState? readinessState = null,
        string? readinessStrategy = null) => new(
            item.Index,
            item.Action.EntryIndex,
            item.Action.Kind,
            status,
            staleReason,
            windowHandle ?? item.Action.WindowHandle,
            explanation,
            readinessState,
            readinessStrategy);

    private static string EntryDisplayName(RestorePlanEntry entry)
    {
        string processName = entry.SavedIdentity.ProcessName;
        if (!string.IsNullOrWhiteSpace(processName))
            return processName;

        try
        {
            string fileName = Path.GetFileNameWithoutExtension(entry.SavedIdentity.ExecutablePath);
            if (!string.IsNullOrWhiteSpace(fileName))
                return fileName;
        }
        catch
        {
            // Invalid persisted paths must not prevent local progress reporting.
        }

        return $"entry {entry.EntryIndex + 1}";
    }

    private static string NormalizeProcessName(string? processName)
    {
        string value = (processName ?? "").Trim();
        return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? value[..^4]
            : value;
    }

    private static bool IsLaunch(RestoreActionKind kind) => kind is
        RestoreActionKind.LaunchApplication or
        RestoreActionKind.OpenResource or
        RestoreActionKind.LaunchDedicatedBrowser or
        RestoreActionKind.LaunchWebApp or
        RestoreActionKind.ActivatePackagedApplication;

    private static WindowRecord ToWindowRecord(RestoreTargetPlacement placement) => new()
    {
        NormalLeft = placement.Left,
        NormalTop = placement.Top,
        NormalRight = placement.Right,
        NormalBottom = placement.Bottom,
        ShowCmd = placement.ShowCmd,
        SavedDpi = placement.TargetDpi,
        CoordinatesAreFinal = true,
        MonitorId = placement.TargetMonitorId,
        MonitorIndex = placement.TargetMonitorIndex
    };

    private static BrowserSession ToBrowserSession(RestoreBrowserSession session) => new()
    {
        Browser = session.Browser,
        ActiveTitle = session.ActiveTitle,
        WindowIndex = session.WindowIndex,
        Left = session.Left,
        Top = session.Top,
        Width = session.Width,
        Height = session.Height,
        State = session.State,
        Tabs = session.Tabs.Select(tab => new BrowserTab
        {
            Url = tab.Url,
            Title = tab.Title,
            Index = tab.Index,
            Active = tab.Active,
            Pinned = tab.Pinned,
            GroupIndex = tab.GroupIndex
        }).ToList(),
        Groups = session.Groups.Select(group => new BrowserTabGroup
        {
            Index = group.Index,
            Title = group.Title,
            Color = group.Color,
            Collapsed = group.Collapsed
        }).ToList()
    };

    private readonly record struct IndexedAction(int Index, RestoreAction Action);

    private sealed class PlacementVerificationSession
    {
        internal PlacementVerificationSession(
            EntryExecutionState state,
            string strategy,
            WindowPlacementVerificationPolicy policy)
        {
            State = state;
            Strategy = strategy;
            Policy = policy;
        }

        internal EntryExecutionState State { get; }
        internal string Strategy { get; }
        internal WindowPlacementVerificationPolicy Policy { get; }
        internal int RetryCount { get; set; }
        internal bool WasApplied { get; set; }
    }

    private sealed class EntryExecutionState
    {
        internal EntryExecutionState(RestorePlanEntry planEntry)
        {
            PlanEntry = planEntry;
            Status = planEntry.Outcome switch
            {
                RestorePlanEntryOutcome.Excluded => RestoreExecutionEntryStatus.Excluded,
                RestorePlanEntryOutcome.Blocked => RestoreExecutionEntryStatus.Blocked,
                RestorePlanEntryOutcome.Cancelled => RestoreExecutionEntryStatus.Cancelled,
                _ => RestoreExecutionEntryStatus.Pending
            };
            Explanation = planEntry.Explanation;
        }

        internal RestorePlanEntry PlanEntry { get; }
        internal RestoreExecutionEntryStatus Status { get; set; }
        internal long? AssignedWindowHandle { get; set; }
        internal string Explanation { get; set; }
        internal AppReadinessState? ReadinessState { get; set; }
        internal string? ReadinessStrategy { get; set; }
        internal int? PlacementActionIndex { get; set; }
        internal WindowPlacementVerificationState? PlacementVerification { get; set; }
        internal int PlacementRetryCount { get; set; }
        internal string? PlacementVerificationStrategy { get; set; }
        internal int? PlacementTolerancePixels { get; set; }
    }
}
