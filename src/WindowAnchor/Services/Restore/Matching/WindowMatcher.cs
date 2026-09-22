using System.Collections.Generic;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>
/// Stable public matching API. Detailed scoring, ambiguity, and learned-hint policy is owned by
/// the internal <see cref="WindowMatchResolver"/> so model and extraction files remain focused.
/// </summary>
public static class WindowMatcher
{
    public static IReadOnlyList<WindowMatchCandidate> FindCandidates(
        SavedWindowIdentity saved,
        IEnumerable<LiveWindowIdentity> liveWindows,
        WindowIdentityHint? learnedHint = null,
        WindowMatchPolicy? policy = null) =>
        WindowMatchResolver.FindCandidates(saved, liveWindows, learnedHint, policy);

    /// <summary>Scores and resolves candidates using the configured deterministic thresholds.</summary>
    public static WindowMatchResolution Resolve(
        SavedWindowIdentity saved,
        IEnumerable<LiveWindowIdentity> liveWindows,
        WindowIdentityHint? learnedHint = null,
        WindowMatchPolicy? policy = null) =>
        WindowMatchResolver.Resolve(saved, liveWindows, learnedHint, policy);

    /// <summary>Resolves an already-scored candidate set without observing external state.</summary>
    public static WindowMatchResolution ResolveCandidates(
        IReadOnlyList<WindowMatchCandidate> candidates,
        WindowMatchPolicy? policy = null) =>
        WindowMatchResolver.ResolveCandidates(candidates, policy);

    public static double TitleSimilarity(string? first, string? second) =>
        WindowMatchResolver.TitleSimilarity(first, second);

    /// <summary>Compares two persisted composite identities without runtime handles or PIDs.</summary>
    public static bool MatchesHint(WindowIdentityHint expected, WindowIdentityHint observed) =>
        WindowMatchResolver.MatchesHint(expected, observed);
}
