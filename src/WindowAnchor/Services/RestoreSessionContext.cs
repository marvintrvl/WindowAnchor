using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Current state of one workspace entry during a restore execution.</summary>
public enum RestoreEntryStatus
{
    Pending,
    LaunchRequested,
    LaunchFailed,
    Assigned
}

/// <summary>Kind of side effect recorded by a restore session.</summary>
public enum RestoreSessionActionKind
{
    BrowserRestore,
    ProcessLaunch,
    ResourceOpen,
    WindowAssigned,
    StaleAssignmentReleased,
    WindowRestored,
    MinimizeOtherWindows
}

/// <summary>A structured record of one action attempted during workspace restore.</summary>
public sealed record RestoreSessionAction(
    RestoreSessionActionKind Kind,
    int? EntryIndex,
    IntPtr? Hwnd,
    bool Succeeded,
    string Description);

/// <summary>The final state of one workspace entry after a restore execution.</summary>
public sealed record RestoreEntryResult(
    int EntryIndex,
    string EntryId,
    RestoreEntryStatus Status,
    IntPtr? AssignedHwnd,
    bool TitleMatched);

/// <summary>Structured outcome produced from the state owned by one restore session.</summary>
public sealed record RestoreSessionResult(
    DateTimeOffset StartedAt,
    TimeSpan Elapsed,
    bool WasCancelled,
    IReadOnlyList<RestoreEntryResult> Entries,
    IReadOnlyList<RestoreSessionAction> Actions,
    IReadOnlySet<IntPtr> AssignedHwnds);

/// <summary>
/// Owns all mutable state for one restore execution. In particular, this is the only type
/// allowed to commit or release an entry-to-HWND assignment, which keeps assignment one-to-one
/// across every reconciliation pass.
/// </summary>
internal sealed class RestoreSessionContext
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly WorkspaceEntry[] _entries;
    private readonly Dictionary<int, EntryState> _entryStates;
    private readonly Dictionary<int, CommittedAssignment> _entryAssignments = new();
    private readonly Dictionary<IntPtr, int> _hwndAssignments = new();
    private readonly HashSet<int> _assignedEntryIndices = new();
    private readonly HashSet<IntPtr> _assignedHwnds = new();
    private readonly HashSet<int> _correctlyMatchedEntries = new();
    private readonly List<RestoreSessionAction> _actions = new();

    internal RestoreSessionContext(WorkspaceSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        CancellationToken = cancellationToken;
        _entries = snapshot.Entries.ToArray();
        _entryStates = _entries
            .Select((entry, index) => new EntryState(entry, index))
            .ToDictionary(state => state.EntryIndex);
    }

    internal CancellationToken CancellationToken { get; }
    internal IReadOnlyList<WorkspaceEntry> Entries => _entries;
    internal IReadOnlySet<int> AssignedEntryIndices => _assignedEntryIndices;
    internal IReadOnlySet<IntPtr> AssignedHwnds => _assignedHwnds;
    internal IReadOnlySet<int> CorrectlyMatchedEntries => _correctlyMatchedEntries;

    internal bool IsEntryAssigned(int entryIndex) => _entryAssignments.ContainsKey(entryIndex);
    internal bool RequiresMatching(int entryIndex) => _entryStates[entryIndex].RequiresRematching;

    /// <summary>
    /// Commits a proposed match if neither side already belongs to a different assignment.
    /// A rejected proposal never mutates session state.
    /// </summary>
    internal bool TryCommitAssignment(WindowRestoreMatch proposal)
    {
        if (!_entryStates.TryGetValue(proposal.EntryIndex, out var state) ||
            proposal.Hwnd == IntPtr.Zero ||
            _entryAssignments.ContainsKey(proposal.EntryIndex) ||
            _hwndAssignments.ContainsKey(proposal.Hwnd))
            return false;

        _entryAssignments.Add(
            proposal.EntryIndex,
            new CommittedAssignment(proposal.Hwnd, proposal.Pid));
        _hwndAssignments.Add(proposal.Hwnd, proposal.EntryIndex);
        _assignedEntryIndices.Add(proposal.EntryIndex);
        _assignedHwnds.Add(proposal.Hwnd);

        state.Status = RestoreEntryStatus.Assigned;
        state.AssignedHwnd = proposal.Hwnd;
        state.TitleMatched = proposal.TitleMatched;
        state.RequiresRematching = false;
        state.Entry.WasRestored = true;
        if (proposal.TitleMatched)
            _correctlyMatchedEntries.Add(proposal.EntryIndex);

        Record(
            RestoreSessionActionKind.WindowAssigned,
            proposal.EntryIndex,
            proposal.Hwnd,
            succeeded: true,
            "Committed window assignment");
        return true;
    }

    /// <summary>
    /// Releases assignments only when the latest native inventory verifies that their original
    /// HWND no longer exists. Those entries then explicitly return to pending/rematch state.
    /// </summary>
    internal void ReconcileLiveWindows(
        IReadOnlyDictionary<IntPtr, (uint Pid, WindowRecord Record)> liveWindows,
        Func<IntPtr, bool> isWindowAlive)
    {
        var staleAssignments = _entryAssignments
            .Where(assignment => OriginalWindowIsGone(assignment.Value, liveWindows, isWindowAlive))
            .OrderBy(assignment => assignment.Key)
            .ToList();

        foreach (var (entryIndex, assignment) in staleAssignments)
        {
            IntPtr hwnd = assignment.Hwnd;
            var state = _entryStates[entryIndex];
            state.RequiresRematching = true;

            _entryAssignments.Remove(entryIndex);
            _hwndAssignments.Remove(hwnd);
            _assignedEntryIndices.Remove(entryIndex);
            _assignedHwnds.Remove(hwnd);
            _correctlyMatchedEntries.Remove(entryIndex);

            state.Status = RestoreEntryStatus.Pending;
            state.AssignedHwnd = null;
            state.TitleMatched = false;
            state.Entry.WasRestored = false;

            Record(
                RestoreSessionActionKind.StaleAssignmentReleased,
                entryIndex,
                hwnd,
                succeeded: true,
                "Released assignment after inventory verified the window was gone");
        }
    }

    internal void RecordBrowserRestore(bool succeeded) => Record(
        RestoreSessionActionKind.BrowserRestore,
        entryIndex: null,
        hwnd: null,
        succeeded,
        succeeded ? "Restored browser session" : "Browser session restore was unavailable");

    internal void RecordLaunch(
        int entryIndex,
        RestoreSessionActionKind kind,
        bool succeeded,
        string description)
    {
        if (kind is not RestoreSessionActionKind.ProcessLaunch and
            not RestoreSessionActionKind.ResourceOpen)
            throw new ArgumentOutOfRangeException(nameof(kind));

        var state = _entryStates[entryIndex];
        if (!IsEntryAssigned(entryIndex))
            state.Status = succeeded ? RestoreEntryStatus.LaunchRequested : RestoreEntryStatus.LaunchFailed;

        Record(kind, entryIndex, hwnd: null, succeeded, description);
    }

    internal void RecordWindowRestored(int entryIndex, IntPtr hwnd) => Record(
        RestoreSessionActionKind.WindowRestored,
        entryIndex,
        hwnd,
        succeeded: true,
        "Applied saved window placement");

    internal void RecordMinimizeOthers(bool succeeded) => Record(
        RestoreSessionActionKind.MinimizeOtherWindows,
        entryIndex: null,
        hwnd: null,
        succeeded,
        "Minimized windows outside the final restore assignment");

    internal RestoreSessionResult Complete()
    {
        _stopwatch.Stop();

        var entries = _entryStates.Values
            .OrderBy(state => state.EntryIndex)
            .Select(state => new RestoreEntryResult(
                state.EntryIndex,
                state.Entry.EntryId,
                state.Status,
                state.AssignedHwnd,
                state.TitleMatched))
            .ToArray();

        return new RestoreSessionResult(
            _startedAt,
            _stopwatch.Elapsed,
            CancellationToken.IsCancellationRequested,
            entries,
            _actions.ToArray(),
            new HashSet<IntPtr>(_assignedHwnds));
    }

    private void Record(
        RestoreSessionActionKind kind,
        int? entryIndex,
        IntPtr? hwnd,
        bool succeeded,
        string description) =>
        _actions.Add(new RestoreSessionAction(kind, entryIndex, hwnd, succeeded, description));

    private static bool OriginalWindowIsGone(
        CommittedAssignment assignment,
        IReadOnlyDictionary<IntPtr, (uint Pid, WindowRecord Record)> liveWindows,
        Func<IntPtr, bool> isWindowAlive)
    {
        if (liveWindows.TryGetValue(assignment.Hwnd, out var current))
        {
            // An HWND cannot move between processes. A different PID therefore proves that the
            // numeric handle was recycled after the originally assigned window was destroyed.
            return assignment.Pid != 0 && current.Pid != assignment.Pid;
        }

        return !isWindowAlive(assignment.Hwnd);
    }

    private readonly record struct CommittedAssignment(IntPtr Hwnd, uint Pid);

    private sealed class EntryState
    {
        internal EntryState(WorkspaceEntry entry, int entryIndex)
        {
            Entry = entry;
            EntryIndex = entryIndex;
            RequiresRematching = true;
            entry.WasRestored = false;
        }

        internal WorkspaceEntry Entry { get; }
        internal int EntryIndex { get; }
        internal RestoreEntryStatus Status { get; set; }
        internal IntPtr? AssignedHwnd { get; set; }
        internal bool TitleMatched { get; set; }
        internal bool RequiresRematching { get; set; }
    }
}
