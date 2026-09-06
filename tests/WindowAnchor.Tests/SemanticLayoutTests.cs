using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class SemanticLayoutTests
{
    [Fact]
    public void Capture_can_normalize_visible_frame_instead_of_invisible_resize_border()
    {
        MonitorInfo monitor = SavedMonitor(
            "primary",
            0,
            (0, 0, 1920, 1080),
            (0, 0, 1920, 1040),
            96,
            primary: true);
        var window = new WindowRecord
        {
            NormalLeft = -8,
            NormalTop = -8,
            NormalRight = 968,
            NormalBottom = 1048,
            SavedDpi = 96
        };

        NormalizedWindowLayout layout = Assert.IsType<NormalizedWindowLayout>(
            WindowLayoutGeometry.Capture(
                window,
                monitor,
                new WindowBounds(0, 0, 960, 1040)));

        Assert.Equal(WindowLayoutKind.LeftHalf, layout.Kind);
        Assert.Equal(0, layout.X);
        Assert.Equal(0, layout.Y);
    }

    [Fact]
    public void Visible_target_is_expanded_by_current_invisible_frame_for_window_placement()
    {
        var target = new WindowAnchor.Native.NativeMethodsWindow.Rect
        {
            Left = 0,
            Top = 0,
            Right = 960,
            Bottom = 1040
        };
        var outer = new WindowAnchor.Native.NativeMethodsWindow.Rect
        {
            Left = 92,
            Top = 92,
            Right = 1108,
            Bottom = 908
        };
        var visible = new WindowAnchor.Native.NativeMethodsWindow.Rect
        {
            Left = 100,
            Top = 100,
            Right = 1100,
            Bottom = 900
        };

        WindowAnchor.Native.NativeMethodsWindow.Rect compensated =
            WindowService.CompensateForVisibleFrame(target, outer, visible);

        Assert.Equal(-8, compensated.Left);
        Assert.Equal(-8, compensated.Top);
        Assert.Equal(968, compensated.Right);
        Assert.Equal(1048, compensated.Bottom);
    }

    [Fact]
    public void Capture_derives_left_half_on_negative_origin_portrait_work_area()
    {
        var monitor = SavedMonitor(
            "portrait",
            0,
            bounds: (-1200, -200, 0, 1720),
            work: (-1200, -160, 0, 1680),
            dpi: 144,
            primary: true);
        var window = new WindowRecord
        {
            NormalLeft = -1200,
            NormalTop = -160,
            NormalRight = -600,
            NormalBottom = 1680,
            ShowCmd = 1
        };

        NormalizedWindowLayout layout = Assert.IsType<NormalizedWindowLayout>(
            WindowLayoutGeometry.Capture(window, monitor));

        Assert.Equal(WindowLayoutKind.LeftHalf, layout.Kind);
        Assert.Equal(HorizontalWindowAnchor.Left, layout.HorizontalAnchor);
        Assert.Equal(VerticalWindowAnchor.Stretch, layout.VerticalAnchor);
        Assert.Equal((0d, 0d, .5d, 1d), (layout.X, layout.Y, layout.Width, layout.Height));
    }

    [Fact]
    public void Exact_topology_preserves_exact_pixels_and_separate_maximized_state()
    {
        WorkspaceEntry entry = Entry(
            "external",
            1,
            (2100, 120, 3100, 920),
            Layout(WindowLayoutKind.LeftHalf, 0, 0, .5, 1),
            showCmd: 3,
            dpi: 144);
        RestoreMonitor exact = CurrentMonitor(
            "external",
            1,
            bounds: (1920, 0, 3840, 1080),
            work: (1920, 0, 3840, 1040),
            dpi: 144);

        RestoreTargetPlacement placement = Plan(entry, [exact], exactMatch: true);

        Assert.Equal((2100, 120, 3100, 920),
            (placement.Left, placement.Top, placement.Right, placement.Bottom));
        Assert.Equal(3, placement.ShowCmd);
        Assert.Equal(RestorePlacementStrategy.ExactPixels, placement.Strategy);
        Assert.False(placement.WasClamped);
    }

    [Fact]
    public void Missing_monitor_maps_semantic_half_to_primary_work_area()
    {
        WorkspaceEntry entry = Entry(
            "disconnected",
            1,
            (3840, 0, 5760, 2160),
            Layout(WindowLayoutKind.RightHalf, .5, 0, .5, 1),
            showCmd: 3,
            dpi: 144);
        RestoreMonitor laptop = CurrentMonitor(
            "laptop",
            0,
            bounds: (0, 0, 1536, 864),
            work: (0, 0, 1536, 824),
            dpi: 120,
            primary: true);

        RestorePlan plan = BuildPlan(entry, [laptop], exactMatch: false);
        RestoreTargetPlacement placement = Assert.Single(plan.Entries).TargetPlacement;

        Assert.Equal(RestoreMonitorMappingKind.PrimaryFallback, placement.MonitorMapping);
        Assert.Equal(RestorePlacementStrategy.Semantic, placement.Strategy);
        Assert.Equal(WindowLayoutKind.RightHalf, placement.SemanticKind);
        Assert.Equal((768, 0, 1536, 824),
            (placement.Left, placement.Top, placement.Right, placement.Bottom));
        Assert.Equal(3, placement.ShowCmd);
        RestorePlanPreviewEntry preview = Assert.Single(RestorePlanPreviewBuilder.Build(plan).Entries);
        Assert.Equal(RestorePreviewOutcomeKind.Adapted, preview.Outcome);
        Assert.Contains("RightHalf layout", preview.TargetLabel);
    }

    [Fact]
    public void Work_area_and_dpi_change_uses_normalized_custom_geometry()
    {
        WorkspaceEntry entry = Entry(
            "display",
            0,
            (200, 100, 1000, 700),
            Layout(WindowLayoutKind.Custom, .1, .2, .6, .5),
            dpi: 96);
        RestoreMonitor resized = CurrentMonitor(
            "display",
            0,
            bounds: (-2560, 0, 0, 1440),
            work: (-2560, 40, 0, 1400),
            dpi: 192,
            primary: true);

        RestoreTargetPlacement placement = Plan(entry, [resized], exactMatch: false);

        Assert.Equal(RestorePlacementStrategy.Normalized, placement.Strategy);
        Assert.Equal((-2304, 312, -768, 992),
            (placement.Left, placement.Top, placement.Right, placement.Bottom));
        Assert.True(placement.WasDpiScaled);
        Assert.False(placement.WasClamped);
    }

    [Fact]
    public void Legacy_missing_monitor_rectangle_is_clamped_fully_onscreen()
    {
        WorkspaceEntry entry = Entry(
            "gone",
            2,
            (4200, 900, 5600, 1900),
            layout: null,
            dpi: 96);
        RestoreMonitor laptop = CurrentMonitor(
            "laptop",
            0,
            bounds: (0, 0, 1366, 768),
            work: (0, 0, 1366, 728),
            dpi: 96,
            primary: true);

        RestoreTargetPlacement placement = Plan(entry, [laptop], exactMatch: false);

        Assert.Equal(RestorePlacementStrategy.LegacyDpiScaledAndClamped, placement.Strategy);
        Assert.True(placement.WasClamped);
        Assert.True(placement.Left >= 0 && placement.Top >= 0);
        Assert.True(placement.Right <= 1366 && placement.Bottom <= 728);
        Assert.True(placement.Right > placement.Left && placement.Bottom > placement.Top);
    }

    [Fact]
    public void Exact_topology_requires_bounds_work_area_dpi_and_duplicate_id_order_to_match()
    {
        MonitorInfo first = SavedMonitor(
            "duplicate-edid",
            0,
            (-1920, 0, 0, 1080),
            (-1920, 0, 0, 1040),
            96,
            primary: false);
        MonitorInfo second = SavedMonitor(
            "duplicate-edid",
            1,
            (0, 0, 1920, 1080),
            (0, 0, 1920, 1040),
            120,
            primary: true);
        MonitorInfo changedTaskbar = SavedMonitor(
            "duplicate-edid",
            1,
            (0, 0, 1920, 1080),
            (0, 0, 1920, 1000),
            120,
            primary: true);

        Assert.True(RestoreObservationBuilder.MonitorTopologiesMatchExactly(
            [first, second],
            [Clone(first), Clone(second)]));
        Assert.False(RestoreObservationBuilder.MonitorTopologiesMatchExactly(
            [first, second],
            [Clone(first), changedTaskbar]));
    }

    private static RestoreTargetPlacement Plan(
        WorkspaceEntry entry,
        IReadOnlyList<RestoreMonitor> current,
        bool exactMatch)
        => Assert.Single(BuildPlan(entry, current, exactMatch).Entries).TargetPlacement;

    private static RestorePlan BuildPlan(
        WorkspaceEntry entry,
        IReadOnlyList<RestoreMonitor> current,
        bool exactMatch)
    {
        var snapshot = new WorkspaceSnapshot
        {
            WorkspaceId = "workspace-layout-test",
            Name = "Layout test",
            Entries = [entry]
        };
        return RestorePlanner.Build(
            snapshot,
            new RestoreLiveInventory
            {
                Resources =
                [
                    new RestoreResourceObservation(
                        0,
                        RestoreResourceKind.Executable,
                        RestoreResourceAvailability.Available)
                ]
            },
            new RestoreMonitorTopology { Monitors = current, IsExactMatch = exactMatch },
            RestoreMode.Standard);
    }

    private static WorkspaceEntry Entry(
        string monitorId,
        int monitorIndex,
        (int Left, int Top, int Right, int Bottom) rectangle,
        NormalizedWindowLayout? layout,
        int showCmd = 1,
        uint dpi = 96) => new()
    {
        ExecutablePath = @"C:\Apps\editor.exe",
        ProcessName = "editor",
        WindowClassName = "EditorWindow",
        MonitorId = monitorId,
        MonitorIndex = monitorIndex,
        Position = new WindowRecord
        {
            ExecutablePath = @"C:\Apps\editor.exe",
            ProcessName = "editor",
            ClassName = "EditorWindow",
            MonitorId = monitorId,
            MonitorIndex = monitorIndex,
            NormalLeft = rectangle.Left,
            NormalTop = rectangle.Top,
            NormalRight = rectangle.Right,
            NormalBottom = rectangle.Bottom,
            NormalizedLayout = layout,
            ShowCmd = showCmd,
            SavedDpi = dpi
        }
    };

    private static NormalizedWindowLayout Layout(
        WindowLayoutKind kind,
        double x,
        double y,
        double width,
        double height) => new()
    {
        Kind = kind,
        X = x,
        Y = y,
        Width = width,
        Height = height
    };

    private static MonitorInfo SavedMonitor(
        string id,
        int index,
        (int Left, int Top, int Right, int Bottom) bounds,
        (int Left, int Top, int Right, int Bottom) work,
        uint dpi,
        bool primary) => new()
    {
        MonitorId = id,
        Index = index,
        WidthPixels = bounds.Right - bounds.Left,
        HeightPixels = bounds.Bottom - bounds.Top,
        BoundsLeft = bounds.Left,
        BoundsTop = bounds.Top,
        BoundsRight = bounds.Right,
        BoundsBottom = bounds.Bottom,
        WorkAreaLeft = work.Left,
        WorkAreaTop = work.Top,
        WorkAreaRight = work.Right,
        WorkAreaBottom = work.Bottom,
        Dpi = dpi,
        IsPrimary = primary
    };

    private static RestoreMonitor CurrentMonitor(
        string id,
        int index,
        (int Left, int Top, int Right, int Bottom) bounds,
        (int Left, int Top, int Right, int Bottom) work,
        uint dpi,
        bool primary = false) => new(
            id,
            index,
            bounds.Left,
            bounds.Top,
            bounds.Right,
            bounds.Bottom,
            dpi,
            primary,
            work.Left,
            work.Top,
            work.Right,
            work.Bottom);

    private static MonitorInfo Clone(MonitorInfo monitor) => SavedMonitor(
        monitor.MonitorId,
        monitor.Index,
        (monitor.BoundsLeft, monitor.BoundsTop, monitor.BoundsRight, monitor.BoundsBottom),
        (monitor.WorkAreaLeft, monitor.WorkAreaTop, monitor.WorkAreaRight, monitor.WorkAreaBottom),
        monitor.Dpi,
        monitor.IsPrimary);
}
