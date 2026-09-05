using System;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Pure capture-time derivation of monitor-relative and semantic window geometry.</summary>
public static class WindowLayoutGeometry
{
    private const double DetectionTolerance = 0.035;

    public static NormalizedWindowLayout? Capture(WindowRecord window, MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(monitor);

        (int left, int top, int right, int bottom) = monitor.HasValidWorkArea
            ? (monitor.WorkAreaLeft, monitor.WorkAreaTop, monitor.WorkAreaRight, monitor.WorkAreaBottom)
            : (monitor.BoundsLeft, monitor.BoundsTop, monitor.BoundsRight, monitor.BoundsBottom);
        int workWidth = right - left;
        int workHeight = bottom - top;
        int windowWidth = window.NormalRight - window.NormalLeft;
        int windowHeight = window.NormalBottom - window.NormalTop;
        if (workWidth <= 0 || workHeight <= 0 || windowWidth <= 0 || windowHeight <= 0)
            return null;

        double x = Round((double)(window.NormalLeft - left) / workWidth);
        double y = Round((double)(window.NormalTop - top) / workHeight);
        double width = Round((double)windowWidth / workWidth);
        double height = Round((double)windowHeight / workHeight);

        return new NormalizedWindowLayout
        {
            X = x,
            Y = y,
            Width = width,
            Height = height,
            HorizontalAnchor = DetectHorizontalAnchor(x, width),
            VerticalAnchor = DetectVerticalAnchor(y, height),
            Kind = DetectKind(x, y, width, height)
        };
    }

    public static bool IsValid(NormalizedWindowLayout? layout) =>
        layout is not null &&
        double.IsFinite(layout.X) &&
        double.IsFinite(layout.Y) &&
        double.IsFinite(layout.Width) &&
        double.IsFinite(layout.Height) &&
        layout.Width > 0 &&
        layout.Height > 0;

    private static WindowLayoutKind DetectKind(double x, double y, double width, double height)
    {
        if (Matches(x, y, width, height, 0, 0, 1, 1)) return WindowLayoutKind.Full;
        if (Matches(x, y, width, height, 0, 0, .5, 1)) return WindowLayoutKind.LeftHalf;
        if (Matches(x, y, width, height, .5, 0, .5, 1)) return WindowLayoutKind.RightHalf;
        if (Matches(x, y, width, height, 0, 0, 1, .5)) return WindowLayoutKind.TopHalf;
        if (Matches(x, y, width, height, 0, .5, 1, .5)) return WindowLayoutKind.BottomHalf;
        if (Matches(x, y, width, height, 0, 0, 1d / 3d, 1)) return WindowLayoutKind.LeftThird;
        if (Matches(x, y, width, height, 1d / 3d, 0, 1d / 3d, 1)) return WindowLayoutKind.CenterThird;
        if (Matches(x, y, width, height, 2d / 3d, 0, 1d / 3d, 1)) return WindowLayoutKind.RightThird;
        if (Near(x + width / 2, .5) && Near(y + height / 2, .5)) return WindowLayoutKind.Centered;
        return WindowLayoutKind.Custom;
    }

    private static HorizontalWindowAnchor DetectHorizontalAnchor(double x, double width)
    {
        if (Near(x, 0) && Near(x + width, 1)) return HorizontalWindowAnchor.Stretch;
        if (Near(x, 0)) return HorizontalWindowAnchor.Left;
        if (Near(x + width, 1)) return HorizontalWindowAnchor.Right;
        if (Near(x + width / 2, .5)) return HorizontalWindowAnchor.Center;
        return HorizontalWindowAnchor.Custom;
    }

    private static VerticalWindowAnchor DetectVerticalAnchor(double y, double height)
    {
        if (Near(y, 0) && Near(y + height, 1)) return VerticalWindowAnchor.Stretch;
        if (Near(y, 0)) return VerticalWindowAnchor.Top;
        if (Near(y + height, 1)) return VerticalWindowAnchor.Bottom;
        if (Near(y + height / 2, .5)) return VerticalWindowAnchor.Center;
        return VerticalWindowAnchor.Custom;
    }

    private static bool Matches(
        double x, double y, double width, double height,
        double expectedX, double expectedY, double expectedWidth, double expectedHeight) =>
        Near(x, expectedX) && Near(y, expectedY) &&
        Near(width, expectedWidth) && Near(height, expectedHeight);

    private static bool Near(double value, double expected) =>
        Math.Abs(value - expected) <= DetectionTolerance;

    private static double Round(double value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);
}
