using System;
using WindowAnchor.Native;

namespace WindowAnchor.Services;

/// <summary>Pure DPI and frame-compensation calculations used by native placement mutation.</summary>
internal static class WindowPlacementGeometry
{
    internal static NativeMethodsWindow.Rect Scale(NativeMethodsWindow.Rect saved, uint savedDpi, uint targetDpi)
    {
        if (savedDpi == targetDpi || savedDpi == 0) return saved;
        double scale = (double)targetDpi / savedDpi;
        return new NativeMethodsWindow.Rect
        {
            Left = (int)(saved.Left * scale), Top = (int)(saved.Top * scale),
            Right = (int)(saved.Right * scale), Bottom = (int)(saved.Bottom * scale)
        };
    }

    internal static NativeMethodsWindow.Rect CompensateVisibleFrame(
        NativeMethodsWindow.Rect desiredVisible,
        NativeMethodsWindow.Rect currentOuter,
        NativeMethodsWindow.Rect currentVisible)
    {
        if (currentOuter.Right <= currentOuter.Left || currentOuter.Bottom <= currentOuter.Top ||
            currentVisible.Right <= currentVisible.Left || currentVisible.Bottom <= currentVisible.Top)
            return desiredVisible;
        return new NativeMethodsWindow.Rect
        {
            Left = desiredVisible.Left - Math.Max(0, currentVisible.Left - currentOuter.Left),
            Top = desiredVisible.Top - Math.Max(0, currentVisible.Top - currentOuter.Top),
            Right = desiredVisible.Right + Math.Max(0, currentOuter.Right - currentVisible.Right),
            Bottom = desiredVisible.Bottom + Math.Max(0, currentOuter.Bottom - currentVisible.Bottom)
        };
    }
}
