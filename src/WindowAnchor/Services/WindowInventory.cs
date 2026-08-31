using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using WindowAnchor.Native;

namespace WindowAnchor.Services;

/// <summary>Policy-free bounds observed for a native top-level window.</summary>
public readonly record struct WindowBounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>
/// Raw facts observed for one native top-level window. Product decisions such as whether the
/// window is saved, matched, closed, counted as a switch risk, or minimized are intentionally
/// absent from this model.
/// </summary>
public sealed record ObservedWindow(
    IntPtr Hwnd,
    uint ProcessId,
    IntPtr OwnerHwnd,
    bool IsVisible,
    string ClassName,
    string Title,
    WindowBounds? Bounds,
    string ExecutablePath,
    string ProcessName,
    string AppUserModelId);

/// <summary>Named product policy applied to raw native window observations.</summary>
public enum WindowCandidatePolicy
{
    CaptureCandidate,
    RestoreMatchCandidate,
    SwitchCloseCandidate,
    SwitchRiskCandidate,
    MinimizeCandidate
}

/// <summary>Policy-free source of native top-level window observations.</summary>
public interface IRawWindowInventory
{
    IReadOnlyList<ObservedWindow> EnumerateWindows();
    bool IsWindowAlive(IntPtr hWnd);
}

/// <summary>Native implementation of the raw top-level window inventory.</summary>
public sealed class WindowInventory : IRawWindowInventory
{
    public IReadOnlyList<ObservedWindow> EnumerateWindows()
    {
        var windows = new List<ObservedWindow>();

        NativeMethodsWindow.EnumWindows((hWnd, _) =>
        {
            windows.Add(ObserveWindow(hWnd));
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    public bool IsWindowAlive(IntPtr hWnd) => NativeMethodsWindow.IsWindow(hWnd);

    private static ObservedWindow ObserveWindow(IntPtr hWnd)
    {
        bool isVisible = NativeMethodsWindow.IsWindowVisible(hWnd);
        IntPtr ownerHwnd = NativeMethodsWindow.GetWindow(hWnd, NativeMethodsWindow.GW_OWNER);

        var className = new StringBuilder(256);
        NativeMethodsWindow.GetClassName(hWnd, className, className.Capacity);

        var title = new StringBuilder(256);
        NativeMethodsWindow.GetWindowText(hWnd, title, title.Capacity);

        WindowBounds? bounds = null;
        if (NativeMethodsWindow.GetWindowRect(hWnd, out var rect))
            bounds = new WindowBounds(rect.Left, rect.Top, rect.Right, rect.Bottom);

        NativeMethodsWindow.GetWindowThreadProcessId(hWnd, out uint processId);
        string executablePath = "";
        string processName = "";
        try
        {
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
            executablePath = process.MainModule?.FileName ?? "";
        }
        catch
        {
            // Elevated or short-lived processes may not be queryable. The remaining raw facts
            // are still useful to switch-risk policy and should not be discarded.
        }

        string appUserModelId = WebAppService.GetWindowAppUserModelId(hWnd);
        if (string.IsNullOrEmpty(appUserModelId) &&
            executablePath.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
        {
            appUserModelId = WebAppService.GetProcessAppUserModelId(processId);
        }

        return new ObservedWindow(
            hWnd,
            processId,
            ownerHwnd,
            isVisible,
            className.ToString(),
            title.ToString(),
            bounds,
            executablePath,
            processName,
            appUserModelId);
    }
}

/// <summary>Pure, testable named policies for selecting raw observed windows.</summary>
public static class WindowPolicyEvaluator
{
    private static readonly HashSet<string> ShellWindowClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "DV2ControlHost", "MsgrIMEWindowClass",
        "SysShadow", "Button", "Windows.UI.Core.CoreWindow",
        "Progman", "WorkerW",
        "NotifyIconOverflowWindow", "TrayClockWClass", "MSTaskListWClass",
        "MSTaskSwWClass", "ReBarWindow32", "TopLevelWindowForOverflowXamlIsland"
    };

    public static bool Includes(
        ObservedWindow window,
        WindowCandidatePolicy policy,
        uint ownProcessId = 0)
    {
        bool isOwnWindow = ownProcessId != 0 && window.ProcessId == ownProcessId;
        bool isShellChrome = ShellWindowClasses.Contains(window.ClassName);

        return policy switch
        {
            WindowCandidatePolicy.CaptureCandidate =>
                IsLegacyLayoutCandidate(window, isShellChrome),

            WindowCandidatePolicy.RestoreMatchCandidate =>
                IsLegacyLayoutCandidate(window, isShellChrome),

            WindowCandidatePolicy.SwitchCloseCandidate =>
                !isOwnWindow && IsLegacyLayoutCandidate(window, isShellChrome),

            // Safe-switch preflight is deliberately conservative: owned dialogs, untitled
            // transients, and small utility windows remain visible to risk detection. Shell
            // chrome and WindowAnchor's own UI are the only product exclusions here.
            WindowCandidatePolicy.SwitchRiskCandidate =>
                window.IsVisible && !isOwnWindow && !isShellChrome,

            WindowCandidatePolicy.MinimizeCandidate =>
                !isOwnWindow && IsLegacyLayoutCandidate(window, isShellChrome),

            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null)
        };
    }

    private static bool IsLegacyLayoutCandidate(ObservedWindow window, bool isShellChrome)
    {
        if (!window.IsVisible || window.OwnerHwnd != IntPtr.Zero || isShellChrome)
            return false;
        if (string.IsNullOrWhiteSpace(window.Title))
            return false;
        if (window.Bounds is { } bounds && (bounds.Width < 100 || bounds.Height < 100))
            return false;
        return true;
    }
}
