using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Controls;
using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.UI;

public partial class SettingsWindow : FluentWindow
{
    private readonly WorkspaceService  _workspaceService;
    private readonly StorageService    _storageService;
    private readonly LayoutCoordinator _coordinator;
    private readonly SettingsService   _settingsService;
    private readonly MonitorService    _monitorService;
    private readonly Action            _showHelp;
    private bool _suppressToggle;


    // ── Constructor ──────────────────────────────────────────────────────────

    public SettingsWindow(
        WorkspaceService workspaceService,
        StorageService   storageService,
        LayoutCoordinator coordinator,
        SettingsService  settingsService,
        MonitorService   monitorService,
        Action           showHelp)
    {
        _workspaceService = workspaceService;
        _storageService   = storageService;
        _coordinator      = coordinator;
        _settingsService  = settingsService;
        _monitorService   = monitorService;
        _showHelp         = showHelp ?? throw new ArgumentNullException(nameof(showHelp));
        InitializeComponent();
        PreviewKeyDown += OnHotkeyRecordKeyDown;
        Loaded += (_, _) =>
        {
            // Set toggle without firing handler
            _suppressToggle = true;
            AutostartToggle.IsChecked = AutostartService.IsEnabled();
            _suppressToggle = false;

            _suppressToggle = true;
            NotificationsToggle.IsChecked = _settingsService.Settings.NotificationsEnabled;
            _suppressToggle = false;

            InitialiseRestoreWorkflowUI();

            InitialiseBrowserIntegrationUI();
            RefreshLearnedMatchSummary();

            // Populate startup behavior controls
            InitialiseStartupBehaviorUI();
            InitialiseHotkeyUI();
            InitialiseMonitorUI();

            Refresh();
        };
    }

    private void OnOpenHelpClick(object sender, RoutedEventArgs e) => _showHelp();

    private void OnNotificationsToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        _settingsService.Settings.NotificationsEnabled = NotificationsToggle.IsChecked.GetValueOrDefault();
        _settingsService.Save();
    }

    private void InitialiseBrowserIntegrationUI()
    {
        var installed = BrowserIntegrationService.GetInstalledBrowserNames();
        ChromeExtensionButton.IsEnabled = installed.Contains("Google Chrome");
        EdgeExtensionButton.IsEnabled = installed.Contains("Microsoft Edge");
        BraveExtensionButton.IsEnabled = installed.Contains("Brave");
        OperaExtensionButton.IsEnabled = installed.Contains("Opera");
    }

    private static void OpenBrowserExtensionSetup(string browserName)
        => BrowserIntegrationService.OpenManagementPage(browserName);

    private void OnChromeExtensionSetup(object sender, RoutedEventArgs e)
        => OpenBrowserExtensionSetup("Google Chrome");

    private void OnEdgeExtensionSetup(object sender, RoutedEventArgs e)
        => OpenBrowserExtensionSetup("Microsoft Edge");

    private void OnBraveExtensionSetup(object sender, RoutedEventArgs e)
        => OpenBrowserExtensionSetup("Brave");

    private void OnOperaExtensionSetup(object sender, RoutedEventArgs e)
        => OpenBrowserExtensionSetup("Opera");

    private void OnRemoveBrowserConnection(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "Remove WindowAnchor's native browser connection registrations? The browser extension itself will not be uninstalled.",
            "Remove Browser Connection", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);
        if (result == System.Windows.MessageBoxResult.OK)
            BrowserIntegrationService.RemoveNativeHostRegistrations();
    }

    private void RefreshLearnedMatchSummary()
    {
        int count = _settingsService.Settings.WindowMatchHints?.Count ?? 0;
        LearnedMatchCountText.Text = count == 0
            ? "No remembered choices. Ambiguous matches will always ask before assignment."
            : $"{count} remembered choice{(count == 1 ? "" : "s")}. " +
              "Hints use stable workspace/entry IDs and composite app identity; HWND/PID are never saved.";
    }

    private void OnClearRememberedMatches(object sender, RoutedEventArgs e)
    {
        int count = _settingsService.Settings.WindowMatchHints?.Count ?? 0;
        if (count == 0) return;
        System.Windows.MessageBoxResult result = System.Windows.MessageBox.Show(
            this,
            $"Clear {count} remembered window choice{(count == 1 ? "" : "s")}? " +
            "Future ambiguous restores will ask again.",
            "Clear Remembered Window Choices",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Question);
        if (result != System.Windows.MessageBoxResult.OK) return;
        _settingsService.ClearAllWindowMatches();
        RefreshLearnedMatchSummary();
    }

}
