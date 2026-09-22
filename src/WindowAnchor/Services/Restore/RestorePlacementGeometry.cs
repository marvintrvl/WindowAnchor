using System;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Pure normalized-layout and work-area geometry used by restore planning.</summary>
internal static class RestorePlacementGeometry
{
    internal readonly record struct Rectangle(int Left, int Top, int Right, int Bottom);

    internal static Rectangle AdaptNormalized(
        NormalizedWindowLayout layout,
        RestoreMonitor target)
    {
        (double x, double y, double width, double height) = layout.Kind switch
        {
            WindowLayoutKind.Full => (0, 0, 1, 1),
            WindowLayoutKind.LeftHalf => (0, 0, .5, 1),
            WindowLayoutKind.RightHalf => (.5, 0, .5, 1),
            WindowLayoutKind.TopHalf => (0, 0, 1, .5),
            WindowLayoutKind.BottomHalf => (0, .5, 1, .5),
            WindowLayoutKind.LeftThird => (0, 0, 1d / 3d, 1),
            WindowLayoutKind.CenterThird => (1d / 3d, 0, 1d / 3d, 1),
            WindowLayoutKind.RightThird => (2d / 3d, 0, 1d / 3d, 1),
            WindowLayoutKind.Centered => (
                .5 - layout.Width / 2,
                .5 - layout.Height / 2,
                layout.Width,
                layout.Height),
            _ => (layout.X, layout.Y, layout.Width, layout.Height)
        };

        int workWidth = Math.Max(1, target.WorkAreaWidth);
        int workHeight = Math.Max(1, target.WorkAreaHeight);
        int left = target.EffectiveWorkAreaLeft + Scale(workWidth, x);
        int top = target.EffectiveWorkAreaTop + Scale(workHeight, y);
        int windowWidth = Math.Max(1, Scale(workWidth, width));
        int windowHeight = Math.Max(1, Scale(workHeight, height));
        return new Rectangle(left, top, left + windowWidth, top + windowHeight);
    }

    internal static Rectangle ClampToWorkArea(
        Rectangle rectangle,
        RestoreMonitor target,
        out bool wasClamped)
    {
        int workLeft = target.EffectiveWorkAreaLeft;
        int workTop = target.EffectiveWorkAreaTop;
        int workRight = target.EffectiveWorkAreaRight;
        int workBottom = target.EffectiveWorkAreaBottom;
        int workWidth = Math.Max(1, workRight - workLeft);
        int workHeight = Math.Max(1, workBottom - workTop);

        int width = rectangle.Right - rectangle.Left;
        int height = rectangle.Bottom - rectangle.Top;
        if (width <= 0) width = Math.Min(800, workWidth);
        if (height <= 0) height = Math.Min(600, workHeight);
        width = Math.Clamp(width, 1, workWidth);
        height = Math.Clamp(height, 1, workHeight);
        int left = Math.Clamp(rectangle.Left, workLeft, workRight - width);
        int top = Math.Clamp(rectangle.Top, workTop, workBottom - height);
        var clamped = new Rectangle(left, top, left + width, top + height);
        wasClamped = clamped != rectangle;
        return clamped;
    }

    internal static int Scale(int value, double scale) =>
        (int)Math.Round(value * scale, MidpointRounding.AwayFromZero);

    internal static RestoreTargetPlacement Build(
        WorkspaceEntry entry,
        IReadOnlyList<MonitorInfo> savedMonitors,
        IReadOnlyList<RestoreMonitor> monitors,
        bool topologyIsExact,
        ICollection<RestorePlanIssue> warnings)
    {
        WindowRecord position = entry.Position ?? new WindowRecord();
        string savedMonitorId = FirstNonEmpty(entry.MonitorId, position.MonitorId);
        int savedMonitorIndex = !string.IsNullOrWhiteSpace(entry.MonitorId)
            ? entry.MonitorIndex
            : position.MonitorIndex;
        RestoreMonitor? target = monitors.FirstOrDefault(monitor =>
            savedMonitorId.Length > 0 &&
            string.Equals(monitor.MonitorId, savedMonitorId, StringComparison.OrdinalIgnoreCase));
        RestoreMonitorMappingKind mapping = RestoreMonitorMappingKind.ExactStableId;

        if (target is null)
        {
            target = monitors.FirstOrDefault(monitor => monitor.MonitorIndex == savedMonitorIndex);
            mapping = target is null
                ? RestoreMonitorMappingKind.PrimaryFallback
                : RestoreMonitorMappingKind.SavedIndexFallback;
        }
        if (target is null)
            target = monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? monitors.FirstOrDefault();
        if (target is null)
            mapping = RestoreMonitorMappingKind.Unavailable;

        if (monitors.Count == 0)
        {
            warnings.Add(Warning(
                RestorePlanIssueCode.MonitorTopologyUnavailable,
                "No current monitor topology was supplied; the saved DPI and coordinates are retained."));
        }
        else if (savedMonitorId.Length > 0 && mapping != RestoreMonitorMappingKind.ExactStableId)
        {
            warnings.Add(Warning(
                RestorePlanIssueCode.SavedMonitorUnavailable,
                "The saved monitor is unavailable; a deterministic topology fallback was selected."));
        }

        uint savedDpi = position.SavedDpi > 0 ? position.SavedDpi : 96;
        uint targetDpi = target is { Dpi: > 0 } ? target.Dpi : savedDpi;
        bool dpiChanged = savedDpi != targetDpi;
        var rectangle = new RestorePlacementGeometry.Rectangle(
            position.NormalLeft,
            position.NormalTop,
            position.NormalRight,
            position.NormalBottom);
        RestorePlacementStrategy strategy = RestorePlacementStrategy.Unavailable;
        WindowLayoutKind semanticKind = position.NormalizedLayout?.Kind ?? WindowLayoutKind.Custom;
        bool wasClamped = false;

        bool useExactPixels = target is not null &&
            topologyIsExact &&
            mapping == RestoreMonitorMappingKind.ExactStableId;
        if (useExactPixels)
        {
            strategy = RestorePlacementStrategy.ExactPixels;
        }
        else if (target is not null)
        {
            MonitorInfo? sourceMonitor = savedMonitors.FirstOrDefault(monitor =>
                savedMonitorId.Length > 0 &&
                string.Equals(monitor.MonitorId, savedMonitorId, StringComparison.OrdinalIgnoreCase))
                ?? savedMonitors.FirstOrDefault(monitor => monitor.Index == savedMonitorIndex);
            NormalizedWindowLayout? layout = WindowLayoutGeometry.IsValid(position.NormalizedLayout)
                ? position.NormalizedLayout
                : sourceMonitor is not null
                    ? WindowLayoutGeometry.Capture(position, sourceMonitor)
                    : null;

            if (WindowLayoutGeometry.IsValid(layout))
            {
                rectangle = RestorePlacementGeometry.AdaptNormalized(layout!, target);
                semanticKind = layout!.Kind;
                strategy = layout!.Kind == WindowLayoutKind.Custom
                    ? RestorePlacementStrategy.Normalized
                    : RestorePlacementStrategy.Semantic;
            }
            else
            {
                double scale = (double)targetDpi / savedDpi;
                rectangle = new RestorePlacementGeometry.Rectangle(
                    RestorePlacementGeometry.Scale(position.NormalLeft, scale),
                    RestorePlacementGeometry.Scale(position.NormalTop, scale),
                    RestorePlacementGeometry.Scale(position.NormalRight, scale),
                    RestorePlacementGeometry.Scale(position.NormalBottom, scale));
                strategy = RestorePlacementStrategy.LegacyDpiScaledAndClamped;
            }

            rectangle = RestorePlacementGeometry.ClampToWorkArea(rectangle, target, out wasClamped);
            if (wasClamped)
            {
                warnings.Add(Warning(
                    RestorePlanIssueCode.PlacementClamped,
                    "The adapted placement was clamped to the visible monitor work area."));
            }
        }

        int left = rectangle.Left;
        int top = rectangle.Top;
        int right = rectangle.Right;
        int bottom = rectangle.Bottom;
        if (right <= left || bottom <= top)
        {
            warnings.Add(Warning(
                RestorePlanIssueCode.InvalidSavedPlacement,
                "The saved placement has non-positive width or height."));
        }

        return new RestoreTargetPlacement(
            target?.MonitorId ?? savedMonitorId,
            target?.MonitorIndex ?? savedMonitorIndex,
            mapping,
            left,
            top,
            right,
            bottom,
            position.ShowCmd,
            savedDpi,
            targetDpi,
            dpiChanged,
            strategy,
            semanticKind,
            wasClamped);
    }
    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

    private static RestorePlanIssue Warning(RestorePlanIssueCode code, string explanation) =>
        new(code, RestorePlanIssueSeverity.Warning, explanation);
}
