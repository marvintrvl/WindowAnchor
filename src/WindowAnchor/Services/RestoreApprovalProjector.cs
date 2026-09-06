using System;
using System.Collections.Generic;
using System.Linq;

namespace WindowAnchor.Services;

/// <summary>Pure approval projection over an already-built restore preview.</summary>
internal static class RestoreApprovalProjector
{
    /// <summary>
    /// Derives an approved plan from an immutable preview without observing the environment or
    /// recomputing any match. Disabled entries retain their preview evidence but contribute no
    /// executable actions or blocking errors.
    /// </summary>
    internal static RestorePlan DeriveApprovedPlan(
        RestorePlan preview,
        IEnumerable<int> disabledEntryIndexes)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(disabledEntryIndexes);

        HashSet<int> disabled = preview.DisabledEntryIndexes
            .Concat(disabledEntryIndexes)
            .Where(index => index >= 0 && index < preview.Entries.Count)
            .ToHashSet();
        bool disabledOrdinaryBrowser = preview.Entries.Any(entry =>
            disabled.Contains(entry.EntryIndex) &&
            !entry.SavedIdentity.IsWebApp &&
            !entry.SavedIdentity.IsDedicatedBrowserWindow &&
            !string.IsNullOrWhiteSpace(entry.SavedIdentity.BrowserFamily));

        RestoreAction AdaptAction(RestoreAction action) =>
            disabledOrdinaryBrowser &&
            action.Condition == RestoreActionCondition.BrowserSessionUnavailable
                ? action with
                {
                    Condition = RestoreActionCondition.Always,
                    Explanation = "Launch the browser directly because browser-session " +
                                  "restoration was disabled with a browser entry."
                }
                : action;

        RestorePlanEntry[] entries = preview.Entries.Select(entry =>
        {
            if (disabled.Contains(entry.EntryIndex))
            {
                return entry with
                {
                    Outcome = RestorePlanEntryOutcome.Excluded,
                    Explanation = "The user disabled this entry in the approved preview.",
                    LaunchRequirement = RestoreLaunchRequirement.None(
                        "No launch is approved for a disabled entry."),
                    Actions = Array.Empty<RestoreAction>(),
                    Warnings = entry.Warnings
                        .Where(issue => issue.Code != RestorePlanIssueCode.AmbiguousMatch)
                        .ToArray(),
                    BlockingErrors = Array.Empty<RestorePlanIssue>()
                };
            }

            return entry with { Actions = entry.Actions.Select(AdaptAction).ToArray() };
        }).ToArray();

        bool hasApprovedEntry = entries.Any(entry => entry.Outcome is not (
            RestorePlanEntryOutcome.Excluded or
            RestorePlanEntryOutcome.Cancelled or
            RestorePlanEntryOutcome.Blocked));
        RestoreAction[] actions = preview.Actions
            .Where(action => action.EntryIndex is not int index || !disabled.Contains(index))
            .Where(action => !disabledOrdinaryBrowser ||
                action.Kind != RestoreActionKind.RestoreBrowserSession)
            .Where(action => hasApprovedEntry ||
                action.Kind != RestoreActionKind.MinimizeOtherWindows)
            .Select(AdaptAction)
            .ToArray();
        RestorePlanIssue[] entryWarnings = preview.Entries
            .SelectMany(entry => entry.Warnings)
            .ToArray();
        List<RestorePlanIssue> globalWarnings = preview.Warnings.ToList();
        foreach (RestorePlanIssue warning in entryWarnings)
            globalWarnings.Remove(warning);
        RestorePlanIssue[] approvedWarnings = globalWarnings
            .Concat(entries.SelectMany(entry => entry.Warnings))
            .ToArray();
        RestorePlanIssue[] approvedErrors = entries
            .SelectMany(entry => entry.BlockingErrors)
            .ToArray();
        HashSet<long> protectedHandles = preview.ProtectedWindowHandles
            .Concat(preview.Entries
                .Where(entry => disabled.Contains(entry.EntryIndex))
                .Select(entry => entry.SelectedMatch?.WindowHandle)
                .OfType<long>())
            .ToHashSet();

        return preview with
        {
            DisabledEntryIndexes = disabled,
            ProtectedWindowHandles = protectedHandles,
            BrowserSessions = disabledOrdinaryBrowser
                ? Array.Empty<RestoreBrowserSession>()
                : preview.BrowserSessions.ToArray(),
            Entries = entries,
            Actions = actions,
            Warnings = approvedWarnings,
            BlockingErrors = approvedErrors
        };
    }

    /// <summary>
    /// Returns a new plan with one ambiguous entry assigned to the candidate explicitly selected
    /// by the user. No matching or external observation is repeated.
    /// </summary>
    internal static RestorePlan ResolveAmbiguousMatch(
        RestorePlan preview,
        int entryIndex,
        long windowHandle)
    {
        ArgumentNullException.ThrowIfNull(preview);
        RestorePlanEntry entry = preview.Entries.SingleOrDefault(item =>
                item.EntryIndex == entryIndex)
            ?? throw new ArgumentOutOfRangeException(nameof(entryIndex));
        bool isUnresolvedAmbiguity = entry.SelectedMatch is null && entry.Warnings.Any(issue =>
            issue.Code == RestorePlanIssueCode.AmbiguousMatch);
        bool isUserResolvedAmbiguity = entry.SelectedMatch?.IsUserSelected == true;
        if (!isUnresolvedAmbiguity && !isUserResolvedAmbiguity)
        {
            throw new InvalidOperationException(
                "Only an ambiguous entry or a prior user-selected match can accept a candidate selection.");
        }
        RestorePlanCandidate candidate = entry.Candidates.SingleOrDefault(item =>
                item.WindowHandle == windowHandle && item.IsEligible)
            ?? throw new ArgumentException(
                "The requested window is not an eligible candidate for this entry.",
                nameof(windowHandle));
        bool ownedByAnotherEntry = preview.Entries.Any(item =>
            item.EntryIndex != entryIndex &&
            item.SelectedMatch?.WindowHandle == windowHandle &&
            !preview.DisabledEntryIndexes.Contains(item.EntryIndex));
        if (ownedByAnotherEntry)
        {
            throw new InvalidOperationException(
                "The selected window is already assigned to another workspace entry.");
        }

        WindowMatchEvidence selectionEvidence = new(
            WindowMatchEvidenceKind.UserSelectedCandidate,
            true,
            0,
            "The user explicitly selected this candidate in Restore Preview.");
        RestorePlanCandidate selected = candidate with
        {
            Confidence = candidate.Confidence == WindowMatchConfidence.Exact
                ? WindowMatchConfidence.Exact
                : WindowMatchConfidence.Strong,
            Evidence = candidate.Evidence.Append(selectionEvidence).ToArray(),
            IsUserSelected = true
        };
        RestoreAction placementAction = new(
            entryIndex,
            RestoreActionKind.RestoreExistingWindow,
            selected.WindowHandle,
            "",
            "",
            false,
            entry.TargetPlacement,
            "Assign the candidate explicitly selected in Restore Preview and apply its target placement.");
        RestorePlanEntry resolvedEntry = entry with
        {
            Outcome = RestorePlanEntryOutcome.Matched,
            Explanation = "The user resolved the ambiguous match by selecting a specific live window.",
            SelectedMatch = selected,
            LaunchRequirement = RestoreLaunchRequirement.None(
                "The selected live window is the user-confirmed target for this entry."),
            Actions = [placementAction],
            Warnings = entry.Warnings
                .Where(issue => issue.Code != RestorePlanIssueCode.AmbiguousMatch)
                .ToArray()
        };
        RestorePlanEntry[] entries = preview.Entries
            .Select(item => item.EntryIndex == entryIndex ? resolvedEntry : item)
            .ToArray();
        var actions = preview.Actions
            .Where(action => action.EntryIndex != entryIndex)
            .ToList();
        int terminalMinimize = actions.FindIndex(action =>
            action.Kind == RestoreActionKind.MinimizeOtherWindows);
        if (terminalMinimize >= 0)
            actions.Insert(terminalMinimize, placementAction);
        else
            actions.Add(placementAction);

        var currentEntryHandles = entry.Candidates
            .Where(item => item.IsWithinAmbiguityMargin)
            .Select(item => item.WindowHandle)
            .ToHashSet();
        HashSet<long> protectedHandles = preview.ProtectedWindowHandles
            .Where(handle => !currentEntryHandles.Contains(handle))
            .Concat(entries
                .Where(item => item.EntryIndex != entryIndex &&
                    item.Warnings.Any(issue => issue.Code == RestorePlanIssueCode.AmbiguousMatch))
                .SelectMany(item => item.Candidates)
                .Where(item => item.IsWithinAmbiguityMargin)
                .Select(item => item.WindowHandle))
            .ToHashSet();
        RestorePlanIssue[] entryWarnings = entries.SelectMany(item => item.Warnings).ToArray();
        List<RestorePlanIssue> globalWarnings = preview.Warnings.ToList();
        foreach (RestorePlanIssue warning in preview.Entries.SelectMany(item => item.Warnings))
            globalWarnings.Remove(warning);

        return preview with
        {
            Entries = entries,
            Actions = actions.ToArray(),
            Warnings = globalWarnings.Concat(entryWarnings).ToArray(),
            ProtectedWindowHandles = protectedHandles
        };
    }
}
