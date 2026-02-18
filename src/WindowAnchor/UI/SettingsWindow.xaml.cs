using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
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
    private bool _suppressToggle;

    // ── View-model rows ──────────────────────────────────────────────────────

    // These are internal (not private) so SonarLint recognises the public binding properties
    // as accessible outside this class — they are consumed via XAML {Binding ...} reflection.
    internal sealed class WorkspaceRow : INotifyPropertyChanged
    {
        public WorkspaceSnapshot Source    { get; init; } = null!;
        public string Name                 => Source.Name;
        public string MonitorFingerprint   => Source.MonitorFingerprint;
        public string SavedAtDisplay       => Source.SavedAt.ToLocalTime().ToString("g");
        public int    EntryCount           => Source.Entries.Count;

        private bool   _isEditing;
        private string _editName = "";
        public bool   IsEditing { get => _isEditing; set { _isEditing = value; OnPropertyChanged(); } }
        public string EditName  { get => _editName;  set { _editName  = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    internal sealed class ProfileRow : INotifyPropertyChanged
    {
        public MonitorProfile Source   { get; init; } = null!;
        public string Fingerprint      => Source.Fingerprint;
        public string DisplayName      => Source.DisplayName;
        public string LastSavedDisplay => Source.LastSaved.ToString("g");
        public int    WindowCount      => Source.Windows.Count;

        private bool   _isEditing;
        private string _editName = "";
        public bool   IsEditing { get => _isEditing; set { _isEditing = value; OnPropertyChanged(); } }
        public string EditName  { get => _editName;  set { _editName  = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // ── Constructor ──────────────────────────────────────────────────────────

    public SettingsWindow(WorkspaceService workspaceService, StorageService storageService, LayoutCoordinator coordinator)
    {
        _workspaceService = workspaceService;
        _storageService   = storageService;
        _coordinator      = coordinator;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            // Set toggle without firing handler
            _suppressToggle = true;
            AutostartToggle.IsChecked = AutostartService.IsEnabled();
            _suppressToggle = false;
            Refresh();
        };
    }

    // ── Refresh ──────────────────────────────────────────────────────────────

    private void Refresh()
    {
        var wsRows = _workspaceService.GetAllWorkspaces()
            .OrderByDescending(w => w.SavedAt)
            .Select(w => new WorkspaceRow { Source = w })
            .ToList();
        WorkspacesList.ItemsSource = wsRows;
        WorkspaceCountText.Text    = $"{wsRows.Count} workspace{(wsRows.Count == 1 ? "" : "s")} saved";
        WorkspacesEmpty.Visibility = wsRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var profileRows = _storageService.LoadAllProfiles()
            .OrderByDescending(p => p.LastSaved)
            .Select(p => new ProfileRow { Source = p })
            .ToList();
        ProfilesList.ItemsSource = profileRows;
        ProfileCountText.Text    = $"{profileRows.Count} monitor profile{(profileRows.Count == 1 ? "" : "s")} saved";
        ProfilesEmpty.Visibility = profileRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Autostart toggle ──────────────────────────────────────────────────────

    private void OnAutostartToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        if (AutostartToggle.IsChecked == true)
            AutostartService.Enable();
        else
            AutostartService.Disable();
    }

    // ── Save new workspace — inline name card ────────────────────────────────

    private void OnSaveNewWorkspace(object sender, RoutedEventArgs e)
    {
        // Show inline name input, hide normal row
        WsSaveNormal.Visibility = Visibility.Collapsed;
        WsSaveInput.Visibility  = Visibility.Visible;
        WsNameBox.Text          = "";
        WsNameBox.Focus();
    }

    private async void OnWsNameBoxKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)  await CommitWsName();
        if (e.Key == Key.Escape) CancelWsSave();
    }

    private async void OnWsNameConfirm(object sender, RoutedEventArgs e) => await CommitWsName();
    private void       OnWsNameCancel (object sender, RoutedEventArgs e) => CancelWsSave();

    private async Task CommitWsName()
    {
        string name = WsNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) { WsNameBox.Focus(); return; }
        CancelWsSave();
        await Task.Run(() => _workspaceService.TakeSnapshot(name));
        Refresh();
    }

    private void CancelWsSave()
    {
        WsSaveInput.Visibility  = Visibility.Collapsed;
        WsSaveNormal.Visibility = Visibility.Visible;
    }

    // ── Workspace row — ⋯ popup ───────────────────────────────────────────────

    private void OnWorkspaceMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.Tag is not WorkspaceRow row) return;

        var menu = new ContextMenu();

        var restore = new System.Windows.Controls.MenuItem { Header = "Restore Workspace" };
        restore.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowCounterclockwise24 };
        restore.Click += (_, _) => _ = _coordinator.RestoreWorkspaceAsync(row.Source);
        menu.Items.Add(restore);

        var viewWindows = new System.Windows.Controls.MenuItem { Header = "View & Edit Windows…" };
        viewWindows.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.AppsList24 };
        viewWindows.Click += (_, _) => DoViewWindows(row);
        menu.Items.Add(viewWindows);

        menu.Items.Add(new Separator());

        var rename = new System.Windows.Controls.MenuItem { Header = "Rename…" };
        rename.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Edit24 };
        rename.Click += (_, _) => DoRenameWorkspace(row);
        menu.Items.Add(rename);

        var delete = new System.Windows.Controls.MenuItem { Header = "Delete" };
        delete.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Delete24 };
        delete.Click += (_, _) => DoDeleteWorkspace(row);
        menu.Items.Add(delete);

        menu.PlacementTarget = btn;
        menu.Placement       = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen          = true;
    }

    private void DoViewWindows(WorkspaceRow row)
    {
        var dlg = new WorkspaceWindowsDialog(row.Source, _storageService) { Owner = this };
        dlg.ShowDialog();
        Refresh();
    }

    private void DoRenameWorkspace(WorkspaceRow row)
    {
        // Cancel any other editing rows first
        if (WorkspacesList.ItemsSource is System.Collections.Generic.List<WorkspaceRow> rows)
            foreach (var r in rows) r.IsEditing = false;

        row.EditName  = row.Name;
        row.IsEditing = true;
    }

    // ── Workspace inline edit events ─────────────────────────────────────────

    private void OnWsEditKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox tb) return;
        if (tb.Tag is not WorkspaceRow row) return;
        if (e.Key == Key.Enter)  CommitWsRename(row);
        if (e.Key == Key.Escape) row.IsEditing = false;
    }

    private void OnWsEditSave(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is WorkspaceRow row)
            CommitWsRename(row);
    }

    private void OnWsEditCancel(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is WorkspaceRow row)
            row.IsEditing = false;
    }

    private void CommitWsRename(WorkspaceRow row)
    {
        string newName = row.EditName.Trim();
        if (!string.IsNullOrEmpty(newName) && newName != row.Name)
            _storageService.RenameWorkspace(row.Source, newName);
        row.IsEditing = false;
        Refresh();
    }

    private void DoDeleteWorkspace(WorkspaceRow row)
    {
        _storageService.DeleteWorkspace(row.Source);
        Refresh();
    }

    // ── Profile row — ⋯ popup ────────────────────────────────────────────────

    private void OnProfileMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.Tag is not ProfileRow row) return;

        var menu = new ContextMenu();

        var restore = new System.Windows.Controls.MenuItem { Header = "Restore Layout" };
        restore.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowCounterclockwise24 };
        restore.Click += (_, _) => _coordinator.RestoreLayout();
        menu.Items.Add(restore);

        var viewWindows = new System.Windows.Controls.MenuItem { Header = "View & Edit Windows\u2026" };
        viewWindows.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.AppsList24 };
        viewWindows.Click += (_, _) => DoViewProfileWindows(row);
        menu.Items.Add(viewWindows);

        menu.Items.Add(new Separator());

        var rename = new System.Windows.Controls.MenuItem { Header = "Rename\u2026" };
        rename.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Edit24 };
        rename.Click += (_, _) => DoRenameProfile(row);
        menu.Items.Add(rename);

        var delete = new System.Windows.Controls.MenuItem { Header = "Delete" };
        delete.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Delete24 };
        delete.Click += (_, _) => DoDeleteProfile(row);
        menu.Items.Add(delete);

        menu.PlacementTarget = btn;
        menu.Placement       = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen          = true;
    }

    private void DoViewProfileWindows(ProfileRow row)
    {
        var dlg = new ProfileWindowsDialog(row.Source, _storageService) { Owner = this };
        dlg.ShowDialog();
        Refresh();
    }

    private void DoRenameProfile(ProfileRow row)
    {
        // Cancel any other editing rows first
        if (ProfilesList.ItemsSource is System.Collections.Generic.List<ProfileRow> rows)
            foreach (var r in rows) r.IsEditing = false;

        row.EditName  = row.DisplayName;
        row.IsEditing = true;
    }

    // ── Profile inline edit events ────────────────────────────────────────────

    private void OnPrEditKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox tb) return;
        if (tb.Tag is not ProfileRow row) return;
        if (e.Key == Key.Enter)  CommitPrRename(row);
        if (e.Key == Key.Escape) row.IsEditing = false;
    }

    private void OnPrEditSave(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is ProfileRow row)
            CommitPrRename(row);
    }

    private void OnPrEditCancel(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is ProfileRow row)
            row.IsEditing = false;
    }

    private void CommitPrRename(ProfileRow row)
    {
        string newName = row.EditName.Trim();
        if (!string.IsNullOrEmpty(newName) && newName != row.DisplayName)
            _storageService.RenameProfile(row.Source, newName);
        row.IsEditing = false;
        Refresh();
    }

    private void DoDeleteProfile(ProfileRow row)
    {
        _storageService.DeleteProfile(row.Source);
        Refresh();
    }

    // ── Save current layout — inline name card ────────────────────────────────

    private void OnSaveCurrentProfile(object sender, RoutedEventArgs e)
    {
        PrSaveNormal.Visibility = Visibility.Collapsed;
        PrSaveInput.Visibility  = Visibility.Visible;
        PrNameBox.Text = $"Profile {DateTime.Now:HH:mm}";
        PrNameBox.SelectAll();
        PrNameBox.Focus();
    }

    private async void OnPrNameBoxKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)  await CommitPrSave();
        if (e.Key == Key.Escape) CancelPrSave();
    }

    private async void OnPrNameConfirm(object sender, RoutedEventArgs e) => await CommitPrSave();
    private void       OnPrNameCancel (object sender, RoutedEventArgs e) => CancelPrSave();

    private async Task CommitPrSave()
    {
        string name = PrNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) { PrNameBox.Focus(); return; }
        CancelPrSave();
        await Task.Run(() => _coordinator.SaveCurrentLayoutWithName(name));
        Refresh();
    }

    private void CancelPrSave()
    {
        PrSaveInput.Visibility  = Visibility.Collapsed;
        PrSaveNormal.Visibility = Visibility.Visible;
    }
}

/// <summary>Inline dialog to view and remove individual windows in a saved workspace.</summary>
internal sealed class WorkspaceWindowsDialog : FluentWindow
{
    private readonly WorkspaceSnapshot _snapshot;
    private readonly StorageService    _storageService;
    private readonly StackPanel        _listPanel;

    public WorkspaceWindowsDialog(WorkspaceSnapshot snapshot, StorageService storageService)
    {
        _snapshot      = snapshot;
        _storageService = storageService;

        Title  = $"{snapshot.Name} — Saved Windows";
        Width  = 560; Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titleBar = new Wpf.Ui.Controls.TitleBar
        {
            Title       = $"{snapshot.Name} — Saved Windows",
            ShowMinimize = false,
            ShowMaximize = false,
        };
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        // Column headers — 50/50 split
        var headerGrid = new Grid { Margin = new Thickness(16, 10, 16, 4) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        AddHeaderCell(headerGrid, "Application", 0);
        AddHeaderCell(headerGrid, "Window Title", 1);
        Grid.SetRow(headerGrid, 1);

        var headerWrap = new StackPanel();
        headerWrap.Children.Add(headerGrid);
        headerWrap.Children.Add(new Separator { Margin = new Thickness(12, 0, 12, 0), Opacity = 0.4 });

        _listPanel = new StackPanel { Margin = new Thickness(8, 0, 8, 0) };

        var scroll = new ScrollViewer
        {
            Content = new StackPanel
            {
                Children = { headerWrap, _listPanel }
            },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 0),
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        // Footer
        var footer = new Border
        {
            Padding = new Thickness(16, 10, 16, 16),
            Child = new System.Windows.Controls.TextBlock
            {
                Text       = "Removing a window prevents it from being relaunched when this workspace is restored.",
                FontSize   = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextFillColorTertiaryBrush"],
            },
        };
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
        Loaded += (_, _) => RebuildList();
    }

    private static void AddHeaderCell(Grid g, string text, int col)
    {
        var tb = new System.Windows.Controls.TextBlock
        {
            Text       = text,
            FontSize   = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        Grid.SetColumn(tb, col);
        g.Children.Add(tb);
    }

    private void RebuildList()
    {
        _listPanel.Children.Clear();
        foreach (var entry in _snapshot.Entries.ToList())
        {
            var row = new Grid { Margin = new Thickness(4, 1, 4, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });

            // Application name + exe subtitle
            var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 6, 4, 6) };
            nameStack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text         = entry.ProcessName,
                FontSize     = 13,
                FontWeight   = FontWeights.Medium,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground   = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextFillColorPrimaryBrush"],
            });
            if (!string.IsNullOrEmpty(entry.FilePath))
            {
                nameStack.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text         = System.IO.Path.GetFileName(entry.FilePath),
                    FontSize     = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground   = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextFillColorTertiaryBrush"],
                });
            }
            Grid.SetColumn(nameStack, 0);
            row.Children.Add(nameStack);

            // Window title (truncated)
            var titleTb = new System.Windows.Controls.TextBlock
            {
                Text         = entry.Position.TitleSnippet,
                FontSize     = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground   = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextFillColorSecondaryBrush"],
                Margin       = new Thickness(0, 0, 4, 0),
                ToolTip      = entry.Position.TitleSnippet,
            };
            Grid.SetColumn(titleTb, 1);
            row.Children.Add(titleTb);

            // Remove button
            var removeBtn = new Wpf.Ui.Controls.Button
            {
                Icon       = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Delete24, FontSize = 14 },
                Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
                Padding    = new Thickness(6),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip    = "Remove from workspace",
            };
            var captured = entry;
            removeBtn.Click += (_, _) =>
            {
                _snapshot.Entries.Remove(captured);
                _storageService.SaveWorkspace(_snapshot);
                RebuildList();
            };
            Grid.SetColumn(removeBtn, 2);
            row.Children.Add(removeBtn);

            // Hover highlight border
            var border = new Border
            {
                CornerRadius = new CornerRadius(6),
                Child = row,
            };
            border.MouseEnter += (_, _) =>
                border.Background = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["SubtleFillColorSecondaryBrush"];
            border.MouseLeave += (_, _) =>
                border.Background = System.Windows.Media.Brushes.Transparent;

            _listPanel.Children.Add(border);
        }

        if (_snapshot.Entries.Count == 0)
        {
            _listPanel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text              = "No windows remaining in this workspace.",
                FontSize          = 13,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin            = new Thickness(0, 24, 0, 24),
                Foreground        = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextFillColorTertiaryBrush"],
            });
        }
    }
}

/// <summary>Inline dialog to view and remove individual windows in a saved monitor profile.</summary>
internal sealed class ProfileWindowsDialog : FluentWindow
{
    private readonly MonitorProfile _profile;
    private readonly StorageService _storageService;
    private readonly StackPanel     _listPanel;

    public ProfileWindowsDialog(MonitorProfile profile, StorageService storageService)
    {
        _profile        = profile;
        _storageService = storageService;

        Title  = $"{profile.DisplayName} \u2014 Saved Windows";
        Width  = 560; Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titleBar = new Wpf.Ui.Controls.TitleBar
        {
            Title        = $"{profile.DisplayName} \u2014 Saved Windows",
            ShowMinimize = false,
            ShowMaximize = false,
        };
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        var headerGrid = new Grid { Margin = new Thickness(16, 10, 16, 4) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        AddHeaderCell(headerGrid, "Application", 0);
        AddHeaderCell(headerGrid, "Window Title", 1);

        var headerWrap = new StackPanel();
        headerWrap.Children.Add(headerGrid);
        headerWrap.Children.Add(new Separator { Margin = new Thickness(12, 0, 12, 0), Opacity = 0.4 });

        _listPanel = new StackPanel { Margin = new Thickness(8, 0, 8, 0) };

        var scroll = new ScrollViewer
        {
            Content = new StackPanel { Children = { headerWrap, _listPanel } },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var footer = new Border
        {
            Padding = new Thickness(16, 10, 16, 16),
            Child   = new System.Windows.Controls.TextBlock
            {
                Text         = "Removing a window prevents it from being repositioned when this profile is restored.",
                FontSize     = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground   = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextFillColorTertiaryBrush"],
            },
        };
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
        Loaded += (_, _) => RebuildList();
    }

    private static void AddHeaderCell(Grid g, string text, int col)
    {
        var tb = new System.Windows.Controls.TextBlock
        {
            Text       = text,
            FontSize   = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        Grid.SetColumn(tb, col);
        g.Children.Add(tb);
    }

    private void RebuildList()
    {
        _listPanel.Children.Clear();
        foreach (var record in _profile.Windows.ToList())
        {
            var row = new Grid { Margin = new Thickness(4, 1, 4, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });

            var nameCell = new System.Windows.Controls.TextBlock
            {
                Text              = record.ProcessName,
                FontSize          = 13,
                FontWeight        = FontWeights.Medium,
                TextTrimming      = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground        = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextFillColorPrimaryBrush"],
                Margin            = new Thickness(8, 6, 4, 6),
            };
            Grid.SetColumn(nameCell, 0);
            row.Children.Add(nameCell);

            var titleCell = new System.Windows.Controls.TextBlock
            {
                Text              = record.TitleSnippet,
                FontSize          = 12,
                TextTrimming      = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground        = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextFillColorSecondaryBrush"],
                Margin            = new Thickness(0, 0, 4, 0),
                ToolTip           = record.TitleSnippet,
            };
            Grid.SetColumn(titleCell, 1);
            row.Children.Add(titleCell);

            var removeBtn = new Wpf.Ui.Controls.Button
            {
                Icon       = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Delete24, FontSize = 14 },
                Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
                Padding    = new Thickness(6, 6, 6, 6),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip    = "Remove from profile",
            };
            var captured = record;
            removeBtn.Click += (_, _) =>
            {
                _profile.Windows.Remove(captured);
                _storageService.SaveProfile(_profile);
                RebuildList();
            };
            Grid.SetColumn(removeBtn, 2);
            row.Children.Add(removeBtn);

            var border = new Border { CornerRadius = new CornerRadius(6), Child = row };
            border.MouseEnter += (_, _) =>
                border.Background = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["SubtleFillColorSecondaryBrush"];
            border.MouseLeave += (_, _) =>
                border.Background = System.Windows.Media.Brushes.Transparent;

            _listPanel.Children.Add(border);
        }

        if (_profile.Windows.Count == 0)
        {
            _listPanel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text                = "No windows remaining in this profile.",
                FontSize            = 13,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin              = new Thickness(0, 24, 0, 24),
                Foreground          = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextFillColorTertiaryBrush"],
            });
        }
    }
}

/// <summary>Inline rename dialog — avoids a separate XAML file.</summary>
internal sealed class RenameDialog : FluentWindow
{
    public string NewName => _box.Text;
    private readonly System.Windows.Controls.TextBox _box;

    public RenameDialog(string current, string dialogTitle = "Rename")
    {
        Title = dialogTitle;
        Width = 380; Height = 180;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ExtendsContentIntoTitleBar = true;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // title bar
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // content

        var titleBar = new Wpf.Ui.Controls.TitleBar
        {
            Title = dialogTitle,
            ShowMinimize = false,
            ShowMaximize = false,
        };
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        var sp = new StackPanel { Margin = new Thickness(20, 8, 20, 20) };
        Grid.SetRow(sp, 1);

        sp.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "New name:",
            Margin = new Thickness(0, 0, 0, 6),
            FontSize = 13,
        });

        _box = new System.Windows.Controls.TextBox
        {
            Text = current,
            FontSize = 13,
            Padding = new Thickness(8),
        };
        sp.Children.Add(_box);

        var btn = new Wpf.Ui.Controls.Button
        {
            Content = "Rename",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            Margin = new Thickness(0, 12, 0, 0),
            IsDefault = true,
        };
        btn.Click += (_, _) => { DialogResult = true; };
        sp.Children.Add(btn);

        root.Children.Add(sp);
        Content = root;

        Loaded += (_, _) => { _box.SelectAll(); _box.Focus(); };
    }
}
