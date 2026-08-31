using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class RestoreExecutorTests
{
    [Fact]
    public async Task Approved_window_action_is_revalidated_and_executed_once()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\editor.exe", "Notes");
        LiveWindowIdentity live = Live(10, 1010, entry.ExecutablePath, "Notes");
        RestorePlan plan = Plan(Snapshot(entry), [live]);
        var inventory = Inventory((10, 1010, entry.ExecutablePath, "Notes"));
        var mutation = new RecordingWindowMutation();

        RestoreExecutionResult result = await Executor(inventory, mutation).ExecuteAsync(plan);

        Assert.Equal(RestoreExecutionStatus.Completed, result.Status);
        Assert.Equal(new IntPtr(10), Assert.Single(mutation.Restores).Hwnd);
        Assert.Equal(2, inventory.LiveInventoryCalls);
        Assert.Equal(RestoreExecutionEntryStatus.Restored, Assert.Single(result.Entries).Status);
        Assert.Equal(
            Enumerable.Range(0, plan.Actions.Count),
            result.Actions.Select(action => action.ActionIndex));
    }

    [Theory]
    [InlineData(false, RestorePlanStaleReason.WindowClosed)]
    [InlineData(true, RestorePlanStaleReason.WindowReplaced)]
    public async Task Closed_or_replaced_hwnd_is_reported_stale_without_mutation(
        bool replaceHandle,
        RestorePlanStaleReason expectedReason)
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\editor.exe", "Notes");
        RestorePlan plan = Plan(
            Snapshot(entry),
            [Live(20, 2020, entry.ExecutablePath, "Notes")]);
        var inventory = new FakeWindowInventory
        {
            Live = replaceHandle
                ? Records((20, 9090, entry.ExecutablePath, "Replacement"))
                : new Dictionary<IntPtr, (uint Pid, WindowRecord Record)>(),
            IsAliveProvider = _ => replaceHandle
        };
        var mutation = new RecordingWindowMutation();

        RestoreExecutionResult result = await Executor(inventory, mutation).ExecuteAsync(plan);

        Assert.Equal(RestoreExecutionStatus.StalePlan, result.Status);
        RestoreExecutionActionResult action = Assert.Single(result.Actions);
        Assert.Equal(RestoreExecutionActionStatus.Stale, action.Status);
        Assert.Equal(expectedReason, action.StaleReason);
        Assert.Equal(RestoreExecutionEntryStatus.Stale, Assert.Single(result.Entries).Status);
        Assert.Empty(mutation.Restores);
    }

    [Fact]
    public async Task Resource_removed_after_plan_approval_becomes_stale_without_process_launch()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\editor.exe", "Notes");
        RestorePlan plan = Plan(
            Snapshot(entry),
            resources:
            [
                new(0, RestoreResourceKind.Executable, RestoreResourceAvailability.Available,
                    entry.ExecutablePath)
            ]);
        var resources = new FakeRestoreResourceBoundary();
        resources.AvailabilityByTarget[entry.ExecutablePath] = RestoreResourceAvailability.Missing;
        var process = new RecordingRestoreProcessLauncher();

        RestoreExecutionResult result = await Executor(
            new FakeWindowInventory(),
            new RecordingWindowMutation(),
            process,
            resources: resources).ExecuteAsync(plan);

        Assert.Equal(RestoreExecutionStatus.StalePlan, result.Status);
        Assert.Contains(
            result.Actions,
            action => action.Kind == RestoreActionKind.LaunchApplication &&
                      action.StaleReason == RestorePlanStaleReason.ResourceMissing);
        Assert.Equal(RestoreExecutionEntryStatus.Stale, Assert.Single(result.Entries).Status);
        Assert.Empty(process.Launches);
    }

    [Fact]
    public async Task Eligible_window_appearing_after_preview_marks_plan_stale_before_any_launch()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\editor.exe", "Notes");
        RestorePlan plan = Plan(
            Snapshot(entry),
            resources:
            [
                new(0, RestoreResourceKind.Executable, RestoreResourceAvailability.Available,
                    entry.ExecutablePath)
            ]);
        var inventory = Inventory((25, 2525, entry.ExecutablePath, "Notes"));
        var mutation = new RecordingWindowMutation();
        var process = new RecordingRestoreProcessLauncher();

        RestoreExecutionResult result = await Executor(
            inventory,
            mutation,
            process).ExecuteAsync(plan);

        Assert.Equal(RestoreExecutionStatus.StalePlan, result.Status);
        Assert.Contains(
            result.Actions,
            action => action.StaleReason == RestorePlanStaleReason.WindowInventoryChanged);
        Assert.Empty(process.Launches);
        Assert.Empty(mutation.Restores);
    }

    [Fact]
    public async Task Launch_uses_fixed_waits_and_reconciles_new_window_from_approved_await_action()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\editor.exe", "Notes");
        RestorePlan plan = Plan(
            Snapshot(entry),
            resources:
            [
                new(0, RestoreResourceKind.Executable, RestoreResourceAvailability.Available,
                    entry.ExecutablePath)
            ]);
        var inventory = new FakeWindowInventory();
        var process = new RecordingRestoreProcessLauncher
        {
            OnLaunch = _ => inventory.Live = Records((30, 3030, entry.ExecutablePath, "Notes"))
        };
        var clock = new FakeRestoreClock();
        var mutation = new RecordingWindowMutation();

        RestoreExecutionResult result = await Executor(
            inventory,
            mutation,
            process,
            clock).ExecuteAsync(plan);

        Assert.Equal([TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2)], clock.Delays);
        Assert.Equal(RestoreExecutionStatus.Completed, result.Status);
        Assert.Equal(new IntPtr(30), Assert.Single(mutation.Restores).Hwnd);
        Assert.Equal(RestoreExecutionEntryStatus.Restored, Assert.Single(result.Entries).Status);
        Assert.Equal(plan.Actions.Count, result.Actions.Count);
        Assert.All(result.Actions, action => Assert.NotEqual(RestoreExecutionActionStatus.Stale, action.Status));
    }

    [Fact]
    public async Task Browser_failure_executes_only_the_approved_fallback_then_reconciles()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\brave.exe", "Browser");
        entry.ProcessName = "brave";
        WorkspaceSnapshot snapshot = Snapshot(entry);
        snapshot.BrowserSessions.Add(new BrowserSession
        {
            Browser = "brave",
            ActiveTitle = "Browser",
            Tabs = [new BrowserTab { Url = "https://example.test/", Title = "Example" }]
        });
        RestorePlan plan = Plan(
            snapshot,
            browserAvailability: BrowserSessionRestoreAvailability.Available);
        Assert.Contains(plan.Actions, action => action.Kind == RestoreActionKind.RestoreBrowserSession);
        Assert.Contains(
            plan.Actions,
            action => action.Condition == RestoreActionCondition.BrowserSessionUnavailable);

        var browser = new FakeBrowserSessionConnector { RestoreResult = false };
        var inventory = new FakeWindowInventory();
        var process = new RecordingRestoreProcessLauncher
        {
            OnLaunch = _ => inventory.Live = Records((40, 4040, entry.ExecutablePath, "Browser"))
        };

        RestoreExecutionResult result = await Executor(
            inventory,
            new RecordingWindowMutation(),
            process,
            new FakeRestoreClock(),
            browser).ExecuteAsync(plan);

        Assert.Equal(1, browser.RestoreCalls);
        Assert.Single(browser.RestoredSessions);
        Assert.Single(process.Launches);
        Assert.Equal(RestoreActionCondition.BrowserSessionUnavailable, process.Launches[0].Condition);
        Assert.Equal(RestoreExecutionEntryStatus.Restored, Assert.Single(result.Entries).Status);
        Assert.Equal(RestoreExecutionStatus.StalePlan, result.Status);
    }

    [Fact]
    public async Task Successful_browser_action_skips_approved_fallback()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\brave.exe", "Browser");
        entry.ProcessName = "brave";
        WorkspaceSnapshot snapshot = Snapshot(entry);
        snapshot.BrowserSessions.Add(new BrowserSession { Browser = "brave" });
        RestorePlan plan = Plan(
            snapshot,
            browserAvailability: BrowserSessionRestoreAvailability.Available);
        var browser = new FakeBrowserSessionConnector { RestoreResult = true };
        var inventory = new FakeWindowInventory();
        var clock = new FakeRestoreClock
        {
            OnDelay = count =>
            {
                if (count == 1)
                    inventory.Live = Records((50, 5050, entry.ExecutablePath, "Browser"));
            }
        };
        var process = new RecordingRestoreProcessLauncher();

        RestoreExecutionResult result = await Executor(
            inventory,
            new RecordingWindowMutation(),
            process,
            clock,
            browser).ExecuteAsync(plan);

        Assert.Empty(process.Launches);
        Assert.Contains(
            result.Actions,
            action => action.Kind == RestoreActionKind.LaunchApplication &&
                      action.Status == RestoreExecutionActionStatus.Skipped);
        Assert.Equal(RestoreExecutionStatus.Completed, result.Status);
        Assert.Equal(RestoreExecutionEntryStatus.Restored, Assert.Single(result.Entries).Status);
    }

    [Fact]
    public async Task Align_and_minimize_uses_only_final_revalidated_assignments()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\editor.exe", "Notes");
        RestorePlan plan = Plan(
            Snapshot(entry),
            [Live(60, 6060, entry.ExecutablePath, "Notes")],
            mode: RestoreMode.AlignAndMinimize);
        var inventory = Inventory((60, 6060, entry.ExecutablePath, "Notes"));
        var mutation = new RecordingWindowMutation();

        RestoreExecutionResult result = await Executor(inventory, mutation).ExecuteAsync(plan);

        Assert.Equal(new IntPtr(60), Assert.Single(Assert.Single(mutation.MinimizeCalls)));
        Assert.Equal([60L], result.AssignedWindowHandles);
        Assert.Equal(
            RestoreActionKind.MinimizeOtherWindows,
            Assert.Single(result.Actions, action => action.Kind == RestoreActionKind.MinimizeOtherWindows).Kind);
    }

    [Fact]
    public async Task Selective_plan_executes_included_entry_and_reports_excluded_entry()
    {
        WorkspaceEntry included = Entry(@"C:\Apps\editor.exe", "Primary notes");
        WorkspaceEntry excluded = Entry(@"C:\Apps\chat.exe", "Secondary chat");
        excluded.MonitorId = "secondary";
        excluded.Position.MonitorId = "secondary";
        WorkspaceSnapshot snapshot = Snapshot(included, excluded);
        RestorePlan plan = Plan(
            snapshot,
            [
                Live(61, 6161, included.ExecutablePath, "Primary notes"),
                Live(62, 6262, excluded.ExecutablePath, "Secondary chat")
            ],
            mode: RestoreMode.Selective(["primary"]));
        var inventory = Inventory(
            (61, 6161, included.ExecutablePath, "Primary notes"),
            (62, 6262, excluded.ExecutablePath, "Secondary chat"));
        var mutation = new RecordingWindowMutation();

        RestoreExecutionResult result = await Executor(inventory, mutation).ExecuteAsync(plan);

        Assert.Equal(new IntPtr(61), Assert.Single(mutation.Restores).Hwnd);
        Assert.Equal(RestoreExecutionEntryStatus.Restored, result.Entries[0].Status);
        Assert.Equal(RestoreExecutionEntryStatus.Excluded, result.Entries[1].Status);
        Assert.DoesNotContain(plan.Actions, action => action.EntryIndex == 1);
    }

    [Fact]
    public async Task Disabled_entry_is_not_moved_or_minimized_by_align_plan()
    {
        WorkspaceEntry included = Entry(@"C:\Apps\editor.exe", "Notes");
        WorkspaceEntry disabled = Entry(@"C:\Apps\chat.exe", "Chat");
        RestorePlan preview = Plan(
            Snapshot(included, disabled),
            [
                Live(63, 6363, included.ExecutablePath, "Notes"),
                Live(64, 6464, disabled.ExecutablePath, "Chat")
            ],
            mode: RestoreMode.AlignAndMinimize);
        RestorePlan approved = RestorePlanner.DeriveApprovedPlan(preview, [1]);
        var inventory = Inventory(
            (63, 6363, included.ExecutablePath, "Notes"),
            (64, 6464, disabled.ExecutablePath, "Chat"));
        var mutation = new RecordingWindowMutation();

        RestoreExecutionResult result = await Executor(inventory, mutation).ExecuteAsync(approved);

        Assert.Equal(new IntPtr(63), Assert.Single(mutation.Restores).Hwnd);
        HashSet<IntPtr> keep = Assert.Single(mutation.MinimizeCalls);
        Assert.Contains(new IntPtr(63), keep);
        Assert.Contains(new IntPtr(64), keep);
        Assert.Equal([63L], result.AssignedWindowHandles);
        Assert.Equal(RestoreExecutionEntryStatus.Excluded, result.Entries[1].Status);
    }

    [Fact]
    public async Task Pre_cancelled_execution_preserves_initial_reconciliation_and_skips_later_actions()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\editor.exe", "Notes");
        RestorePlan plan = Plan(
            Snapshot(entry),
            [Live(70, 7070, entry.ExecutablePath, "Notes")]);
        var inventory = Inventory((70, 7070, entry.ExecutablePath, "Notes"));
        var mutation = new RecordingWindowMutation();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        RestoreExecutionResult result = await Executor(inventory, mutation).ExecuteAsync(
            plan,
            cancellation.Token);

        Assert.True(result.WasCancelled);
        Assert.Equal(RestoreExecutionStatus.Cancelled, result.Status);
        Assert.Single(mutation.Restores);
        Assert.Equal(RestoreExecutionActionStatus.Succeeded, Assert.Single(result.Actions).Status);
    }

    private static RestoreExecutor Executor(
        FakeWindowInventory inventory,
        RecordingWindowMutation mutation,
        RecordingRestoreProcessLauncher? process = null,
        FakeRestoreClock? clock = null,
        FakeBrowserSessionConnector? browser = null,
        FakeRestoreResourceBoundary? resources = null) => new(
            inventory,
            mutation,
            process ?? new RecordingRestoreProcessLauncher(),
            clock ?? new FakeRestoreClock(),
            resources ?? new FakeRestoreResourceBoundary(),
            browser);

    private static RestorePlan Plan(
        WorkspaceSnapshot snapshot,
        IReadOnlyList<LiveWindowIdentity>? live = null,
        IReadOnlyList<RestoreResourceObservation>? resources = null,
        BrowserSessionRestoreAvailability browserAvailability =
            BrowserSessionRestoreAvailability.NotAvailable,
        RestoreMode? mode = null) => RestorePlanner.Build(
            snapshot,
            new RestoreLiveInventory
            {
                Windows = live ?? Array.Empty<LiveWindowIdentity>(),
                Resources = resources ?? Array.Empty<RestoreResourceObservation>(),
                BrowserSessionRestore = browserAvailability
            },
            new RestoreMonitorTopology
            {
                Monitors = [new RestoreMonitor("primary", 0, 0, 0, 1920, 1080, 96, true)]
            },
            mode ?? RestoreMode.Standard);

    private static WorkspaceSnapshot Snapshot(params WorkspaceEntry[] entries) => new()
    {
        WorkspaceId = "executor-workspace",
        Name = "Executor fixture",
        Entries = entries.ToList()
    };

    private static WorkspaceEntry Entry(string executable, string title) => new()
    {
        ExecutablePath = executable,
        ProcessName = Path.GetFileNameWithoutExtension(executable),
        WindowClassName = "EditorWindow",
        MonitorId = "primary",
        Position = Record(executable, title)
    };

    private static LiveWindowIdentity Live(
        int hwnd,
        uint pid,
        string executable,
        string title) => WindowIdentityExtractor.FromLive(
            new IntPtr(hwnd),
            pid,
            Record(executable, title));

    private static FakeWindowInventory Inventory(
        params (int Hwnd, uint Pid, string Executable, string Title)[] windows) => new()
    {
        Live = Records(windows)
    };

    private static Dictionary<IntPtr, (uint Pid, WindowRecord Record)> Records(
        params (int Hwnd, uint Pid, string Executable, string Title)[] windows) =>
        windows.ToDictionary(
            window => new IntPtr(window.Hwnd),
            window => (window.Pid, Record(window.Executable, window.Title)));

    private static WindowRecord Record(string executable, string title) => new()
    {
        ExecutablePath = executable,
        ProcessName = Path.GetFileNameWithoutExtension(executable),
        ClassName = "EditorWindow",
        TitleSnippet = title,
        MonitorId = "primary",
        SavedDpi = 96,
        NormalRight = 800,
        NormalBottom = 600
    };
}
