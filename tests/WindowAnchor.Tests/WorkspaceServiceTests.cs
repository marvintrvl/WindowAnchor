using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class WorkspaceServiceTests
{
    [Fact]
    public void Take_snapshot_maps_fake_window_inventory_without_persisting_it()
    {
        using var directory = new TestDirectory();
        var primary = Monitor("primary", index: 0, primary: true);
        var portrait = Monitor("portrait", index: 1, primary: false);
        var monitors = new FakeMonitorInventory
        {
            Fingerprint = "fake1234",
            Monitors = [primary, portrait]
        };
        var windows = new FakeWindowInventory
        {
            Snapshot =
            [
                Record(@"C:\Apps\first.exe", "First", "primary", 96),
                Record(@"C:\Apps\second.exe", "Second", "portrait", 144)
            ]
        };
        var mutation = new RecordingWindowMutation();
        var service = CreateService(directory, windows, mutation, monitors);

        var snapshot = service.TakeSnapshot(
            "Portrait only",
            saveFiles: false,
            monitorIds: new HashSet<string> { "portrait" });

        Assert.Equal("fake1234", snapshot.MonitorFingerprint);
        Assert.Same(monitors.Monitors, windows.SuppliedMonitors);
        Assert.Equal(
            WindowCandidatePolicy.CaptureCandidate,
            Assert.Single(windows.SnapshotPolicies));
        Assert.Equal(portrait, Assert.Single(snapshot.Monitors));
        var entry = Assert.Single(snapshot.Entries);
        Assert.Equal("second", entry.ProcessName);
        Assert.Equal("portrait", entry.MonitorId);
        Assert.Equal((uint)144, entry.Position.SavedDpi);
        Assert.False(snapshot.SavedWithFiles);
        Assert.Empty(new StorageService(directory.Path).LoadAllWorkspaces());
    }

    [Fact]
    public void Take_snapshot_preserves_distinct_window_multiplicity()
    {
        using var directory = new TestDirectory();
        WindowRecord first = Record("explorer.exe", "Downloads - File Explorer", "primary", 96);
        first.ProcessName = "explorer";
        first.ClassName = "CabinetWClass";
        first.FolderPath = @"C:\Users\Example\Downloads";
        WindowRecord duplicate = Record("explorer.exe", "Downloads - File Explorer", "primary", 96);
        duplicate.ProcessName = "explorer";
        duplicate.ClassName = "CabinetWClass";
        duplicate.FolderPath = first.FolderPath;
        duplicate.NormalLeft = 200;
        var service = CreateService(
            directory,
            new FakeWindowInventory { Snapshot = [first, duplicate] },
            new RecordingWindowMutation(),
            new FakeMonitorInventory { Monitors = [Monitor("primary", 0, true)] });

        WorkspaceSnapshot snapshot = service.TakeSnapshot("Explorer", saveFiles: false);

        Assert.Equal(2, snapshot.Entries.Count);
        Assert.All(snapshot.Entries, entry => Assert.Equal(first.FolderPath, entry.Position.FolderPath));
    }

    [Fact]
    public async Task Complete_capture_is_not_written_until_one_explicit_persist_call()
    {
        using var directory = new TestDirectory();
        _ = new StorageService(directory.Path); // Create the import marker before write counting.
        var writer = new RecordingAtomicFileWriter();
        var storage = new StorageService(directory.Path, writer);
        var monitors = new FakeMonitorInventory
        {
            Fingerprint = "capture1",
            Monitors = [Monitor("primary", 0, primary: true)]
        };
        var windows = new FakeWindowInventory
        {
            Snapshot = [Record(@"C:\Apps\chrome.exe", "Research tabs", "primary", 96)]
        };
        var browser = new FakeBrowserSessionConnector
        {
            CaptureResult = BrowserCaptureResult.Captured(
                [new BrowserSession { Browser = "chrome", ActiveTitle = "Research tabs" }])
        };
        var service = CreateService(
            directory,
            windows,
            new RecordingWindowMutation(),
            monitors,
            storage,
            browser);

        WorkspaceCaptureResult capture = await service.CaptureWorkspaceAsync(
            "Complete Capture",
            saveFiles: false);

        Assert.Empty(writer.Destinations);
        Assert.Empty(storage.LoadAllWorkspaces());
        Assert.Equal(BrowserCaptureStatus.Captured, capture.BrowserCapture.Status);
        Assert.Equal("Research tabs", Assert.Single(browser.SelectedTitles));
        Assert.Single(capture.Snapshot.BrowserSessions);

        service.PersistCapture(
            capture,
            WorkspaceArtifactKind.NamedWorkspace,
            IncompleteBrowserCapturePolicy.SavePartialWorkspace);

        Assert.Single(writer.Destinations);
        var saved = Assert.Single(storage.LoadAllWorkspaces());
        Assert.Equal("Complete Capture", saved.Name);
        Assert.Single(saved.BrowserSessions);
    }

    [Theory]
    [InlineData(BrowserCaptureStatus.Unavailable)]
    [InlineData(BrowserCaptureStatus.TimedOut)]
    public async Task Incomplete_browser_capture_records_outcome_and_saves_window_snapshot_once(
        BrowserCaptureStatus status)
    {
        using var directory = new TestDirectory();
        _ = new StorageService(directory.Path);
        var writer = new RecordingAtomicFileWriter();
        var storage = new StorageService(directory.Path, writer);
        var browser = new FakeBrowserSessionConnector
        {
            CaptureResult = BrowserCaptureResult.Empty(status, "Injected connector outcome")
        };
        var service = CreateService(
            directory,
            new FakeWindowInventory
            {
                Snapshot = [Record(@"C:\Apps\chrome.exe", "Browser", "primary", 96)]
            },
            new RecordingWindowMutation(),
            new FakeMonitorInventory { Monitors = [Monitor("primary", 0, true)] },
            storage,
            browser);

        WorkspaceCaptureResult capture = await service.CaptureWorkspaceAsync(
            "Partial Capture",
            saveFiles: false);

        Assert.Equal(status, capture.BrowserCapture.Status);
        Assert.Single(capture.Snapshot.Entries);
        Assert.Empty(capture.Snapshot.BrowserSessions);
        Assert.Empty(writer.Destinations);

        service.PersistCapture(
            capture,
            WorkspaceArtifactKind.NamedWorkspace,
            IncompleteBrowserCapturePolicy.SavePartialWorkspace);

        Assert.Single(writer.Destinations);
        Assert.Single(storage.LoadAllWorkspaces());
    }

    [Fact]
    public async Task Unexpected_browser_failure_becomes_a_partial_result_without_discarding_windows()
    {
        using var directory = new TestDirectory();
        var storage = new StorageService(directory.Path);
        var browser = new FakeBrowserSessionConnector
        {
            CaptureException = new InvalidOperationException("Injected browser failure")
        };
        var service = CreateService(
            directory,
            new FakeWindowInventory
            {
                Snapshot = [Record(@"C:\Apps\chrome.exe", "Browser", "primary", 96)]
            },
            new RecordingWindowMutation(),
            new FakeMonitorInventory { Monitors = [Monitor("primary", 0, true)] },
            storage,
            browser);

        WorkspaceCaptureResult capture = await service.CaptureWorkspaceAsync(
            "Failed Browser Capture",
            saveFiles: false);

        Assert.Equal(BrowserCaptureStatus.Failed, capture.BrowserCapture.Status);
        Assert.Contains("Injected browser failure", capture.BrowserCapture.Detail);
        Assert.Single(capture.Snapshot.Entries);
        Assert.Empty(storage.LoadAllWorkspaces());

        service.PersistCapture(
            capture,
            WorkspaceArtifactKind.NamedWorkspace,
            IncompleteBrowserCapturePolicy.SavePartialWorkspace);
        Assert.Single(storage.LoadAllWorkspaces());
    }

    [Fact]
    public async Task Browser_capture_is_explicitly_skipped_when_no_browser_window_is_selected()
    {
        using var directory = new TestDirectory();
        var browser = new FakeBrowserSessionConnector();
        var service = CreateService(
            directory,
            new FakeWindowInventory
            {
                Snapshot = [Record(@"C:\Apps\editor.exe", "Notes", "primary", 96)]
            },
            new RecordingWindowMutation(),
            new FakeMonitorInventory { Monitors = [Monitor("primary", 0, true)] },
            browserConnector: browser);

        WorkspaceCaptureResult capture = await service.CaptureWorkspaceAsync(
            "No Browser",
            saveFiles: false);

        Assert.Equal(BrowserCaptureStatus.Skipped, capture.BrowserCapture.Status);
        Assert.Equal(0, browser.CaptureCalls);
    }

    [Fact]
    public async Task Require_complete_browser_policy_prevents_partial_persistence()
    {
        using var directory = new TestDirectory();
        var storage = new StorageService(directory.Path);
        var service = CreateService(
            directory,
            new FakeWindowInventory
            {
                Snapshot = [Record(@"C:\Apps\chrome.exe", "Browser", "primary", 96)]
            },
            new RecordingWindowMutation(),
            new FakeMonitorInventory { Monitors = [Monitor("primary", 0, true)] },
            storage);
        WorkspaceCaptureResult capture = await service.CaptureWorkspaceAsync(
            "Strict Capture",
            saveFiles: false);

        Assert.Equal(BrowserCaptureStatus.Unavailable, capture.BrowserCapture.Status);
        Assert.Throws<InvalidOperationException>(() => service.PersistCapture(
            capture,
            WorkspaceArtifactKind.NamedWorkspace,
            IncompleteBrowserCapturePolicy.RequireCompleteBrowserCapture));
        Assert.Empty(storage.LoadAllWorkspaces());
    }

    [Theory]
    [InlineData(WorkspaceArtifactKind.Checkpoint)]
    [InlineData(WorkspaceArtifactKind.TemporaryCapture)]
    public async Task Headless_capture_pipeline_can_target_non_named_repositories(
        WorkspaceArtifactKind destination)
    {
        using var directory = new TestDirectory();
        var storage = new StorageService(directory.Path);
        var service = CreateService(
            directory,
            new FakeWindowInventory
            {
                Snapshot = [Record(@"C:\Apps\editor.exe", "Notes", "primary", 96)]
            },
            new RecordingWindowMutation(),
            new FakeMonitorInventory { Monitors = [Monitor("primary", 0, true)] },
            storage);

        WorkspaceCaptureResult capture = await service.CaptureWorkspaceAsync(
            "Headless Capture",
            saveFiles: false,
            captureBrowserSessions: false);
        service.PersistCapture(
            capture,
            destination,
            IncompleteBrowserCapturePolicy.SavePartialWorkspace);

        Assert.Empty(storage.LoadAllWorkspaces());
        Assert.Equal(
            destination == WorkspaceArtifactKind.Checkpoint ? 1 : 0,
            storage.Checkpoints.Load().Workspaces.Count);
        Assert.Equal(
            destination == WorkspaceArtifactKind.TemporaryCapture ? 1 : 0,
            storage.TemporaryCaptures.Load().Workspaces.Count);
    }

    [Fact]
    public async Task Native_capture_failure_is_observable_and_does_not_persist()
    {
        using var directory = new TestDirectory();
        var storage = new StorageService(directory.Path);
        var service = CreateService(
            directory,
            new ThrowingSnapshotWindowInventory(),
            new RecordingWindowMutation(),
            new FakeMonitorInventory(),
            storage);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CaptureWorkspaceAsync("Inventory Failure", saveFiles: false));

        Assert.Equal("Injected snapshot inventory failure", error.Message);
        Assert.Empty(storage.LoadAllWorkspaces());
    }

    [Fact]
    public async Task Pre_cancelled_capture_does_not_construct_or_persist_a_snapshot()
    {
        using var directory = new TestDirectory();
        var storage = new StorageService(directory.Path);
        var windows = new FakeWindowInventory
        {
            Snapshot = [Record(@"C:\Apps\editor.exe", "Notes", "primary", 96)]
        };
        var service = CreateService(
            directory,
            windows,
            new RecordingWindowMutation(),
            new FakeMonitorInventory { Monitors = [Monitor("primary", 0, true)] },
            storage);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CaptureWorkspaceAsync(
                "Cancelled Capture",
                saveFiles: false,
                cancellationToken: cancellation.Token));

        Assert.Null(windows.SuppliedMonitors);
        Assert.Empty(storage.LoadAllWorkspaces());
    }

    [Fact]
    public async Task Pre_cancelled_restore_is_stopped_before_checkpoint_or_mutation()
    {
        using var directory = new TestDirectory();
        const string exe = @"C:\Apps\editor.exe";
        var position = Record(exe, "notes", "primary", 96);
        position.ClassName = "EditorWindow";
        var entry = new WorkspaceEntry
        {
            ExecutablePath = exe,
            ProcessName = "editor",
            WindowClassName = "EditorWindow",
            Position = position
        };
        var windows = new FakeWindowInventory
        {
            Live = new Dictionary<IntPtr, (uint Pid, WindowRecord Record)>
            {
                [new IntPtr(71)] = (7001, Record(exe, "notes", "primary", 96))
            }
        };
        windows.Live[new IntPtr(71)].Record.ClassName = "EditorWindow";
        var mutation = new RecordingWindowMutation();
        var service = CreateService(directory, windows, mutation, new FakeMonitorInventory());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await service.RestoreWorkspaceAsync(new WorkspaceSnapshot { Entries = [entry] }, cancellation.Token);

        // Planning is read-only. The cancelled transaction never captures a checkpoint and never
        // enters executor revalidation or native mutation.
        Assert.Equal(1, windows.LiveInventoryCalls);
        Assert.All(
            windows.LivePolicies,
            policy => Assert.Equal(WindowCandidatePolicy.RestoreMatchCandidate, policy));
        Assert.Empty(windows.SnapshotPolicies);
        Assert.Empty(mutation.Restores);
        Assert.False(entry.WasRestored);
        Assert.Empty(new StorageService(directory.Path).Checkpoints.Load().Workspaces);
    }

    [Fact]
    public async Task Align_and_minimize_uses_the_final_session_owned_hwnd_set()
    {
        using var directory = new TestDirectory();
        const string exe = @"C:\Apps\editor.exe";
        var firstPosition = Record(exe, "first", "primary", 96);
        firstPosition.ClassName = "EditorWindow";
        var secondPosition = Record(exe, "second", "primary", 96);
        secondPosition.ClassName = "EditorWindow";
        var snapshot = new WorkspaceSnapshot
        {
            Entries =
            [
                new WorkspaceEntry
                {
                    ExecutablePath = exe,
                    ProcessName = "editor",
                    WindowClassName = "EditorWindow",
                    Position = firstPosition
                },
                new WorkspaceEntry
                {
                    ExecutablePath = exe,
                    ProcessName = "editor",
                    WindowClassName = "EditorWindow",
                    Position = secondPosition
                }
            ]
        };
        var liveRecord = Record(exe, "one live editor", "primary", 96);
        liveRecord.ClassName = "EditorWindow";
        var windows = new FakeWindowInventory
        {
            Live = new Dictionary<IntPtr, (uint Pid, WindowRecord Record)>
            {
                [new IntPtr(91)] = (9001, liveRecord)
            }
        };
        var mutation = new RecordingWindowMutation();
        var service = CreateService(directory, windows, mutation, new FakeMonitorInventory());

        RestoreSessionResult result = await service.RestoreWorkspaceWithResultAsync(
            snapshot,
            minimizeOthers: true);

        Assert.Equal(new IntPtr(91), Assert.Single(result.AssignedHwnds));
        Assert.Equal(new IntPtr(91), Assert.Single(Assert.Single(mutation.MinimizeCalls)));
        Assert.Equal(
            WindowCandidatePolicy.MinimizeCandidate,
            Assert.Single(mutation.MinimizePolicies));
        Assert.Single(mutation.Restores);
        Assert.Contains(
            result.Actions,
            action => action.Kind == RestoreSessionActionKind.MinimizeOtherWindows);
    }

    [Fact]
    public async Task Native_inventory_failure_is_observable_at_the_restore_boundary()
    {
        using var directory = new TestDirectory();
        var service = CreateService(
            directory,
            new ThrowingWindowInventory(),
            new RecordingWindowMutation(),
            new FakeMonitorInventory());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RestoreWorkspaceAsync(new WorkspaceSnapshot()));

        Assert.Equal("Injected native inventory failure", error.Message);
    }

    [Fact]
    public async Task Mutation_failure_is_observable_at_the_restore_boundary()
    {
        using var directory = new TestDirectory();
        const string exe = @"C:\Apps\editor.exe";
        var position = Record(exe, "notes", "primary", 96);
        position.ClassName = "EditorWindow";
        var windows = new FakeWindowInventory
        {
            Live = new Dictionary<IntPtr, (uint Pid, WindowRecord Record)>
            {
                [new IntPtr(81)] = (8001, position)
            }
        };
        var entry = new WorkspaceEntry
        {
            ExecutablePath = exe,
            ProcessName = "editor",
            WindowClassName = "EditorWindow",
            Position = position
        };
        var storage = new StorageService(directory.Path);
        var service = CreateService(
            directory,
            windows,
            new ThrowingWindowMutation(),
            new FakeMonitorInventory(),
            storage);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RestoreWorkspaceAsync(new WorkspaceSnapshot { Entries = [entry] }));

        Assert.Equal("Injected mutation failure", error.Message);
        Assert.Single(storage.Checkpoints.Load().Workspaces);
    }

    [Fact]
    public async Task Restore_commits_checkpoint_before_first_window_mutation()
    {
        using var directory = new TestDirectory();
        var events = new List<string>();
        const string exe = @"C:\Apps\editor.exe";
        WindowRecord saved = Record(exe, "notes", "primary", 96);
        saved.ClassName = "EditorWindow";
        WindowRecord live = Record(exe, "notes", "primary", 96);
        live.ClassName = "EditorWindow";
        var snapshot = new WorkspaceSnapshot
        {
            Name = "Target",
            Monitors = [Monitor("primary", 0, true)],
            Entries =
            [
                new WorkspaceEntry
                {
                    ExecutablePath = exe,
                    ProcessName = "editor",
                    WindowClassName = "EditorWindow",
                    MonitorId = "primary",
                    Position = saved
                }
            ]
        };
        var windows = new FakeWindowInventory
        {
            Snapshot = [live],
            Live = new Dictionary<IntPtr, (uint Pid, WindowRecord Record)>
            {
                [new IntPtr(301)] = (3001, live)
            },
            OnSnapshotWindows = () => events.Add("capture")
        };
        var mutation = new RecordingWindowMutation
        {
            OnRestore = (_, _) => events.Add("mutation")
        };
        var storage = new StorageService(directory.Path);
        var service = CreateService(
            directory,
            windows,
            mutation,
            new FakeMonitorInventory { Monitors = [Monitor("primary", 0, true)] },
            storage,
            restoreClock: new FakeRestoreClock(),
            placementProbe: new FakeWindowPlacementProbe());

        RestoreExecutionResult result = await service.RestoreWorkspaceWithExecutionResultAsync(
            snapshot,
            RestoreMode.Standard);

        Assert.Equal(["capture", "mutation"], events.Take(2));
        Assert.Equal(RestoreCheckpointStatus.Created, result.Checkpoint?.Status);
        WorkspaceSnapshot checkpoint = Assert.Single(storage.Checkpoints.Load().Workspaces);
        Assert.Equal(result.Checkpoint?.CheckpointId, checkpoint.WorkspaceId);
        Assert.Equal(snapshot.WorkspaceId, checkpoint.Checkpoint?.TargetWorkspaceId);
        Assert.NotEmpty(mutation.Restores);
    }

    [Fact]
    public async Task Restore_checkpoint_uses_fast_resource_capture_and_reports_its_stages()
    {
        using var directory = new TestDirectory();
        const string exe = @"C:\Apps\Code.exe";
        WindowRecord current = Record(
            exe,
            "README.md - Untitled (Workspace) - Visual Studio Code",
            "primary",
            96);
        current.ClassName = "Chrome_WidgetWin_1";
        var target = new WorkspaceSnapshot
        {
            Name = "Target",
            Monitors = [Monitor("primary", 0, true)],
            Entries =
            [
                new WorkspaceEntry
                {
                    ExecutablePath = exe,
                    ProcessName = "Code",
                    WindowClassName = current.ClassName,
                    MonitorId = "primary",
                    Position = current
                }
            ]
        };
        var windows = new FakeWindowInventory
        {
            Snapshot = [current],
            Live = new Dictionary<IntPtr, (uint Pid, WindowRecord Record)>
            {
                [new IntPtr(304)] = (3004, current)
            }
        };
        var storage = new StorageService(directory.Path);
        var progress = new RecordingProgress<RestoreProgressReport>();
        var service = CreateService(
            directory,
            windows,
            new RecordingWindowMutation(),
            new FakeMonitorInventory { Monitors = [Monitor("primary", 0, true)] },
            storage,
            restoreClock: new FakeRestoreClock(),
            placementProbe: new FakeWindowPlacementProbe());

        RestoreExecutionResult result = await service.RestoreWorkspaceWithExecutionResultAsync(
            target,
            RestoreMode.Standard,
            progress: progress);

        Assert.Equal(RestoreCheckpointStatus.Created, result.Checkpoint?.Status);
        WorkspaceSnapshot checkpoint = Assert.Single(storage.Checkpoints.Load().Workspaces);
        WorkspaceEntry captured = Assert.Single(checkpoint.Entries);
        Assert.True(checkpoint.SavedWithFiles);
        Assert.Equal("README.md", captured.FilePath);
        Assert.Equal(40, captured.FileConfidence);
        Assert.Null(captured.LaunchArg);
        Assert.Contains(progress.Reports,
            report => report.Stage == RestoreProgressStage.PreparingCheckpoint);
        Assert.Contains(progress.Reports,
            report => report.Stage == RestoreProgressStage.DetectingResources &&
                      report.Message.Contains("Code", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(progress.Reports,
            report => report.Stage == RestoreProgressStage.SavingCheckpoint);
    }

    [Fact]
    public async Task Checkpoint_write_failure_rejects_restore_without_any_mutation()
    {
        using var directory = new TestDirectory();
        _ = new StorageService(directory.Path);
        var failingWriter = new ThrowingAtomicFileWriter();
        var storage = new StorageService(directory.Path, failingWriter);
        const string exe = @"C:\Apps\editor.exe";
        WindowRecord record = Record(exe, "notes", "primary", 96);
        record.ClassName = "EditorWindow";
        var windows = new FakeWindowInventory
        {
            Snapshot = [record],
            Live = new Dictionary<IntPtr, (uint Pid, WindowRecord Record)>
            {
                [new IntPtr(302)] = (3002, record)
            }
        };
        var mutation = new RecordingWindowMutation();
        var service = CreateService(
            directory,
            windows,
            mutation,
            new FakeMonitorInventory { Monitors = [Monitor("primary", 0, true)] },
            storage);
        var snapshot = new WorkspaceSnapshot
        {
            Name = "Blocked target",
            Monitors = [Monitor("primary", 0, true)],
            Entries =
            [
                new WorkspaceEntry
                {
                    ExecutablePath = exe,
                    ProcessName = "editor",
                    WindowClassName = "EditorWindow",
                    MonitorId = "primary",
                    Position = record
                }
            ]
        };

        RestoreExecutionResult result = await service.RestoreWorkspaceWithExecutionResultAsync(
            snapshot,
            RestoreMode.Standard);

        Assert.Equal(RestoreExecutionStatus.Rejected, result.Status);
        Assert.Equal(RestoreCheckpointStatus.Failed, result.Checkpoint?.Status);
        Assert.Equal(1, failingWriter.Calls);
        Assert.Empty(mutation.Restores);
        Assert.Empty(storage.Checkpoints.Load().Workspaces);
    }

    [Fact]
    public async Task Undo_uses_normal_planner_and_creates_an_undoable_safety_checkpoint()
    {
        using var directory = new TestDirectory();
        var clock = new FakeCheckpointClock();
        var storage = new StorageService(directory.Path, checkpointClock: clock);
        const string exe = @"C:\Apps\editor.exe";
        WindowRecord prior = Record(exe, "notes", "primary", 96);
        prior.ClassName = "EditorWindow";
        prior.NormalLeft = 10;
        var recovery = new WorkspaceSnapshot
        {
            Name = "Before restore",
            Monitors = [Monitor("primary", 0, true)],
            Entries =
            [
                new WorkspaceEntry
                {
                    ExecutablePath = exe,
                    ProcessName = "editor",
                    WindowClassName = "EditorWindow",
                    MonitorId = "primary",
                    Position = prior
                }
            ]
        };
        storage.Checkpoints.Save(
            recovery,
            WorkspaceCheckpointTrigger.Restore,
            Guid.NewGuid().ToString("D"));

        clock.UtcNow = clock.UtcNow.AddHours(1);
        WindowRecord current = Record(exe, "notes", "primary", 96);
        current.ClassName = "EditorWindow";
        current.NormalLeft = 700;
        var windows = new FakeWindowInventory
        {
            Snapshot = [current],
            Live = new Dictionary<IntPtr, (uint Pid, WindowRecord Record)>
            {
                [new IntPtr(303)] = (3003, current)
            }
        };
        var mutation = new RecordingWindowMutation();
        var service = CreateService(
            directory,
            windows,
            mutation,
            new FakeMonitorInventory { Monitors = [Monitor("primary", 0, true)] },
            storage,
            restoreClock: new FakeRestoreClock(),
            placementProbe: new FakeWindowPlacementProbe());

        RestoreExecutionResult result = Assert.IsType<RestoreExecutionResult>(
            await service.UndoLastRestoreAsync());

        Assert.Equal(WorkspaceCheckpointTrigger.Undo, result.Checkpoint?.Trigger);
        Assert.NotEmpty(mutation.Restores);
        Assert.All(mutation.Restores, restore => Assert.Equal(10, restore.Record.NormalLeft));
        Assert.Equal(2, storage.Checkpoints.Load().Workspaces.Count);
        WorkspaceSnapshot safety = Assert.IsType<WorkspaceSnapshot>(storage.Checkpoints.GetLatest());
        Assert.Equal(WorkspaceCheckpointTrigger.Undo, safety.Checkpoint?.Trigger);
        Assert.Equal(700, Assert.Single(safety.Entries).Position.NormalLeft);
    }

    [Fact]
    public async Task Approved_preview_is_executed_without_silently_replanning_changed_inventory()
    {
        using var directory = new TestDirectory();
        const string exe = @"C:\Apps\editor.exe";
        var savedPosition = Record(exe, "notes", "primary", 96);
        savedPosition.ClassName = "EditorWindow";
        var snapshot = new WorkspaceSnapshot
        {
            WorkspaceId = "approved-preview-workspace",
            Entries =
            [
                new WorkspaceEntry
                {
                    ExecutablePath = exe,
                    ProcessName = "editor",
                    WindowClassName = "EditorWindow",
                    Position = savedPosition
                }
            ]
        };
        var firstLive = Record(exe, "notes", "primary", 96);
        firstLive.ClassName = "EditorWindow";
        var replacementLive = Record(exe, "notes", "primary", 96);
        replacementLive.ClassName = "EditorWindow";
        var windows = new FakeWindowInventory
        {
            Live = new Dictionary<IntPtr, (uint Pid, WindowRecord Record)>
            {
                [new IntPtr(101)] = (10101, firstLive)
            }
        };
        var mutation = new RecordingWindowMutation();
        var service = CreateService(directory, windows, mutation, new FakeMonitorInventory());
        RestorePlan preview = service.CreateRestorePlan(snapshot, RestoreMode.Standard);
        windows.Live = new Dictionary<IntPtr, (uint Pid, WindowRecord Record)>
        {
            [new IntPtr(102)] = (10202, replacementLive)
        };

        RestoreExecutionResult result = await service.ExecuteApprovedRestorePlanAsync(
            snapshot,
            preview);

        Assert.Equal(RestoreExecutionStatus.StalePlan, result.Status);
        Assert.Contains(
            result.Actions,
            action => action.StaleReason is RestorePlanStaleReason.WindowClosed or
                RestorePlanStaleReason.WindowInventoryChanged);
        Assert.Empty(mutation.Restores);
        Assert.Equal(2, windows.LiveInventoryCalls);
        Assert.False(snapshot.Entries[0].WasRestored);
    }

    [Fact]
    public void Launch_planning_characterizes_dedicated_store_and_missing_document_entries()
    {
        using var directory = new TestDirectory();
        var service = CreateService(
            directory,
            new FakeWindowInventory(),
            new RecordingWindowMutation(),
            new FakeMonitorInventory());
        var fixture = TestDirectory.LoadFixture("current.workspace.json");

        var dedicated = fixture.Entries.Single(entry => entry.IsDedicatedBrowserWindow);
        var dedicatedStart = service.BuildProcessStartInfo(dedicated);
        Assert.Equal(dedicated.ExecutablePath, dedicatedStart.FileName);
        Assert.Equal($"--new-window \"{dedicated.BrowserUrl}\"", dedicatedStart.Arguments);
        Assert.False(dedicatedStart.UseShellExecute);

        var store = fixture.Entries.Single(entry => entry.ProcessName == "Notepad");
        var storeStart = service.BuildProcessStartInfo(store);
        Assert.Equal("explorer.exe", storeStart.FileName);
        Assert.Equal($"shell:AppsFolder\\{store.AppUserModelId}", storeStart.Arguments);
        Assert.True(storeStart.UseShellExecute);

        var missing = fixture.Entries.Single(entry => entry.LaunchArg == @"Z:\missing\gone.txt");
        Assert.False(File.Exists(missing.LaunchArg));
        var missingStart = service.BuildProcessStartInfo(missing);
        Assert.Equal(missing.LaunchArg, missingStart.FileName);
        Assert.True(missingStart.UseShellExecute);
    }

    [Fact]
    public void Restore_planning_repairs_a_stale_versioned_packaged_app_without_saved_aumid()
    {
        using var directory = new TestDirectory();
        const string oldExecutable =
            @"C:\Program Files\WindowsApps\Contoso.Suite_1.0_x64__abc\Host.exe";
        const string currentExecutable =
            @"C:\Program Files\WindowsApps\Contoso.Suite_2.0_x64__abc\Host.exe";
        const string aumid = "Contoso.Suite_abc!App";
        WindowRecord position = Record(oldExecutable, "Contoso Suite", "primary", 96);
        var snapshot = new WorkspaceSnapshot
        {
            Name = "Package update fixture",
            Entries =
            [
                new WorkspaceEntry
                {
                    ExecutablePath = oldExecutable,
                    ProcessName = "Host",
                    WindowClassName = position.ClassName,
                    MonitorId = "primary",
                    Position = position
                }
            ],
            Monitors = [Monitor("primary", 0, primary: true)]
        };
        var resources = new FakeRestoreResourceBoundary
        {
            DefaultAvailability = RestoreResourceAvailability.Missing
        };
        var packages = new FakePackagedAppResolver
        {
            Resolution = new PackagedAppResolution(
                aumid,
                currentExecutable,
                "Contoso.Suite_abc",
                true)
        };
        var service = CreateService(
            directory,
            new FakeWindowInventory(),
            new RecordingWindowMutation(),
            new FakeMonitorInventory
            {
                Monitors = [Monitor("primary", 0, primary: true)]
            },
            restoreResources: resources,
            packagedApps: packages);

        RestorePlan plan = service.CreateRestorePlan(snapshot, RestoreMode.Standard);

        RestorePlanEntry entry = Assert.Single(plan.Entries);
        Assert.Empty(entry.BlockingErrors);
        RestoreAction action = Assert.Single(entry.Actions,
            action => action.Kind == RestoreActionKind.ActivatePackagedApplication);
        Assert.Equal($"shell:AppsFolder\\{aumid}", action.Arguments);
        Assert.Contains(entry.Warnings, warning => warning.Code == RestorePlanIssueCode.StaleResource);
        Assert.Equal((oldExecutable, ""), Assert.Single(packages.Calls));
    }

    [Fact]
    public void Create_restore_plan_applies_hint_loaded_from_versioned_settings()
    {
        using var directory = new TestDirectory();
        var storage = new StorageService(directory.Path);
        var settings = new SettingsService(Path.Combine(directory.Path, "settings.json"), storage);
        WindowRecord savedRecord = Record(@"C:\Apps\editor.exe", "Alpha report", "primary", 96);
        var entry = new WorkspaceEntry
        {
            ExecutablePath = savedRecord.ExecutablePath,
            ProcessName = savedRecord.ProcessName,
            WindowClassName = savedRecord.ClassName,
            MonitorId = "primary",
            Position = savedRecord
        };
        var snapshot = new WorkspaceSnapshot
        {
            Name = "Learned fixture",
            Entries = [entry],
            Monitors = [Monitor("primary", 0, primary: true)]
        };
        WindowRecord north = Record(entry.ExecutablePath, "Alpha report north", "primary", 96);
        WindowRecord south = Record(entry.ExecutablePath, "Alpha report south", "primary", 96);
        settings.RememberWindowMatch(
            snapshot.WorkspaceId,
            entry.EntryId,
            WindowIdentityExtractor.ToHint(WindowIdentityExtractor.FromLive(
                new IntPtr(81),
                1081,
                north)));
        var windows = new FakeWindowInventory
        {
            Live = new Dictionary<IntPtr, (uint Pid, WindowRecord Record)>
            {
                [new IntPtr(82)] = (1082, south),
                [new IntPtr(81)] = (1081, north)
            }
        };
        var service = CreateService(
            directory,
            windows,
            new RecordingWindowMutation(),
            new FakeMonitorInventory
            {
                Monitors = [Monitor("primary", 0, primary: true)]
            },
            storage,
            settings: settings);

        RestorePlan plan = service.CreateRestorePlan(snapshot, RestoreMode.Standard);

        RestorePlanCandidate selected = Assert.IsType<RestorePlanCandidate>(
            Assert.Single(plan.Entries).SelectedMatch);
        Assert.Equal(81, selected.WindowHandle);
        Assert.True(selected.IsLearnedHintMatch);
    }

    private static WorkspaceService CreateService(
        TestDirectory directory,
        IWindowInventory inventory,
        IWindowMutation mutation,
        IMonitorInventory monitors,
        StorageService? storage = null,
        IBrowserSessionConnector? browserConnector = null,
        SettingsService? settings = null,
        IRestoreResourceBoundary? restoreResources = null,
        IPackagedAppResolver? packagedApps = null,
        IRestoreClock? restoreClock = null,
        IWindowPlacementProbe? placementProbe = null) => new(
            storage ?? new StorageService(directory.Path),
            inventory,
            mutation,
            monitors,
            new JumpListService(),
            new WebAppService(),
            browserConnector,
            restoreClock: restoreClock,
            restoreResources: restoreResources,
            settingsService: settings,
            packagedAppResolver: packagedApps,
            placementProbe: placementProbe);

    private static MonitorInfo Monitor(string id, int index, bool primary) => new()
    {
        MonitorId = id,
        FriendlyName = id,
        DeviceName = $@"\\.\DISPLAY{index + 1}",
        Index = index,
        WidthPixels = primary ? 3840 : 1440,
        HeightPixels = primary ? 2160 : 2560,
        IsPrimary = primary
    };

    private static WindowRecord Record(
        string executablePath,
        string title,
        string monitorId,
        uint dpi) => new()
    {
        ExecutablePath = executablePath,
        ProcessName = System.IO.Path.GetFileNameWithoutExtension(executablePath),
        ClassName = "WindowClass",
        TitleSnippet = title,
        MonitorId = monitorId,
        MonitorIndex = monitorId == "primary" ? 0 : 1,
        MonitorName = monitorId,
        SavedDpi = dpi,
        NormalLeft = monitorId == "primary" ? 0 : -1440,
        NormalTop = 0,
        NormalRight = monitorId == "primary" ? 1920 : 0,
        NormalBottom = 1080
    };
}
