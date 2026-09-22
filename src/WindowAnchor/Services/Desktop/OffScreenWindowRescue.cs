using System;
using System.Collections.Generic;
using System.Linq;
using WindowAnchor.Models;
using WindowAnchor.Native;

namespace WindowAnchor.Services;

/// <summary>Configures when the manual foreground-window rescue command intervenes.</summary>
public sealed record OffScreenWindowRescuePolicy
{
    /// <summary>Minimum fraction of the window's normal bounds that must remain in a work area.</summary>
    public double MinimumVisibleAreaRatio { get; init; } = 0.25;
}

/// <summary>Outcome of a single manual foreground-window rescue request.</summary>
public enum OffScreenWindowRescueStatus
{
    Rescued,
    AlreadyVisible,
    NoActiveWindow,
    PlacementUnavailable,
    ApplyFailed
}

/// <summary>Explained outcome of a manual foreground-window rescue request.</summary>
public sealed record OffScreenWindowRescueResult(
    OffScreenWindowRescueStatus Status,
    IntPtr WindowHandle,
    NativeMethodsWindow.Rect OriginalBounds,
    NativeMethodsWindow.Rect? RescuedBounds = null);

/// <summary>Pure geometry for deciding whether a window is reachable and where to place it.</summary>
internal static class OffScreenWindowRescueGeometry
{
    internal static bool TryGetRescueBounds(
        NativeMethodsWindow.Rect bounds,
        IEnumerable<MonitorInfo> monitors,
        OffScreenWindowRescuePolicy policy,
        out NativeMethodsWindow.Rect rescuedBounds)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.MinimumVisibleAreaRatio is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(policy));

        MonitorInfo[] targets = monitors
            .Where(monitor => monitor.HasValidWorkArea || monitor.HasValidBounds)
            .OrderBy(monitor => monitor.Index)
            .ThenBy(monitor => monitor.MonitorId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!IsValid(bounds) || targets.Length == 0 || IsSufficientlyVisible(bounds, targets, policy))
        {
            rescuedBounds = bounds;
            return false;
        }

        MonitorInfo nearest = targets
            .OrderBy(monitor => SquaredDistance(bounds, WorkArea(monitor)))
            .ThenByDescending(monitor => monitor.IsPrimary)
            .ThenBy(monitor => monitor.Index)
            .First();
        rescuedBounds = ClampToWorkArea(bounds, WorkArea(nearest));
        return true;
    }

    private static bool IsSufficientlyVisible(
        NativeMethodsWindow.Rect bounds,
        IEnumerable<MonitorInfo> monitors,
        OffScreenWindowRescuePolicy policy)
    {
        long area = (long)(bounds.Right - bounds.Left) * (bounds.Bottom - bounds.Top);
        long visibleArea = monitors.Sum(monitor => IntersectionArea(bounds, WorkArea(monitor)));
        return visibleArea >= area * policy.MinimumVisibleAreaRatio;
    }

    private static NativeMethodsWindow.Rect ClampToWorkArea(
        NativeMethodsWindow.Rect bounds,
        NativeMethodsWindow.Rect workArea)
    {
        int width = Math.Min(bounds.Right - bounds.Left, workArea.Right - workArea.Left);
        int height = Math.Min(bounds.Bottom - bounds.Top, workArea.Bottom - workArea.Top);
        int left = Math.Clamp(bounds.Left, workArea.Left, workArea.Right - width);
        int top = Math.Clamp(bounds.Top, workArea.Top, workArea.Bottom - height);
        return new NativeMethodsWindow.Rect
        {
            Left = left,
            Top = top,
            Right = left + width,
            Bottom = top + height
        };
    }

    private static NativeMethodsWindow.Rect WorkArea(MonitorInfo monitor) => monitor.HasValidWorkArea
        ? new NativeMethodsWindow.Rect
        {
            Left = monitor.WorkAreaLeft, Top = monitor.WorkAreaTop,
            Right = monitor.WorkAreaRight, Bottom = monitor.WorkAreaBottom
        }
        : new NativeMethodsWindow.Rect
        {
            Left = monitor.BoundsLeft, Top = monitor.BoundsTop,
            Right = monitor.BoundsRight, Bottom = monitor.BoundsBottom
        };

    private static bool IsValid(NativeMethodsWindow.Rect rect) =>
        rect.Right > rect.Left && rect.Bottom > rect.Top;

    private static long IntersectionArea(NativeMethodsWindow.Rect first, NativeMethodsWindow.Rect second)
    {
        int width = Math.Max(0, Math.Min(first.Right, second.Right) - Math.Max(first.Left, second.Left));
        int height = Math.Max(0, Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Top, second.Top));
        return (long)width * height;
    }

    private static long SquaredDistance(NativeMethodsWindow.Rect first, NativeMethodsWindow.Rect second)
    {
        long horizontal = first.Right < second.Left ? second.Left - first.Right :
            second.Right < first.Left ? first.Left - second.Right : 0;
        long vertical = first.Bottom < second.Top ? second.Top - first.Bottom :
            second.Bottom < first.Top ? first.Top - second.Bottom : 0;
        return horizontal * horizontal + vertical * vertical;
    }
}
