using System.Text.Json;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class ExplorerTabSessionTests
{
    [Fact]
    public void Capture_groups_shell_tabs_by_top_level_window_and_preserves_active_tab()
    {
        IReadOnlyDictionary<IntPtr, ExplorerWindowSession> sessions =
            ExplorerTabSessionSnapshotBuilder.Build(
            [
                new ExplorerTabObservation(new IntPtr(10), new IntPtr(101), @"C:\One", false),
                new ExplorerTabObservation(new IntPtr(20), new IntPtr(201), @"C:\Other", true),
                new ExplorerTabObservation(new IntPtr(10), new IntPtr(102), @"C:\Two", true),
                new ExplorerTabObservation(new IntPtr(10), new IntPtr(103), @"C:\Three", false)
            ]);

        ExplorerWindowSession session = sessions[new IntPtr(10)];
        Assert.Equal([@"C:\One", @"C:\Two", @"C:\Three"], session.TabPaths);
        Assert.Equal(1, session.ActiveTabIndex);
        Assert.Equal(@"C:\Other", Assert.Single(sessions[new IntPtr(20)].TabPaths));
    }

    [Fact]
    public void Explorer_adapter_persists_tabs_only_when_folder_capture_is_enabled()
    {
        var window = ExplorerRecord();
        var adapter = new ExplorerFolderAdapter();

        WorkspaceEntry captured = Assert.IsType<WorkspaceEntry>(adapter.TryCapture(Context(window, true)));
        WorkspaceEntry privateCapture = Assert.IsType<WorkspaceEntry>(adapter.TryCapture(Context(window, false)));

        Assert.Equal(window.ExplorerTabPaths, captured.ExplorerTabPaths);
        Assert.Equal(1, captured.ExplorerActiveTabIndex);
        Assert.Equal(@"C:\Two", captured.LaunchArg);
        Assert.Empty(privateCapture.ExplorerTabPaths);
        Assert.Equal(0, privateCapture.ExplorerActiveTabIndex);
        Assert.Null(privateCapture.LaunchArg);
    }

    [Fact]
    public void V8_migration_seeds_the_legacy_active_folder_as_one_explorer_tab()
    {
        WorkspaceSnapshot legacy = Snapshot(ExplorerEntry());
        legacy.SchemaVersion = 8;
        legacy.EnsureLayoutVariants();
        string json = JsonSerializer.Serialize(legacy, JsonOptions);

        MigratedDocument<WorkspaceSnapshot> migrated = WorkspaceSchemaMigrator.Migrate(
            json,
            "legacy-v8.workspace.json",
            JsonOptions);

        WorkspaceEntry entry = Assert.Single(migrated.Value.Entries);
        Assert.Equal(WorkspaceSnapshot.CurrentSchemaVersion, migrated.Value.SchemaVersion);
        Assert.Equal([@"C:\Two"], entry.ExplorerTabPaths);
        Assert.Equal(0, entry.ExplorerActiveTabIndex);
    }

    [Fact]
    public void Planner_reconciles_tabs_without_reopening_an_exact_folder_match()
    {
        WorkspaceEntry entry = ExplorerEntry();
        RestorePlan plan = BuildPlan(entry, exactTopology: false, RestoreMode.Standard);

        RestorePlanEntry planned = Assert.Single(plan.Entries);
        Assert.Equal(entry.ExplorerTabPaths, planned.ExplorerSession?.TabPaths);
        Assert.Equal(1, planned.ExplorerSession?.ActiveTabIndex);
        Assert.Contains(planned.Actions, action => action.Kind == RestoreActionKind.RestoreExistingWindow);
        RestoreAction tabs = Assert.Single(
            planned.Actions,
            action => action.Kind == RestoreActionKind.RestoreExplorerTabs);
        Assert.Equal(42, tabs.WindowHandle);
        Assert.DoesNotContain(planned.Actions, action => action.Kind == RestoreActionKind.OpenResource);
    }

    [Fact]
    public void Move_existing_policy_does_not_create_explorer_tabs()
    {
        RestorePlan plan = BuildPlan(ExplorerEntry(), exactTopology: false, RestoreMode.MoveExisting);

        Assert.DoesNotContain(plan.Actions, action =>
            action.Kind == RestoreActionKind.RestoreExplorerTabs);
        Assert.Contains(plan.Actions, action =>
            action.Kind == RestoreActionKind.RestoreExistingWindow);
    }

    [Fact]
    public async Task Executor_restores_tabs_on_the_revalidated_existing_explorer_window()
    {
        WorkspaceEntry entry = ExplorerEntry();
        RestorePlan plan = BuildPlan(entry, exactTopology: true, RestoreMode.Resume);
        var inventory = new FakeWindowInventory
        {
            Live =
            {
                [new IntPtr(42)] = (4242, ExplorerRecord())
            }
        };
        var tabs = new FakeExplorerTabSessionRestorer
        {
            Result = new ExplorerTabRestoreResult(3, 1, 2, 0, true)
        };
        var progress = new RecordingProgress<RestoreProgressReport>();
        var executor = new RestoreExecutor(
            inventory,
            new RecordingWindowMutation(),
            new RecordingRestoreProcessLauncher(),
            new FakeRestoreClock(),
            new FakeRestoreResourceBoundary(),
            readinessProbe: new FakeAppReadinessProbe(inventory),
            explorerTabRestorer: tabs);

        RestoreExecutionResult result = await executor.ExecuteAsync(plan, progress: progress);

        (IntPtr handle, RestoreExplorerSession session) = Assert.Single(tabs.Calls);
        Assert.Equal(new IntPtr(42), handle);
        Assert.Equal(entry.ExplorerTabPaths, session.TabPaths);
        Assert.Equal(RestoreExecutionStatus.Completed, result.Status);
        Assert.Equal(RestoreExecutionActionStatus.Succeeded, Assert.Single(result.Actions).Status);
        Assert.Contains(progress.Reports, report =>
            report.Stage == RestoreProgressStage.RestoringExplorerTabs);
    }

    [Fact]
    public async Task Executor_reports_a_partial_explorer_tab_restore_without_losing_the_assignment()
    {
        WorkspaceEntry entry = ExplorerEntry();
        RestorePlan plan = BuildPlan(entry, exactTopology: true, RestoreMode.Resume);
        var inventory = new FakeWindowInventory
        {
            Live =
            {
                [new IntPtr(42)] = (4242, ExplorerRecord())
            }
        };
        var tabs = new FakeExplorerTabSessionRestorer
        {
            Result = new ExplorerTabRestoreResult(3, 1, 1, 1, true)
        };
        var executor = new RestoreExecutor(
            inventory,
            new RecordingWindowMutation(),
            new RecordingRestoreProcessLauncher(),
            new FakeRestoreClock(),
            new FakeRestoreResourceBoundary(),
            readinessProbe: new FakeAppReadinessProbe(inventory),
            explorerTabRestorer: tabs);

        RestoreExecutionResult result = await executor.ExecuteAsync(plan);

        Assert.Equal(RestoreExecutionStatus.CompletedWithFailures, result.Status);
        Assert.Equal([42L], result.AssignedWindowHandles);
        Assert.Equal(RestoreExecutionEntryStatus.Failed, Assert.Single(result.Entries).Status);
        Assert.Equal(RestoreExecutionActionStatus.Failed, Assert.Single(result.Actions).Status);
    }

    [Fact]
    public async Task Live_windows_11_explorer_tab_restore_smoke_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("WINDOWANCHOR_LIVE_EXPLORER_SMOKE"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        using var directory = new TestDirectory();
        string first = Path.Combine(directory.Path, "First");
        string second = Path.Combine(directory.Path, "Second");
        string third = Path.Combine(directory.Path, "Third");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        Directory.CreateDirectory(third);
        var service = new ExplorerTabSessionService();
        HashSet<IntPtr> baseline = service.CaptureOpenWindows().Keys.ToHashSet();
        IntPtr windowHandle = IntPtr.Zero;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/new,\"{first}\"",
                UseShellExecute = true
            });
            windowHandle = await WaitForExplorerWindowAsync(
                service,
                first,
                baseline,
                TimeSpan.FromSeconds(10));
            Assert.NotEqual(IntPtr.Zero, windowHandle);

            var desired = new RestoreExplorerSession([first, second, third], 2);
            ExplorerTabRestoreResult restored = await service.RestoreAsync(
                windowHandle,
                desired,
                CancellationToken.None);
            Assert.True(restored.Succeeded, JsonSerializer.Serialize(restored));
            ExplorerWindowSession observed = await WaitForExplorerSessionAsync(
                service,
                windowHandle,
                desired,
                TimeSpan.FromSeconds(10));

            Assert.Equal(2, restored.OpenedTabCount);
            Assert.Equal(0, restored.FailedTabCount);
            Assert.Equal(NormalizeCounts(desired.TabPaths), NormalizeCounts(observed.TabPaths));
            Assert.Equal(third, observed.TabPaths[observed.ActiveTabIndex], ignoreCase: true);

            ExplorerTabRestoreResult idempotent = await service.RestoreAsync(
                windowHandle,
                desired,
                CancellationToken.None);
            Assert.True(idempotent.Succeeded);
            Assert.Equal(0, idempotent.OpenedTabCount);
        }
        finally
        {
            if (windowHandle != IntPtr.Zero && !baseline.Contains(windowHandle))
                PostMessage(windowHandle, 0x0010, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private static RestorePlan BuildPlan(
        WorkspaceEntry entry,
        bool exactTopology,
        RestoreMode mode) => RestorePlanner.Build(
        Snapshot(entry),
        new RestoreLiveInventory
        {
            Windows =
            [
                WindowIdentityExtractor.FromLive(new IntPtr(42), 4242, ExplorerRecord())
            ]
        },
        new RestoreMonitorTopology
        {
            Monitors = [new RestoreMonitor("primary", 0, 0, 0, 1920, 1080, 96, true)],
            IsExactMatch = exactTopology
        },
        mode);

    private static WorkspaceSnapshot Snapshot(WorkspaceEntry entry) => new()
    {
        WorkspaceId = Guid.NewGuid().ToString("D"),
        Name = "Explorer tabs",
        SavedAt = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc),
        Entries = [entry]
    };

    private static WorkspaceEntry ExplorerEntry() => new()
    {
        ExecutablePath = @"C:\Windows\explorer.exe",
        ProcessName = "explorer",
        WindowClassName = "CabinetWClass",
        FilePath = @"C:\Two",
        FileConfidence = 95,
        FileSource = "EXPLORER_FOLDER",
        LaunchArg = @"C:\Two",
        ExplorerTabPaths = [@"C:\One", @"C:\Two", @"C:\Three"],
        ExplorerActiveTabIndex = 1,
        MonitorId = "primary",
        Position = ExplorerRecord()
    };

    private static WindowRecord ExplorerRecord() => new()
    {
        ExecutablePath = @"C:\Windows\explorer.exe",
        ProcessName = "explorer",
        ClassName = "CabinetWClass",
        TitleSnippet = "Two",
        FolderPath = @"C:\Two",
        ExplorerTabPaths = [@"C:\One", @"C:\Two", @"C:\Three"],
        ExplorerActiveTabIndex = 1,
        MonitorId = "primary",
        SavedDpi = 96,
        NormalRight = 800,
        NormalBottom = 600
    };

    private static AppAdapterCaptureContext Context(WindowRecord window, bool saveFiles) => new(
        window,
        saveFiles,
        new CaptureResourceSearchBudget(false, TimeSpan.Zero, default),
        Progress: null,
        ResourceProgressCurrent: 0,
        ResourceProgressTotal: 0,
        BuildFullJumpListCache: false);

    private static async Task<IntPtr> WaitForExplorerWindowAsync(
        ExplorerTabSessionService service,
        string path,
        IReadOnlySet<IntPtr> excludedHandles,
        TimeSpan timeout)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            KeyValuePair<IntPtr, ExplorerWindowSession> match = service.CaptureOpenWindows()
                .FirstOrDefault(item =>
                    !excludedHandles.Contains(item.Key) &&
                    item.Value.TabPaths.Any(tab =>
                        string.Equals(tab, path, StringComparison.OrdinalIgnoreCase)));
            if (match.Key != IntPtr.Zero)
                return match.Key;
            await Task.Delay(100);
        }
        return IntPtr.Zero;
    }

    private static async Task<ExplorerWindowSession> WaitForExplorerSessionAsync(
        ExplorerTabSessionService service,
        IntPtr windowHandle,
        RestoreExplorerSession desired,
        TimeSpan timeout)
    {
        Stopwatch timer = Stopwatch.StartNew();
        ExplorerWindowSession? lastObserved = null;
        IReadOnlyList<ExplorerTabObservation> lastObservations = [];
        while (timer.Elapsed < timeout)
        {
            lastObservations = service.CaptureOpenTabs();
            IReadOnlyDictionary<IntPtr, ExplorerWindowSession> sessions =
                ExplorerTabSessionSnapshotBuilder.Build(lastObservations);
            if (sessions.TryGetValue(windowHandle, out ExplorerWindowSession? observed) &&
                NormalizeCounts(observed.TabPaths).SequenceEqual(NormalizeCounts(desired.TabPaths)) &&
                observed.ActiveTabIndex >= 0 &&
                observed.ActiveTabIndex < observed.TabPaths.Count &&
                string.Equals(
                    observed.TabPaths[observed.ActiveTabIndex],
                    desired.TabPaths[desired.ActiveTabIndex],
                    StringComparison.OrdinalIgnoreCase))
            {
                return observed;
            }
            lastObserved = observed;
            await Task.Delay(100);
        }
        throw new TimeoutException(
            "The live File Explorer session did not reach the saved tab state. " +
            $"Desired={JsonSerializer.Serialize(desired)}; " +
            $"Observed={JsonSerializer.Serialize(lastObserved)}; " +
            "Handles=" + string.Join(",", lastObservations
                .Where(item => item.WindowHandle == windowHandle)
                .Select(item => $"{item.Location}|{item.TabHandle}|{item.IsActive}")));
    }

    private static IEnumerable<string> NormalizeCounts(IEnumerable<string> paths) => paths
        .GroupBy(path => path.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase)
        .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
        .Select(group => $"{group.Key}|{group.Count()}");

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
