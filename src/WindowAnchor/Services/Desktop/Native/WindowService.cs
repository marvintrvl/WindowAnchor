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
public class WindowService : IWindowInventory, IWindowMutation, IWorkspaceSwitchWindowController,
    IDisplayRecoveryEnvironment
{
    private readonly SettingsService? _settingsService;
    private readonly IRawWindowInventory _rawInventory;
    private readonly IExplorerTabSessionCapture _explorerTabs;
    private readonly IVirtualDesktopAssociation _virtualDesktops;

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
        SettingsService? settingsService = null,
        IExplorerTabSessionCapture? explorerTabs = null,
        IVirtualDesktopAssociation? virtualDesktops = null)
    {
        _rawInventory = rawInventory;
        _settingsService = settingsService;
        _explorerTabs = explorerTabs ?? new ExplorerTabSessionService();
        _virtualDesktops = virtualDesktops ?? new VirtualDesktopAssociationService();
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

        // Build the Explorer session map once before iterating. Shell.Application exposes one
        // automation object per tab, while the raw inventory exposes one top-level window.
        IReadOnlyDictionary<IntPtr, ExplorerWindowSession> explorerSessions =
            _explorerTabs.CaptureOpenWindows();

        foreach (var observed in _rawInventory.EnumerateWindows())
        {
            if (!WindowPolicyEvaluator.Includes(observed, policy))
                continue;

            var record = CaptureWindowRecord(observed, explorerSessions, captureTerminalTabs: true);
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

    /// <summary>Builds the enriched record for one raw top-level window.</summary>
    private WindowRecord? CaptureWindowRecord(
        ObservedWindow observed,
        IReadOnlyDictionary<IntPtr, ExplorerWindowSession>? explorerSessions = null,
        bool captureTerminalTabs = false)
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
        ExplorerWindowSession? explorerSession = null;
        if (explorerSessions != null &&
            processName.Equals("explorer", StringComparison.OrdinalIgnoreCase) &&
            explorerSessions.TryGetValue(hWnd, out explorerSession) &&
            explorerSession.TabPaths.Count > 0)
        {
            int activeIndex = Math.Clamp(
                explorerSession.ActiveTabIndex,
                0,
                explorerSession.TabPaths.Count - 1);
            folderPath = explorerSession.TabPaths[activeIndex];
        }

        (List<TerminalTab> terminalTabs, int terminalActiveIndex) =
            captureTerminalTabs && processName.Equals("windowsterminal", StringComparison.OrdinalIgnoreCase)
                ? TerminalTabCaptureService.Capture(hWnd)
                : (new List<TerminalTab>(), 0);
        VirtualDesktopCaptureResult virtualDesktop =
            _settingsService?.Settings.EnableVirtualDesktopAssociation == true
                ? _virtualDesktops.TryGetWindowDesktopId(hWnd)
                : new VirtualDesktopCaptureResult(VirtualDesktopAssociationStatus.Unsupported);

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
            ExplorerTabPaths = explorerSession?.TabPaths.ToList() ?? new List<string>(),
            ExplorerActiveTabIndex = explorerSession?.ActiveTabIndex ?? 0,
            TerminalTabs = terminalTabs,
            TerminalActiveTabIndex = terminalActiveIndex,
            AppUserModelId = appUserModelId,
            BrowserUrl = browserUrl,
            VirtualDesktopId = virtualDesktop.Status == VirtualDesktopAssociationStatus.Available
                ? virtualDesktop.DesktopId?.ToString("D") ?? ""
                : "",
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
        NativeMethodsWindow.Rect saved, uint savedDpi, uint targetDpi) =>
        WindowPlacementGeometry.Scale(saved, savedDpi, targetDpi);

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
        IReadOnlyDictionary<IntPtr, ExplorerWindowSession> explorerSessions =
            _explorerTabs.CaptureOpenWindows();

        foreach (var observed in _rawInventory.EnumerateWindows())
        {
            if (!WindowPolicyEvaluator.Includes(observed, policy))
                continue;

            var record = CaptureWindowRecord(observed, explorerSessions);
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
        NativeMethodsWindow.Rect currentVisible) =>
        WindowPlacementGeometry.CompensateVisibleFrame(desiredVisible, currentOuter, currentVisible);

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

    /// <summary>
    /// Moves the foreground window into its nearest current work area only when too little of its
    /// normal bounds remain visible. The current show state and restored bounds are preserved.
    /// </summary>
    public OffScreenWindowRescueResult RescueForegroundWindow(
        IReadOnlyList<MonitorInfo> monitors,
        OffScreenWindowRescuePolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        IntPtr hWnd = NativeMethodsWindow.GetForegroundWindow();
        if (hWnd == IntPtr.Zero)
            return new OffScreenWindowRescueResult(
                OffScreenWindowRescueStatus.NoActiveWindow, hWnd, default);

        var placement = new NativeMethodsWindow.WindowPlacement
        {
            Length = Marshal.SizeOf<NativeMethodsWindow.WindowPlacement>()
        };
        if (!NativeMethodsWindow.GetWindowPlacement(hWnd, ref placement))
            return new OffScreenWindowRescueResult(
                OffScreenWindowRescueStatus.PlacementUnavailable, hWnd, default);

        NativeMethodsWindow.Rect original = placement.RcNormalPosition;
        if (!OffScreenWindowRescueGeometry.TryGetRescueBounds(
                original,
                monitors,
                policy ?? new OffScreenWindowRescuePolicy(),
                out NativeMethodsWindow.Rect rescued))
        {
            return new OffScreenWindowRescueResult(
                OffScreenWindowRescueStatus.AlreadyVisible, hWnd, original);
        }

        placement.RcNormalPosition = rescued;
        placement.Length = Marshal.SizeOf<NativeMethodsWindow.WindowPlacement>();
        if (!NativeMethodsWindow.SetWindowPlacement(hWnd, ref placement))
            return new OffScreenWindowRescueResult(
                OffScreenWindowRescueStatus.ApplyFailed, hWnd, original);
        if (placement.ShowCmd == 3)
            NativeMethodsWindow.ShowWindow(hWnd, 3);

        AppLogger.Info(
            "window.foreground_rescued",
            "Moved an insufficiently visible foreground window into the nearest work area",
            LogField.Public("showCmd", placement.ShowCmd));
        return new OffScreenWindowRescueResult(
            OffScreenWindowRescueStatus.Rescued, hWnd, original, rescued);
    }

    /// <summary>
    /// Detects a foreground borderless/fullscreen window without treating ordinary maximized
    /// windows as fullscreen. Automatic display recovery defers to this state so a game or video
    /// player is never repeatedly repositioned while changing display modes.
    /// </summary>
    public bool IsForegroundWindowFullscreen(IReadOnlyList<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        IntPtr hWnd = NativeMethodsWindow.GetForegroundWindow();
        if (hWnd == IntPtr.Zero || !NativeMethodsWindow.IsWindowVisible(hWnd) ||
            !NativeMethodsWindow.GetWindowRect(hWnd, out NativeMethodsWindow.Rect bounds))
        {
            return false;
        }

        MonitorInfo? monitor = MonitorService.GetMonitorForWindow(hWnd, monitors.ToList());
        return monitor is not null && IsFullscreenBounds(bounds, monitor);
    }

    internal static bool IsFullscreenBounds(NativeMethodsWindow.Rect bounds, MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        if (!monitor.HasValidBounds)
            return false;

        const int tolerance = 2;
        return bounds.Left <= monitor.BoundsLeft + tolerance &&
               bounds.Top <= monitor.BoundsTop + tolerance &&
               bounds.Right >= monitor.BoundsRight - tolerance &&
               bounds.Bottom >= monitor.BoundsBottom - tolerance;
    }

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
