using WindowAnchor.Native;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class LayoutAndMonitorTests
{
    [Fact]
    public void Topology_signature_captures_restore_relevant_monitor_state()
    {
        var baseline = new List<WindowAnchor.Models.MonitorInfo>
        {
            Monitor("duplicate-edid", @"\\.\DISPLAY1", -1920, 0, 0, 1080, -1920, 0, 0, 1040, 96, true, 1),
            Monitor("duplicate-edid", @"\\.\DISPLAY2", 0, 0, 2560, 1440, 0, 0, 2560, 1400, 144, false, 1)
        };

        string signature = DisplayTopologySignature.Create(baseline);
        Assert.Equal(signature, DisplayTopologySignature.Create(baseline.AsEnumerable().Reverse()));

        foreach (var changed in new[]
        {
            baseline.Select(m => Copy(m, dpi: m.Dpi == 96 ? 120u : m.Dpi)).ToList(),
            baseline.Select(m => Copy(m, orientation: m.Orientation == 1 ? 2 : m.Orientation)).ToList(),
            baseline.Select(m => Copy(m, workAreaBottom: m.WorkAreaBottom - 40)).ToList(),
            baseline.Skip(1).ToList(),
            baseline.Select(m => Copy(m, boundsLeft: m.BoundsLeft + 20)).ToList()
        })
        {
            Assert.NotEqual(signature, DisplayTopologySignature.Create(changed));
        }
    }

    [Fact]
    public async Task Stabilizer_waits_for_an_unchanged_topology_before_continuing()
    {
        var inventory = new FakeMonitorInventory
        {
            Fingerprint = "stable-fingerprint",
            Monitors = [Monitor("dock", @"\\.\DISPLAY1", 0, 0, 1920, 1080, 0, 0, 1920, 1040, 96, true, 1)]
        };
        var clock = new FakeRestoreClock();
        clock.OnDelay = count =>
        {
            if (count == 1)
                inventory.Monitors = [Monitor("dock", @"\\.\DISPLAY1", 0, 0, 1920, 1080, 0, 0, 1920, 1040, 120, true, 1)];
            if (count == 2)
                inventory.Monitors = [Monitor("dock", @"\\.\DISPLAY1", 0, 0, 1920, 1080, 0, 0, 1920, 1040, 144, true, 1)];
        };
        var stabilizer = new DisplayTopologyStabilizer(
            inventory, clock, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(3));

        DisplayTopologyStabilizationResult result = await stabilizer.WaitForStableTopologyAsync();

        Assert.True(result.IsStable);
        Assert.Equal(TimeSpan.FromSeconds(1.5), result.Elapsed);
        Assert.Equal(144u, inventory.Monitors[0].Dpi);
    }

    [Fact]
    public async Task Stabilizer_times_out_without_declaring_an_unstable_topology_ready()
    {
        var inventory = new FakeMonitorInventory
        {
            Monitors = [Monitor("primary", @"\\.\DISPLAY1", 0, 0, 1920, 1080, 0, 0, 1920, 1040, 96, true, 1)]
        };
        var clock = new FakeRestoreClock();
        clock.OnDelay = count => inventory.Monitors =
        [Monitor("primary", @"\\.\DISPLAY1", 0, 0, 1920 + count, 1080, 0, 0, 1920, 1040, 96, true, count % 2)];
        var stabilizer = new DisplayTopologyStabilizer(
            inventory, clock, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2));

        DisplayTopologyStabilizationResult result = await stabilizer.WaitForStableTopologyAsync();

        Assert.False(result.IsStable);
        Assert.True(result.TimedOut);
        Assert.Equal(TimeSpan.FromSeconds(2), result.Elapsed);
    }

    [Fact]
    public void Mixed_dpi_rectangle_is_scaled_without_physical_monitor_hardware()
    {
        var saved = new NativeMethodsWindow.Rect
        {
            Left = -1920,
            Top = 120,
            Right = -960,
            Bottom = 1080
        };

        var scaled = WindowService.ScaleCoordsForDpi(saved, savedDpi: 96, targetDpi: 144);

        Assert.Equal(-2880, scaled.Left);
        Assert.Equal(180, scaled.Top);
        Assert.Equal(-1440, scaled.Right);
        Assert.Equal(1620, scaled.Bottom);
    }

    [Theory]
    [InlineData(96, 96)]
    [InlineData(0, 144)]
    public void Dpi_mapping_preserves_coordinates_when_scaling_is_not_applicable(
        uint savedDpi,
        uint targetDpi)
    {
        var saved = new NativeMethodsWindow.Rect
        {
            Left = 100,
            Top = 200,
            Right = 900,
            Bottom = 800
        };

        var mapped = WindowService.ScaleCoordsForDpi(saved, savedDpi, targetDpi);

        Assert.Equal(saved.Left, mapped.Left);
        Assert.Equal(saved.Top, mapped.Top);
        Assert.Equal(saved.Right, mapped.Right);
        Assert.Equal(saved.Bottom, mapped.Bottom);
    }

    [Fact]
    public void Monitor_fingerprint_is_stable_across_inventory_order()
    {
        string first = MonitorService.ComputeFingerprint(["ABCD:EF01:1", "1234:5678:0"]);
        string second = MonitorService.ComputeFingerprint(["1234:5678:0", "ABCD:EF01:1"]);

        Assert.Equal(first, second);
        Assert.Matches("^[0-9a-f]{8}$", first);
        Assert.Equal("no_monitors", MonitorService.ComputeFingerprint([]));
        Assert.NotEqual(first, MonitorService.ComputeFingerprint(["1234:5678:0"]));
    }

    [Fact]
    public void Rescue_leaves_a_sufficiently_visible_window_untouched()
    {
        var bounds = Rect(1600, 120, 2400, 720);
        bool moved = OffScreenWindowRescueGeometry.TryGetRescueBounds(
            bounds,
            [Monitor("primary", @"\\.\DISPLAY1", 0, 0, 1920, 1080, 0, 0, 1920, 1040, 144, true, 0)],
            new OffScreenWindowRescuePolicy { MinimumVisibleAreaRatio = 0.25 },
            out NativeMethodsWindow.Rect rescued);

        Assert.False(moved);
        Assert.Equal(bounds, rescued);
    }

    [Fact]
    public void Rescue_moves_a_fully_off_screen_window_to_the_nearest_negative_origin_work_area()
    {
        var bounds = Rect(-4200, 100, -3400, 700);
        bool moved = OffScreenWindowRescueGeometry.TryGetRescueBounds(
            bounds,
            [
                Monitor("duplicate-edid", @"\\.\DISPLAY1", -1920, 0, 0, 1080, -1920, 0, 0, 1040, 96, false, 1),
                Monitor("duplicate-edid", @"\\.\DISPLAY2", 0, 0, 2560, 1440, 0, 0, 2560, 1400, 144, true, 1)
            ],
            new OffScreenWindowRescuePolicy(),
            out NativeMethodsWindow.Rect rescued);

        Assert.True(moved);
        Assert.Equal((-1920, 100, -1120, 700),
            (rescued.Left, rescued.Top, rescued.Right, rescued.Bottom));
    }

    [Fact]
    public void Rescue_clamps_missing_monitor_restore_bounds_without_changing_maximized_window_size()
    {
        // A maximized window's WINDOWPLACEMENT normal rectangle is its future restore bounds.
        var restoreBounds = Rect(4200, -100, 5200, 900);
        bool moved = OffScreenWindowRescueGeometry.TryGetRescueBounds(
            restoreBounds,
            [Monitor("primary", @"\\.\DISPLAY1", 0, 0, 1920, 1080, 0, 0, 1920, 1040, 96, true, 0)],
            new OffScreenWindowRescuePolicy { MinimumVisibleAreaRatio = 0.50 },
            out NativeMethodsWindow.Rect rescued);

        Assert.True(moved);
        Assert.Equal(restoreBounds.Right - restoreBounds.Left, rescued.Right - rescued.Left);
        Assert.Equal(restoreBounds.Bottom - restoreBounds.Top, rescued.Bottom - rescued.Top);
        Assert.Equal((920, 0, 1920, 1000),
            (rescued.Left, rescued.Top, rescued.Right, rescued.Bottom));
    }

    [Fact]
    public void Rescue_uses_monitor_bounds_when_a_work_area_is_unavailable()
    {
        var unavailableWorkArea = Monitor("fallback", @"\\.\DISPLAY1", 0, 0, 1200, 800, 0, 0, 0, 0, 96, true, 0);
        bool moved = OffScreenWindowRescueGeometry.TryGetRescueBounds(
            Rect(2000, 0, 2600, 400),
            [unavailableWorkArea],
            new OffScreenWindowRescuePolicy(),
            out NativeMethodsWindow.Rect rescued);

        Assert.True(moved);
        Assert.Equal((600, 0, 1200, 400),
            (rescued.Left, rescued.Top, rescued.Right, rescued.Bottom));
    }

    [Fact]
    public void Rescue_rejects_an_invalid_visibility_threshold()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OffScreenWindowRescueGeometry.TryGetRescueBounds(
                Rect(2000, 0, 2600, 400),
                [Monitor("primary", @"\\.\DISPLAY1", 0, 0, 1200, 800, 0, 0, 1200, 760, 96, true, 0)],
                new OffScreenWindowRescuePolicy { MinimumVisibleAreaRatio = 1.1 },
                out _));
    }

    private static NativeMethodsWindow.Rect Rect(int left, int top, int right, int bottom) => new()
    {
        Left = left,
        Top = top,
        Right = right,
        Bottom = bottom
    };

    private static WindowAnchor.Models.MonitorInfo Monitor(
        string id, string deviceName, int left, int top, int right, int bottom,
        int workLeft, int workTop, int workRight, int workBottom,
        uint dpi, bool primary, int orientation) => new()
    {
        MonitorId = id,
        DeviceName = deviceName,
        BoundsLeft = left,
        BoundsTop = top,
        BoundsRight = right,
        BoundsBottom = bottom,
        WorkAreaLeft = workLeft,
        WorkAreaTop = workTop,
        WorkAreaRight = workRight,
        WorkAreaBottom = workBottom,
        WidthPixels = right - left,
        HeightPixels = bottom - top,
        Dpi = dpi,
        IsPrimary = primary,
        Orientation = orientation
    };

    private static WindowAnchor.Models.MonitorInfo Copy(
        WindowAnchor.Models.MonitorInfo monitor,
        uint? dpi = null,
        int? orientation = null,
        int? workAreaBottom = null,
        int? boundsLeft = null) => new()
    {
        MonitorId = monitor.MonitorId,
        DeviceName = monitor.DeviceName,
        BoundsLeft = boundsLeft ?? monitor.BoundsLeft,
        BoundsTop = monitor.BoundsTop,
        BoundsRight = monitor.BoundsRight,
        BoundsBottom = monitor.BoundsBottom,
        WorkAreaLeft = monitor.WorkAreaLeft,
        WorkAreaTop = monitor.WorkAreaTop,
        WorkAreaRight = monitor.WorkAreaRight,
        WorkAreaBottom = workAreaBottom ?? monitor.WorkAreaBottom,
        Dpi = dpi ?? monitor.Dpi,
        IsPrimary = monitor.IsPrimary,
        Orientation = orientation ?? monitor.Orientation
    };
}
