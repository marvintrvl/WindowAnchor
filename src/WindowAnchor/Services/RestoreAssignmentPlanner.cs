using System;
using System.Collections.Generic;
using System.Linq;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>
/// Owns candidate ranking and one-HWND-per-entry assignment for one pure planning session.
/// The instance is deliberately scoped to a single <see cref="RestorePlanner.Build"/> call.
/// </summary>
internal sealed class RestoreAssignmentPlanner
{
    private readonly string _workspaceId;
    private readonly LiveWindowIdentity[] _liveWindows;
    private readonly IReadOnlyList<WindowMatchHint> _matchHints;
    private readonly HashSet<IntPtr> _consumedWindowHandles = new();
    private readonly HashSet<long> _protectedWindowHandles = new();

    internal RestoreAssignmentPlanner(
        string workspaceId,
        IEnumerable<LiveWindowIdentity> liveWindows,
        IReadOnlyList<WindowMatchHint> matchHints)
    {
        _workspaceId = workspaceId ?? "";
        ArgumentNullException.ThrowIfNull(liveWindows);
        _matchHints = matchHints ?? throw new ArgumentNullException(nameof(matchHints));
        _liveWindows = liveWindows
            .OrderBy(window => window.Hwnd.ToInt64())
            .ToArray();
    }

    internal IReadOnlySet<long> ProtectedWindowHandles => _protectedWindowHandles;

    internal RestoreAssignmentResult Resolve(
        WorkspaceEntry entry,
        SavedWindowIdentity savedIdentity)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(savedIdentity);

        WindowMatchHint? learnedHint = _matchHints.FirstOrDefault(hint =>
            hint.WorkspaceId.Equals(_workspaceId, StringComparison.OrdinalIgnoreCase) &&
            hint.EntryId.Equals(entry.EntryId, StringComparison.OrdinalIgnoreCase));
        WindowMatchResolution resolution = WindowMatchResolver.Resolve(
            savedIdentity,
            _liveWindows.Where(window => !_consumedWindowHandles.Contains(window.Hwnd)),
            learnedHint?.Identity);
        RestorePlanCandidate[] candidates = ToPlanCandidates(resolution.Candidates);
        WindowMatchCandidate? selectedMatch = resolution.SelectedCandidate;
        RestorePlanCandidate? selected = selectedMatch is null
            ? null
            : candidates.Single(candidate =>
                candidate.WindowHandle == selectedMatch.Hwnd.ToInt64());

        if (resolution.IsAmbiguous)
        {
            foreach (RestorePlanCandidate candidate in candidates.Where(candidate =>
                         candidate.IsWithinAmbiguityMargin))
            {
                _protectedWindowHandles.Add(candidate.WindowHandle);
            }
        }
        else if (selectedMatch is not null)
        {
            _consumedWindowHandles.Add(selectedMatch.Hwnd);
        }

        return new RestoreAssignmentResult(
            resolution,
            candidates,
            selectedMatch,
            selected);
    }

    private static RestorePlanCandidate[] ToPlanCandidates(
        IReadOnlyList<WindowMatchCandidate> candidates) => candidates
        .Select(candidate => new RestorePlanCandidate(
            candidate.Hwnd.ToInt64(),
            candidate.ProcessId,
            candidate.IsEligible,
            candidate.Score,
            candidate.Confidence,
            candidate.Evidence.ToArray(),
            candidate.TitleSimilarityScore,
            candidate.IsTopScoreTie,
            candidate.Title,
            candidate.ProcessName,
            candidate.WindowClassName,
            candidate.MonitorId,
            candidate.Bounds,
            candidate.IdentityHint,
            candidate.IsWithinAmbiguityMargin,
            candidate.IsLearnedHintMatch,
            IsUserSelected: false,
            CanRememberChoice: candidate.IsEligible && candidates.Count(other =>
                WindowMatchResolver.MatchesHint(candidate.IdentityHint, other.IdentityHint)) == 1,
            ShowCmd: candidate.ShowCmd))
        .ToArray();
}

/// <summary>Immutable result of one entry's assignment attempt within a planning session.</summary>
internal sealed record RestoreAssignmentResult(
    WindowMatchResolution Resolution,
    IReadOnlyList<RestorePlanCandidate> Candidates,
    WindowMatchCandidate? SelectedMatch,
    RestorePlanCandidate? SelectedPlanCandidate);
