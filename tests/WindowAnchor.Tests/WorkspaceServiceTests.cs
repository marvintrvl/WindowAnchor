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
    public async Task Pre_cancelled_restore_characterizes_current_phase_one_behavior()
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

        // The current restore checks cancellation after phase-one matching and repositioning.
        // One read builds the plan, one validates that the preview is still current, and a final
        // read immediately revalidates the approved HWND before mutation. Cancellation is still
        // observed after this initial reconciliation phase.
        Assert.Equal(3, windows.LiveInventoryCalls);
        Assert.All(
            windows.LivePolicies,
            policy => Assert.Equal(WindowCandidatePolicy.RestoreMatchCandidate, policy));
        Assert.Equal(new IntPtr(71), Assert.Single(mutation.Restores).Hwnd);
        Assert.True(entry.WasRestored);
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
        var service = CreateService(
            directory,
            windows,
            new ThrowingWindowMutation(),
            new FakeMonitorInventory());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RestoreWorkspaceAsync(new WorkspaceSnapshot { Entries = [entry] }));

        Assert.Equal("Injected mutation failure", error.Message);
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

    private static WorkspaceService CreateService(
        TestDirectory directory,
        IWindowInventory inventory,
        IWindowMutation mutation,
        IMonitorInventory monitors,
        StorageService? storage = null,
        IBrowserSessionConnector? browserConnector = null) => new(
            storage ?? new StorageService(directory.Path),
            inventory,
            mutation,
            monitors,
            new JumpListService(),
            new WebAppService(),
            browserConnector);

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
