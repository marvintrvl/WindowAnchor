using System;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;
using WindowAnchor.Models;
using WindowAnchor.Services;
using DrawingIcon = System.Drawing.Icon;

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
            RefreshLogicalPathAliasSummary();
            RefreshLearnedMatchSummary();
            RefreshPersistentApplicationSummary();

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

    private void RefreshLogicalPathAliasSummary()
    {
        IReadOnlyDictionary<string, string> aliases = _settingsService.Settings.LogicalPathAliases ??
            new Dictionary<string, string>();
        LogicalPathAliasSummaryText.Text = aliases.Count == 0
            ? "No aliases configured. Workspace paths remain absolute on this device."
            : string.Join("  ", aliases.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"${{{pair.Key}}} = {pair.Value}"));
    }

    private void OnSaveLogicalPathAlias(object sender, RoutedEventArgs e)
    {
        try
        {
            _settingsService.SetLogicalPathAlias(
                LogicalPathAliasNameTextBox.Text,
                LogicalPathAliasRootTextBox.Text);
            RefreshLogicalPathAliasSummary();
        }
        catch (ArgumentException ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Logical Path Alias",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private void OnRemoveLogicalPathAlias(object sender, RoutedEventArgs e)
    {
        try
        {
            _settingsService.SetLogicalPathAlias(LogicalPathAliasNameTextBox.Text, null);
            LogicalPathAliasRootTextBox.Clear();
            RefreshLogicalPathAliasSummary();
        }
        catch (ArgumentException ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Logical Path Alias",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
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

    private void RefreshPersistentApplicationSummary()
    {
        PersistentApplicationIdentity[] configured = (_settingsService.Settings.PersistentApplications ?? [])
            .OrderBy(PersistentApplicationLabel, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        PersistentApplicationCandidate[] liveCandidates = _workspaceService
            .GetOpenPersistentApplicationCandidates()
            .ToArray();
        var livePaths = liveCandidates.ToDictionary(
            candidate => candidate.Identity,
            candidate => candidate.ExecutablePath);
        int count = configured.Length;
        PersistentApplicationSummaryText.Text = count == 0
            ? "No apps are globally preserved. Exact Switch will only keep destination and per-entry protected windows."
            : $"{count} app{(count == 1 ? "" : "s")} will remain open during Exact Switch in every workspace.";

        PersistentApplicationsPanel.Children.Clear();
        PersistentApplicationsEmptyText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (PersistentApplicationIdentity identity in configured)
        {
            livePaths.TryGetValue(identity, out string? executablePath);
            PersistentApplicationsPanel.Children.Add(BuildPersistentApplicationRow(identity, executablePath));
        }
    }

    private void OnAddPersistentApplication(object sender, RoutedEventArgs e)
    {
        PersistentApplicationCandidate[] candidates = _workspaceService
            .GetOpenPersistentApplicationCandidates()
            .Where(candidate => !(_settingsService.Settings.PersistentApplications ?? [])
                .Contains(candidate.Identity))
            .ToArray();
        if (candidates.Length == 0)
        {
            System.Windows.MessageBox.Show(
                this,
                "No additional eligible open applications were found.",
                "Keep Open During Workspace Switches",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        var menu = new ContextMenu();
        foreach (PersistentApplicationCandidate candidate in candidates)
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = PersistentApplicationLabel(candidate.Identity),
                Tag = candidate.Identity
            };
            item.Click += (_, _) =>
            {
                _settingsService.AddPersistentApplication(candidate.Identity);
                RefreshPersistentApplicationSummary();
                PersistentApplicationsExpander.IsExpanded = true;
            };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = sender as System.Windows.Controls.Button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private FrameworkElement BuildPersistentApplicationRow(
        PersistentApplicationIdentity identity,
        string? executablePath)
    {
        var row = new Grid
        {
            Margin = new Thickness(0, 0, 0, 8)
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconFrame = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(4),
            Background = (System.Windows.Media.Brush)FindResource("ControlFillColorSecondaryBrush"),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        ImageSource? source = TryLoadApplicationIcon(executablePath);
        if (source is not null)
        {
            iconFrame.Child = new System.Windows.Controls.Image
            {
                Source = source,
                Width = 24,
                Height = 24,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        else
        {
            iconFrame.Child = new System.Windows.Controls.TextBlock
            {
                Text = PersistentApplicationLabel(identity)[..1].ToUpperInvariant(),
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush")
            };
        }
        row.Children.Add(iconFrame);

        var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        details.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = PersistentApplicationLabel(identity),
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (!string.IsNullOrWhiteSpace(identity.AppUserModelId))
        {
            details.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Packaged app",
                FontSize = 10,
                Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush")
            });
        }
        Grid.SetColumn(details, 1);
        row.Children.Add(details);

        var remove = new Wpf.Ui.Controls.Button
        {
            Appearance = ControlAppearance.Transparent,
            Icon = new SymbolIcon
            {
                Symbol = SymbolRegular.Delete24,
                FontSize = 14
            },
            Padding = new Thickness(6),
            ToolTip = $"Stop keeping {PersistentApplicationLabel(identity)} open",
            Tag = identity,
            VerticalAlignment = VerticalAlignment.Center
        };
        remove.Click += OnRemovePersistentApplication;
        Grid.SetColumn(remove, 2);
        row.Children.Add(remove);
        return row;
    }

    private void OnRemovePersistentApplication(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PersistentApplicationIdentity identity })
            return;
        _settingsService.RemovePersistentApplication(identity);
        RefreshPersistentApplicationSummary();
    }

    private static ImageSource? TryLoadApplicationIcon(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return null;
        try
        {
            using DrawingIcon? icon = DrawingIcon.ExtractAssociatedIcon(executablePath);
            if (icon is null) return null;
            BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string PersistentApplicationLabel(PersistentApplicationIdentity identity)
    {
        string executableName = Path.GetFileNameWithoutExtension(identity.ExecutableName);
        if (!string.IsNullOrWhiteSpace(executableName))
            return executableName;
        string packageName = identity.AppUserModelId.Split('!', 2)[0].Split('_')[0];
        return string.IsNullOrWhiteSpace(packageName) ? "Application" : packageName;
    }

}
