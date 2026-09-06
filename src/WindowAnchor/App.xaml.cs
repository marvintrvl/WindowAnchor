using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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
    private UI.SettingsWindow? _settingsWindow;
    private UI.HelpReferenceWindow? _helpWindow;
    private int _shutdownStarted;

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
        var workspaceService  = new WorkspaceService(
            storageService,
            windowService,
            _monitorService,
            jumpListService,
            webAppService,
            browserSessionBridge,
            settingsService: _settingsService);

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

        // ── Startup workspace restore (deferred to the dispatcher idle queue) ──
        var startupBehavior = _settingsService.Settings.StartupBehavior;
        if (startupBehavior != StartupBehavior.None)
        {
            _ = Dispatcher.InvokeAsync(async () =>
            {
                await HandleStartupRestoreAsync(startupBehavior);
            }, DispatcherPriority.ApplicationIdle);
        }

        ScheduleFirstRunOnboarding(minimized);
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

    private async void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        string fingerprint = _monitorService?.GetCurrentMonitorFingerprint() ?? "unknown";
        AppLogger.Info(
            "display.settings_changed",
            "Received a display-settings change notification",
            LogField.Identifier("monitorFingerprint", fingerprint));
        if (_coordinator is not null)
            await _coordinator.HandleDisplayChangeAsync();
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        ShowSettingsWindow();
    }

    private void ShowSettingsWindow()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new UI.SettingsWindow(
            _workspaceService!,
            _storageService!,
            _coordinator!,
            _settingsService!,
            _monitorService!,
            () => ShowHelpWindow());
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void OnOpenHelpClick(object sender, RoutedEventArgs e) => ShowHelpWindow();

    private void ShowHelpWindow(bool firstRun = false)
    {
        if (_helpWindow is { IsVisible: true })
        {
            _helpWindow.Activate();
            return;
        }

        var help = new UI.HelpReferenceWindow(firstRun);
        _helpWindow = help;
        help.SaveWorkspaceRequested += (_, _) => ShowSaveWorkspaceDialog();
        help.SettingsRequested += (_, _) => ShowSettingsWindow();
        help.Closed += (_, _) =>
        {
            if (ReferenceEquals(_helpWindow, help))
                _helpWindow = null;
        };
        help.Show();
    }

    private void ScheduleFirstRunOnboarding(bool minimized)
    {
        if (_settingsService is null || _workspaceService is null)
            return;

        bool hasSavedWorkspaces = _workspaceService.GetAllWorkspaces().Count > 0;
        bool shouldShow = FirstRunOnboardingPolicy.ShouldShow(
            _settingsService.Settings.OnboardingCompleted,
            minimized,
            _settingsService.IsSaveBlocked,
            hasSavedWorkspaces);

        if (!shouldShow)
        {
            // A workspace is stronger evidence of an established installation than a missing
            // settings file. Record that fact silently instead of showing first-run UI later.
            if (!minimized &&
                !_settingsService.IsSaveBlocked &&
                !_settingsService.Settings.OnboardingCompleted &&
                hasSavedWorkspaces)
            {
                _settingsService.Settings.OnboardingCompleted = true;
                _settingsService.Save();
            }
            return;
        }

        // Persist before showing so an application restart cannot turn onboarding into spam.
        _settingsService.Settings.OnboardingCompleted = true;
        _settingsService.Save();
        AppLogger.Info(
            "onboarding.first_run_scheduled",
            "Scheduled the first interactive-launch guide");
        _ = Dispatcher.InvokeAsync(
            () => ShowHelpWindow(firstRun: true),
            DispatcherPriority.ApplicationIdle);
    }

    private async void ShowSaveWorkspaceDialog()
    {
        var workflow = new UI.SaveWorkspaceWorkflow(_workspaceService!, _settingsService);
        UI.SaveWorkspaceWorkflowResult result = await workflow.RunAsync();
        if (result.Status == UI.SaveWorkspaceWorkflowStatus.Saved)
        {
            ShowBalloon("Workspace Saved",
                $"\u201c{result.WorkspaceName}\u201d saved \u2014 {result.SelectedWindowCount} window(s)");
        }
    }

    private void OnTrayMenuOpened(object sender, RoutedEventArgs e)
    {
        PopulateWorkspacesMenu();
        if (_trayIcon?.ContextMenu is { } trayMenu)
        {
            foreach (object item in trayMenu.Items)
            {
                if (item is System.Windows.Controls.MenuItem menuItem &&
                    menuItem.Name == "UndoLastRestoreMenuItem")
                {
                    menuItem.IsEnabled = _coordinator?.CanUndoLastRestore == true;
                    break;
                }
            }
        }
    }

    private async void OnUndoLastRestoreClick(object sender, RoutedEventArgs e)
    {
        if (_coordinator is null) return;
        try
        {
            await UI.RestorePlanPreviewWorkflow.RunUndoAsync(_coordinator);
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                "restore.undo_failed",
                "Undo Last Restore failed",
                ex,
                LogField.Public("errorCategory", "restore_undo"));
            ShowBalloon(
                "Undo Failed",
                "The previous desktop state could not be restored.",
                H.NotifyIcon.Core.NotificationIcon.Warning);
        }
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
        if (_coordinator is not null)
            _ = UI.RestorePlanPreviewWorkflow.RunWorkspaceDefaultAsync(
                _coordinator,
                snapshot);
    }

    private void OnSwitchWorkspaceClick(WindowAnchor.Models.WorkspaceSnapshot snapshot)
    {
        if (_coordinator is not null)
            _ = UI.RestorePlanPreviewWorkflow.RunSwitchAsync(_coordinator, snapshot);
    }

    private void OnAlignWorkspaceClick(WindowAnchor.Models.WorkspaceSnapshot snapshot)
    {
        if (_coordinator is not null)
            _ = UI.RestorePlanPreviewWorkflow.RunAsync(
                _coordinator,
                snapshot,
                RestoreMode.AlignAndMinimize);
    }

    private async void OnExitClick(object sender, RoutedEventArgs e)
    {
        AppLogger.Info(
            "app.exit_requested",
            "User requested application exit");
        await DisposeOwnedServicesAsync();
        Current.Shutdown();
    }

    private async Task DisposeOwnedServicesAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
            return;

        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        if (_coordinator is not null)
            await _coordinator.DisposeAsync();
        if (_workspaceService is not null)
            await _workspaceService.DisposeAsync();
        _hotkeyService?.Dispose();
        _trayIcon?.Dispose();
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
            _ = UI.RestorePlanPreviewWorkflow.RunWorkspaceDefaultAsync(
                _coordinator!,
                ws);
    }

    private void SwitchDefaultWorkspace()
    {
        string? workspaceId = _settingsService?.Settings.DefaultWorkspaceId;
        if (string.IsNullOrEmpty(workspaceId)) return;

        var ws = _workspaceService?.GetAllWorkspaces()
            .FirstOrDefault(w => w.WorkspaceId.Equals(workspaceId, StringComparison.OrdinalIgnoreCase));
        if (ws != null)
            _ = UI.RestorePlanPreviewWorkflow.RunSwitchAsync(_coordinator!, ws);
    }

    private void RestoreWorkspaceByIndex(int index)
    {
        var workspaces = GetOrderedWorkspaces();
        if (index < workspaces.Count)
            _ = UI.RestorePlanPreviewWorkflow.RunWorkspaceDefaultAsync(
                _coordinator!,
                workspaces[index]);
    }

    private void SwitchWorkspaceByIndex(int index)
    {
        var workspaces = GetOrderedWorkspaces();
        if (index < workspaces.Count)
            _ = UI.RestorePlanPreviewWorkflow.RunSwitchAsync(_coordinator!, workspaces[index]);
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
        _ = DisposeOwnedServicesAsync();
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
        _ = DisposeOwnedServicesAsync();
    }
}

