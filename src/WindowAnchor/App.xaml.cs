using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using H.NotifyIcon;
using Wpf.Ui.Appearance;
using WindowAnchor.Services;

namespace WindowAnchor;

public partial class App : System.Windows.Application
{
    private TaskbarIcon?        _trayIcon;
    private LayoutCoordinator?  _coordinator;
    private MonitorService?     _monitorService;
    private WorkspaceService?   _workspaceService;
    private StorageService?     _storageService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool minimized = e.Args.Length > 0 &&
            e.Args[0].Equals("--minimized", StringComparison.OrdinalIgnoreCase);

        // Global exception handlers — prevent ghost tray icons
        DispatcherUnhandledException        += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;

        AppLogger.Info("WindowAnchor starting");

        // Apply system theme (Mica/dark/light) before any window opens
        ApplicationThemeManager.ApplySystemTheme();

        _trayIcon = (TaskbarIcon)FindResource("TrayIcon");
        _trayIcon.ForceCreate();

        var storageService    = new StorageService();
        _storageService       = storageService;
        _monitorService       = new MonitorService();
        var windowService     = new WindowService();
        var jumpListService   = new JumpListService();
        var workspaceService  = new WorkspaceService(storageService, windowService, _monitorService, jumpListService);

        _workspaceService = workspaceService;
        _coordinator      = new LayoutCoordinator(_monitorService, windowService, workspaceService);

        string initialFingerprint = _monitorService.GetCurrentMonitorFingerprint();
        AppLogger.Info($"Initial monitor fingerprint: {initialFingerprint}");
        if (minimized) AppLogger.Info("Started with --minimized — staying in tray.");

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        string fingerprint = _monitorService?.GetCurrentMonitorFingerprint() ?? "unknown";
        AppLogger.Info($"DisplaySettingsChanged — new fingerprint: {fingerprint}");
        _coordinator?.HandleDisplayChangeAsync();
    }

    private void OnRestoreNowClick(object sender, RoutedEventArgs e)
    {
        _coordinator?.RestoreLayout();
    }

    private void OnSaveNowClick(object sender, RoutedEventArgs e)
    {
        _coordinator?.SaveCurrentLayout();
    }

    private void OnSaveWorkspaceClick(object sender, RoutedEventArgs e)
    {
        var dialog = new UI.SaveWorkspaceDialog();
        if (dialog.ShowDialog() == true)
        {
            _coordinator?.SaveWorkspaceSnapshot(dialog.WorkspaceName);
        }
    }
    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        var settings = new UI.SettingsWindow(_workspaceService!, _storageService!, _coordinator!);
        settings.Show();
    }

    private void OnTrayMenuOpened(object sender, RoutedEventArgs e)
    {
        PopulateWorkspacesMenu();
    }

    private void PopulateWorkspacesMenu()
    {
        // Find the "Workspaces ▸" MenuItem inside the tray ContextMenu
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

        var workspaces = _workspaceService?.GetAllWorkspaces() ?? new System.Collections.Generic.List<WindowAnchor.Models.WorkspaceSnapshot>();

        if (workspaces.Count == 0)
        {
            workspacesItem.Items.Add(new System.Windows.Controls.MenuItem { Header = "(no saved workspaces)", IsEnabled = false });
            return;
        }

        foreach (var ws in workspaces)
        {
            var item = new System.Windows.Controls.MenuItem { Header = $"Restore: {ws.Name}" };
            var captured = ws;
            item.Click += (_, _) => OnRestoreWorkspaceClick(captured);
            workspacesItem.Items.Add(item);
        }
    }

    private void OnRestoreWorkspaceClick(WindowAnchor.Models.WorkspaceSnapshot snapshot)
    {
        _coordinator?.RestoreWorkspaceAsync(snapshot);
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        AppLogger.Info("User requested exit.");
        _trayIcon?.Dispose();
        Current.Shutdown();
    }

    // ── Balloon helper ────────────────────────────────────────────────────────

    public void ShowBalloon(string title, string message,
        H.NotifyIcon.Core.NotificationIcon icon = H.NotifyIcon.Core.NotificationIcon.Info)
    {
        try
        {
            _trayIcon?.ShowNotification(title, message, icon);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"ShowBalloon failed: {ex.Message}");
        }
    }

    // ── Global exception handlers ─────────────────────────────────────────────

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.Error("Unhandled dispatcher exception", e.Exception);
        _trayIcon?.Dispose();   // prevent ghost tray icon
        e.Handled = false;      // let Windows show the crash dialog
    }

    private void OnDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            AppLogger.Error("Unhandled domain exception", ex);
        _trayIcon?.Dispose();   // prevent ghost tray icon
    }
}

