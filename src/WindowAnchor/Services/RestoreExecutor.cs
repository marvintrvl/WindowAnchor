using System;
using System.Collections.Generic;
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
    private static readonly TimeSpan InitialApplicationDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SlowLauncherDelay = TimeSpan.FromSeconds(2);

    private readonly IWindowInventory _windowInventory;
    private readonly IWindowMutation _windowMutation;
    private readonly IRestoreProcessLauncher _processLauncher;
    private readonly IRestoreClock _clock;
    private readonly IRestoreResourceBoundary _resources;
    private readonly IBrowserSessionConnector? _browserConnector;

    public RestoreExecutor(
        IWindowInventory windowInventory,
        IWindowMutation windowMutation,
        IRestoreProcessLauncher processLauncher,
        IRestoreClock clock,
        IRestoreResourceBoundary resources,
        IBrowserSessionConnector? browserConnector = null)
    {
        _windowInventory = windowInventory ?? throw new ArgumentNullException(nameof(windowInventory));
        _windowMutation = windowMutation ?? throw new ArgumentNullException(nameof(windowMutation));
        _processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _browserConnector = browserConnector;
    }

    /// <summary>Executes an approved plan through injectable process, browser, window, and clock boundaries.</summary>
    public async Task<RestoreExecutionResult> ExecuteAsync(
        RestorePlan plan,
        CancellationToken cancellationToken = default)
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
        bool activityCanCreateWindows = false;

        foreach (IndexedAction item in indexedActions.Where(item =>
                     item.Action.Kind == RestoreActionKind.RestoreBrowserSession))
        {
            bool succeeded = await ExecuteBrowserActionAsync(
                plan,
                item,
                results,
                cancellationToken).ConfigureAwait(false);
            browserSessionSucceeded = succeeded;
            activityCanCreateWindows |= succeeded;
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

        foreach (IndexedAction item in indexedActions.Where(item => IsLaunch(item.Action.Kind)))
        {
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

            bool launched = ExecuteLaunchAction(item, entries, results);
            activityCanCreateWindows |= launched;
        }

        IndexedAction[] awaitActions = indexedActions
            .Where(item => item.Action.Kind == RestoreActionKind.AwaitWindowAppearance)
            .ToArray();

        if (activityCanCreateWindows && awaitActions.Length > 0)
        {
            if (!await DelayOrCancelAsync(InitialApplicationDelay, cancellationToken).ConfigureAwait(false))
            {
                MarkRemaining(indexedActions, results, RestoreExecutionActionStatus.Cancelled,
                    "Cancellation interrupted the initial application wait.");
                return Complete(
                    plan,
                    entries,
                    results,
                    assignedHwnds,
                    RestoreExecutionStatus.Cancelled,
                    wasCancelled: true);
            }

            ReconcileAwaitingWindows(awaitActions, entries, results, assignedHwnds);

            // Preserve the existing fixed second pass even when the first reconciliation found
            // every window. WA-004 replaces these fixed waits with readiness signals later.
            if (!await DelayOrCancelAsync(SlowLauncherDelay, cancellationToken).ConfigureAwait(false))
            {
                MarkRemaining(indexedActions, results, RestoreExecutionActionStatus.Cancelled,
                    "Cancellation interrupted the slow-launcher wait.");
                return Complete(
                    plan,
                    entries,
                    results,
                    assignedHwnds,
                    RestoreExecutionStatus.Cancelled,
                    wasCancelled: true);
            }
            ReconcileAwaitingWindows(awaitActions, entries, results, assignedHwnds);
        }

        foreach (IndexedAction item in awaitActions.Where(item => !results.ContainsKey(item.Index)))
        {
            results[item.Index] = Result(
                item,
                RestoreExecutionActionStatus.Skipped,
                staleReason: null,
                activityCanCreateWindows
                    ? "No eligible window appeared during the compatibility wait phases."
                    : "No successful launch or browser action required a reconciliation wait.");
            if (item.Action.EntryIndex is int entryIndex &&
                entries.TryGetValue(entryIndex, out EntryExecutionState? state) &&
                state.Status is RestoreExecutionEntryStatus.Pending or
                    RestoreExecutionEntryStatus.LaunchRequested)
            {
                state.Status = RestoreExecutionEntryStatus.AwaitingWindow;
                state.Explanation = "No eligible live window appeared during this execution.";
            }
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
                .Where(candidate => candidate.IsEligible)
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
            if (!plannedEligible.SequenceEqual(currentEligible))
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

    private void ReconcileAwaitingWindows(
        IEnumerable<IndexedAction> awaitActions,
        IDictionary<int, EntryExecutionState> entries,
        IDictionary<int, RestoreExecutionActionResult> results,
        ISet<IntPtr> assignedHwnds)
    {
        Dictionary<IntPtr, (uint Pid, WindowRecord Record)> liveRecords =
            _windowInventory.GetWindowsWithPids(WindowCandidatePolicy.RestoreMatchCandidate);
        LiveWindowIdentity[] liveIdentities = liveRecords
            .Select(window => WindowIdentityExtractor.FromLive(
                window.Key,
                window.Value.Pid,
                window.Value.Record))
            .OrderBy(window => window.Hwnd.ToInt64())
            .ToArray();

        foreach (IndexedAction item in awaitActions)
        {
            if (results.ContainsKey(item.Index) || item.Action.EntryIndex is not int entryIndex ||
                !entries.TryGetValue(entryIndex, out EntryExecutionState? state))
                continue;
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
                continue;

            WindowMatchCandidate? selected = WindowMatcher.FindCandidates(
                    state.PlanEntry.SavedIdentity,
                    liveIdentities.Where(window => !assignedHwnds.Contains(window.Hwnd)))
                .FirstOrDefault(candidate => candidate.IsEligible);
            if (selected is null) continue;

            RestorePlanStaleReason? stale = RevalidateWindow(
                state.PlanEntry,
                selected.Hwnd,
                selected.ProcessId);
            if (stale is not null)
            {
                MarkStale(item, state, results, stale.Value);
                continue;
            }

            RestoreTargetPlacement placement = item.Action.TargetPlacement ?? state.PlanEntry.TargetPlacement;
            _windowMutation.RestoreSingleWindow(selected.Hwnd, ToWindowRecord(placement));
            assignedHwnds.Add(selected.Hwnd);
            state.Status = RestoreExecutionEntryStatus.Restored;
            state.AssignedWindowHandle = selected.Hwnd.ToInt64();
            state.Explanation = "Assigned and positioned a newly appeared eligible window.";
            results[item.Index] = Result(
                item,
                RestoreExecutionActionStatus.Succeeded,
                staleReason: null,
                state.Explanation,
                selected.Hwnd.ToInt64());
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
                entry.Explanation))
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
        long? windowHandle = null) => new(
            item.Index,
            item.Action.EntryIndex,
            item.Action.Kind,
            status,
            staleReason,
            windowHandle ?? item.Action.WindowHandle,
            explanation);

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
    }
}
