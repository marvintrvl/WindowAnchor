using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class RestoreSessionContextTests
{
    [Fact]
    public void Multi_pass_reconciliation_does_not_reuse_a_live_hwnd_for_an_unresolved_entry()
    {
        const string exe = @"C:\Apps\editor.exe";
        var snapshot = new WorkspaceSnapshot
        {
            Entries =
            [
                Entry(exe, "first document"),
                Entry(exe, "second document")
            ]
        };
        var live = LiveWindows((101, Record(exe, "one live editor")));
        var session = new RestoreSessionContext(snapshot, CancellationToken.None);

        var firstPass = WindowRestorePlanner.PlanMatches(
            session.Entries,
            live,
            session.AssignedEntryIndices,
            session.AssignedHwnds);
        Assert.True(session.TryCommitAssignment(Assert.Single(firstPass)));

        session.ReconcileLiveWindows(live, live.ContainsKey);
        var laterPass = WindowRestorePlanner.PlanMatches(
            session.Entries,
            live,
            session.AssignedEntryIndices,
            session.AssignedHwnds);

        Assert.Empty(laterPass);
        var result = session.Complete();
        Assert.Equal(new IntPtr(101), Assert.Single(result.AssignedHwnds));
        Assert.Equal(RestoreEntryStatus.Assigned, result.Entries[0].Status);
        Assert.Equal(RestoreEntryStatus.Pending, result.Entries[1].Status);
    }

    [Fact]
    public void Central_commit_rejects_the_same_hwnd_for_two_entries()
    {
        var snapshot = new WorkspaceSnapshot
        {
            Entries = [Entry(@"C:\Apps\one.exe", "one"), Entry(@"C:\Apps\two.exe", "two")]
        };
        var session = new RestoreSessionContext(snapshot, CancellationToken.None);
        var hwnd = new IntPtr(201);

        Assert.True(session.TryCommitAssignment(new WindowRestoreMatch(0, hwnd, false, null)));
        Assert.False(session.TryCommitAssignment(new WindowRestoreMatch(1, hwnd, false, null)));

        var result = session.Complete();
        Assert.Equal(hwnd, Assert.Single(result.AssignedHwnds));
        Assert.Equal(RestoreEntryStatus.Assigned, result.Entries[0].Status);
        Assert.Equal(RestoreEntryStatus.Pending, result.Entries[1].Status);
    }

    [Fact]
    public void Stale_assignment_is_released_only_after_inventory_verifies_the_original_is_gone()
    {
        const string exe = @"C:\Apps\editor.exe";
        var snapshot = new WorkspaceSnapshot { Entries = [Entry(exe, "notes")] };
        var session = new RestoreSessionContext(snapshot, CancellationToken.None);
        var original = new IntPtr(301);
        var replacement = new IntPtr(302);
        Assert.True(session.TryCommitAssignment(
            new WindowRestoreMatch(0, original, true, null)));

        // A replacement-like window appearing beside the original is not enough to release it.
        var originalAndReplacement = LiveWindows(
            (302, Record(exe, "notes")),
            (301, Record(exe, "notes")));
        session.ReconcileLiveWindows(originalAndReplacement, originalAndReplacement.ContainsKey);
        Assert.True(session.IsEntryAssigned(0));
        Assert.False(session.RequiresMatching(0));

        var replacementOnly = LiveWindows((302, Record(exe, "notes")));
        // Filtering the original out of enumeration still does not prove destruction.
        session.ReconcileLiveWindows(replacementOnly, hwnd => hwnd == original);
        Assert.True(session.IsEntryAssigned(0));
        Assert.False(session.RequiresMatching(0));

        // Only the native liveness check permits the original assignment to be released.
        session.ReconcileLiveWindows(replacementOnly, _ => false);
        Assert.False(session.IsEntryAssigned(0));
        Assert.True(session.RequiresMatching(0));

        var proposal = Assert.Single(WindowRestorePlanner.PlanMatches(
            session.Entries,
            replacementOnly,
            session.AssignedEntryIndices,
            session.AssignedHwnds));
        Assert.True(session.TryCommitAssignment(proposal));

        var result = session.Complete();
        Assert.Equal(replacement, Assert.Single(result.AssignedHwnds));
        Assert.DoesNotContain(original, result.AssignedHwnds);
        Assert.Contains(
            result.Actions,
            action => action.Kind == RestoreSessionActionKind.StaleAssignmentReleased &&
                      action.Hwnd == original);
    }

    [Fact]
    public void Recycled_hwnd_is_released_when_its_process_identity_changes()
    {
        const string exe = @"C:\Apps\editor.exe";
        var snapshot = new WorkspaceSnapshot { Entries = [Entry(exe, "notes")] };
        var session = new RestoreSessionContext(snapshot, CancellationToken.None);
        var recycledHwnd = new IntPtr(351);
        Assert.True(session.TryCommitAssignment(
            new WindowRestoreMatch(0, recycledHwnd, true, null, Pid: 3501)));
        var recycledInventory = new Dictionary<IntPtr, (uint Pid, WindowRecord Record)>
        {
            [recycledHwnd] = (9901, Record(exe, "different process window"))
        };

        session.ReconcileLiveWindows(recycledInventory, _ => true);

        Assert.False(session.IsEntryAssigned(0));
        Assert.True(session.RequiresMatching(0));
        Assert.Empty(session.AssignedHwnds);
        Assert.Contains(
            session.Complete().Actions,
            action => action.Kind == RestoreSessionActionKind.StaleAssignmentReleased &&
                      action.Hwnd == recycledHwnd);
    }

    [Fact]
    public void Equal_candidates_are_deterministic_regardless_of_inventory_insertion_order()
    {
        const string exe = @"C:\Apps\editor.exe";
        var entry = Entry(exe, "same title");
        var ascending = LiveWindows(
            (401, Record(exe, "same title")),
            (402, Record(exe, "same title")));
        var descending = LiveWindows(
            (402, Record(exe, "same title")),
            (401, Record(exe, "same title")));

        var first = Assert.Single(WindowRestorePlanner.PlanMatches(
            [entry], ascending, new HashSet<int>()));
        var second = Assert.Single(WindowRestorePlanner.PlanMatches(
            [entry], descending, new HashSet<int>()));

        Assert.Equal(new IntPtr(401), first.Hwnd);
        Assert.Equal(first.Hwnd, second.Hwnd);
    }

    [Fact]
    public void Structured_result_contains_actions_cancellation_and_timing()
    {
        var snapshot = new WorkspaceSnapshot
        {
            Entries = [Entry(@"C:\Apps\one.exe", "one"), Entry(@"C:\Apps\two.exe", "two")]
        };
        using var cancellation = new CancellationTokenSource();
        var session = new RestoreSessionContext(snapshot, cancellation.Token);
        session.RecordBrowserRestore(succeeded: true);
        session.RecordLaunch(
            0,
            RestoreSessionActionKind.ProcessLaunch,
            succeeded: true,
            "Launched test process");
        session.RecordLaunch(
            1,
            RestoreSessionActionKind.ResourceOpen,
            succeeded: false,
            "Failed test resource");
        cancellation.Cancel();

        RestoreSessionResult result = session.Complete();

        Assert.True(result.WasCancelled);
        Assert.NotEqual(default, result.StartedAt);
        Assert.True(result.Elapsed >= TimeSpan.Zero);
        Assert.Equal(RestoreEntryStatus.LaunchRequested, result.Entries[0].Status);
        Assert.Equal(RestoreEntryStatus.LaunchFailed, result.Entries[1].Status);
        Assert.Collection(
            result.Actions,
            action => Assert.Equal(RestoreSessionActionKind.BrowserRestore, action.Kind),
            action => Assert.Equal(RestoreSessionActionKind.ProcessLaunch, action.Kind),
            action => Assert.Equal(RestoreSessionActionKind.ResourceOpen, action.Kind));
    }

    private static WorkspaceEntry Entry(string exe, string title) => new()
    {
        ExecutablePath = exe,
        ProcessName = Path.GetFileNameWithoutExtension(exe),
        WindowClassName = "EditorWindow",
        Position = Record(exe, title)
    };

    private static WindowRecord Record(string exe, string title) => new()
    {
        ExecutablePath = exe,
        ProcessName = Path.GetFileNameWithoutExtension(exe),
        ClassName = "EditorWindow",
        TitleSnippet = title
    };

    private static Dictionary<IntPtr, (uint Pid, WindowRecord Record)> LiveWindows(
        params (int Hwnd, WindowRecord Record)[] windows) =>
        windows.ToDictionary(window => new IntPtr(window.Hwnd),
                             window => ((uint)window.Hwnd, window.Record));
}
