using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Mutable state shared by every ordered execution phase of one restore session.</summary>
internal sealed class RestoreExecutionContext
{
    internal RestoreExecutionContext(RestorePlan plan)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Entries = plan.Entries.ToDictionary(
            entry => entry.EntryIndex,
            entry => new RestoreEntryExecutionState(entry));
        Results = new Dictionary<int, RestoreExecutionActionResult>();
        AssignedHwnds = new HashSet<IntPtr>();
        IndexedActions = plan.Actions
            .Select((action, index) => new IndexedRestoreAction(index, action))
            .ToArray();
    }

    internal RestorePlan Plan { get; }
    internal Dictionary<int, RestoreEntryExecutionState> Entries { get; }
    internal Dictionary<int, RestoreExecutionActionResult> Results { get; }
    internal HashSet<IntPtr> AssignedHwnds { get; }
    internal IndexedRestoreAction[] IndexedActions { get; }
    internal bool? BrowserSessionSucceeded { get; set; }
}

internal readonly record struct IndexedRestoreAction(int Index, RestoreAction Action);

internal sealed class RestoreEntryExecutionState
{
    internal RestoreEntryExecutionState(RestorePlanEntry planEntry)
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
    internal AppReadinessState? ReadinessState { get; set; }
    internal string? ReadinessStrategy { get; set; }
    internal int? PlacementActionIndex { get; set; }
    internal WindowPlacementVerificationState? PlacementVerification { get; set; }
    internal int PlacementRetryCount { get; set; }
    internal string? PlacementVerificationStrategy { get; set; }
    internal int? PlacementTolerancePixels { get; set; }
}

internal static class RestoreExecutionSupport
{
    internal static void MarkStale(
        IndexedRestoreAction item,
        RestoreEntryExecutionState state,
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

    internal static void MarkRemaining(
        IEnumerable<IndexedRestoreAction> actions,
        IDictionary<int, RestoreExecutionActionResult> results,
        RestoreExecutionActionStatus status,
        string explanation)
    {
        foreach (IndexedRestoreAction item in actions.Where(item => !results.ContainsKey(item.Index)))
            results[item.Index] = Result(item, status, staleReason: null, explanation);
    }

    internal static RestoreExecutionActionResult Result(
        IndexedRestoreAction item,
        RestoreExecutionActionStatus status,
        RestorePlanStaleReason? staleReason,
        string explanation,
        long? windowHandle = null,
        AppReadinessState? readinessState = null,
        string? readinessStrategy = null) => new(
            item.Index,
            item.Action.EntryIndex,
            item.Action.Kind,
            status,
            staleReason,
            windowHandle ?? item.Action.WindowHandle,
            explanation,
            readinessState,
            readinessStrategy);

    internal static string EntryDisplayName(RestorePlanEntry entry)
    {
        string processName = entry.SavedIdentity.ProcessName;
        if (!string.IsNullOrWhiteSpace(processName))
            return processName;

        try
        {
            string fileName = Path.GetFileNameWithoutExtension(entry.SavedIdentity.ExecutablePath);
            if (!string.IsNullOrWhiteSpace(fileName))
                return fileName;
        }
        catch
        {
            // Invalid persisted paths must not prevent local progress reporting.
        }

        return $"entry {entry.EntryIndex + 1}";
    }

    internal static bool IsLaunch(RestoreActionKind kind) => kind is
        RestoreActionKind.LaunchApplication or
        RestoreActionKind.OpenResource or
        RestoreActionKind.LaunchDedicatedBrowser or
        RestoreActionKind.LaunchWebApp or
        RestoreActionKind.ActivatePackagedApplication;

    internal static WindowRecord ToWindowRecord(RestoreTargetPlacement placement) => new()
    {
        NormalLeft = placement.Left,
        NormalTop = placement.Top,
        NormalRight = placement.Right,
        NormalBottom = placement.Bottom,
        ShowCmd = placement.ShowCmd,
        SavedDpi = placement.TargetDpi,
        CoordinatesAreFinal = true,
        CoordinatesRepresentVisibleBounds = placement.ShowCmd == 1 && placement.Strategy is
            RestorePlacementStrategy.Semantic or RestorePlacementStrategy.Normalized,
        MonitorId = placement.TargetMonitorId,
        MonitorIndex = placement.TargetMonitorIndex
    };

    internal static BrowserSession ToBrowserSession(RestoreBrowserSession session) => new()
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
}

internal sealed class RestoreWindowRevalidator
{
    private readonly IWindowInventory _windowInventory;

    internal RestoreWindowRevalidator(IWindowInventory windowInventory) =>
        _windowInventory = windowInventory ?? throw new ArgumentNullException(nameof(windowInventory));

    internal RestorePlanStaleReason? Revalidate(
        RestorePlanEntry entry,
        IntPtr hwnd,
        uint expectedPid)
    {
        Dictionary<IntPtr, (uint Pid, WindowRecord Record)> current =
            _windowInventory.GetWindowsWithPids(WindowCandidatePolicy.RestoreMatchCandidate);
        return Revalidate(entry, hwnd, expectedPid, current);
    }

    internal RestorePlanStaleReason? Revalidate(
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
}
