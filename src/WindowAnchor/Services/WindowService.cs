using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using WindowAnchor.Models;
using WindowAnchor.Native;

namespace WindowAnchor.Services;

/// <summary>
/// Enumerates, captures, and restores top-level application windows via P/Invoke.
/// Uses <c>EnumWindows</c>, <c>GetWindowPlacement</c>, <c>QueryFullProcessImageName</c>,
/// and <c>SetWindowPlacement</c> for DPI-aware position handling.
/// </summary>
public class WindowService
{
    // Skip-list of well-known OS-chrome window classes that should never be saved.
    private static readonly string[] OsWindowClassSkipList = new[]
    {
        // Shell and DWM infrastructure
        "Shell_TrayWnd", "DV2ControlHost", "MsgrIMEWindowClass",
        "SysShadow", "Button", "Windows.UI.Core.CoreWindow",
        "Progman", "WorkerW",
        // Additional explorer.exe-hosted shell utility windows that are not
        // user content windows (system tray overflow, clock, task list, etc.)
        "NotifyIconOverflowWindow", "TrayClockWClass", "MSTaskListWClass",
        "MSTaskSwWClass", "ReBarWindow32", "TopLevelWindowForOverflowXamlIsland"
    };

    /// <summary>
    /// Snapshots all visible top-level user windows.
    /// When <paramref name="monitors"/> is supplied (from
    /// <see cref="MonitorService.GetCurrentMonitors"/>), each record is tagged with the
    /// monitor it belongs to via <see cref="WindowRecord.MonitorId"/> etc.
    /// </summary>
    public List<WindowRecord> SnapshotAllWindows(List<MonitorInfo>? monitors = null)
    {
        var records = new List<WindowRecord>();

        // Build Explorer folder map once before iterating — uses Shell.Application COM to
        // get the folder open in each File Explorer window, keyed by HWND.
        var explorerFolderMap = BuildExplorerFolderMap();

        NativeMethodsWindow.EnumWindows((hWnd, lParam) =>
        {
            if (ShouldIncludeWindow(hWnd))
            {
                var record = CaptureWindowRecord(hWnd, explorerFolderMap);
                if (record != null)
                {
                    // Tag with monitor while HWND is still valid
                    if (monitors != null)
                    {
                        var mon = MonitorService.GetMonitorForWindow(hWnd, monitors);
                        if (mon != null)
                        {
                            record.MonitorId    = mon.MonitorId;
                            record.MonitorIndex = mon.Index;
                            record.MonitorName  = mon.FriendlyName;
                        }
                    }
                    records.Add(record);
                }
            }
            return true;
        }, IntPtr.Zero);

        return records;
    }

    /// <summary>
    /// Uses the Shell.Application COM object (always available on Windows, no extra reference
    /// required) to enumerate all open File Explorer windows and return a map of
    /// HWND → folder path. Only windows where <c>win.Name == "File Explorer"</c> are included.
    /// Failures are silently swallowed so a COM error never breaks a snapshot.
    /// </summary>
    private static Dictionary<IntPtr, string> BuildExplorerFolderMap()
    {
        var map = new Dictionary<IntPtr, string>();
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return map;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic windows = shell.Windows();
            int count = (int)windows.Count;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    dynamic win = windows.Item(i);
                    if (win == null) continue;

                    // Filter to File Explorer windows only (not Internet Explorer)
                    string winName = (win.Name as string) ?? "";
                    if (!winName.Equals("File Explorer", StringComparison.OrdinalIgnoreCase) &&
                        !winName.Equals("Windows Explorer", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // LocationURL is a file:/// URI — convert to a local path
                    string locationUrl = (win.LocationURL as string) ?? "";
                    if (string.IsNullOrEmpty(locationUrl)) continue;

                    if (!Uri.TryCreate(locationUrl, UriKind.Absolute, out var uri)) continue;
                    string folderPath = Uri.UnescapeDataString(uri.LocalPath);

                    // HWND comes back as int from the COM automation layer
                    IntPtr hwnd = new IntPtr((int)win.HWND);
                    map[hwnd] = folderPath;
                }
                catch { /* Skip any individual window that fails */ }
            }
        }
        catch { /* COM unavailable — return empty map, caller degrades gracefully */ }
        return map;
    }

    private bool ShouldIncludeWindow(IntPtr hWnd)
    {
        if (!NativeMethodsWindow.IsWindowVisible(hWnd)) return false;

        // Skip windows that have an owner (child windows, dialogs owned by main windows)
        if (NativeMethodsWindow.GetWindow(hWnd, NativeMethodsWindow.GW_OWNER) != IntPtr.Zero) return false;

        // Skip OS/Shell windows per spec skip-list
        var className = new StringBuilder(256);
        NativeMethodsWindow.GetClassName(hWnd, className, className.Capacity);
        string cName = className.ToString();
        if (OsWindowClassSkipList.Contains(cName)) return false;

        // Skip windows with no title (often background windows)
        var title = new StringBuilder(256);
        NativeMethodsWindow.GetWindowText(hWnd, title, title.Capacity);
        if (string.IsNullOrWhiteSpace(title.ToString())) return false;

        // Skip windows that are too small to be user content windows.
        // explorer.exe hosts many tiny shell utility windows (tray overflow,
        // notification popups, etc.) that have titles and no owner but are not
        // meaningful windows to save/restore.
        if (NativeMethodsWindow.GetWindowRect(hWnd, out var sizeRect))
        {
            int w = sizeRect.Right - sizeRect.Left;
            int h = sizeRect.Bottom - sizeRect.Top;
            if (w < 100 || h < 100) return false;
        }

        return true;
    }

    private WindowRecord? CaptureWindowRecord(IntPtr hWnd, Dictionary<IntPtr, string>? explorerFolderMap = null)
    {
        var placement = new NativeMethodsWindow.WindowPlacement();
        placement.Length = Marshal.SizeOf(typeof(NativeMethodsWindow.WindowPlacement));

        if (!NativeMethodsWindow.GetWindowPlacement(hWnd, ref placement)) return null;

        // Windows 11 Snap Layouts fix: GetWindowPlacement.rcNormalPosition might be stale
        // because Snap uses SetWindowPos/DWM which don't update rcNormalPosition.
        // For normal (non-maximized/minimized) windows, compare with actual position.
        if (placement.ShowCmd == 1) // 1 = SW_SHOWNORMAL
        {
            NativeMethodsWindow.Rect actualRect;
            if (NativeMethodsWindow.GetWindowRect(hWnd, out actualRect))
            {
                // If actual position differs from rcNormalPosition, use actual
                // Lowered threshold to 5 pixels for better Snap detection
                int leftDiff = Math.Abs(actualRect.Left - placement.RcNormalPosition.Left);
                int topDiff = Math.Abs(actualRect.Top - placement.RcNormalPosition.Top);
                int rightDiff = Math.Abs(actualRect.Right - placement.RcNormalPosition.Right);
                int bottomDiff = Math.Abs(actualRect.Bottom - placement.RcNormalPosition.Bottom);

                // Threshold 15px: DWM frame shadows cause 7-14px misalignment
                // between GetWindowRect and rcNormalPosition on all windows.
                // Real Snap Layout diffs are 100-1000+ px, so 15px is safe.
                if (leftDiff > 15 || topDiff > 15 || rightDiff > 15 || bottomDiff > 15)
                {
                    AppLogger.Info($"Snap stale rcNormalPosition on hwnd {hWnd} — using GetWindowRect");
                    placement.RcNormalPosition = actualRect;
                }
            }
        }

        uint processId;
        NativeMethodsWindow.GetWindowThreadProcessId(hWnd, out processId);

        string exePath = "";
        string processName = "";
        try
        {
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
            exePath = process.MainModule?.FileName ?? "";
        }
        catch
        {
            // Fails on elevated processes if we are not admin
        }

        var className = new StringBuilder(256);
        NativeMethodsWindow.GetClassName(hWnd, className, className.Capacity);

        var title = new StringBuilder(256);
        NativeMethodsWindow.GetWindowText(hWnd, title, title.Capacity);
        string fullTitle = title.ToString();
        // Store up to 200 chars — enough for any realistic window title while still bounding
        // storage size. The old 40-char limit clipped " - Word" / " - Notepad" suffixes on
        // longer document names, causing Tier-1 pattern matches to fail and Tier-2 jump-list
        // lookups to run on windows where Tier 1 would have succeeded.
        string snippet = fullTitle.Length > 200 ? fullTitle.Substring(0, 200) : fullTitle;

        // For File Explorer windows, resolve the open folder via the pre-built COM map
        string folderPath = "";
        if (explorerFolderMap != null &&
            processName.Equals("explorer", StringComparison.OrdinalIgnoreCase) &&
            explorerFolderMap.TryGetValue(hWnd, out string? fp))
        {
            folderPath = fp ?? "";
        }

        return new WindowRecord
        {
            ExecutablePath = exePath,
            ProcessName = processName,
            ClassName = className.ToString(),
            TitleSnippet = snippet,
            ShowCmd = placement.ShowCmd,
            NormalLeft = placement.RcNormalPosition.Left,
            NormalTop = placement.RcNormalPosition.Top,
            NormalRight = placement.RcNormalPosition.Right,
            NormalBottom = placement.RcNormalPosition.Bottom,
            SavedDpi = NativeMethodsWindow.GetDpiForWindow(hWnd),
            FolderPath = folderPath,
        };
    }

    public void RestoreWindow(IntPtr hWnd, WindowRecord record)
    {
        var placement = new NativeMethodsWindow.WindowPlacement();
        placement.Length = Marshal.SizeOf(typeof(NativeMethodsWindow.WindowPlacement));

        // Get current placement to preserve flags
        if (!NativeMethodsWindow.GetWindowPlacement(hWnd, ref placement))
        {
            AppLogger.Warn($"GetWindowPlacement failed for hwnd {hWnd}");
            return;
        }

        // ── DPI scaling ───────────────────────────────────────────────────────
        // GetWindowPlacement coords are workspace coords which are DPI-relative.
        // If the DPI has changed since save (different monitor DPI, user rescaled),
        // scale the saved coordinates so the window lands at the correct size/position.
        uint currentDpi = NativeMethodsWindow.GetDpiForWindow(hWnd);
        uint savedDpi = record.SavedDpi > 0 ? record.SavedDpi : 96;

        var savedRect = new NativeMethodsWindow.Rect
        {
            Left   = record.NormalLeft,
            Top    = record.NormalTop,
            Right  = record.NormalRight,
            Bottom = record.NormalBottom
        };

        var targetRect = ScaleCoordsForDpi(savedRect, savedDpi, currentDpi);
        if (savedDpi != currentDpi)
        {
            AppLogger.Info($"DPI changed ({savedDpi} → {currentDpi}) — scaling coords");
        }

        placement.ShowCmd = record.ShowCmd;
        placement.RcNormalPosition.Left   = targetRect.Left;
        placement.RcNormalPosition.Top    = targetRect.Top;
        placement.RcNormalPosition.Right  = targetRect.Right;
        placement.RcNormalPosition.Bottom = targetRect.Bottom;

        // Spec: "Always set WINDOWPLACEMENT.length before EVERY P/Invoke call"
        placement.Length = Marshal.SizeOf(typeof(NativeMethodsWindow.WindowPlacement));

        bool success = NativeMethodsWindow.SetWindowPlacement(hWnd, ref placement);
        if (!success)
            AppLogger.Warn($"SetWindowPlacement failed for hwnd {hWnd}");

        // Spec: "If ShowCmd == 3 (maximized), also call ShowWindow(hwnd, 3)"
        if (record.ShowCmd == 3)
            NativeMethodsWindow.ShowWindow(hWnd, 3);
        // Spec: preserved ShowCmd == 2 (minimized) — no ShowWindow needed, SetWindowPlacement handles it
    }

    /// <summary>
    /// Scales saved window coordinates when the DPI has changed between save and restore.
    /// GetWindowPlacement workspace coordinates are DPI-relative, so a window saved at
    /// 96 DPI will be at the wrong position/size on a 144 DPI monitor without scaling.
    /// </summary>
    public static NativeMethodsWindow.Rect ScaleCoordsForDpi(
        NativeMethodsWindow.Rect saved, uint savedDpi, uint targetDpi)
    {
        if (savedDpi == targetDpi || savedDpi == 0) return saved;
        double scale = (double)targetDpi / savedDpi;
        return new NativeMethodsWindow.Rect
        {
            Left   = (int)(saved.Left   * scale),
            Top    = (int)(saved.Top    * scale),
            Right  = (int)(saved.Right  * scale),
            Bottom = (int)(saved.Bottom * scale)
        };
    }

    /// <summary>
    /// Returns a dictionary of all currently visible windows keyed by HWND,
    /// pairing each with its process ID and a captured <see cref="WindowRecord"/>.
    /// Used by <c>WorkspaceService</c> to match restored entries to live windows.
    /// </summary>
    public Dictionary<IntPtr, (uint Pid, WindowRecord Record)> GetAllWindowsWithPids()
    {
        var result = new Dictionary<IntPtr, (uint Pid, WindowRecord Record)>();

        NativeMethodsWindow.EnumWindows((hWnd, _) =>
        {
            if (!ShouldIncludeWindow(hWnd)) return true;

            var record = CaptureWindowRecord(hWnd);
            if (record == null) return true;

            NativeMethodsWindow.GetWindowThreadProcessId(hWnd, out uint pid);
            result[hWnd] = (pid, record);
            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>
    /// Public alias for <see cref="RestoreWindow"/> — restores a single window to the
    /// position described by <paramref name="record"/>.
    /// </summary>
    public void RestoreSingleWindow(IntPtr hWnd, WindowRecord record) => RestoreWindow(hWnd, record);

    /// <summary>
    /// Phase 3 Verify: Write snapshot to JSON for manual inspection.
    /// Call this during testing to confirm all visible apps are captured with correct positions.
    /// </summary>
    public void WriteDebugSnapshotToFile(string filePath)
    {
        var snapshot = SnapshotAllWindows();
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        string json = JsonSerializer.Serialize(snapshot, options);
        File.WriteAllText(filePath, json);
        AppLogger.Info($"Wrote debug snapshot to {filePath} — {snapshot.Count} windows");
    }

    // ── Close all user windows ─────────────────────────────────────────────

    /// <summary>
    /// Gracefully closes all visible top-level user windows by posting WM_CLOSE.
    /// WindowAnchor's own windows are excluded.  Apps with unsaved work will show
    /// their own save-confirmation dialogs; the window stays open until the user
    /// responds (or cancels).
    /// Returns the number of windows that were sent a close message.
    /// </summary>
    public int CloseAllUserWindows()
    {
        int closed = 0;
        var ownPid = (uint)Process.GetCurrentProcess().Id;

        NativeMethodsWindow.EnumWindows((hWnd, _) =>
        {
            if (!ShouldIncludeWindow(hWnd)) return true;

            NativeMethodsWindow.GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == ownPid) return true;   // skip our own windows

            NativeMethodsWindow.PostMessage(hWnd, NativeMethodsWindow.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            closed++;
            return true;
        }, IntPtr.Zero);

        AppLogger.Info($"CloseAllUserWindows: sent WM_CLOSE to {closed} windows");
        return closed;
    }

    /// <summary>
    /// Returns the number of visible top-level user windows (excluding
    /// WindowAnchor's own windows).  Used to poll whether all windows have
    /// finished closing after <see cref="CloseAllUserWindows"/>.
    /// </summary>
    public int CountUserWindows()
    {
        int count = 0;
        var ownPid = (uint)Process.GetCurrentProcess().Id;

        NativeMethodsWindow.EnumWindows((hWnd, _) =>
        {
            if (!ShouldIncludeWindow(hWnd)) return true;

            NativeMethodsWindow.GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid != ownPid)
                count++;
            return true;
        }, IntPtr.Zero);

        return count;
    }
}
