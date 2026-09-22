using System;
using System.Collections.Generic;
using System.Linq;
using WindowAnchor.Models;
using WindowAnchor.Services;
using Xunit;

namespace WindowAnchor.Tests;

public class AppAdapterTests
{
    [Fact]
    public void ChromiumAdapter_CapturesPwaMetadataAndIdentity()
    {
        var adapter = new ChromiumWebAppAdapter(new WebAppService(
        [
            new WebAppInfo(
                "Chrome.App.app-id",
                @"C:\Users\Test\Desktop\Calendar.lnk",
                @"C:\Program Files\Google\Chrome\Application\chrome_proxy.exe",
                "--profile-directory=Default --app-id=app-id",
                "Calendar")
        ]));
        var window = Window("chrome", "Chrome.App.app-id");

        WorkspaceEntry entry = Assert.IsType<WorkspaceEntry>(adapter.TryCapture(Capture(window)));
        SavedWindowIdentity identity = adapter.EnrichIdentity(entry, WindowIdentityExtractor.FromSaved(entry));

        Assert.True(entry.IsWebApp);
        Assert.Equal("WEB_APP_SHORTCUT", entry.FileSource);
        Assert.Equal("Calendar", entry.WebAppName);
        Assert.Equal("chromium-web-app", identity.AppAdapterIdentity);
    }

    [Fact]
    public void Registry_UsesGenericFallback_WhenSpecializedAdapterDeclines()
    {
        var fallback = new RecordingAdapter("generic-win32", captureResult: Entry("fallback"));
        var declining = new RecordingAdapter("declining", canHandleWindow: true);
        var registry = new AppAdapterRegistry([declining], fallback);

        WorkspaceEntry entry = registry.Capture(Capture(Window("sample")));

        Assert.Equal("fallback", entry.ProcessName);
        Assert.True(declining.CaptureCalled);
        Assert.True(fallback.CaptureCalled);
    }

    [Fact]
    public void Registry_UsesAdapterLaunchDecision_WhenAdapterSuppliesOne()
    {
        var overrideAdapter = new RecordingAdapter(
            "test-app",
            canHandleEntry: true,
            launchDecision: LaunchDecision());
        var registry = new AppAdapterRegistry([overrideAdapter], new RecordingAdapter("generic-win32"));

        bool handled = registry.TryPlanLaunch(LaunchContext(Entry("test")), out RestoreLaunchDecision decision);

        Assert.True(handled);
        Assert.Same(overrideAdapter.LaunchDecision, decision);
    }

    [Fact]
    public void Registry_ExposesAdapterReadinessAndVerificationStrategies()
    {
        var readiness = new TestReadinessStrategy();
        var verification = new TestVerificationStrategy();
        var adapter = new RecordingAdapter(
            "test-app",
            readinessStrategy: readiness,
            placementVerificationStrategy: verification);
        var registry = new AppAdapterRegistry([adapter], new RecordingAdapter("generic-win32"));

        Assert.Same(readiness, Assert.Single(registry.ReadinessStrategies));
        Assert.Same(verification, Assert.Single(registry.PlacementVerificationStrategies));
    }

    private static AppAdapterCaptureContext Capture(WindowRecord window) => new(
        window,
        SaveFiles: true,
        new CaptureResourceSearchBudget(
            enabled: false,
            limit: TimeSpan.Zero,
            CancellationToken.None),
        Progress: null,
        ResourceProgressCurrent: 1,
        ResourceProgressTotal: 1,
        BuildFullJumpListCache: false);

    private static AppAdapterLaunchContext LaunchContext(WorkspaceEntry entry) => new(
        0,
        entry,
        HasSelectedMatch: false,
        CorrectResourceMatched: false,
        BrowserSessionScheduled: false,
        Array.Empty<RunningApplicationIdentity>(),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<(int EntryIndex, RestoreResourceKind Kind), RestoreResourceObservation>(),
        new RestoreTargetPlacement(
            "monitor",
            0,
            RestoreMonitorMappingKind.ExactStableId,
            0,
            0,
            800,
            600,
            1,
            96,
            96,
            false));

    private static RestoreLaunchDecision LaunchDecision() => new(
        RestoreLaunchRequirement.None("Adapter launch test."),
        Array.Empty<RestoreAction>(),
        false,
        false,
        false,
        Array.Empty<RestorePlanIssue>(),
        Array.Empty<RestorePlanIssue>());

    private static WindowRecord Window(string processName, string appUserModelId = "") => new()
    {
        ProcessName = processName,
        ExecutablePath = $@"C:\Apps\{processName}.exe",
        AppUserModelId = appUserModelId,
        ClassName = "WindowClass",
        TitleSnippet = "Window"
    };

    private static WorkspaceEntry Entry(string processName) => new()
    {
        ProcessName = processName,
        ExecutablePath = $@"C:\Apps\{processName}.exe",
        Position = Window(processName)
    };

    private sealed class RecordingAdapter : IAppAdapter
    {
        private readonly bool _canHandleWindow;
        private readonly bool _canHandleEntry;
        private readonly WorkspaceEntry? _captureResult;

        internal RecordingAdapter(
            string name,
            bool canHandleWindow = false,
            bool canHandleEntry = false,
            WorkspaceEntry? captureResult = null,
            RestoreLaunchDecision? launchDecision = null,
            IAppReadinessStrategy? readinessStrategy = null,
            IWindowPlacementVerificationStrategy? placementVerificationStrategy = null)
        {
            Name = name;
            _canHandleWindow = canHandleWindow;
            _canHandleEntry = canHandleEntry;
            _captureResult = captureResult;
            LaunchDecision = launchDecision;
            ReadinessStrategy = readinessStrategy;
            PlacementVerificationStrategy = placementVerificationStrategy;
        }

        public string Name { get; }
        public RestoreLaunchDecision? LaunchDecision { get; }
        public bool CaptureCalled { get; private set; }
        public IAppReadinessStrategy? ReadinessStrategy { get; }
        public IWindowPlacementVerificationStrategy? PlacementVerificationStrategy { get; }
        public bool CanHandle(WindowRecord window) => _canHandleWindow;
        public bool CanHandle(WorkspaceEntry entry) => _canHandleEntry;

        public WorkspaceEntry? TryCapture(AppAdapterCaptureContext context)
        {
            CaptureCalled = true;
            return _captureResult;
        }

        public SavedWindowIdentity EnrichIdentity(WorkspaceEntry entry, SavedWindowIdentity identity) =>
            identity with { AppAdapterIdentity = Name };

        public bool TryPlanLaunch(AppAdapterLaunchContext context, out RestoreLaunchDecision decision)
        {
            decision = LaunchDecision!;
            return LaunchDecision != null;
        }
    }

    private sealed class TestReadinessStrategy : IAppReadinessStrategy
    {
        public string Name => "test-readiness";
        public bool CanHandle(SavedWindowIdentity identity) => true;
        public AppReadinessDecision Evaluate(AppReadinessContext context) =>
            new(AppReadinessState.ProcessStarted, "Adapter readiness test.");
    }

    private sealed class TestVerificationStrategy : IWindowPlacementVerificationStrategy
    {
        public string Name => "test-verification";
        public bool CanHandle(SavedWindowIdentity identity) => true;
        public WindowPlacementVerificationPolicy GetPolicy(RestorePlanEntry entry) =>
            WindowPlacementVerificationPolicy.Default;
    }
}
