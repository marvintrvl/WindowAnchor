using System;
using System.Collections.Generic;
using System.Linq;

namespace WindowAnchor.Services;

/// <summary>Projects the shared execution context into the stable public result models.</summary>
internal static class RestoreResultAggregator
{
    internal static RestoreExecutionResult Complete(
        RestoreExecutionContext context,
        RestoreExecutionStatus status,
        bool wasCancelled)
    {
        RestoreExecutionEntryResult[] entryResults = context.Entries.Values
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
            context.Plan.WorkspaceId,
            status,
            wasCancelled,
            entryResults,
            context.Results.Values.OrderBy(action => action.ActionIndex).ToArray(),
            context.AssignedHwnds.Select(hwnd => hwnd.ToInt64()).ToHashSet());
    }

    internal static RestoreExecutionStatus DetermineStatus(RestoreExecutionContext context) =>
        context.Results.Values.Any(result => result.Status == RestoreExecutionActionStatus.Stale)
            ? RestoreExecutionStatus.StalePlan
            : context.Results.Values.Any(result => result.Status == RestoreExecutionActionStatus.Failed)
                ? RestoreExecutionStatus.CompletedWithFailures
                : RestoreExecutionStatus.Completed;
}
