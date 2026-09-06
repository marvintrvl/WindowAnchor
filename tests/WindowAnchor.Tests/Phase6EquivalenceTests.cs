using System.Security.Cryptography;
using System.Text;
using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class Phase6EquivalenceTests
{
    [Fact]
    public async Task All_window_capture_matches_golden_shape_and_progress_order()
    {
        using var directory = new TestDirectory();
        MonitorInfo primary = Monitor("primary", 0, true);
        MonitorInfo secondary = Monitor("secondary", 1, false);
        var inventory = new FakeWindowInventory
        {
            Snapshot =
            [
                Record(@"C:\Apps\WindowAnchor.exe", "WindowAnchor", primary),
                Record(@"C:\Windows\notepad.exe", "draft.txt - Notepad", primary),
                Record("explorer.exe", "Projects - File Explorer", secondary, @"C:\Projects"),
                Record(
                    @"C:\Apps\brave.exe",
                    "Fixture PWA",
                    primary,
                    appUserModelId: "Chrome._crx_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
                Record(
                    @"C:\Apps\chrome.exe",
                    "Fixture Site",
                    secondary,
                    browserUrl: "https://example.test/work")
            ]
        };
        var trace = new List<string>();
        inventory.OnSnapshotWindows = () => trace.Add("native-snapshot");
        var browser = new OrderedBrowserConnector(trace)
        {
            Result = BrowserCaptureResult.Captured(
            [
                new BrowserSession
                {
                    Browser = "chrome",
                    ActiveTitle = "Fixture Site",
                    WindowIndex = 2,
                    Left = 10,
                    Top = 20,
                    Width = 1200,
                    Height = 800,
                    State = "normal",
                    Tabs =
                    [
                        new BrowserTab
                        {
                            Url = "https://example.test/work",
                            Title = "Fixture Site",
                            Index = 0,
                            Active = true,
                            Pinned = false
                        }
                    ]
                }
            ])
        };
        var progress = new RecordingProgress<SaveProgressReport>();
        await using WorkspaceService service = CreateService(
            directory,
            inventory,
            new FakeMonitorInventory
            {
                Fingerprint = "golden-fingerprint",
                Monitors = [primary, secondary]
            },
            browser);

        WorkspaceCaptureResult capture = await service.CaptureWorkspaceAsync(
            "Golden capture",
            saveFiles: true,
            progress: progress,
            searchCommonFolders: true,
            commonFolderSearchBudget: TimeSpan.Zero,
            buildFullJumpListCache: false);

        Assert.Equal(["native-snapshot", "browser-capture"], trace);
        Assert.Equal(
            [
                WorkspaceCaptureProgressStage.DetectingResources,
                WorkspaceCaptureProgressStage.DetectingResources,
                WorkspaceCaptureProgressStage.DetectingResources,
                WorkspaceCaptureProgressStage.DetectingResources,
                WorkspaceCaptureProgressStage.DetectingResources,
                WorkspaceCaptureProgressStage.Finalizing,
                WorkspaceCaptureProgressStage.CapturingBrowserSession,
                WorkspaceCaptureProgressStage.Finalizing
            ],
            progress.Reports.Select(report => report.Stage));
        Assert.Equal((5, 5), (progress.Reports[5].Current, progress.Reports[5].Total));
        Assert.Equal((4, 4), (progress.Reports[^1].Current, progress.Reports[^1].Total));
        Assert.Equal(
            "snapshot|Golden capture|golden-fingerprint|True|primary:0:True:1920x1080;secondary:1:False:1280x1024\n" +
            "entry|notepad|WindowClass||draft.txt|40|TITLE_PARSE||False|||False||primary|0|Primary|draft.txt - Notepad|10,20,810,620|96|\n" +
            "entry|explorer|WindowClass||C:\\Projects|95|EXPLORER_FOLDER|C:\\Projects|False|||False||secondary|1|Secondary|Projects - File Explorer|1300,30,2200,730|120|C:\\Projects\n" +
            "entry|brave|WindowClass|Chrome._crx_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa||0|WEB_APP_AUMID||True|Fixture PWA|C:\\Apps\\brave.exe|False||primary|0|Primary|Fixture PWA|10,20,810,620|96|\n" +
            "entry|chrome|WindowClass|||0|BROWSER_URL||False|||True|https://example.test/work|secondary|1|Secondary|Fixture Site|1300,30,2200,730|120|\n" +
            "browser|Captured|1|chrome:Fixture Site:2:10,20,1200,800:normal:https://example.test/work",
            Project(capture));
        Assert.True(Guid.TryParse(capture.Snapshot.WorkspaceId, out _));
        Assert.All(capture.Snapshot.Entries, entry => Assert.True(Guid.TryParse(entry.EntryId, out _)));
        Assert.Equal(capture.Snapshot.Entries.Count, capture.Snapshot.Entries.Select(entry => entry.EntryId).Distinct().Count());
        Assert.InRange(capture.Snapshot.SavedAt, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task Selective_capture_matches_golden_shape_without_reenumerating_windows()
    {
        using var directory = new TestDirectory();
        MonitorInfo primary = Monitor("primary", 0, true);
        MonitorInfo secondary = Monitor("secondary", 1, false);
        var inventory = new FakeWindowInventory
        {
            Snapshot = [Record(@"C:\Apps\unselected.exe", "Unselected", primary)]
        };
        List<WindowRecord> selected =
        [
            Record(@"C:\Apps\editor.exe", "Notes", secondary),
            Record("explorer.exe", "Docs - File Explorer", primary, @"C:\Docs")
        ];
        var progress = new RecordingProgress<SaveProgressReport>();
        await using WorkspaceService service = CreateService(
            directory,
            inventory,
            new FakeMonitorInventory
            {
                Fingerprint = "selective-fingerprint",
                Monitors = [primary, secondary]
            });

        WorkspaceCaptureResult capture = await service.CaptureWorkspaceAsync(
            "Selective golden",
            saveFiles: false,
            monitorIds: new HashSet<string> { "not-used-for-selected-windows" },
            progress: progress,
            selectedWindows: selected,
            captureBrowserSessions: false);

        Assert.Empty(inventory.SnapshotPolicies);
        Assert.Equal(["primary", "secondary"], capture.Snapshot.Monitors.Select(monitor => monitor.MonitorId));
        Assert.Equal(
            [
                WorkspaceCaptureProgressStage.DetectingResources,
                WorkspaceCaptureProgressStage.DetectingResources,
                WorkspaceCaptureProgressStage.Finalizing,
                WorkspaceCaptureProgressStage.Finalizing
            ],
            progress.Reports.Select(report => report.Stage));
        Assert.Equal(BrowserCaptureStatus.Skipped, capture.BrowserCapture.Status);
        Assert.Equal(
            "snapshot|Selective golden|selective-fingerprint|False|primary:0:True:1920x1080;secondary:1:False:1280x1024\n" +
            "entry|editor|WindowClass|||0|NONE||False|||False||secondary|1|Secondary|Notes|1300,30,2200,730|120|\n" +
            "entry|explorer|WindowClass|||0|NONE||False|||False||primary|0|Primary|Docs - File Explorer|10,20,810,620|96|C:\\Docs\n" +
            "browser|Skipped|0|",
            Project(capture));
    }

    [Fact]
    public async Task Capture_cancellation_after_native_work_never_crosses_browser_boundary()
    {
        using var directory = new TestDirectory();
        MonitorInfo primary = Monitor("primary", 0, true);
        var inventory = new FakeWindowInventory
        {
            Snapshot =
            [
                Record(@"C:\Apps\first.exe", "First", primary),
                Record(@"C:\Apps\second.exe", "Second", primary)
            ]
        };
        var browser = new OrderedBrowserConnector([]);
        using var cancellation = new CancellationTokenSource();
        var progress = new CallbackProgress<SaveProgressReport>(report =>
        {
            if (report.Stage == WorkspaceCaptureProgressStage.DetectingResources)
                cancellation.Cancel();
        });
        await using WorkspaceService service = CreateService(
            directory,
            inventory,
            new FakeMonitorInventory { Monitors = [primary] },
            browser);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CaptureWorkspaceAsync(
            "Cancelled",
            saveFiles: false,
            progress: progress,
            captureBrowserSessions: true,
            cancellationToken: cancellation.Token));

        Assert.Equal(0, browser.CaptureCalls);
    }

    [Fact]
    public void Common_folder_search_stops_at_the_shared_budget_boundary()
    {
        using var directory = new TestDirectory();
        var budget = new CaptureResourceSearchBudget(
            enabled: true,
            limit: TimeSpan.Zero,
            CancellationToken.None);

        string? result = CaptureResourceResolver.SearchFileInCommonLocations(
            "missing.txt",
            [directory.Path],
            budget,
            out bool timedOut);

        Assert.Null(result);
        Assert.True(timedOut);
        Assert.False(budget.CanSearch);
    }

    [Fact]
    public void Restore_plan_matches_whole_object_golden_fingerprint()
    {
        MonitorInfo primary = Monitor("primary", 0, true);
        WindowRecord position = Record(@"C:\Apps\editor.exe", "Quarterly notes", primary);
        var entry = new WorkspaceEntry
        {
            EntryId = "entry-golden-1",
            ExecutablePath = position.ExecutablePath,
            ProcessName = position.ProcessName,
            WindowClassName = position.ClassName,
            MonitorId = position.MonitorId,
            MonitorIndex = position.MonitorIndex,
            MonitorName = position.MonitorName,
            Position = position
        };
        var snapshot = new WorkspaceSnapshot
        {
            WorkspaceId = "workspace-golden-1",
            Name = "Golden restore",
            SavedAt = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc),
            MonitorFingerprint = "golden",
            Monitors = [primary],
            Entries = [entry]
        };
        LiveWindowIdentity live = WindowIdentityExtractor.FromLive(
            new IntPtr(71),
            7100,
            Record(@"C:\Apps\editor.exe", "Quarterly notes", primary));

        RestorePlan plan = RestorePlanner.Build(
            snapshot,
            new RestoreLiveInventory { Windows = [live] },
            new RestoreMonitorTopology
            {
                IsExactMatch = true,
                Monitors =
                [
                    new RestoreMonitor(
                        primary.MonitorId,
                        primary.Index,
                        primary.BoundsLeft,
                        primary.BoundsTop,
                        primary.BoundsRight,
                        primary.BoundsBottom,
                        primary.Dpi,
                        primary.IsPrimary,
                        primary.WorkAreaLeft,
                        primary.WorkAreaTop,
                        primary.WorkAreaRight,
                        primary.WorkAreaBottom)
                ]
            },
            RestoreMode.AlignAndMinimize);

        Assert.Equal(
            "F4880FB8CAF84ECBB7038D98468059D866ADC55DB2394F9360BFC0D640210DB8",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plan.ToRedactedJson()))));
    }

    private static WorkspaceService CreateService(
        TestDirectory directory,
        IWindowInventory inventory,
        IMonitorInventory monitors,
        IBrowserSessionConnector? browser = null) => new(
            new StorageService(directory.Path),
            inventory,
            new RecordingWindowMutation(),
            monitors,
            new JumpListService(),
            new WebAppService([]),
            browser);

    private static MonitorInfo Monitor(string id, int index, bool primary) => new()
    {
        MonitorId = id,
        FriendlyName = primary ? "Primary" : "Secondary",
        DeviceName = $@"\\.\DISPLAY{index + 1}",
        Index = index,
        WidthPixels = primary ? 1920 : 1280,
        HeightPixels = primary ? 1080 : 1024,
        BoundsLeft = primary ? 0 : 1920,
        BoundsTop = 0,
        BoundsRight = primary ? 1920 : 3200,
        BoundsBottom = primary ? 1080 : 1024,
        WorkAreaLeft = primary ? 0 : 1920,
        WorkAreaTop = 0,
        WorkAreaRight = primary ? 1920 : 3200,
        WorkAreaBottom = primary ? 1040 : 984,
        Dpi = primary ? 96u : 120u,
        IsPrimary = primary
    };

    private static WindowRecord Record(
        string executable,
        string title,
        MonitorInfo monitor,
        string folderPath = "",
        string appUserModelId = "",
        string browserUrl = "") => new()
    {
        ExecutablePath = executable,
        ProcessName = Path.GetFileNameWithoutExtension(executable),
        ClassName = "WindowClass",
        TitleSnippet = title,
        AppUserModelId = appUserModelId,
        BrowserUrl = browserUrl,
        FolderPath = folderPath,
        MonitorId = monitor.MonitorId,
        MonitorIndex = monitor.Index,
        MonitorName = monitor.FriendlyName,
        SavedDpi = monitor.Dpi,
        NormalLeft = monitor.IsPrimary ? 10 : 1300,
        NormalTop = monitor.IsPrimary ? 20 : 30,
        NormalRight = monitor.IsPrimary ? 810 : 2200,
        NormalBottom = monitor.IsPrimary ? 620 : 730
    };

    private static string Project(WorkspaceCaptureResult capture)
    {
        WorkspaceSnapshot snapshot = capture.Snapshot;
        string monitors = string.Join(
            ';',
            snapshot.Monitors.Select(monitor =>
                $"{monitor.MonitorId}:{monitor.Index}:{monitor.IsPrimary}:{monitor.WidthPixels}x{monitor.HeightPixels}"));
        var lines = new List<string>
        {
            $"snapshot|{snapshot.Name}|{snapshot.MonitorFingerprint}|{snapshot.SavedWithFiles}|{monitors}"
        };
        lines.AddRange(snapshot.Entries.Select(entry =>
            $"entry|{entry.ProcessName}|{entry.WindowClassName}|{entry.AppUserModelId}|" +
            $"{entry.FilePath}|{entry.FileConfidence}|{entry.FileSource}|{entry.LaunchArg}|" +
            $"{entry.IsWebApp}|{entry.WebAppName}|{entry.WebAppLaunchTarget}|" +
            $"{entry.IsDedicatedBrowserWindow}|{entry.BrowserUrl}|{entry.MonitorId}|" +
            $"{entry.MonitorIndex}|{entry.MonitorName}|{entry.Position.TitleSnippet}|" +
            $"{entry.Position.NormalLeft},{entry.Position.NormalTop}," +
            $"{entry.Position.NormalRight},{entry.Position.NormalBottom}|" +
            $"{entry.Position.SavedDpi}|{entry.Position.FolderPath}"));
        lines.Add(
            $"browser|{capture.BrowserCapture.Status}|{snapshot.BrowserSessions.Count}|" +
            string.Join(';', snapshot.BrowserSessions.Select(session =>
                $"{session.Browser}:{session.ActiveTitle}:{session.WindowIndex}:" +
                $"{session.Left},{session.Top},{session.Width},{session.Height}:" +
                $"{session.State}:{string.Join(',', session.Tabs.Select(tab => tab.Url))}")));
        return string.Join('\n', lines);
    }

    private sealed class OrderedBrowserConnector(List<string> trace) : IBrowserSessionConnector
    {
        internal BrowserCaptureResult Result { get; init; } =
            BrowserCaptureResult.Captured([]);
        internal int CaptureCalls { get; private set; }

        public Task<BrowserCaptureResult> CaptureAsync(
            string workspaceName,
            IEnumerable<string> selectedBrowserTitles,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureCalls++;
            trace.Add("browser-capture");
            return Task.FromResult(Result);
        }

        public Task<bool> RestoreAsync(
            string workspaceName,
            List<BrowserSession> sessions,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
