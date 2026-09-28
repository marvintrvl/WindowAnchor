using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WindowAnchor.Services;

/// <summary>Runs the approved browser, existing-window, launch, and final minimize mutations.</summary>
internal sealed class RestoreBrowserAndLaunchPhase
{
    private readonly IWindowMutation _windowMutation;
    private readonly IRestoreProcessLauncher _processLauncher;
    private readonly IRestoreResourceBoundary _resources;
    private readonly IBrowserSessionConnector? _browserConnector;
    private readonly IExplorerTabSessionRestorer _explorerTabs;
    private readonly RestoreWindowRevalidator _revalidator;
    private readonly IVirtualDesktopAssociation _virtualDesktops;

    internal RestoreBrowserAndLaunchPhase(
        IWindowMutation windowMutation,
        IRestoreProcessLauncher processLauncher,
        IRestoreResourceBoundary resources,
        IBrowserSessionConnector? browserConnector,
        IExplorerTabSessionRestorer explorerTabs,
        RestoreWindowRevalidator revalidator,
        IVirtualDesktopAssociation virtualDesktops)
    {
        _windowMutation = windowMutation;
        _processLauncher = processLauncher;
        _resources = resources;
        _browserConnector = browserConnector;
        _explorerTabs = explorerTabs;
        _revalidator = revalidator;
        _virtualDesktops = virtualDesktops;
    }

    internal async Task RestoreBrowserSessionsAsync(
        RestoreExecutionContext context,
        CancellationToken cancellationToken,
        IProgress<RestoreProgressReport>? progress)
    {
        foreach (IndexedRestoreAction item in context.IndexedActions.Where(item =>
                     item.Action.Kind == RestoreActionKind.RestoreBrowserSession))
        {
            progress?.Report(new RestoreProgressReport(
                RestoreProgressStage.CapturingBrowserSession,
                "Restoring browser session",
                "Waiting for the browser connector."));
            context.BrowserSessionSucceeded = await ExecuteBrowserActionAsync(
                context,
                item,
                cancellationToken).ConfigureAwait(false);
        }
    }

    internal void RestoreExistingWindows(RestoreExecutionContext context)
    {
        foreach (IndexedRestoreAction item in context.IndexedActions.Where(item =>
                     item.Action.Kind == RestoreActionKind.RestoreExistingWindow))
        {
            ExecuteWindowAction(context, item);
        }
    }

    internal void LaunchApplications(
        RestoreExecutionContext context,
        IProgress<RestoreProgressReport>? progress)
    {
        IndexedRestoreAction[] launchActions = context.IndexedActions
            .Where(item => RestoreExecutionSupport.IsLaunch(item.Action.Kind))
            .ToArray();
        int launchIndex = 0;
        foreach (IndexedRestoreAction item in launchActions)
        {
            launchIndex++;
            RestorePlanEntry? launchEntry = item.Action.EntryIndex is int entryIndex
                ? context.Plan.Entries.FirstOrDefault(entry => entry.EntryIndex == entryIndex)
                : null;
            progress?.Report(new RestoreProgressReport(
                RestoreProgressStage.LaunchingApplications,
                launchEntry is null
                    ? "Launching applications"
                    : $"Launching {RestoreExecutionSupport.EntryDisplayName(launchEntry)}",
                launchEntry?.SavedIdentity.Title ?? "",
                launchIndex,
                launchActions.Length));
            if (item.Action.Condition == RestoreActionCondition.BrowserSessionUnavailable &&
                context.BrowserSessionSucceeded == true)
            {
                context.Results[item.Index] = RestoreExecutionSupport.Result(
                    item,
                    RestoreExecutionActionStatus.Skipped,
                    staleReason: null,
                    "The browser-session action succeeded, so its approved fallback was not needed.");
                continue;
            }

            ExecuteLaunchAction(context, item);
        }
    }

    internal void MinimizeOtherWindows(RestoreExecutionContext context)
    {
        foreach (IndexedRestoreAction item in context.IndexedActions.Where(item =>
                     item.Action.Kind == RestoreActionKind.MinimizeOtherWindows))
        {
            var keep = new HashSet<IntPtr>(context.AssignedHwnds);
            keep.UnionWith(context.Plan.ProtectedWindowHandles.Select(handle => new IntPtr(handle)));
            _windowMutation.MinimizeUserWindowsExcept(
                WindowCandidatePolicy.MinimizeCandidate,
                keep);
            context.Results[item.Index] = RestoreExecutionSupport.Result(
                item,
                RestoreExecutionActionStatus.Succeeded,
                staleReason: null,
                "Minimized windows outside the final approved assignment set.");
        }
    }

    internal void MoveWindowsToVirtualDesktops(
        RestoreExecutionContext context,
        IProgress<RestoreProgressReport>? progress)
    {
        IndexedRestoreAction[] actions = context.IndexedActions.Where(item =>
            item.Action.Kind == RestoreActionKind.MoveWindowToVirtualDesktop).ToArray();
        for (int index = 0; index < actions.Length; index++)
        {
            IndexedRestoreAction item = actions[index];
            progress?.Report(new RestoreProgressReport(
                RestoreProgressStage.MovingVirtualDesktops,
                $"Associating virtual desktop ({index + 1}/{actions.Length})"));
            if (item.Action.EntryIndex is not int entryIndex ||
                !context.Entries.TryGetValue(entryIndex, out RestoreEntryExecutionState? state) ||
                item.Action.WindowHandle is not long handle ||
                !Guid.TryParse(item.Action.Target, out Guid desktopId))
            {
                context.Results[item.Index] = RestoreExecutionSupport.Result(
                    item, RestoreExecutionActionStatus.Skipped, null,
                    "Virtual-desktop association was unavailable because the approved action was incomplete.");
                continue;
            }

            uint expectedPid = state.PlanEntry.SelectedMatch?.ProcessId ?? 0;
            if (_revalidator.Revalidate(state.PlanEntry, new IntPtr(handle), expectedPid) is not null)
            {
                context.Results[item.Index] = RestoreExecutionSupport.Result(
                    item, RestoreExecutionActionStatus.Skipped, null,
                    "The matched window changed before its optional virtual-desktop association; it was left accessible.");
                continue;
            }

            VirtualDesktopMoveResult result = _virtualDesktops.TryMoveWindowToDesktop(
                new IntPtr(handle), desktopId);
            context.Results[item.Index] = RestoreExecutionSupport.Result(
                item,
                result.Status == VirtualDesktopAssociationStatus.Available
                    ? RestoreExecutionActionStatus.Succeeded
                    : RestoreExecutionActionStatus.Skipped,
                null,
                result.Status == VirtualDesktopAssociationStatus.Available
                    ? "Moved the matched window to its saved virtual desktop."
                    : "The saved virtual desktop is unavailable or no longer exists; the window was left accessible.");
        }
    }

    internal async Task<bool> RestoreExplorerTabsAsync(
        RestoreExecutionContext context,
        CancellationToken cancellationToken,
        IProgress<RestoreProgressReport>? progress)
    {
        IndexedRestoreAction[] actions = context.IndexedActions
            .Where(item => item.Action.Kind == RestoreActionKind.RestoreExplorerTabs &&
                !context.Results.ContainsKey(item.Index))
            .ToArray();
        for (int actionIndex = 0; actionIndex < actions.Length; actionIndex++)
        {
            IndexedRestoreAction item = actions[actionIndex];
            if (item.Action.EntryIndex is not int entryIndex ||
                !context.Entries.TryGetValue(entryIndex, out RestoreEntryExecutionState? state) ||
                state.PlanEntry.ExplorerSession is not { } session)
            {
                context.Results[item.Index] = RestoreExecutionSupport.Result(
                    item,
                    RestoreExecutionActionStatus.Failed,
                    staleReason: null,
                    "The approved File Explorer tab action is incomplete.");
                continue;
            }

            long? handle = state.AssignedWindowHandle ?? item.Action.WindowHandle;
            if (handle is null)
            {
                state.Status = RestoreExecutionEntryStatus.Failed;
                state.Explanation = "The target File Explorer window did not become ready.";
                context.Results[item.Index] = RestoreExecutionSupport.Result(
                    item,
                    RestoreExecutionActionStatus.Failed,
                    staleReason: null,
                    state.Explanation);
                continue;
            }

            uint expectedPid = state.PlanEntry.SelectedMatch?.WindowHandle == handle
                ? state.PlanEntry.SelectedMatch.ProcessId
                : 0;
            RestorePlanStaleReason? stale = _revalidator.Revalidate(
                state.PlanEntry,
                new IntPtr(handle.Value),
                expectedPid);
            if (stale is not null)
            {
                RestoreExecutionSupport.MarkStale(item, state, context.Results, stale.Value);
                continue;
            }

            progress?.Report(new RestoreProgressReport(
                RestoreProgressStage.RestoringExplorerTabs,
                $"Restoring File Explorer tabs ({actionIndex + 1}/{actions.Length})",
                $"{session.TabPaths.Count} saved tabs",
                actionIndex + 1,
                actions.Length));
            try
            {
                ExplorerTabRestoreResult result = await _explorerTabs.RestoreAsync(
                    new IntPtr(handle.Value),
                    session,
                    cancellationToken).ConfigureAwait(false);
                bool succeeded = result.Succeeded;
                state.Status = succeeded
                    ? RestoreExecutionEntryStatus.Restored
                    : RestoreExecutionEntryStatus.Failed;
                state.AssignedWindowHandle = handle;
                context.AssignedHwnds.Add(new IntPtr(handle.Value));
                state.Explanation = succeeded
                    ? $"Restored {session.TabPaths.Count} saved File Explorer tabs."
                    : $"Restored {result.OpenedTabCount} File Explorer tabs; " +
                      $"{result.FailedTabCount} could not be restored.";
                context.Results[item.Index] = RestoreExecutionSupport.Result(
                    item,
                    succeeded
                        ? RestoreExecutionActionStatus.Succeeded
                        : RestoreExecutionActionStatus.Failed,
                    staleReason: null,
                    state.Explanation,
                    handle);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                state.Status = RestoreExecutionEntryStatus.Cancelled;
                state.Explanation = "File Explorer tab restoration was cancelled.";
                context.Results[item.Index] = RestoreExecutionSupport.Result(
                    item,
                    RestoreExecutionActionStatus.Cancelled,
                    staleReason: null,
                    state.Explanation,
                    handle);
                return false;
            }
            catch (Exception ex)
            {
                state.Status = RestoreExecutionEntryStatus.Failed;
                state.Explanation = $"File Explorer tab restoration failed ({ex.GetType().Name}).";
                context.Results[item.Index] = RestoreExecutionSupport.Result(
                    item,
                    RestoreExecutionActionStatus.Failed,
                    staleReason: null,
                    state.Explanation,
                    handle);
            }
        }
        return true;
    }

    private async Task<bool> ExecuteBrowserActionAsync(
        RestoreExecutionContext context,
        IndexedRestoreAction item,
        CancellationToken cancellationToken)
    {
        if (_browserConnector is null)
        {
            context.Results[item.Index] = RestoreExecutionSupport.Result(
                item,
                RestoreExecutionActionStatus.Stale,
                RestorePlanStaleReason.BrowserSessionUnavailable,
                "The approved browser-session connector is no longer available.");
            return false;
        }

        try
        {
            bool restored = await _browserConnector.RestoreAsync(
                context.Plan.WorkspaceName,
                context.Plan.BrowserSessions.Select(RestoreExecutionSupport.ToBrowserSession).ToList(),
                cancellationToken).ConfigureAwait(false);
            context.Results[item.Index] = RestoreExecutionSupport.Result(
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
            context.Results[item.Index] = RestoreExecutionSupport.Result(
                item,
                RestoreExecutionActionStatus.Cancelled,
                staleReason: null,
                "Browser-session restoration was cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            context.Results[item.Index] = RestoreExecutionSupport.Result(
                item,
                RestoreExecutionActionStatus.Failed,
                staleReason: null,
                $"Browser-session restoration failed ({ex.GetType().Name}); approved fallbacks remain eligible.");
            return false;
        }
    }

    private void ExecuteWindowAction(
        RestoreExecutionContext context,
        IndexedRestoreAction item)
    {
        if (item.Action.EntryIndex is not int entryIndex ||
            !context.Entries.TryGetValue(entryIndex, out RestoreEntryExecutionState? state) ||
            item.Action.WindowHandle is not long handle ||
            item.Action.TargetPlacement is null)
        {
            context.Results[item.Index] = RestoreExecutionSupport.Result(
                item,
                RestoreExecutionActionStatus.Failed,
                staleReason: null,
                "The approved window action is incomplete.");
            return;
        }

        uint expectedPid = state.PlanEntry.SelectedMatch?.ProcessId ?? 0;
        RestorePlanStaleReason? stale = _revalidator.Revalidate(
            state.PlanEntry,
            new IntPtr(handle),
            expectedPid);
        if (stale is not null)
        {
            RestoreExecutionSupport.MarkStale(item, state, context.Results, stale.Value);
            return;
        }

        _windowMutation.RestoreSingleWindow(
            new IntPtr(handle),
            RestoreExecutionSupport.ToWindowRecord(item.Action.TargetPlacement));
        context.AssignedHwnds.Add(new IntPtr(handle));
        state.Status = RestoreExecutionEntryStatus.Restored;
        state.AssignedWindowHandle = handle;
        state.PlacementActionIndex = item.Index;
        state.Explanation = "Applied the approved placement to the revalidated live window.";
        context.Results[item.Index] = RestoreExecutionSupport.Result(
            item,
            RestoreExecutionActionStatus.Succeeded,
            staleReason: null,
            state.Explanation);
    }

    private bool ExecuteLaunchAction(
        RestoreExecutionContext context,
        IndexedRestoreAction item)
    {
        RestoreResourceValidation validation = _resources.Revalidate(item.Action);
        if (!validation.IsAvailable)
        {
            RestorePlanStaleReason reason = validation.Availability == RestoreResourceAvailability.Missing
                ? RestorePlanStaleReason.ResourceMissing
                : RestorePlanStaleReason.ResourceChanged;
            context.Results[item.Index] = RestoreExecutionSupport.Result(
                item,
                RestoreExecutionActionStatus.Stale,
                reason,
                validation.Explanation);
            if (item.Action.EntryIndex is int staleIndex &&
                context.Entries.TryGetValue(staleIndex, out RestoreEntryExecutionState? staleEntry))
            {
                staleEntry.Status = RestoreExecutionEntryStatus.Stale;
                staleEntry.Explanation = validation.Explanation;
            }
            return false;
        }

        try
        {
            _processLauncher.Launch(item.Action);
            context.Results[item.Index] = RestoreExecutionSupport.Result(
                item,
                RestoreExecutionActionStatus.Succeeded,
                staleReason: null,
                "Executed the approved launch action after resource revalidation.");
            if (item.Action.EntryIndex is int entryIndex &&
                context.Entries.TryGetValue(entryIndex, out RestoreEntryExecutionState? state) &&
                state.Status != RestoreExecutionEntryStatus.Restored)
            {
                state.Status = RestoreExecutionEntryStatus.LaunchRequested;
                state.Explanation = "The approved launch action was requested successfully.";
            }
            return true;
        }
        catch (Exception ex)
        {
            context.Results[item.Index] = RestoreExecutionSupport.Result(
                item,
                RestoreExecutionActionStatus.Failed,
                staleReason: null,
                $"The approved launch action failed ({ex.GetType().Name}).");
            if (item.Action.EntryIndex is int entryIndex &&
                context.Entries.TryGetValue(entryIndex, out RestoreEntryExecutionState? state) &&
                state.Status != RestoreExecutionEntryStatus.Restored)
            {
                state.Status = RestoreExecutionEntryStatus.Failed;
                state.Explanation = "The approved launch action failed.";
            }
            return false;
        }
    }
}
