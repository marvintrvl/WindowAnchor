using System;
using System.Collections.Generic;
using System.Linq;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

internal readonly record struct WindowRestoreMatch(
    int EntryIndex,
    IntPtr Hwnd,
    bool TitleMatched,
    double? TitleSimilarityScore,
    uint Pid = 0,
    WindowMatchCandidate? Candidate = null);

/// <summary>
/// Compatibility planner that turns scored <see cref="WindowMatcher"/> candidates into the
/// existing one-entry-per-HWND assignment proposals. Native enumeration and window mutation
/// remain at the caller boundary.
/// </summary>
internal static class WindowRestorePlanner
{
    internal static IReadOnlyList<WindowRestoreMatch> PlanMatches(
        IReadOnlyList<WorkspaceEntry> entries,
        IReadOnlyDictionary<IntPtr, (uint Pid, WindowRecord Record)> liveWindows,
        IReadOnlySet<int> restoredEntries,
        IReadOnlySet<IntPtr>? unavailableHwnds = null)
    {
        var matches = new List<WindowRestoreMatch>();
        var consumedHwnds = unavailableHwnds is null
            ? new HashSet<IntPtr>()
            : new HashSet<IntPtr>(unavailableHwnds);
        LiveWindowIdentity[] liveIdentities = liveWindows
            .Select(window => WindowIdentityExtractor.FromLive(
                window.Key,
                window.Value.Pid,
                window.Value.Record))
            .OrderBy(window => window.Hwnd.ToInt64())
            .ToArray();

        for (int entryIndex = 0; entryIndex < entries.Count; entryIndex++)
        {
            if (restoredEntries.Contains(entryIndex)) continue;

            SavedWindowIdentity saved = WindowIdentityExtractor.FromSaved(entries[entryIndex]);
            WindowMatchCandidate? selected = WindowMatcher.FindCandidates(
                    saved,
                    liveIdentities.Where(live => !consumedHwnds.Contains(live.Hwnd)))
                .FirstOrDefault(candidate => candidate.IsEligible);
            if (selected == null) continue;

            consumedHwnds.Add(selected.Hwnd);
            bool titleMatched = selected.Evidence.Any(evidence =>
                evidence.Matched && evidence.Kind is
                    WindowMatchEvidenceKind.PwaIdentityExact or
                    WindowMatchEvidenceKind.DedicatedBrowserSiteExact or
                    WindowMatchEvidenceKind.DocumentNameInTitle);
            matches.Add(new WindowRestoreMatch(
                entryIndex,
                selected.Hwnd,
                titleMatched,
                selected.TitleSimilarityScore,
                selected.ProcessId,
                selected));
        }

        return matches;
    }

    internal static bool SameSite(string first, string second) =>
        WindowMatcher.SameSite(first, second);

    internal static double TitleSimilarity(string first, string second) =>
        WindowMatcher.TitleSimilarity(first, second);
}
