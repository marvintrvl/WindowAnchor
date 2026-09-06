using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class RestoreExecutionContextTests
{
    [Fact]
    public void Context_initializes_entry_states_and_stable_action_indexes()
    {
        RestorePlan plan = BuildPlan(
            [Entry(@"C:\Apps\one.exe", "one"), Entry(@"C:\Apps\two.exe", "two")],
            [new RestoreAction(0, RestoreActionKind.LaunchApplication, null, "one.exe", "", false, null, "one")]);

        var context = new RestoreExecutionContext(plan);

        Assert.Equal([0, 1], context.Entries.Keys.OrderBy(index => index));
        Assert.All(context.Entries.Values, state =>
            Assert.Equal(RestoreExecutionEntryStatus.Pending, state.Status));
        IndexedRestoreAction indexed = Assert.Single(context.IndexedActions);
        Assert.Equal(0, indexed.Index);
        Assert.Same(plan.Actions[0], indexed.Action);
    }

    [Fact]
    public void Result_aggregation_preserves_entry_and_action_order_and_assignment_set()
    {
        RestorePlan plan = BuildPlan(
            [Entry(@"C:\Apps\one.exe", "one"), Entry(@"C:\Apps\two.exe", "two")],
            [
                new RestoreAction(1, RestoreActionKind.LaunchApplication, null, "two.exe", "", false, null, "two"),
                new RestoreAction(0, RestoreActionKind.LaunchApplication, null, "one.exe", "", false, null, "one")
            ]);
        var context = new RestoreExecutionContext(plan);
        context.Entries[1].Status = RestoreExecutionEntryStatus.Failed;
        context.Entries[0].Status = RestoreExecutionEntryStatus.Restored;
        context.Entries[0].AssignedWindowHandle = 41;
        context.AssignedHwnds.Add(new IntPtr(41));
        context.Results[1] = RestoreExecutionSupport.Result(
            context.IndexedActions[1], RestoreExecutionActionStatus.Succeeded, null, "one");
        context.Results[0] = RestoreExecutionSupport.Result(
            context.IndexedActions[0], RestoreExecutionActionStatus.Failed, null, "two");

        RestoreExecutionResult result = RestoreResultAggregator.Complete(
            context,
            RestoreResultAggregator.DetermineStatus(context),
            wasCancelled: false);

        Assert.Equal(RestoreExecutionStatus.CompletedWithFailures, result.Status);
        Assert.Equal([0, 1], result.Entries.Select(entry => entry.EntryIndex));
        Assert.Equal([0, 1], result.Actions.Select(action => action.ActionIndex));
        Assert.Equal(41, Assert.Single(result.AssignedWindowHandles));
    }

    [Fact]
    public void Revalidation_reports_closed_only_after_native_liveness_rejects_the_handle()
    {
        RestorePlanEntry entry = Assert.Single(BuildPlan([Entry(@"C:\Apps\one.exe", "one")]).Entries);
        var inventory = new FakeWindowInventory
        {
            Live = new Dictionary<IntPtr, (uint Pid, WindowRecord Record)>(),
            IsAliveProvider = _ => false
        };
        var revalidator = new RestoreWindowRevalidator(inventory);

        RestorePlanStaleReason? reason = revalidator.Revalidate(entry, new IntPtr(51), 5100);

        Assert.Equal(RestorePlanStaleReason.WindowClosed, reason);
    }

    [Fact]
    public void Revalidation_keeps_a_live_but_filtered_handle_distinct_from_a_closed_handle()
    {
        RestorePlanEntry entry = Assert.Single(BuildPlan([Entry(@"C:\Apps\one.exe", "one")]).Entries);
        var inventory = new FakeWindowInventory
        {
            Live = new Dictionary<IntPtr, (uint Pid, WindowRecord Record)>(),
            IsAliveProvider = _ => true
        };
        var revalidator = new RestoreWindowRevalidator(inventory);

        RestorePlanStaleReason? reason = revalidator.Revalidate(entry, new IntPtr(52), 5200);

        Assert.Equal(RestorePlanStaleReason.WindowNoLongerEligible, reason);
    }

    [Fact]
    public void Revalidation_rejects_a_recycled_handle_with_a_different_process()
    {
        WorkspaceEntry saved = Entry(@"C:\Apps\one.exe", "one");
        RestorePlanEntry entry = Assert.Single(BuildPlan([saved]).Entries);
        var hwnd = new IntPtr(53);
        var observed = Record(@"C:\Apps\one.exe", "one");
        var inventory = new FakeWindowInventory
        {
            Live = new Dictionary<IntPtr, (uint Pid, WindowRecord Record)>
            {
                [hwnd] = (9999, observed)
            }
        };
        var revalidator = new RestoreWindowRevalidator(inventory);

        RestorePlanStaleReason? reason = revalidator.Revalidate(entry, hwnd, 5300);

        Assert.Equal(RestorePlanStaleReason.WindowReplaced, reason);
    }

    [Fact]
    public void Equal_candidates_remain_ambiguous_regardless_of_inventory_order()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\editor.exe", "same title");
        LiveWindowIdentity first = WindowIdentityExtractor.FromLive(
            new IntPtr(61), 6100, Record(entry.ExecutablePath, "same title"));
        LiveWindowIdentity second = WindowIdentityExtractor.FromLive(
            new IntPtr(62), 6200, Record(entry.ExecutablePath, "same title"));

        RestoreAssignmentResult ascending = new RestoreAssignmentPlanner("workspace", [first, second], [])
            .Resolve(entry, WindowIdentityExtractor.FromSaved(entry));
        RestoreAssignmentResult descending = new RestoreAssignmentPlanner("workspace", [second, first], [])
            .Resolve(entry, WindowIdentityExtractor.FromSaved(entry));

        Assert.True(ascending.Resolution.IsAmbiguous);
        Assert.True(descending.Resolution.IsAmbiguous);
        Assert.Null(ascending.SelectedMatch);
        Assert.Null(descending.SelectedMatch);
        Assert.Equal([61L, 62L], ascending.Candidates.Select(candidate => candidate.WindowHandle));
        Assert.Equal([61L, 62L], descending.Candidates.Select(candidate => candidate.WindowHandle));
    }

    private static RestorePlan BuildPlan(
        IReadOnlyList<WorkspaceEntry> entries,
        IReadOnlyList<RestoreAction>? actions = null)
    {
        RestorePlan basePlan = RestorePlanner.Build(
            new WorkspaceSnapshot { WorkspaceId = "workspace", Entries = entries.ToList() },
            new RestoreLiveInventory(),
            new RestoreMonitorTopology(),
            RestoreMode.Standard);
        return actions is null ? basePlan : basePlan with { Actions = actions };
    }

    private static WorkspaceEntry Entry(string executable, string title) => new()
    {
        ExecutablePath = executable,
        ProcessName = Path.GetFileNameWithoutExtension(executable),
        WindowClassName = "EditorWindow",
        Position = Record(executable, title)
    };

    private static WindowRecord Record(string executable, string title) => new()
    {
        ExecutablePath = executable,
        ProcessName = Path.GetFileNameWithoutExtension(executable),
        ClassName = "EditorWindow",
        TitleSnippet = title
    };
}
