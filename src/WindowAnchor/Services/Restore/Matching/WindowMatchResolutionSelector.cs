using System;
using System.Collections.Generic;
using System.Linq;

namespace WindowAnchor.Services;

/// <summary>Pure deterministic selection and ambiguity policy over scored candidates.</summary>
internal static class WindowMatchResolutionSelector
{
    internal static WindowMatchResolution Resolve(IReadOnlyList<WindowMatchCandidate> candidates, WindowMatchPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        WindowMatchCandidate[] eligible = candidates.Where(candidate => candidate.IsEligible)
            .OrderByDescending(candidate => candidate.Score).ThenBy(candidate => candidate.Hwnd.ToInt64()).ToArray();
        if (eligible.Length == 0)
            return new(WindowMatchConfidence.Missing, null, candidates, "No live window met the minimum evidence threshold.");
        WindowMatchCandidate best = eligible[0];
        if (eligible.Length > 1 && best.Score - eligible[1].Score <= policy.AmbiguityScoreMargin)
            return new(WindowMatchConfidence.Ambiguous, null, candidates,
                $"The top {eligible.Count(candidate => best.Score - candidate.Score <= policy.AmbiguityScoreMargin)} candidates are within the {policy.AmbiguityScoreMargin:0}-point safety margin.");
        string reason = best.Evidence.Where(evidence => evidence.Matched).OrderByDescending(evidence => evidence.ScoreContribution)
            .Select(evidence => evidence.Explanation).FirstOrDefault() ?? "The candidate met the configured evidence threshold.";
        return new(best.Confidence, best, candidates, $"{best.Confidence} confidence. {reason}");
    }
}
