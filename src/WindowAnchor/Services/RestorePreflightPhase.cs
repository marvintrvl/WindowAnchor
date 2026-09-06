using System;
using System.Collections.Generic;
using System.Linq;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Rejects an approved plan before mutation when reviewed preconditions became stale.</summary>
internal sealed class RestorePreflightPhase
{
    private readonly IWindowInventory _windowInventory;
    private readonly IRestoreResourceBoundary _resources;
    private readonly IBrowserSessionConnector? _browserConnector;
    private readonly RestoreWindowRevalidator _revalidator;

    internal RestorePreflightPhase(
        IWindowInventory windowInventory,
        IRestoreResourceBoundary resources,
        IBrowserSessionConnector? browserConnector,
        RestoreWindowRevalidator revalidator)
    {
        _windowInventory = windowInventory;
        _resources = resources;
        _browserConnector = browserConnector;
        _revalidator = revalidator;
    }

    internal RestoreExecutionResult? Execute(RestoreExecutionContext context)
    {
        RestorePlan plan = context.Plan;
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

            IndexedRestoreAction[] entryActions = context.IndexedActions.Where(action =>
                action.Action.EntryIndex == entry.EntryIndex).ToArray();
            if (entryActions.Length == 0)
                continue;
            IndexedRestoreAction firstAction = entryActions[0];

            if (entry.SelectedMatch is not null)
            {
                RestorePlanStaleReason? staleWindow = _revalidator.Revalidate(
                    entry,
                    new IntPtr(entry.SelectedMatch.WindowHandle),
                    entry.SelectedMatch.ProcessId,
                    current);
                if (staleWindow is not null)
                {
                    RestoreExecutionSupport.MarkStale(
                        firstAction,
                        context.Entries[entry.EntryIndex],
                        context.Results,
                        staleWindow.Value);
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
            // Disappearing candidates are expected while switching. A newly eligible HWND was not
            // reviewed and could change assignment, so it invalidates the approved plan.
            if (currentEligible.Except(plannedEligible).Any())
            {
                RestoreExecutionSupport.MarkStale(
                    firstAction,
                    context.Entries[entry.EntryIndex],
                    context.Results,
                    RestorePlanStaleReason.WindowInventoryChanged);
            }
        }

        foreach (IndexedRestoreAction item in context.IndexedActions.Where(action =>
                     RestoreExecutionSupport.IsLaunch(action.Action.Kind) &&
                     !context.Results.ContainsKey(action.Index)))
        {
            RestoreResourceValidation validation = _resources.Revalidate(item.Action);
            if (validation.IsAvailable)
                continue;

            RestorePlanStaleReason reason = validation.Availability == RestoreResourceAvailability.Missing
                ? RestorePlanStaleReason.ResourceMissing
                : RestorePlanStaleReason.ResourceChanged;
            context.Results[item.Index] = RestoreExecutionSupport.Result(
                item,
                RestoreExecutionActionStatus.Stale,
                reason,
                validation.Explanation);
            if (item.Action.EntryIndex is int entryIndex &&
                context.Entries.TryGetValue(entryIndex, out RestoreEntryExecutionState? state))
            {
                state.Status = RestoreExecutionEntryStatus.Stale;
                state.Explanation = validation.Explanation;
            }
        }

        foreach (IndexedRestoreAction item in context.IndexedActions.Where(action =>
                     action.Action.Kind == RestoreActionKind.RestoreBrowserSession &&
                     !context.Results.ContainsKey(action.Index)))
        {
            if (_browserConnector is not null)
                continue;
            context.Results[item.Index] = RestoreExecutionSupport.Result(
                item,
                RestoreExecutionActionStatus.Stale,
                RestorePlanStaleReason.BrowserSessionUnavailable,
                "The browser-session connector became unavailable while the preview was open.");
        }

        if (!context.Results.Values.Any(result =>
                result.Status == RestoreExecutionActionStatus.Stale))
        {
            return null;
        }

        RestoreExecutionSupport.MarkRemaining(
            context.IndexedActions,
            context.Results,
            RestoreExecutionActionStatus.Skipped,
            "The approved preview became stale before execution; no plan mutation was started.");
        return RestoreResultAggregator.Complete(
            context,
            RestoreExecutionStatus.StalePlan,
            wasCancelled: false);
    }
}
