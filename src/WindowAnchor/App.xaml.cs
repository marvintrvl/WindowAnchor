using System;
using System.Diagnostics;
using System.Linq;
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

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        var settings = new UI.SettingsWindow(_workspaceService!, _storageService!, _coordinator!);
        settings.Show();
    }

    private async void ShowSaveWorkspaceDialog()
    {
        // Build monitor list with per-monitor window counts for the dialog
        System.Collections.Generic.List<(WindowAnchor.Models.MonitorInfo Monitor, int WindowCount)> monitorData = new();
        try
        {
            monitorData = _workspaceService!.GetMonitorDataForDialog();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"ShowSaveWorkspaceDialog: could not enumerate monitors: {ex.Message}");
        }

        var dialog = new UI.SaveWorkspaceDialog(monitorData);
        if (dialog.ShowDialog() != true) return;

        // Read all dialog properties on the UI thread before Task.Run.
        var name       = dialog.WorkspaceName;
        var saveFiles  = dialog.SaveFiles;
        var monitorIds = dialog.SelectedMonitorIds;

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
            await System.Threading.Tasks.Task.Run(
                () => _workspaceService!.TakeSnapshot(name, saveFiles: saveFiles, monitorIds: monitorIds, progress: progress));
            AppLogger.Info($"Workspace '{name}' saved (files={saveFiles})");
            ShowBalloon("Workspace Saved",
                $"\u201c{name}\u201d saved \u2014 {(monitorIds == null ? "all monitors" : $"{monitorIds.Count} monitor(s)")}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("ShowSaveWorkspaceDialog: TakeSnapshot failed", ex);
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

        var workspaces = _workspaceService?.GetAllWorkspaces()
            ?? new System.Collections.Generic.List<WindowAnchor.Models.WorkspaceSnapshot>();

        if (workspaces.Count == 0)
        {
            workspacesItem.Items.Add(new System.Windows.Controls.MenuItem
            {
                Header = "(no saved workspaces)", IsEnabled = false
            });
        }
        else
        {
            foreach (var ws in workspaces.OrderByDescending(w => w.SavedAt))
            {
                var item = new System.Windows.Controls.MenuItem
                {
                    Header = $"Restore: {ws.Name}"
                };
                var captured = ws;
                item.Click += (_, _) => OnRestoreWorkspaceClick(captured);
                workspacesItem.Items.Add(item);
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

