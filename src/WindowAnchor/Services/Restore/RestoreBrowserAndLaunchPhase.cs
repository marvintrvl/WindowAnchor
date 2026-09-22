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
    private readonly RestoreWindowRevalidator _revalidator;

    internal RestoreBrowserAndLaunchPhase(
        IWindowMutation windowMutation,
        IRestoreProcessLauncher processLauncher,
        IRestoreResourceBoundary resources,
        IBrowserSessionConnector? browserConnector,
        RestoreWindowRevalidator revalidator)
    {
        _windowMutation = windowMutation;
        _processLauncher = processLauncher;
        _resources = resources;
        _browserConnector = browserConnector;
        _revalidator = revalidator;
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
