using System;
using System.Runtime.InteropServices;

namespace WindowAnchor.Native;

internal static class NativeMethodsExplorer
{
    internal const uint WM_COMMAND = 0x0111;
    internal const int ExplorerNewTabCommand = 0xA21B;
    internal const int ExplorerSelectTabCommand = 0xA221;
    internal const string ExplorerWindowClass = "CabinetWClass";
    internal const string ExplorerTabWindowClass = "ShellTabWindowClass";

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr FindWindowEx(
        IntPtr parentHandle,
        IntPtr childAfter,
        string className,
        string? windowTitle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(
        IntPtr hWnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessage(
        IntPtr hWnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hWnd, char[] className, int maxCount);
}
