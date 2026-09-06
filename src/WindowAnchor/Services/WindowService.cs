using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using WindowAnchor.Models;
using WindowAnchor.Native;

namespace WindowAnchor.Services;

/// <summary>
/// Applies named selection policies to raw window observations, enriches capture/match records,
/// and performs live window mutations via P/Invoke.
/// </summary>
public class WindowService : IWindowInventory, IWindowMutation, IWorkspaceSwitchWindowController
{
    private readonly SettingsService? _settingsService;
    private readonly IRawWindowInventory _rawInventory;

    /// <param name="settingsService">
    ///   Optional. Supplies <see cref="Models.AppSettings.DedicatedBrowserUrlPatterns"/>; when
    ///   omitted no browser URLs are read during a snapshot.
    /// </param>
    public WindowService(SettingsService? settingsService = null)
        : this(new WindowInventory(), settingsService)
    {
    }

    /// <summary>Creates a window service over an explicit raw inventory.</summary>
    internal WindowService(
        IRawWindowInventory rawInventory,
        SettingsService? settingsService = null)
    {
        _rawInventory = rawInventory;
        _settingsService = settingsService;
    }

    /// <summary>
    /// Snapshots all visible top-level user windows.
    /// When <paramref name="monitors"/> is supplied (from
    /// <see cref="MonitorService.GetCurrentMonitors"/>), each record is tagged with the
    /// monitor it belongs to via <see cref="WindowRecord.MonitorId"/> etc.
    /// </summary>
    public List<WindowRecord> SnapshotWindows(
        WindowCandidatePolicy policy,
        List<MonitorInfo>? monitors = null)
    {
        RequirePolicy(policy, WindowCandidatePolicy.CaptureCandidate);
        var records = new List<WindowRecord>();

        // Build Explorer folder map once before iterating — uses Shell.Application COM to
        // get the folder open in each File Explorer window, keyed by HWND.
        var explorerFolderMap = BuildExplorerFolderMap();

        foreach (var observed in _rawInventory.EnumerateWindows())
        {
            if (!WindowPolicyEvaluator.Includes(observed, policy))
                continue;

            var record = CaptureWindowRecord(observed, explorerFolderMap);
            if (record != null)
            {
                // Tag with monitor while HWND is still valid
                if (monitors != null)
                {
                    var mon = MonitorService.GetMonitorForWindow(observed.Hwnd, monitors);
                    if (mon != null)
                    {
                        record.MonitorId    = mon.MonitorId;
                        record.MonitorIndex = mon.Index;
                        record.MonitorName  = mon.FriendlyName;
                        record.NormalizedLayout = WindowLayoutGeometry.Capture(
                            record,
                            mon,
                            record.ShowCmd == 1 ? observed.VisibleBounds : null);
                    }
                }
                records.Add(record);
            }
        }

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

    private WindowRecord? CaptureWindowRecord(
        ObservedWindow observed,
        Dictionary<IntPtr, string>? explorerFolderMap = null)
    {
        IntPtr hWnd = observed.Hwnd;
        var placement = new NativeMethodsWindow.WindowPlacement();
        placement.Length = Marshal.SizeOf(typeof(NativeMethodsWindow.WindowPlacement));

        if (!NativeMethodsWindow.GetWindowPlacement(hWnd, ref placement)) return null;

        // Windows 11 Snap Layouts fix: GetWindowPlacement.rcNormalPosition might be stale
        // because Snap uses SetWindowPos/DWM which don't update rcNormalPosition.
        // For normal (non-maximized/minimized) windows, compare with actual position.
        if (placement.ShowCmd == 1) // 1 = SW_SHOWNORMAL
        {
            if (observed.Bounds is { } bounds)
            {
                var actualRect = new NativeMethodsWindow.Rect
                {
                    Left = bounds.Left,
                    Top = bounds.Top,
                    Right = bounds.Right,
                    Bottom = bounds.Bottom
                };
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
                    AppLogger.Info(
                        "window.capture_stale_placement_corrected",
                        "Used the live window bounds instead of stale normal-position data",
                        LogField.Public("hwnd", hWnd));
                    placement.RcNormalPosition = actualRect;
                }
            }
        }

        uint processId = observed.ProcessId;
        string exePath = observed.ExecutablePath;
        string processName = observed.ProcessName;
        string fullTitle = observed.Title;
        // Store up to 200 chars — enough for any realistic window title while still bounding
        // storage size. The old 40-char limit clipped " - Word" / " - Notepad" suffixes on
        // longer document names, causing Tier-1 pattern matches to fail and Tier-2 jump-list
        // lookups to run on windows where Tier 1 would have succeeded.
        string snippet = fullTitle.Length > 200 ? fullTitle.Substring(0, 200) : fullTitle;

        // Read the window's explicit AppUserModelID. Two things depend on it:
        //   • Chromium browsers: installed web apps (PWAs) get their own AUMID while sharing
        //     chrome.exe/brave.exe and the window class — the only way to tell them apart.
        //   • Store/MSIX apps (TradingView, Notepad, …): the AUMID is the only way to relaunch
        //     them *with package identity*. Starting their exe under C:\Program Files\WindowsApps
        //     directly gives the app no package container, so it loses its settings.
        // Classic desktop apps usually have no explicit AUMID — the empty string is expected.
        string appUserModelId = observed.AppUserModelId;

        // For browser windows, read the address bar only when the user configured URL patterns
        // and the window's URL matches one. This keeps the (comparatively slow) UI Automation
        // query off the snapshot path for everyone who does not use the feature.
        string browserUrl = "";
        var urlPatterns = _settingsService?.Settings.DedicatedBrowserUrlPatterns;
        if (urlPatterns is { Count: > 0 } && WebAppService.IsChromiumBrowser(processName))
        {
            string url = BrowserUrlService.GetWindowUrl(hWnd);
            if (BrowserUrlService.MatchesAnyPattern(url, urlPatterns))
            {
                browserUrl = url;
                AppLogger.Info(
                    "browser_url.pattern_matched",
                    "A browser window matched a configured dedicated-window pattern",
                    LogField.Url("url", url),
                    LogField.Public("processName", processName));
            }
        }

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
            ClassName = observed.ClassName,
            TitleSnippet = snippet,
            ShowCmd = placement.ShowCmd,
            NormalLeft = placement.RcNormalPosition.Left,
            NormalTop = placement.RcNormalPosition.Top,
            NormalRight = placement.RcNormalPosition.Right,
            NormalBottom = placement.RcNormalPosition.Bottom,
            SavedDpi = NativeMethodsWindow.GetDpiForWindow(hWnd),
            FolderPath = folderPath,
            AppUserModelId = appUserModelId,
            BrowserUrl = browserUrl,
        };
    }

    public void RestoreWindow(IntPtr hWnd, WindowRecord record)
    {
        var placement = new NativeMethodsWindow.WindowPlacement();
        placement.Length = Marshal.SizeOf(typeof(NativeMethodsWindow.WindowPlacement));

        // Get current placement to preserve flags
        if (!NativeMethodsWindow.GetWindowPlacement(hWnd, ref placement))
        {
            AppLogger.Warn(
                "window.placement_read_failed",
                "Could not read a live window placement",
                LogField.Public("hwnd", hWnd),
                LogField.Public("errorCategory", "get_window_placement"));
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

        var targetRect = record.CoordinatesAreFinal
            ? savedRect
            : ScaleCoordsForDpi(savedRect, savedDpi, currentDpi);
        if (record.CoordinatesRepresentVisibleBounds &&
            NativeMethodsWindow.GetWindowRect(hWnd, out var currentOuter) &&
            NativeMethodsWindow.DwmGetWindowAttribute(
                hWnd,
                NativeMethodsWindow.DWMWA_EXTENDED_FRAME_BOUNDS,
                out NativeMethodsWindow.Rect currentVisible,
                Marshal.SizeOf<NativeMethodsWindow.Rect>()) == 0)
        {
            targetRect = CompensateForVisibleFrame(targetRect, currentOuter, currentVisible);
        }
        if (!record.CoordinatesAreFinal && savedDpi != currentDpi)
        {
            AppLogger.Info(
                "window.dpi_coordinates_scaled",
                "Scaled saved window coordinates for a DPI change",
                LogField.Public("savedDpi", savedDpi),
                LogField.Public("currentDpi", currentDpi));
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
            AppLogger.Warn(
                "window.placement_write_failed",
                "Could not apply a saved window placement",
                LogField.Public("hwnd", hWnd),
                LogField.Public("errorCategory", "set_window_placement"));

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
    public Dictionary<IntPtr, (uint Pid, WindowRecord Record)> GetWindowsWithPids(
        WindowCandidatePolicy policy)
    {
        RequirePolicy(policy, WindowCandidatePolicy.RestoreMatchCandidate);
        var result = new Dictionary<IntPtr, (uint Pid, WindowRecord Record)>();

        foreach (var observed in _rawInventory.EnumerateWindows())
        {
            if (!WindowPolicyEvaluator.Includes(observed, policy))
                continue;

            var record = CaptureWindowRecord(observed);
            if (record == null)
                continue;

            result[observed.Hwnd] = (observed.ProcessId, record);
        }

        return result;
    }

    /// <summary>
    /// Converts desired visible DWM bounds into the outer bounds expected by WINDOWPLACEMENT.
    /// Windows 10/11 commonly add invisible resize borders around normal resizable windows.
    /// </summary>
    internal static NativeMethodsWindow.Rect CompensateForVisibleFrame(
        NativeMethodsWindow.Rect desiredVisible,
        NativeMethodsWindow.Rect currentOuter,
        NativeMethodsWindow.Rect currentVisible)
    {
        if (currentOuter.Right <= currentOuter.Left || currentOuter.Bottom <= currentOuter.Top ||
            currentVisible.Right <= currentVisible.Left || currentVisible.Bottom <= currentVisible.Top)
        {
            return desiredVisible;
        }

        int leftInset = Math.Max(0, currentVisible.Left - currentOuter.Left);
        int topInset = Math.Max(0, currentVisible.Top - currentOuter.Top);
        int rightInset = Math.Max(0, currentOuter.Right - currentVisible.Right);
        int bottomInset = Math.Max(0, currentOuter.Bottom - currentVisible.Bottom);
        return new NativeMethodsWindow.Rect
        {
            Left = desiredVisible.Left - leftInset,
            Top = desiredVisible.Top - topInset,
            Right = desiredVisible.Right + rightInset,
            Bottom = desiredVisible.Bottom + bottomInset
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<RunningApplicationIdentity> GetRunningApplications()
    {
        var applications = new Dictionary<string, RunningApplicationIdentity>(
            StringComparer.OrdinalIgnoreCase);
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                string processName;
                try { processName = process.ProcessName; }
                catch { continue; }

                string executablePath = "";
                try { executablePath = process.MainModule?.FileName ?? ""; }
                catch
                {
                    // Elevated, protected, and short-lived processes are expected here. The
                    // process name still provides a conservative fallback identity.
                }

                string appUserModelId = executablePath.Contains(
                    @"\WindowsApps\",
                    StringComparison.OrdinalIgnoreCase)
                    ? WebAppService.GetProcessAppUserModelId((uint)process.Id)
                    : "";

                string normalizedPath = WindowIdentityExtractor.NormalizePath(executablePath);
                string key = appUserModelId.Length > 0
                    ? $"aumid:{appUserModelId}"
                    : normalizedPath.Length > 0
                        ? $"path:{normalizedPath}"
                        : $"name:{ProcessIdentityNormalizer.Normalize(processName)}";
                applications.TryAdd(
                    key,
                    new RunningApplicationIdentity(executablePath, processName, appUserModelId));
            }
        }

        return applications.Values
            .OrderBy(application => application.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(application => application.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <inheritdoc />
    public bool IsWindowAlive(IntPtr hWnd) => _rawInventory.IsWindowAlive(hWnd);

    /// <summary>
    /// Public alias for <see cref="RestoreWindow"/> — restores a single window to the
    /// position described by <paramref name="record"/>.
    /// </summary>
    public void RestoreSingleWindow(IntPtr hWnd, WindowRecord record) => RestoreWindow(hWnd, record);

    // ── Close all user windows ─────────────────────────────────────────────

    /// <summary>
    /// Returns the raw observations selected for safe-switch risk inspection. Unlike capture
    /// selection, this includes owned dialogs and small/untitled transient windows.
    /// </summary>
    public IReadOnlyList<ObservedWindow> InspectUserWindows(WindowCandidatePolicy policy)
    {
        RequirePolicy(policy, WindowCandidatePolicy.SwitchRiskCandidate);
        uint ownPid = (uint)Process.GetCurrentProcess().Id;
        return _rawInventory.EnumerateWindows()
            .Where(window => WindowPolicyEvaluator.Includes(window, policy, ownPid))
            .ToArray();
    }

    /// <summary>
    /// Posts WM_CLOSE to policy-selected user windows except approved target-workspace handles,
    /// and returns the exact stable set that the switch close phase must wait for.
    /// </summary>
    public IReadOnlySet<IntPtr> RequestCloseUserWindowsExcept(
        WindowCandidatePolicy policy,
        IReadOnlySet<IntPtr> keep)
    {
        RequirePolicy(policy, WindowCandidatePolicy.SwitchCloseCandidate);
        ArgumentNullException.ThrowIfNull(keep);
        var requested = new HashSet<IntPtr>();
        var ownPid = (uint)Process.GetCurrentProcess().Id;

        foreach (var observed in _rawInventory.EnumerateWindows())
        {
            if (!WindowPolicyEvaluator.Includes(observed, policy, ownPid))
                continue;
            if (keep.Contains(observed.Hwnd))
                continue;

            NativeMethodsWindow.PostMessage(
                observed.Hwnd,
                NativeMethodsWindow.WM_CLOSE,
                IntPtr.Zero,
                IntPtr.Zero);
            requested.Add(observed.Hwnd);
        }

        AppLogger.Info(
            "window.close_requests_sent",
            "Sent close requests to user windows",
            LogField.Public("windowCount", requested.Count),
            LogField.Public("preservedWindowCount", keep.Count));
        return requested;
    }

    /// <summary>
    /// Minimizes every visible top-level user window whose handle is <em>not</em> in
    /// <paramref name="keep"/>. WindowAnchor's own windows are always left alone.
    /// Used by the "align &amp; minimize others" restore mode to clear away windows that are not
    /// part of the workspace without closing them. Returns the number of windows minimized.
    /// </summary>
    public int MinimizeUserWindowsExcept(
        WindowCandidatePolicy policy,
        HashSet<IntPtr> keep)
    {
        RequirePolicy(policy, WindowCandidatePolicy.MinimizeCandidate);
        const int SW_MINIMIZE = 6;   // minimize without activating another window
        int minimized = 0;
        var ownPid = (uint)Process.GetCurrentProcess().Id;

        foreach (var observed in _rawInventory.EnumerateWindows())
        {
            if (!WindowPolicyEvaluator.Includes(observed, policy, ownPid))
                continue;
            if (keep.Contains(observed.Hwnd))
                continue;

            NativeMethodsWindow.ShowWindow(observed.Hwnd, SW_MINIMIZE);
            minimized++;
        }

        AppLogger.Info(
            "window.non_workspace_windows_minimized",
            "Minimized windows outside the restored workspace",
            LogField.Public("windowCount", minimized));
        return minimized;
    }

    private static void RequirePolicy(
        WindowCandidatePolicy actual,
        WindowCandidatePolicy expected)
    {
        if (actual != expected)
            throw new ArgumentException(
                $"{expected} is required for this operation; received {actual}.",
                nameof(actual));
    }

}
