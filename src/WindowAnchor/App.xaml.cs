using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        // Settings + hotkeys
        _settingsService = new SettingsService();
        _hotkeyService   = new HotkeyService();
        _hotkeyService.Initialise();
        ApplyHotkeySettings();

        string initialFingerprint = _monitorService.GetCurrentMonitorFingerprint();
        AppLogger.Info($"Initial monitor fingerprint: {initialFingerprint}");
        if (minimized) AppLogger.Info("Started with --minimized — staying in tray.");

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
                    string? defaultName = _settingsService!.Settings.DefaultWorkspaceName;
                    if (!string.IsNullOrEmpty(defaultName))
                        target = workspaces.FirstOrDefault(w =>
                            w.Name.Equals(defaultName, StringComparison.OrdinalIgnoreCase));
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
                AppLogger.Info($"Startup restore: restoring '{target.Name}'");
                await _coordinator!.RestoreWorkspaceAsync(target);
                ShowBalloon("Workspace Restored", $"\u201c{target.Name}\u201d restored on startup");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("HandleStartupRestoreAsync failed", ex);
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        string fingerprint = _monitorService?.GetCurrentMonitorFingerprint() ?? "unknown";
        AppLogger.Info($"DisplaySettingsChanged — new fingerprint: {fingerprint}");
        _coordinator?.HandleDisplayChangeAsync();
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        var settings = new UI.SettingsWindow(_workspaceService!, _storageService!, _coordinator!, _settingsService!);
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
            AppLogger.Warn($"ShowSaveWorkspaceDialog: could not enumerate windows: {ex.Message}");
            windowPreview = new();
        }

        var dialog = new UI.SaveWorkspaceDialog(windowPreview);
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
            await Task.Run(
                () => _workspaceService!.TakeSnapshot(name, saveFiles: saveFiles,
                    selectedWindows: selectedWindows, progress: progress));
            AppLogger.Info($"Workspace '{name}' saved (files={saveFiles})");
            ShowBalloon("Workspace Saved",
                $"\u201c{name}\u201d saved \u2014 {selectedWindows.Count} window(s)");
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
                "OpenSettings"   => () => Dispatcher.Invoke(() => OnOpenSettingsClick(this, new RoutedEventArgs())),
                _ => null,
            };

            if (callback != null)
                _hotkeyService.Register(shortcut.Modifiers, shortcut.Key, callback);
        }
    }

    private void RestoreDefaultWorkspace()
    {
        string? name = _settingsService?.Settings.DefaultWorkspaceName;
        if (string.IsNullOrEmpty(name)) return;

        var ws = _workspaceService?.GetAllWorkspaces()
            .FirstOrDefault(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (ws != null)
            _ = _coordinator!.RestoreWorkspaceAsync(ws);
    }

    private void RestoreWorkspaceByIndex(int index)
    {
        var workspaces = GetOrderedWorkspaces();
        if (index < workspaces.Count)
            _ = _coordinator!.RestoreWorkspaceAsync(workspaces[index]);
    }

    /// <summary>
    /// Returns workspaces in the user's preferred display order (matching the
    /// Settings UI).  The first three entries map to Ctrl+Alt+1/2/3.
    /// </summary>
    private List<Models.WorkspaceSnapshot> GetOrderedWorkspaces()
    {
        var all   = _workspaceService?.GetAllWorkspaces() ?? new();
        var order = _settingsService?.Settings.WorkspaceOrder;
        if (order == null || order.Count == 0)
            return all.OrderByDescending(w => w.SavedAt).ToList();

        var result = new List<Models.WorkspaceSnapshot>();
        foreach (var name in order)
        {
            var ws = all.FirstOrDefault(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (ws != null) result.Add(ws);
        }
        foreach (var ws in all.OrderByDescending(w => w.SavedAt))
        {
            if (!result.Any(r => r.Name.Equals(ws.Name, StringComparison.OrdinalIgnoreCase)))
                result.Add(ws);
        }
        return result;
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

