using System;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowAnchor.Native;

/// <summary>
/// P/Invoke declarations for window enumeration, geometry, and monitor queries.
/// All structs use explicit sequential layout to match the Win32 ABI exactly.
/// </summary>
public static class NativeMethodsWindow
{
    [StructLayout(LayoutKind.Sequential)]
    public struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public Point PtMinPosition;
        public Point PtMaxPosition;
        public Rect RcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowPlacement(IntPtr hWnd, ref WindowPlacement lpwndpl);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPlacement(IntPtr hWnd, [In] ref WindowPlacement lpwndpl);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    // SW_SHOWNORMAL = 1, SW_SHOWMINIMIZED = 2, SW_SHOWMAXIMIZED = 3, SW_RESTORE = 9

    // ── Window close ──────────────────────────────────────────────────────
    public const uint WM_CLOSE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    public const uint GW_OWNER = 4;

    // ── DPI ───────────────────────────────────────────────────────────────────

    /// <summary>Returns the DPI for a window (e.g. 96, 120, 144, 192).</summary>
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>Returns the DPI for a monitor. dpiType 0 = MDT_EFFECTIVE_DPI.</summary>
    [DllImport("Shcore.dll")]
    public static extern int GetDpiForMonitor(
        IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>Returns the monitor nearest to the window. MONITOR_DEFAULTTONEAREST = 2.</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    // ── Monitor geometry (EnumDisplayMonitors / GetMonitorInfo) ────────────────

    /// <summary>
    /// Extended monitor info struct. Must set cbSize before calling GetMonitorInfo.
    /// dwFlags bit 0 = MONITORINFOF_PRIMARY.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct MonitorInfoEx
    {
        public int    cbSize;
        public Rect   rcMonitor;   // Full monitor bounds in virtual desktop coords
        public Rect   rcWork;      // Work area (excludes taskbar)
        public uint   dwFlags;     // MONITORINFOF_PRIMARY = 1
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;    // GDI device name, e.g. "\\\\.\\DISPLAY1"
    }

    public const uint MONITORINFOF_PRIMARY = 1;

    public delegate bool MonitorEnumProc(
        IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);
}
