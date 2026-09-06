using System.Text.Json;
using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class RestorePolicyTests
{
    [Fact]
    public void Workspace_modes_change_missing_and_existing_window_actions()
    {
        WorkspaceEntry missing = Entry(@"C:\Apps\editor.exe", "Notes");
        RestoreLiveInventory missingInventory = Inventory(
            resources:
            [
                new(0, RestoreResourceKind.Executable, RestoreResourceAvailability.Available,
                    missing.ExecutablePath)
            ]);

        RestorePlan resume = Build(Snapshot(missing), missingInventory, RestoreMode.Resume);
        RestorePlan moveExisting = Build(Snapshot(missing), missingInventory, RestoreMode.MoveExisting);
        RestorePlan repairMissing = Build(Snapshot(missing), missingInventory, RestoreMode.Repair);
        RestorePlan previewOnly = Build(Snapshot(missing), missingInventory, RestoreMode.PreviewOnly);

        Assert.Contains(resume.Actions, action => action.Kind == RestoreActionKind.LaunchApplication);
        Assert.Empty(moveExisting.Actions);
        Assert.Empty(repairMissing.Actions);
        Assert.All([moveExisting, repairMissing], plan =>
            Assert.Equal(RestorePlanEntryOutcome.Excluded, Assert.Single(plan.Entries).Outcome));
        Assert.Contains(previewOnly.Actions, action => action.Kind == RestoreActionKind.LaunchApplication);
        Assert.False(previewOnly.CanExecute);

        WorkspaceEntry existing = Entry(@"C:\Apps\editor.exe", "Notes");
        RestoreLiveInventory existingInventory = Inventory(
            windows: [Live(10, existing, left: 0, top: 0, right: 800, bottom: 600)]);
        RestorePlan repairExact = Build(Snapshot(existing), existingInventory, RestoreMode.Repair);
        RestorePlan repairShowState = Build(
            Snapshot(existing),
            Inventory(windows: [Live(11, existing, showCmd: 3)]),
            RestoreMode.Repair);
        RestorePlan move = Build(Snapshot(existing), existingInventory, RestoreMode.MoveExisting);

        Assert.Empty(repairExact.Actions);
        Assert.Equal(RestorePlanEntryOutcome.Matched, Assert.Single(repairExact.Entries).Outcome);
        Assert.Equal(
            RestoreActionKind.RestoreExistingWindow,
            Assert.Single(repairShowState.Actions).Kind);
        Assert.Contains(move.Actions, action => action.Kind == RestoreActionKind.RestoreExistingWindow);
    }

    [Fact]
    public void Per_entry_policy_overrides_workspace_launch_and_reuse_defaults()
    {
        WorkspaceEntry launchOverride = Entry(@"C:\Apps\missing.exe", "Missing");
        launchOverride.RestorePolicy = EntryRestorePolicy.LaunchIfMissing;
        RestorePlan launched = Build(
            Snapshot(launchOverride),
            Inventory(resources:
            [
                new(0, RestoreResourceKind.Executable, RestoreResourceAvailability.Available,
                    launchOverride.ExecutablePath)
            ]),
            RestoreMode.MoveExisting);

        WorkspaceEntry neverLaunch = Entry(@"C:\Apps\missing.exe", "Missing");
        neverLaunch.RestorePolicy = EntryRestorePolicy.NeverLaunch;
        RestorePlan skipped = Build(
            Snapshot(neverLaunch),
            Inventory(resources:
            [
                new(0, RestoreResourceKind.Executable, RestoreResourceAvailability.Available,
                    neverLaunch.ExecutablePath)
            ]),
            RestoreMode.Resume);

        WorkspaceEntry reuse = Entry(@"C:\Apps\editor.exe", "Notes");
        reuse.RestorePolicy = EntryRestorePolicy.ReuseExisting;
        RestorePlan reused = Build(
            Snapshot(reuse),
            Inventory(windows: [Live(20, reuse)]),
            RestoreMode.LaunchFresh);

        Assert.Contains(launched.Actions, action => action.Kind == RestoreActionKind.LaunchApplication);
        Assert.Equal(EntryRestorePolicy.LaunchIfMissing, launched.Entries[0].RestorePolicy.Source);
        Assert.Empty(skipped.Actions);
        Assert.Equal(RestorePlanEntryOutcome.Excluded, skipped.Entries[0].Outcome);
        Assert.Equal(
            RestoreActionKind.RestoreExistingWindow,
            Assert.Single(reused.Actions).Kind);
    }

    [Fact]
    public void Fresh_launch_uses_supported_contract_and_excludes_preexisting_candidates()
    {
        WorkspaceEntry browser = Entry(@"C:\Apps\browser.exe", "Dashboard");
        browser.ProcessName = "browser";
        browser.Position.ProcessName = "browser";
        browser.IsDedicatedBrowserWindow = true;
        browser.BrowserUrl = "https://example.test/dashboard";
        RestorePlan plan = Build(
            Snapshot(browser),
            Inventory(
                windows: [Live(30, browser, browserUrl: browser.BrowserUrl)],
                resources:
                [
                    new(0, RestoreResourceKind.Executable, RestoreResourceAvailability.Available,
                        browser.ExecutablePath)
                ]),
            RestoreMode.LaunchFresh);

        RestorePlanEntry entry = Assert.Single(plan.Entries);
        Assert.Null(entry.SelectedMatch);
        Assert.Contains(30L, entry.ReadinessExcludedWindowHandles);
        Assert.Contains(entry.Actions, action => action.Kind == RestoreActionKind.LaunchDedicatedBrowser);
        Assert.Contains(entry.Actions, action => action.Kind == RestoreActionKind.AwaitWindowAppearance);
        Assert.DoesNotContain(entry.Warnings,
            warning => warning.Code == RestorePlanIssueCode.UnsupportedAlwaysLaunchNew);
    }

    [Fact]
    public void Unsupported_fresh_launch_is_reported_and_reuses_the_safe_match()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\singleton.exe", "Singleton");

        RestorePlan plan = Build(
            Snapshot(entry),
            Inventory(windows: [Live(40, entry)]),
            RestoreMode.LaunchFresh);

        RestorePlanEntry planned = Assert.Single(plan.Entries);
        Assert.Equal(40, planned.SelectedMatch?.WindowHandle);
        Assert.Contains(planned.Warnings,
            warning => warning.Code == RestorePlanIssueCode.UnsupportedAlwaysLaunchNew);
        Assert.Equal(
            RestoreActionKind.RestoreExistingWindow,
            Assert.Single(planned.Actions).Kind);
    }

    [Fact]
    public void Exact_switch_preserves_never_close_and_ignores_managed_entry_actions()
    {
        WorkspaceEntry neverClose = Entry(@"C:\Apps\music.exe", "Music");
        neverClose.RestorePolicy = EntryRestorePolicy.NeverClose;
        WorkspaceEntry ignored = Entry(@"C:\Apps\chat.exe", "Chat");
        ignored.RestorePolicy = EntryRestorePolicy.IgnoreDuringSwitch;

        RestorePlan plan = Build(
            Snapshot(neverClose, ignored),
            Inventory(windows: [Live(50, neverClose), Live(51, ignored)]),
            RestoreMode.ExactSwitch);

        Assert.Equal(RestoreModeKind.ExactSwitch, plan.Mode);
        Assert.Contains(50L, plan.ProtectedWindowHandles);
        Assert.Contains(51L, plan.ProtectedWindowHandles);
        HashSet<IntPtr> switchKeep = LayoutCoordinator.GetSwitchKeepHandles(plan);
        Assert.Contains(new IntPtr(50), switchKeep);
        Assert.Contains(new IntPtr(51), switchKeep);
        Assert.Contains(plan.Actions, action =>
            action.EntryIndex == 0 && action.Kind == RestoreActionKind.RestoreExistingWindow);
        Assert.Equal(RestorePlanEntryOutcome.Excluded, plan.Entries[1].Outcome);
        Assert.DoesNotContain(plan.Actions, action => action.EntryIndex == 1);
    }

    [Fact]
    public void Planning_is_non_mutating_deterministic_and_exposes_mode_and_policy_in_preview()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\editor.exe", "Notes");
        entry.RestorePolicy = EntryRestorePolicy.NeverLaunch;
        WorkspaceSnapshot snapshot = Snapshot(entry);
        string before = JsonSerializer.Serialize(snapshot);

        RestorePlan first = Build(snapshot, Inventory(windows: [Live(60, entry)]), RestoreMode.Repair);
        RestorePlan second = Build(snapshot, Inventory(windows: [Live(60, entry)]), RestoreMode.Repair);
        RestorePlanPreview preview = RestorePlanPreviewBuilder.Build(first);

        Assert.Equal(before, JsonSerializer.Serialize(snapshot));
        Assert.Equal(first.ToRedactedJson(), second.ToRedactedJson());
        Assert.Equal(RestoreModeKind.Repair, preview.Mode);
        Assert.Equal("Never launch", Assert.Single(preview.Entries).PolicyLabel);
    }

    [Fact]
    public async Task Fresh_launch_execution_waits_for_a_new_window_and_preview_only_never_mutates()
    {
        WorkspaceEntry browser = Entry(@"C:\Apps\browser.exe", "Dashboard");
        browser.ProcessName = "browser";
        browser.Position.ProcessName = "browser";
        browser.IsDedicatedBrowserWindow = true;
        browser.BrowserUrl = "https://example.test/dashboard";
        WindowRecord oldRecord = Record(browser, browser.BrowserUrl);
        var inventory = new FakeWindowInventory
        {
            Live = new Dictionary<IntPtr, (uint Pid, WindowRecord Record)>
            {
                [new IntPtr(70)] = (1070, oldRecord)
            }
        };
        RestoreResourceObservation executable = new(
            0,
            RestoreResourceKind.Executable,
            RestoreResourceAvailability.Available,
            browser.ExecutablePath);
        RestorePlan fresh = Build(
            Snapshot(browser),
            Inventory(
                windows: [WindowIdentityExtractor.FromLive(new IntPtr(70), 1070, oldRecord)],
                resources: [executable]),
            RestoreMode.LaunchFresh);
        var process = new RecordingRestoreProcessLauncher
        {
            OnLaunch = _ => inventory.Live[new IntPtr(71)] = (1071, Record(browser, browser.BrowserUrl))
        };
        var mutation = new RecordingWindowMutation();
        var executor = new RestoreExecutor(
            inventory,
            mutation,
            process,
            new FakeRestoreClock(),
            new FakeRestoreResourceBoundary(),
            readinessProbe: new FakeAppReadinessProbe(inventory));

        RestoreExecutionResult freshResult = await executor.ExecuteAsync(fresh);

        Assert.Equal(RestoreExecutionStatus.Completed, freshResult.Status);
        Assert.Equal(new IntPtr(71), Assert.Single(mutation.Restores).Hwnd);
        Assert.DoesNotContain(mutation.Restores, restore => restore.Hwnd == new IntPtr(70));

        RestorePlan previewOnly = Build(
            Snapshot(Entry(@"C:\Apps\missing.exe", "Missing")),
            Inventory(resources:
            [
                new(0, RestoreResourceKind.Executable, RestoreResourceAvailability.Available,
                    @"C:\Apps\missing.exe")
            ]),
            RestoreMode.PreviewOnly);
        var previewProcess = new RecordingRestoreProcessLauncher();
        var previewMutation = new RecordingWindowMutation();
        RestoreExecutionResult previewResult = await new RestoreExecutor(
            new FakeWindowInventory(),
            previewMutation,
            previewProcess,
            new FakeRestoreClock(),
            new FakeRestoreResourceBoundary()).ExecuteAsync(previewOnly);

        Assert.Equal(RestoreExecutionStatus.Rejected, previewResult.Status);
        Assert.Empty(previewProcess.Launches);
        Assert.Empty(previewMutation.Restores);
    }

    private static RestorePlan Build(
        WorkspaceSnapshot snapshot,
        RestoreLiveInventory inventory,
        RestoreMode mode) => RestorePlanner.Build(snapshot, inventory, Topology(), mode);

    private static WorkspaceSnapshot Snapshot(params WorkspaceEntry[] entries) => new()
    {
        WorkspaceId = "11111111-1111-4111-8111-111111111111",
        Name = "Policy fixture",
        SavedAt = new DateTime(2026, 9, 6, 10, 0, 0, DateTimeKind.Utc),
        Monitors =
        [
            new MonitorInfo
            {
                MonitorId = "primary",
                Index = 0,
                WidthPixels = 1920,
                HeightPixels = 1080,
                BoundsRight = 1920,
                BoundsBottom = 1080,
                WorkAreaRight = 1920,
                WorkAreaBottom = 1080,
                Dpi = 96,
                IsPrimary = true
            }
        ],
        Entries = entries.ToList()
    };

    private static WorkspaceEntry Entry(string executable, string title) => new()
    {
        ExecutablePath = executable,
        ProcessName = Path.GetFileNameWithoutExtension(executable),
        WindowClassName = "EditorWindow",
        MonitorId = "primary",
        Position = new WindowRecord
        {
            ExecutablePath = executable,
            ProcessName = Path.GetFileNameWithoutExtension(executable),
            ClassName = "EditorWindow",
            TitleSnippet = title,
            MonitorId = "primary",
            SavedDpi = 96,
            NormalRight = 800,
            NormalBottom = 600,
            ShowCmd = 1
        }
    };

    private static RestoreLiveInventory Inventory(
        IReadOnlyList<LiveWindowIdentity>? windows = null,
        IReadOnlyList<RestoreResourceObservation>? resources = null) => new()
    {
        Windows = windows ?? [],
        Resources = resources ?? []
    };

    private static LiveWindowIdentity Live(
        long hwnd,
        WorkspaceEntry entry,
        int left = 0,
        int top = 0,
        int right = 800,
        int bottom = 600,
        string browserUrl = "",
        int showCmd = 1) => WindowIdentityExtractor.FromLive(
            new IntPtr(hwnd),
            (uint)(1000 + hwnd),
            new WindowRecord
            {
                ExecutablePath = entry.ExecutablePath,
                ProcessName = entry.ProcessName,
                ClassName = entry.WindowClassName,
                TitleSnippet = entry.Position.TitleSnippet,
                BrowserUrl = browserUrl,
                MonitorId = "primary",
                SavedDpi = 96,
                NormalLeft = left,
                NormalTop = top,
                NormalRight = right,
                NormalBottom = bottom,
                ShowCmd = showCmd
            });

    private static WindowRecord Record(WorkspaceEntry entry, string browserUrl = "") => new()
    {
        ExecutablePath = entry.ExecutablePath,
        ProcessName = entry.ProcessName,
        ClassName = entry.WindowClassName,
        TitleSnippet = entry.Position.TitleSnippet,
        BrowserUrl = browserUrl,
        MonitorId = "primary",
        SavedDpi = 96,
        NormalRight = 800,
        NormalBottom = 600,
        ShowCmd = 1
    };

    private static RestoreMonitorTopology Topology() => new()
    {
        IsExactMatch = true,
        Monitors =
        [
            new RestoreMonitor(
                "primary", 0, 0, 0, 1920, 1080, 96, true,
                WorkAreaLeft: 0,
                WorkAreaTop: 0,
                WorkAreaRight: 1920,
                WorkAreaBottom: 1080)
        ]
    };
}
