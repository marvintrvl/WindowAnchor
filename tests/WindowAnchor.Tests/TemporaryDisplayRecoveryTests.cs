using WindowAnchor.Models;
using WindowAnchor.Native;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class TemporaryDisplayRecoveryTests
{
    [Fact]
    public void Tracker_offers_recovery_only_when_the_departed_stable_layout_returns()
    {
        var tracker = new TemporaryDisplayRecoveryTracker();
        DisplayTopologySnapshot origin = Topology("desk", "origin", 1920, 1080);
        DisplayTopologySnapshot temporary = Topology("desk", "temporary-resolution", 1280, 720);
        var workspace = new WorkspaceSnapshot { Name = "Desk layout" };
        tracker.Seed(origin);

        TemporaryDisplayRecoveryObservation departure = tracker.Observe(
            temporary,
            fingerprint => fingerprint == "desk" ? workspace : null);
        TemporaryDisplayRecoveryObservation returned = tracker.Observe(
            origin,
            _ => throw new Xunit.Sdk.XunitException("The saved origin should already be known."));
        TemporaryDisplayRecoveryObservation duplicate = tracker.Observe(origin, _ => workspace);

        Assert.True(departure.DepartureObserved);
        Assert.Null(departure.ReturnedWorkspace);
        Assert.Same(workspace, returned.ReturnedWorkspace);
        Assert.False(duplicate.DepartureObserved);
        Assert.Null(duplicate.ReturnedWorkspace);
    }

    [Fact]
    public void Tracker_does_not_offer_recovery_when_no_workspace_matched_the_departed_layout()
    {
        var tracker = new TemporaryDisplayRecoveryTracker();
        DisplayTopologySnapshot origin = Topology("desk", "origin", 1920, 1080);
        DisplayTopologySnapshot temporary = Topology("desk", "temporary-resolution", 1280, 720);
        tracker.Seed(origin);

        tracker.Observe(temporary, _ => null);
        TemporaryDisplayRecoveryObservation returned = tracker.Observe(origin, _ => null);

        Assert.Null(returned.ReturnedWorkspace);
    }

    [Fact]
    public void Fullscreen_detection_requires_the_full_monitor_bounds_not_ordinary_maximize_bounds()
    {
        var monitor = new MonitorInfo
        {
            BoundsLeft = -1920,
            BoundsTop = 0,
            BoundsRight = 0,
            BoundsBottom = 1080,
            WorkAreaLeft = -1920,
            WorkAreaTop = 0,
            WorkAreaRight = 0,
            WorkAreaBottom = 1040
        };

        Assert.True(WindowService.IsFullscreenBounds(Rect(-1921, -1, 1, 1081), monitor));
        Assert.False(WindowService.IsFullscreenBounds(Rect(-1920, 0, 0, 1040), monitor));
    }

    private static DisplayTopologySnapshot Topology(
        string fingerprint,
        string signature,
        int width,
        int height) => new(
        fingerprint,
        signature,
        [new MonitorInfo
        {
            MonitorId = "primary",
            DeviceName = @"\\.\DISPLAY1",
            BoundsRight = width,
            BoundsBottom = height,
            WorkAreaRight = width,
            WorkAreaBottom = height,
            WidthPixels = width,
            HeightPixels = height,
            IsPrimary = true
        }]);

    private static NativeMethodsWindow.Rect Rect(int left, int top, int right, int bottom) => new()
    {
        Left = left,
        Top = top,
        Right = right,
        Bottom = bottom
    };
}
