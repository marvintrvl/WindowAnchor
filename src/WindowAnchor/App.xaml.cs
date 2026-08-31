using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using H.NotifyIcon;
using Wpf.Ui.Appearance;
using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor;

public partial class App : System.Windows.Application
{
    private TaskbarIcon?        _trayIcon;
    private LayoutCoordinator?  _coordinator;
    private MonitorService?     _monitorService;
    private WorkspaceService?   _workspaceService;
    private StorageService?     _storageService;
    private SettingsService?    _settingsService;
    private HotkeyService?     _hotkeyService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length > 0 &&
            e.Args[0].Equals("--export-diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            string destination = e.Args.Length > 1
                ? e.Args[1]
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    $"WindowAnchor-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
            try
            {
                AppLogger.ExportDiagnostics(destination);
                System.Windows.MessageBox.Show(
                    $"A redacted diagnostic export was written to:{Environment.NewLine}{destination}",
                    "WindowAnchor Diagnostics",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    "diagnostics.export_failed",
                    "Could not create a diagnostic export",
                    ex,
                    LogField.Path("destinationPath", destination),
                    LogField.Public("errorCategory", "diagnostic_export"));
                System.Windows.MessageBox.Show(
                    "WindowAnchor could not create the diagnostic export.",
                    "WindowAnchor Diagnostics",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Shutdown(1);
            }
            return;
        }

        if (e.Args.Length > 0 &&
            (e.Args[0].Equals("--native-messaging", StringComparison.OrdinalIgnoreCase) ||
             e.Args[0].StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)))
        {
            NativeMessagingHost.Run();
            Shutdown(0);
            return;
        }

        bool minimized = e.Args.Length > 0 &&
            e.Args[0].Equals("--minimized", StringComparison.OrdinalIgnoreCase);

        // Global exception handlers — prevent ghost tray icons
        DispatcherUnhandledException        += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;

        AppLogger.Info(
            "app.starting",
            "WindowAnchor is starting");

        // Apply system theme (Mica/dark/light) before any window opens
        ApplicationThemeManager.ApplySystemTheme();

        _trayIcon = (TaskbarIcon)FindResource("TrayIcon");
        _trayIcon.ForceCreate();

        var storageService    = new StorageService();
        _storageService       = storageService;
        _monitorService       = new MonitorService();
        // Settings must exist before WindowService: it reads the dedicated-browser URL patterns
        // to decide whether address bars are queried during a snapshot.
        _settingsService      = new SettingsService(storageService);
        AppLogger.MinimumLevel = _settingsService.Settings.DiagnosticLogLevel;
        var windowService     = new WindowService(_settingsService);
        var jumpListService   = new JumpListService();
        var webAppService     = new WebAppService();
        var browserSessionBridge = new BrowserSessionBridge();
        var workspaceService  = new WorkspaceService(storageService, windowService, _monitorService, jumpListService, webAppService, browserSessionBridge);

        _workspaceService = workspaceService;
        _coordinator      = new LayoutCoordinator(_monitorService, windowService, workspaceService);

        // Hotkeys (settings were created above, before WindowService)
        _hotkeyService   = new HotkeyService();
        _hotkeyService.Initialise();
        ApplyHotkeySettings();

        string initialFingerprint = _monitorService.GetCurrentMonitorFingerprint();
        AppLogger.Info(
            "display.initial_topology",
            "Captured the initial display topology",
            LogField.Identifier("monitorFingerprint", initialFingerprint));
        if (minimized)
            AppLogger.Info(
                "app.started_minimized",
                "WindowAnchor started minimized in the notification area");

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        // ── Startup workspace restore (deferred so the tray icon settles) ──
        var startupBehavior = _settingsService.Settings.StartupBehavior;
        if (startupBehavior != StartupBehavior.None)
        {
            _ = Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(2000);
                await HandleStartupRestoreAsync(startupBehavior);
            }, DispatcherPriority.Background);
        }
    }

    // ── Startup workspace restore ─────────────────────────────────────────

    private async Task HandleStartupRestoreAsync(StartupBehavior behavior)
    {
        try
        {
            var workspaces = _workspaceService!.GetAllWorkspaces();
            if (workspaces.Count == 0) return;

            WorkspaceSnapshot? target = null;

            switch (behavior)
            {
                case StartupBehavior.RestoreDefault:
                    string? defaultId = _settingsService!.Settings.DefaultWorkspaceId;
                    if (!string.IsNullOrEmpty(defaultId))
                        target = workspaces.FirstOrDefault(w =>
                            w.WorkspaceId.Equals(defaultId, StringComparison.OrdinalIgnoreCase));
                    break;

                case StartupBehavior.RestoreLastUsed:
                    target = workspaces.OrderByDescending(w => w.SavedAt).FirstOrDefault();
                    break;

                case StartupBehavior.AskUser:
                    var dialog = new UI.StartupWorkspaceDialog(workspaces);
                    if (dialog.ShowDialog() == true)
                        target = dialog.SelectedWorkspace;
                    break;
            }

            if (target != null)
            {
                AppLogger.Info(
                    "app.startup_restore_started",
                    "Started the configured startup restore",
                    LogField.Identifier("workspaceId", target.WorkspaceId),
                    LogField.Workspace("workspaceName", target.Name));
                await _coordinator!.RestoreWorkspaceAsync(target);
                ShowBalloon("Workspace Restored", $"\u201c{target.Name}\u201d restored on startup");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                "app.startup_restore_failed",
                "Startup restore failed",
                ex,
                LogField.Public("errorCategory", "startup_restore"));
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        string fingerprint = _monitorService?.GetCurrentMonitorFingerprint() ?? "unknown";
        AppLogger.Info(
            "display.settings_changed",
            "Received a display-settings change notification",
            LogField.Identifier("monitorFingerprint", fingerprint));
        _coordinator?.HandleDisplayChangeAsync();
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        var settings = new UI.SettingsWindow(_workspaceService!, _storageService!, _coordinator!, _settingsService!, _monitorService!);
        settings.Show();
    }

    private async void ShowSaveWorkspaceDialog()
    {
        // Build per-monitor window lists for the selective-save dialog
        List<(MonitorInfo Monitor, List<WindowRecord> Windows)> windowPreview;
        try
        {
            windowPreview = await Task.Run(() => _workspaceService!.GetWindowPreviewForDialog());
        }
        catch (Exception ex)
        {
            AppLogger.Warn(
                "workspace.preview_enumeration_failed",
                "Could not enumerate windows for the save dialog",
                ex,
                LogField.Public("errorCategory", "window_enumeration"));
            windowPreview = new();
        }

        var dialog = new UI.SaveWorkspaceDialog(windowPreview, _settingsService);
        if (dialog.ShowDialog() != true) return;

        // Read all dialog properties on the UI thread before Task.Run.
        var name            = dialog.WorkspaceName;
        var saveFiles       = dialog.SaveFiles;
        var selectedWindows = dialog.SelectedWindows;

        // Show progress window when file detection is enabled (can take several seconds).
        UI.SaveProgressWindow? progressWindow = null;
        if (saveFiles)
        {
            progressWindow = new UI.SaveProgressWindow(name);
            progressWindow.Show();
        }

        var progress = progressWindow != null
            ? new Progress<Services.SaveProgressReport>(r => progressWindow.ApplyReport(r))
            : (IProgress<Services.SaveProgressReport>?)null;

        try
        {
            WorkspaceCaptureResult capture = await _workspaceService!.CaptureWorkspaceAsync(
                name,
                saveFiles: saveFiles,
                selectedWindows: selectedWindows,
                progress: progress);
            _workspaceService.PersistCapture(
                capture,
                WorkspaceArtifactKind.NamedWorkspace,
                IncompleteBrowserCapturePolicy.SavePartialWorkspace);
            AppLogger.Info(
                "workspace.saved",
                "Saved a named workspace",
                LogField.Identifier("workspaceId", capture.Snapshot.WorkspaceId),
                LogField.Workspace("workspaceName", name),
                LogField.Public("saveFiles", saveFiles),
                LogField.Public("browserCaptureStatus", capture.BrowserCapture.Status));
            ShowBalloon("Workspace Saved",
                $"\u201c{name}\u201d saved \u2014 {selectedWindows.Count} window(s)");
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                "workspace.save_failed",
                "Workspace capture or persistence failed",
                ex,
                LogField.Workspace("workspaceName", name),
                LogField.Public("errorCategory", "workspace_save"));
            System.Windows.MessageBox.Show($"Failed to save workspace: {ex.Message}", "WindowAnchor",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        finally
        {
            progressWindow?.Close();
        }
    }

    private void OnTrayMenuOpened(object sender, RoutedEventArgs e)
    {
        PopulateWorkspacesMenu();
    }

    private void PopulateWorkspacesMenu()
    {
        var trayMenu = _trayIcon?.ContextMenu;
        if (trayMenu == null) return;

        System.Windows.Controls.MenuItem? workspacesItem = null;
        foreach (var item in trayMenu.Items)
        {
            if (item is System.Windows.Controls.MenuItem mi && mi.Name == "WorkspacesMenu")
            {
                workspacesItem = mi;
                break;
            }
        }
        if (workspacesItem is null) return;

        workspacesItem.Items.Clear();

        var workspaces = GetOrderedWorkspaces();

        if (workspaces.Count == 0)
        {
            workspacesItem.Items.Add(new System.Windows.Controls.MenuItem
            {
                Header = "(no saved workspaces)", IsEnabled = false
            });
        }
        else
        {
            foreach (var ws in workspaces)
            {
                var item = new System.Windows.Controls.MenuItem
                {
                    Header = $"Restore: {ws.Name}"
                };
                var captured = ws;
                item.Click += (_, _) => OnRestoreWorkspaceClick(captured);
                workspacesItem.Items.Add(item);
            }

            workspacesItem.Items.Add(new System.Windows.Controls.Separator());

            foreach (var ws in workspaces)
            {
                var switchItem = new System.Windows.Controls.MenuItem
                {
                    Header = $"Switch to: {ws.Name}"
                };
                var captured = ws;
                switchItem.Click += (_, _) => OnSwitchWorkspaceClick(captured);
                workspacesItem.Items.Add(switchItem);
            }

            workspacesItem.Items.Add(new System.Windows.Controls.Separator());

            foreach (var ws in workspaces)
            {
                var alignItem = new System.Windows.Controls.MenuItem
                {
                    Header = $"Align + minimize others: {ws.Name}"
                };
                var captured = ws;
                alignItem.Click += (_, _) => OnAlignWorkspaceClick(captured);
                workspacesItem.Items.Add(alignItem);
            }
        }

        // Always append Save + Manage at the bottom
        workspacesItem.Items.Add(new System.Windows.Controls.Separator());
        var saveItem = new System.Windows.Controls.MenuItem { Header = "Save Current Workspace..." };
        saveItem.Click += (_, _) => ShowSaveWorkspaceDialog();
        workspacesItem.Items.Add(saveItem);

        var manageItem = new System.Windows.Controls.MenuItem { Header = "Manage Workspaces" };
        manageItem.Click += (_, _) => OnOpenSettingsClick(manageItem, new RoutedEventArgs());
        workspacesItem.Items.Add(manageItem);
    }

    private void OnRestoreWorkspaceClick(WindowAnchor.Models.WorkspaceSnapshot snapshot)
    {
        _coordinator?.RestoreWorkspaceAsync(snapshot);
    }

    private void OnSwitchWorkspaceClick(WindowAnchor.Models.WorkspaceSnapshot snapshot)
    {
        _coordinator?.SwitchWorkspaceAsync(snapshot);
    }

    private void OnAlignWorkspaceClick(WindowAnchor.Models.WorkspaceSnapshot snapshot)
    {
        _coordinator?.AlignAndMinimizeOthersAsync(snapshot);
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        AppLogger.Info("User requested exit.");
        _hotkeyService?.Dispose();
        _trayIcon?.Dispose();
        Current.Shutdown();
    }

    // ── Hotkey integration ────────────────────────────────────────────────

    /// <summary>
    /// (Re)registers or unregisters all global hotkeys based on the current
    /// settings.  Called from OnStartup and from SettingsWindow when the user
    /// toggles the switch or changes a shortcut.
    /// </summary>
    public void ApplyHotkeySettings()
    {
        if (_hotkeyService == null || _settingsService == null) return;

        _hotkeyService.UnregisterAll();

        if (!_settingsService.Settings.HotkeysEnabled) return;

        // Merge defaults with any user-customised shortcuts
        var shortcuts = HotkeyService.GetResolvedShortcuts(_settingsService.Settings);

        foreach (var shortcut in shortcuts)
        {
            Action? callback = shortcut.ActionId switch
            {
                "QuickSave"      => () => Dispatcher.Invoke(ShowSaveWorkspaceDialog),
                "RestoreDefault" => () => Dispatcher.Invoke(RestoreDefaultWorkspace),
                "RestoreSlot1"   => () => Dispatcher.Invoke(() => RestoreWorkspaceByIndex(0)),
                "RestoreSlot2"   => () => Dispatcher.Invoke(() => RestoreWorkspaceByIndex(1)),
                "RestoreSlot3"   => () => Dispatcher.Invoke(() => RestoreWorkspaceByIndex(2)),
                "SwitchSlot1"    => () => Dispatcher.Invoke(() => SwitchWorkspaceByIndex(0)),
                "SwitchSlot2"    => () => Dispatcher.Invoke(() => SwitchWorkspaceByIndex(1)),
                "SwitchSlot3"    => () => Dispatcher.Invoke(() => SwitchWorkspaceByIndex(2)),
                "OpenSettings"   => () => Dispatcher.Invoke(() => OnOpenSettingsClick(this, new RoutedEventArgs())),
                "SwitchDefault"  => () => Dispatcher.Invoke(SwitchDefaultWorkspace),
                _ => null,
            };

            if (callback != null)
                _hotkeyService.Register(shortcut.Modifiers, shortcut.Key, callback);
        }
    }

    private void RestoreDefaultWorkspace()
    {
        string? workspaceId = _settingsService?.Settings.DefaultWorkspaceId;
        if (string.IsNullOrEmpty(workspaceId)) return;

        var ws = _workspaceService?.GetAllWorkspaces()
            .FirstOrDefault(w => w.WorkspaceId.Equals(workspaceId, StringComparison.OrdinalIgnoreCase));
        if (ws != null)
            _ = _coordinator!.RestoreWorkspaceAsync(ws);
    }

    private void SwitchDefaultWorkspace()
    {
        string? workspaceId = _settingsService?.Settings.DefaultWorkspaceId;
        if (string.IsNullOrEmpty(workspaceId)) return;

        var ws = _workspaceService?.GetAllWorkspaces()
            .FirstOrDefault(w => w.WorkspaceId.Equals(workspaceId, StringComparison.OrdinalIgnoreCase));
        if (ws != null)
            _ = _coordinator!.SwitchWorkspaceAsync(ws);
    }

    private void RestoreWorkspaceByIndex(int index)
    {
        var workspaces = GetOrderedWorkspaces();
        if (index < workspaces.Count)
            _ = _coordinator!.RestoreWorkspaceAsync(workspaces[index]);
    }

    private void SwitchWorkspaceByIndex(int index)
    {
        var workspaces = GetOrderedWorkspaces();
        if (index < workspaces.Count)
            _ = _coordinator!.SwitchWorkspaceAsync(workspaces[index]);
    }

    /// <summary>
    /// Returns workspaces in the user's preferred display order (matching the
    /// Settings UI).  The first three entries map to Ctrl+Alt+1/2/3.
    /// </summary>
    private List<Models.WorkspaceSnapshot> GetOrderedWorkspaces()
    {
        var all   = _workspaceService?.GetAllWorkspaces() ?? new();
        var order = _settingsService?.Settings.WorkspaceOrderIds;
        if (order == null || order.Count == 0)
            return all.OrderByDescending(w => w.SavedAt).ToList();

        var result = new List<Models.WorkspaceSnapshot>();
        foreach (var workspaceId in order)
        {
            var ws = all.FirstOrDefault(w =>
                w.WorkspaceId.Equals(workspaceId, StringComparison.OrdinalIgnoreCase));
            if (ws != null) result.Add(ws);
        }
        foreach (var ws in all.OrderByDescending(w => w.SavedAt))
        {
            if (!result.Any(r => r.WorkspaceId.Equals(ws.WorkspaceId, StringComparison.OrdinalIgnoreCase)))
                result.Add(ws);
        }
        return result;
    }

    // ── Balloon helper ────────────────────────────────────────────────────────

    public void ShowBalloon(string title, string message,
        H.NotifyIcon.Core.NotificationIcon icon = H.NotifyIcon.Core.NotificationIcon.Info)
    {
        if (_settingsService?.Settings.NotificationsEnabled == false)
            return;

        try
        {
            _trayIcon?.ShowNotification(title, message, icon);
        }
        catch (Exception ex)
        {
            AppLogger.Warn(
                "notification.show_failed",
                "Could not show a notification",
                ex,
                LogField.Public("errorCategory", "notification"));
        }
    }

    // ── Global exception handlers ─────────────────────────────────────────────

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.Error(
            "app.unhandled_dispatcher_exception",
            "An unhandled dispatcher exception reached the application boundary",
            e.Exception,
            LogField.Public("errorCategory", "unhandled_dispatcher_exception"));
        _trayIcon?.Dispose();   // prevent ghost tray icon
        e.Handled = false;      // let Windows show the crash dialog
    }

    private void OnDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            AppLogger.Error(
                "app.unhandled_domain_exception",
                "An unhandled domain exception reached the application boundary",
                ex,
                LogField.Public("errorCategory", "unhandled_domain_exception"));
        _trayIcon?.Dispose();   // prevent ghost tray icon
    }
}

/// <summary>
/// A MenuItem that gives the pointer a short grace period when moving from a
/// submenu header into its popup. This is useful for tray menus because the
/// submenu is hosted in a separate Popup window and may flip to the opposite
/// side of the parent near a screen edge.
/// </summary>
public sealed class DelayedSubmenuMenuItem : System.Windows.Controls.MenuItem
{
    public static readonly DependencyProperty SubmenuCloseDelayProperty =
        DependencyProperty.Register(
            nameof(SubmenuCloseDelay),
            typeof(int),
            typeof(DelayedSubmenuMenuItem),
            new FrameworkPropertyMetadata(250, null, CoerceSubmenuCloseDelay));

    private DispatcherTimer? _submenuCloseTimer;
    private System.Windows.Controls.Primitives.Popup? _submenuPopup;
    private FrameworkElement? _submenuSurface;

    /// <summary>
    /// Time in milliseconds that the submenu remains open after the pointer
    /// leaves the header. Entering the submenu cancels the pending close.
    /// Set to 0 to use normal WPF MenuItem behaviour.
    /// </summary>
    public int SubmenuCloseDelay
    {
        get => (int)GetValue(SubmenuCloseDelayProperty);
        set => SetValue(SubmenuCloseDelayProperty, value);
    }

    public override void OnApplyTemplate()
    {
        DetachPopupHandlers();

        base.OnApplyTemplate();

        _submenuPopup =
            GetTemplateChild("PART_Popup") as System.Windows.Controls.Primitives.Popup;

        if (_submenuPopup != null)
        {
            _submenuPopup.Opened += OnSubmenuPopupOpened;
            _submenuPopup.Closed += OnSubmenuPopupClosed;
            AttachSubmenuSurface(_submenuPopup.Child as FrameworkElement);
        }
    }

    protected override void OnMouseEnter(System.Windows.Input.MouseEventArgs e)
    {
        CancelPendingSubmenuClose();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        // Calling MenuItem.OnMouseLeave immediately lets WPF deselect/close the
        // hierarchy before the pointer can cross a tiny popup boundary. Suppress
        // that immediate close while the submenu is open and close it ourselves
        // after the configured grace period instead.
        if (IsSubmenuOpen && SubmenuCloseDelay > 0)
        {
            StartSubmenuCloseTimer();
            return;
        }

        base.OnMouseLeave(e);
    }

    private static object CoerceSubmenuCloseDelay(DependencyObject d, object baseValue)
    {
        return Math.Max(0, (int)baseValue);
    }

    private void OnSubmenuPopupOpened(object? sender, EventArgs e)
    {
        AttachSubmenuSurface(_submenuPopup?.Child as FrameworkElement);
        CancelPendingSubmenuClose();
    }

    private void OnSubmenuPopupClosed(object? sender, EventArgs e)
    {
        CancelPendingSubmenuClose();
    }

    private void OnSubmenuSurfaceMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        CancelPendingSubmenuClose();
    }

    private void OnSubmenuSurfaceMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (IsSubmenuOpen && !IsMouseOver)
            StartSubmenuCloseTimer();
    }

    private void StartSubmenuCloseTimer()
    {
        if (SubmenuCloseDelay <= 0)
        {
            CloseSubmenuAfterGracePeriod();
            return;
        }

        _submenuCloseTimer ??= new DispatcherTimer(DispatcherPriority.Input);
        _submenuCloseTimer.Tick -= OnSubmenuCloseTimerTick;
        _submenuCloseTimer.Tick += OnSubmenuCloseTimerTick;
        _submenuCloseTimer.Interval = TimeSpan.FromMilliseconds(SubmenuCloseDelay);
        _submenuCloseTimer.Stop();
        _submenuCloseTimer.Start();
    }

    private void CancelPendingSubmenuClose()
    {
        _submenuCloseTimer?.Stop();
    }

    private void OnSubmenuCloseTimerTick(object? sender, EventArgs e)
    {
        _submenuCloseTimer?.Stop();

        // The pointer successfully crossed into either surface: keep it open.
        if (IsMouseOver || _submenuSurface?.IsMouseOver == true)
            return;

        CloseSubmenuAfterGracePeriod();
    }

    private void CloseSubmenuAfterGracePeriod()
    {
        if (!IsSubmenuOpen)
            return;

        IsSubmenuOpen = false;

        // OnMouseLeave was intentionally deferred, so clear the visual highlight
        // when the grace period expires outside both menu surfaces.
        IsHighlighted = false;
    }

    private void AttachSubmenuSurface(FrameworkElement? surface)
    {
        if (ReferenceEquals(_submenuSurface, surface))
            return;

        if (_submenuSurface != null)
        {
            _submenuSurface.MouseEnter -= OnSubmenuSurfaceMouseEnter;
            _submenuSurface.MouseLeave -= OnSubmenuSurfaceMouseLeave;
        }

        _submenuSurface = surface;

        if (_submenuSurface != null)
        {
            _submenuSurface.MouseEnter += OnSubmenuSurfaceMouseEnter;
            _submenuSurface.MouseLeave += OnSubmenuSurfaceMouseLeave;
        }
    }

    private void DetachPopupHandlers()
    {
        CancelPendingSubmenuClose();

        if (_submenuPopup != null)
        {
            _submenuPopup.Opened -= OnSubmenuPopupOpened;
            _submenuPopup.Closed -= OnSubmenuPopupClosed;
        }

        AttachSubmenuSurface(null);
        _submenuPopup = null;
    }
}

