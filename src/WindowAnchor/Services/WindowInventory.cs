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
    string AppUserModelId,
    long ExtendedStyle = 0,
    bool IsCloaked = false,
    IntPtr RootOwnerHwnd = default,
    IntPtr TaskSwitcherRepresentativeHwnd = default);

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
    private static readonly IPackagedAppResolver PackagedApps = new PackagedAppResolver();

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
        long extendedStyle = NativeMethodsWindow
            .GetWindowLongPtr(hWnd, NativeMethodsWindow.GWL_EXSTYLE)
            .ToInt64();
        IntPtr rootOwnerHwnd = NativeMethodsWindow.GetAncestor(
            hWnd,
            NativeMethodsWindow.GA_ROOTOWNER);
        if (rootOwnerHwnd == IntPtr.Zero)
            rootOwnerHwnd = hWnd;
        IntPtr taskSwitcherRepresentativeHwnd = FindTaskSwitcherRepresentative(rootOwnerHwnd);
        bool isCloaked = NativeMethodsWindow.DwmGetWindowAttribute(
                hWnd,
                NativeMethodsWindow.DWMWA_CLOAKED,
                out uint cloakState,
                sizeof(uint)) == 0 &&
            cloakState != 0;

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
            if (string.IsNullOrEmpty(appUserModelId))
            {
                appUserModelId = PackagedApps.Resolve(executablePath)?.AppUserModelId ?? "";
            }
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
            appUserModelId,
            extendedStyle,
            isCloaked,
            rootOwnerHwnd,
            taskSwitcherRepresentativeHwnd);
    }

    private static IntPtr FindTaskSwitcherRepresentative(IntPtr rootOwnerHwnd)
    {
        IntPtr current = rootOwnerHwnd;
        while (current != IntPtr.Zero)
        {
            IntPtr popup = NativeMethodsWindow.GetLastActivePopup(current);
            if (popup == IntPtr.Zero || popup == current)
                break;
            if (NativeMethodsWindow.IsWindowVisible(popup))
                return popup;
            current = popup;
        }

        return current;
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
        bool isPrimaryTaskWindow = IsPrimaryTaskWindow(window, isShellChrome);
        bool isInteractiveRisk = IsInteractiveRisk(window, isShellChrome);

        return policy switch
        {
            WindowCandidatePolicy.CaptureCandidate =>
                isPrimaryTaskWindow && IsLayoutCandidate(window),

            WindowCandidatePolicy.RestoreMatchCandidate =>
                isPrimaryTaskWindow && IsLayoutCandidate(window),

            WindowCandidatePolicy.SwitchCloseCandidate =>
                !isOwnWindow &&
                isPrimaryTaskWindow &&
                IsLayoutCandidate(window),

            // Safe-switch preflight is deliberately conservative: owned dialogs, untitled
            // transients, and small utility windows remain visible to risk detection. Product UI,
            // shell chrome, cloaked windows, and non-activatable background surfaces are excluded.
            WindowCandidatePolicy.SwitchRiskCandidate =>
                !isOwnWindow &&
                isInteractiveRisk,

            WindowCandidatePolicy.MinimizeCandidate =>
                !isOwnWindow &&
                isPrimaryTaskWindow &&
                IsLayoutCandidate(window),

            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null)
        };
    }

    /// <summary>
    /// Selects independently manageable task windows using native ownership, taskbar styles,
    /// activation behavior, and DWM visibility. Application and class names never participate.
    /// </summary>
    internal static bool IsPrimaryTaskWindow(ObservedWindow window, bool isShellChrome = false)
    {
        if (!window.IsVisible || window.IsCloaked || isShellChrome)
            return false;

        bool isAppWindow = HasExtendedStyle(window, NativeMethodsWindow.WS_EX_APPWINDOW);
        if (HasExtendedStyle(window, NativeMethodsWindow.WS_EX_TOOLWINDOW))
            return false;
        if (HasExtendedStyle(window, NativeMethodsWindow.WS_EX_NOACTIVATE) && !isAppWindow)
            return false;

        // WS_EX_APPWINDOW explicitly opts a visible top-level window into taskbar behavior even
        // when it has an owner. Otherwise, only independently owned root windows are restorable.
        if (!isAppWindow && window.OwnerHwnd != IntPtr.Zero)
            return false;

        // The exact Alt+Tab representative algorithm is a Windows implementation detail. Keep
        // its raw result available for diagnostics, but persist the independent root task so a
        // temporary modal popup cannot make the owning application disappear from a snapshot.
        return true;
    }

    /// <summary>
    /// Broader risk policy: owned dialogs remain visible to safe-switch checks, while windows the
    /// OS marks as cloaked, tool-only, or non-activatable are not treated as user work to close.
    /// </summary>
    private static bool IsInteractiveRisk(ObservedWindow window, bool isShellChrome)
    {
        if (!window.IsVisible || window.IsCloaked || isShellChrome)
            return false;

        bool isAppWindow = HasExtendedStyle(window, NativeMethodsWindow.WS_EX_APPWINDOW);
        return !HasExtendedStyle(window, NativeMethodsWindow.WS_EX_TOOLWINDOW) &&
               (!HasExtendedStyle(window, NativeMethodsWindow.WS_EX_NOACTIVATE) || isAppWindow);
    }

    private static bool IsLayoutCandidate(ObservedWindow window)
    {
        if (string.IsNullOrWhiteSpace(window.Title))
            return false;
        if (window.Bounds is { } bounds && (bounds.Width < 100 || bounds.Height < 100))
            return false;
        return true;
    }

    private static bool HasExtendedStyle(ObservedWindow window, long style) =>
        (window.ExtendedStyle & style) != 0;
}
