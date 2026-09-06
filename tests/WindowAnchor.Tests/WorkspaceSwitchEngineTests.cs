using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class WorkspaceSwitchEngineTests
{
    [Fact]
    public async Task Coordinator_commits_checkpoint_before_switch_requests_any_close()
    {
        using var directory = new TestDirectory();
        var storage = new StorageService(directory.Path);
        const string exe = @"C:\Apps\editor.exe";
        var record = new WindowAnchor.Models.WindowRecord
        {
            ExecutablePath = exe,
            ProcessName = "editor",
            ClassName = "EditorWindow",
            TitleSnippet = "notes",
            MonitorId = "primary",
            MonitorIndex = 0,
            SavedDpi = 96,
            NormalRight = 800,
            NormalBottom = 600
        };
        var windows = new FakeWindowInventory
        {
            Snapshot = [record],
            Live = new Dictionary<IntPtr, (uint Pid, WindowAnchor.Models.WindowRecord Record)>
            {
                [new IntPtr(501)] = (5001, record)
            }
        };
        var mutation = new RecordingWindowMutation();
        var monitors = new FakeMonitorInventory
        {
            Fingerprint = "current",
            Monitors =
            [
                new WindowAnchor.Models.MonitorInfo
                {
                    MonitorId = "primary",
                    DeviceName = @"\\.\DISPLAY1",
                    Index = 0,
                    IsPrimary = true,
                    WidthPixels = 1920,
                    HeightPixels = 1080
                }
            ]
        };
        var service = new WorkspaceService(
            storage,
            windows,
            mutation,
            monitors,
            new JumpListService(),
            restoreClock: new FakeRestoreClock());
        var target = new WindowAnchor.Models.WorkspaceSnapshot
        {
            Name = "Switch target",
            MonitorFingerprint = "current",
            Monitors = monitors.Monitors,
            Entries =
            [
                new WindowAnchor.Models.WorkspaceEntry
                {
                    ExecutablePath = exe,
                    ProcessName = "editor",
                    WindowClassName = "EditorWindow",
                    MonitorId = "primary",
                    Position = record
                }
            ]
        };
        RestorePlan plan = service.CreateRestorePlan(target, RestoreMode.Standard);
        var switchWindows = new FakeSwitchWindows([new IntPtr(502)], [new IntPtr(502)])
        {
            Alive = _ => false,
            OnRequestClose = () =>
            {
                WindowAnchor.Models.WorkspaceSnapshot checkpoint =
                    Assert.Single(storage.Checkpoints.Load().Workspaces);
                Assert.Equal(
                    WorkspaceCheckpointTrigger.WorkspaceSwitch,
                    checkpoint.Checkpoint?.Trigger);
            }
        };
        var coordinator = new LayoutCoordinator(
            new MonitorService(),
            service,
            Engine(switchWindows));

        RestoreExecutionResult? result = await coordinator.SwitchWorkspaceAsync(target, plan);

        Assert.NotNull(result);
        Assert.Equal(RestoreCheckpointStatus.Created, result!.Checkpoint?.Status);
        Assert.NotEmpty(switchWindows.LastRequested);
    }

    [Fact]
    public async Task Waits_only_for_handles_that_received_close_requests()
    {
        var windows = new FakeSwitchWindows(
            closeCandidates: [new IntPtr(10)],
            riskCandidates: [new IntPtr(10), new IntPtr(20), new IntPtr(30)])
        {
            Alive = _ => false
        };
        var engine = Engine(windows);
        bool restored = false;

        WorkspaceSwitchResult result = await engine.ExecuteAsync(
            new HashSet<IntPtr>(),
            _ =>
            {
                restored = true;
                return Task.FromResult(Completed());
            });

        Assert.Equal(WorkspaceSwitchStatus.Completed, result.Status);
        Assert.Equal(3, result.RiskWindowCount);
        Assert.Equal(1, result.RequestedCloseCount);
        Assert.True(restored);
        Assert.All(windows.AliveChecks, handle => Assert.Equal(new IntPtr(10), handle));
    }

    [Fact]
    public async Task Preserves_approved_destination_handles()
    {
        var windows = new FakeSwitchWindows(
            closeCandidates: [new IntPtr(10), new IntPtr(20)],
            riskCandidates: [new IntPtr(10), new IntPtr(20)])
        {
            Alive = _ => false
        };
        var engine = Engine(windows);

        WorkspaceSwitchResult result = await engine.ExecuteAsync(
            new HashSet<IntPtr> { new IntPtr(20) },
            _ => Task.FromResult(Completed()));

        Assert.Equal(WorkspaceSwitchStatus.Completed, result.Status);
        Assert.Equal([new IntPtr(10)], windows.LastRequested.OrderBy(handle => handle.ToInt64()));
        Assert.DoesNotContain(new IntPtr(20), windows.LastRequested);
    }

    [Fact]
    public async Task New_switch_cancels_previous_switch_and_serializes_restore_callbacks()
    {
        var windows = new FakeSwitchWindows([], []);
        var engine = Engine(windows);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int concurrent = 0;
        int maximumConcurrent = 0;

        Task<WorkspaceSwitchResult> first = engine.ExecuteAsync(
            new HashSet<IntPtr>(),
            async token =>
            {
                int active = Interlocked.Increment(ref concurrent);
                maximumConcurrent = Math.Max(maximumConcurrent, active);
                firstStarted.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return Completed();
                }
                finally
                {
                    Interlocked.Decrement(ref concurrent);
                }
            });
        await firstStarted.Task;

        Task<WorkspaceSwitchResult> second = engine.ExecuteAsync(
            new HashSet<IntPtr>(),
            _ =>
            {
                int active = Interlocked.Increment(ref concurrent);
                maximumConcurrent = Math.Max(maximumConcurrent, active);
                Interlocked.Decrement(ref concurrent);
                return Task.FromResult(Completed());
            });

        Assert.Equal(WorkspaceSwitchStatus.Cancelled, (await first).Status);
        Assert.Equal(WorkspaceSwitchStatus.Completed, (await second).Status);
        Assert.Equal(1, maximumConcurrent);
    }

    [Fact]
    public async Task Timeout_is_wall_clock_bounded_and_wait_notification_is_not_spammed()
    {
        var windows = new FakeSwitchWindows([new IntPtr(10)], [new IntPtr(10)])
        {
            Alive = _ => true
        };
        var engine = new WorkspaceSwitchEngine(
            windows,
            pollInterval: TimeSpan.FromMilliseconds(1),
            timeout: TimeSpan.FromMilliseconds(20),
            notificationInterval: TimeSpan.FromSeconds(30));
        var progress = new List<WorkspaceSwitchProgress>();
        bool restored = false;

        WorkspaceSwitchResult result = await engine.ExecuteAsync(
            new HashSet<IntPtr>(),
            _ =>
            {
                restored = true;
                return Task.FromResult(Completed());
            },
            progress.Add);

        Assert.Equal(WorkspaceSwitchStatus.TimedOut, result.Status);
        Assert.False(restored);
        Assert.Equal([new IntPtr(10)], result.RemainingWindowHandles);
        Assert.Single(progress, item =>
            item.Kind == WorkspaceSwitchProgressKind.WaitingForClose && item.ShouldNotifyUser);
    }

    [Fact]
    public async Task Disposal_cancels_active_switch_and_concurrent_disposers_wait_for_drain()
    {
        var engine = Engine(new FakeSwitchWindows([], []));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<WorkspaceSwitchResult> execution = engine.ExecuteAsync(
            new HashSet<IntPtr>(),
            async token =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Completed();
            });
        await started.Task;

        Task firstDispose = engine.DisposeAsync().AsTask();
        Task secondDispose = engine.DisposeAsync().AsTask();
        await Task.WhenAll(firstDispose, secondDispose);

        Assert.Equal(WorkspaceSwitchStatus.Cancelled, (await execution).Status);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.ExecuteAsync(
            new HashSet<IntPtr>(),
            _ => Task.FromResult(Completed())));
    }

    private static WorkspaceSwitchEngine Engine(FakeSwitchWindows windows) => new(
        windows,
        pollInterval: TimeSpan.FromMilliseconds(1),
        timeout: TimeSpan.FromSeconds(1),
        notificationInterval: TimeSpan.FromMilliseconds(10));

    private static RestoreExecutionResult Completed() => new(
        "workspace",
        RestoreExecutionStatus.Completed,
        WasCancelled: false,
        Entries: [],
        Actions: [],
        AssignedWindowHandles: new HashSet<long>());

    private sealed class FakeSwitchWindows : IWorkspaceSwitchWindowController
    {
        private readonly IReadOnlyList<IntPtr> _closeCandidates;
        private readonly IReadOnlyList<ObservedWindow> _riskCandidates;

        internal FakeSwitchWindows(
            IReadOnlyList<IntPtr> closeCandidates,
            IReadOnlyList<IntPtr> riskCandidates)
        {
            _closeCandidates = closeCandidates;
            _riskCandidates = riskCandidates.Select(Observed).ToArray();
        }

        internal Func<IntPtr, bool> Alive { get; init; } = _ => false;
        internal Action? OnRequestClose { get; init; }
        internal List<IntPtr> AliveChecks { get; } = new();
        internal IReadOnlySet<IntPtr> LastRequested { get; private set; } = new HashSet<IntPtr>();

        public IReadOnlyList<ObservedWindow> InspectUserWindows(WindowCandidatePolicy policy)
        {
            Assert.Equal(WindowCandidatePolicy.SwitchRiskCandidate, policy);
            return _riskCandidates;
        }

        public IReadOnlySet<IntPtr> RequestCloseUserWindowsExcept(
            WindowCandidatePolicy policy,
            IReadOnlySet<IntPtr> keep)
        {
            Assert.Equal(WindowCandidatePolicy.SwitchCloseCandidate, policy);
            OnRequestClose?.Invoke();
            LastRequested = _closeCandidates.Where(handle => !keep.Contains(handle)).ToHashSet();
            return LastRequested;
        }

        public bool IsWindowAlive(IntPtr hWnd)
        {
            AliveChecks.Add(hWnd);
            return Alive(hWnd);
        }

        private static ObservedWindow Observed(IntPtr hWnd) => new(
            hWnd,
            100,
            IntPtr.Zero,
            true,
            "FixtureWindow",
            "Fixture",
            new WindowBounds(0, 0, 800, 600),
            @"C:\Apps\fixture.exe",
            "fixture",
            "");
    }
}
