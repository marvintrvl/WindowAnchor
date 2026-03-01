using System;
using System.Collections.Generic;
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
    private readonly SettingsService   _settingsService;
    private readonly MonitorService    _monitorService;
    private bool _suppressToggle;

    // ── View-model rows ──────────────────────────────────────────────────────

    // These are internal (not private) so SonarLint recognises the public binding properties
    // as accessible outside this class — they are consumed via XAML {Binding ...} reflection.
    internal sealed class WorkspaceRow : INotifyPropertyChanged
    {
        public WorkspaceSnapshot Source    { get; init; } = null!;
        public string Name                 => Source.Name;
        public string FingerprintLabel     => Source.MonitorFingerprint;
        public string SavedAtDisplay       => Source.SavedAt.ToLocalTime().ToString("g");
        public int    EntryCount           => Source.Entries.Count;
        public string MonitorCountLabel    => Source.Monitors.Count > 0 ? $"{Source.Monitors.Count}" : "—";
        public string SavedWithFilesLabel  => Source.SavedWithFiles ? "Yes" : "—";

        private bool   _isEditing;
        private string _editName = "";
        public bool   IsEditing { get => _isEditing; set { _isEditing = value; OnPropertyChanged(); } }
        public string EditName  { get => _editName;  set { _editName  = value; OnPropertyChanged(); } }

        // ── Display order & default indicator ─────────────────────────────
        private int _position;
        public int Position
        {
            get => _position;
            set { _position = value; OnPropertyChanged(); OnPropertyChanged(nameof(SlotLabel)); OnPropertyChanged(nameof(SlotBadgeVisibility)); }
        }
        public string     SlotLabel           => $"#{_position}";
        public Visibility SlotBadgeVisibility => _position <= 3 ? Visibility.Visible : Visibility.Collapsed;

        private bool _isDefault;
        public bool IsDefault
        {
            get => _isDefault;
            set { _isDefault = value; OnPropertyChanged(); OnPropertyChanged(nameof(DefaultStarVisibility)); }
        }
        public Visibility DefaultStarVisibility => _isDefault ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // ── Hotkey row view-model ────────────────────────────────────────────────

    internal sealed class HotkeyRow : INotifyPropertyChanged
    {
        public string ActionId   { get; init; } = "";
        public string ActionName { get; init; } = "";

        private ModifierKeys _modifiers;
        public ModifierKeys Modifiers
        {
            get => _modifiers;
            set { _modifiers = value; OnPropertyChanged(); DisplayShortcut = HotkeyService.FormatShortcut(value, _key); }
        }

        private Key _key;
        public Key Key
        {
            get => _key;
            set { _key = value; OnPropertyChanged(); DisplayShortcut = HotkeyService.FormatShortcut(_modifiers, value); }
        }

        private string _displayShortcut = "";
        public string DisplayShortcut
        {
            get => _displayShortcut;
            set { _displayShortcut = value; OnPropertyChanged(); }
        }

        private bool _isRecording;
        public bool IsRecording
        {
            get => _isRecording;
            set { _isRecording = value; OnPropertyChanged(); }
        }

        private bool _isCustom;
        public bool IsCustom
        {
            get => _isCustom;
            set { _isCustom = value; OnPropertyChanged(); OnPropertyChanged(nameof(ResetVisibility)); }
        }
        public Visibility ResetVisibility => _isCustom ? Visibility.Visible : Visibility.Collapsed;

        // Default values for reset
        public ModifierKeys DefaultModifiers { get; init; }
        public Key          DefaultKey       { get; init; }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    private readonly List<HotkeyRow> _hotkeyRows = new();
    private HotkeyRow? _recordingRow;

    // ── Monitor row view-model ───────────────────────────────────────────────

    internal sealed class MonitorRow : INotifyPropertyChanged
    {
        public string MonitorId       { get; init; } = "";
        public string HardwareName    { get; init; } = "";
        public string IndexLabel      { get; init; } = "";
        public string ResolutionLabel { get; init; } = "";

        // ── Persisted alias (shown in view mode) ──────────────────────────
        private string _alias = "";
        public string Alias
        {
            get => _alias;
            set { _alias = value; OnPropertyChanged(); OnPropertyChanged(nameof(AliasDisplay)); }
        }

        /// <summary>Displayed in view mode; falls back to an em-dash when no alias is set.</summary>
        public string AliasDisplay => string.IsNullOrWhiteSpace(_alias) ? "\u2014" : _alias;

        // ── Inline editing ────────────────────────────────────────────────
        private string _editAlias = "";
        public string EditAlias
        {
            get => _editAlias;
            set { _editAlias = value; OnPropertyChanged(); }
        }

        private bool _isEditing;
        public bool IsEditing
        {
            get => _isEditing;
            set
            {
                _isEditing = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ViewVisibility));
                OnPropertyChanged(nameof(EditVisibility));
            }
        }

        public Visibility ViewVisibility => _isEditing ? Visibility.Collapsed : Visibility.Visible;
        public Visibility EditVisibility => _isEditing ? Visibility.Visible  : Visibility.Collapsed;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    private readonly List<MonitorRow> _monitorRows = new();

    // ── Constructor ──────────────────────────────────────────────────────────

    public SettingsWindow(
        WorkspaceService workspaceService,
        StorageService   storageService,
        LayoutCoordinator coordinator,
        SettingsService  settingsService,
        MonitorService   monitorService)
    {
        _workspaceService = workspaceService;
        _storageService   = storageService;
        _coordinator      = coordinator;
        _settingsService  = settingsService;
        _monitorService   = monitorService;
        InitializeComponent();
        PreviewKeyDown += OnHotkeyRecordKeyDown;
        Loaded += (_, _) =>
        {
            // Set toggle without firing handler
            _suppressToggle = true;
            AutostartToggle.IsChecked = AutostartService.IsEnabled();
            _suppressToggle = false;

            // Populate startup behavior controls
            InitialiseStartupBehaviorUI();
            InitialiseHotkeyUI();
            InitialiseMonitorUI();

            Refresh();
        };
    }

    // ── Startup-behavior UI ──────────────────────────────────────────────────

    private void InitialiseStartupBehaviorUI()
    {
        _suppressToggle = true;

        var settings = _settingsService.Settings;
        int comboIndex = settings.StartupBehavior switch
        {
            StartupBehavior.RestoreDefault  => 1,
            StartupBehavior.RestoreLastUsed => 2,
            StartupBehavior.AskUser         => 3,
            _                               => 0,
        };
        StartupBehaviorCombo.SelectedIndex = comboIndex;

        RefreshDefaultWorkspaceCombo();
        DefaultWorkspacePanel.Visibility = settings.StartupBehavior == StartupBehavior.RestoreDefault
            ? Visibility.Visible
            : Visibility.Collapsed;

        _suppressToggle = false;
    }

    private void RefreshDefaultWorkspaceCombo()
    {
        DefaultWorkspaceCombo.Items.Clear();
        var workspaces = _workspaceService.GetAllWorkspaces().OrderByDescending(w => w.SavedAt);
        int selectedIdx = -1;
        int idx = 0;
        foreach (var ws in workspaces)
        {
            DefaultWorkspaceCombo.Items.Add(new ComboBoxItem { Content = ws.Name, Tag = ws.Name });
            if (ws.Name == _settingsService.Settings.DefaultWorkspaceName)
                selectedIdx = idx;
            idx++;
        }
        if (selectedIdx >= 0) DefaultWorkspaceCombo.SelectedIndex = selectedIdx;
        else if (DefaultWorkspaceCombo.Items.Count > 0) DefaultWorkspaceCombo.SelectedIndex = 0;
    }

    private void OnStartupBehaviorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressToggle) return;
        if (StartupBehaviorCombo.SelectedItem is not ComboBoxItem item) return;

        var behavior = (item.Tag as string) switch
        {
            "RestoreDefault"  => StartupBehavior.RestoreDefault,
            "RestoreLastUsed" => StartupBehavior.RestoreLastUsed,
            "AskUser"         => StartupBehavior.AskUser,
            _                 => StartupBehavior.None,
        };

        _settingsService.Settings.StartupBehavior = behavior;
        _settingsService.Save();

        DefaultWorkspacePanel.Visibility = behavior == StartupBehavior.RestoreDefault
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (behavior == StartupBehavior.RestoreDefault)
            RefreshDefaultWorkspaceCombo();
    }

    private void OnDefaultWorkspaceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressToggle) return;
        if (DefaultWorkspaceCombo.SelectedItem is ComboBoxItem item)
        {
            _settingsService.Settings.DefaultWorkspaceName = item.Tag as string;
            _settingsService.Save();
        }
    }

    // ── Hotkey UI ────────────────────────────────────────────────────────────

    private void InitialiseHotkeyUI()
    {
        _suppressToggle = true;
        HotkeysToggle.IsChecked = _settingsService.Settings.HotkeysEnabled;
        _suppressToggle = false;

        // Build HotkeyRow list from resolved shortcuts (defaults + custom overrides)
        _hotkeyRows.Clear();
        var resolved = HotkeyService.GetResolvedShortcuts(_settingsService.Settings);
        for (int i = 0; i < HotkeyService.Defaults.Length; i++)
        {
            var def = HotkeyService.Defaults[i];
            var res = resolved[i];
            bool isCustom = res.Modifiers != def.Modifiers || res.Key != def.Key;
            _hotkeyRows.Add(new HotkeyRow
            {
                ActionId         = def.ActionId,
                ActionName       = def.ActionName,
                Modifiers        = res.Modifiers,
                Key              = res.Key,
                DisplayShortcut  = res.DisplayShortcut,
                DefaultModifiers = def.Modifiers,
                DefaultKey       = def.Key,
                IsCustom         = isCustom,
            });
        }
        HotkeyList.ItemsSource = _hotkeyRows;
    }

    private void OnHotkeysToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        bool enabled = HotkeysToggle.IsChecked == true;
        _settingsService.Settings.HotkeysEnabled = enabled;
        _settingsService.Save();

        // Notify the app to register/unregister hotkeys
        if (System.Windows.Application.Current is App app)
            app.ApplyHotkeySettings();
    }

    // ── Hotkey recording ─────────────────────────────────────────────────────

    private void OnChangeHotkey(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.Tag is not HotkeyRow row) return;

        // Cancel any existing recording
        CancelHotkeyRecording();

        _recordingRow = row;
        row.IsRecording = true;
        row.DisplayShortcut = "Press keys\u2026";
    }

    private void OnResetHotkey(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.Tag is not HotkeyRow row) return;

        CancelHotkeyRecording();

        row.Modifiers = row.DefaultModifiers;
        row.Key       = row.DefaultKey;
        row.IsCustom  = false;

        SaveCustomHotkeys();
    }

    private void OnHotkeyRecordKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_recordingRow == null) return;

        // Escape → cancel recording
        if (e.Key == Key.Escape)
        {
            CancelHotkeyRecording();
            e.Handled = true;
            return;
        }

        // Resolve the actual key (Alt combos send Key.System)
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Ignore standalone modifier presses
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        var mods = Keyboard.Modifiers;
        // Require at least one modifier
        if (mods == ModifierKeys.None) return;

        _recordingRow.Modifiers = mods;
        _recordingRow.Key       = key;
        _recordingRow.IsRecording = false;
        _recordingRow.IsCustom  = mods != _recordingRow.DefaultModifiers || key != _recordingRow.DefaultKey;
        _recordingRow = null;

        SaveCustomHotkeys();
        e.Handled = true;
    }

    private void CancelHotkeyRecording()
    {
        if (_recordingRow == null) return;
        // Restore previous display text
        _recordingRow.DisplayShortcut = HotkeyService.FormatShortcut(_recordingRow.Modifiers, _recordingRow.Key);
        _recordingRow.IsRecording = false;
        _recordingRow = null;
    }

    private void SaveCustomHotkeys()
    {
        // Build list of custom (non-default) bindings
        var customs = new List<HotkeyBinding>();
        foreach (var row in _hotkeyRows)
        {
            if (row.IsCustom)
            {
                customs.Add(new HotkeyBinding
                {
                    ActionId  = row.ActionId,
                    Modifiers = HotkeyService.FormatModifiers(row.Modifiers),
                    KeyName   = row.Key.ToString(),
                });
            }
        }
        _settingsService.Settings.CustomHotkeys = customs.Count > 0 ? customs : null;
        _settingsService.Save();

        // Re-register hotkeys with new bindings
        if (System.Windows.Application.Current is App app)
            app.ApplyHotkeySettings();
    }

    // ── Monitor renaming UI ──────────────────────────────────────────────────

    private void InitialiseMonitorUI()
    {
        _monitorRows.Clear();
        var monitors = _monitorService.GetCurrentMonitors();
        foreach (var m in monitors)
        {
            string primary = m.IsPrimary ? " (Primary)" : "";
            _monitorRows.Add(new MonitorRow
            {
                MonitorId       = m.MonitorId,
                HardwareName    = m.FriendlyName,
                IndexLabel      = $"#{m.Index + 1}",
                ResolutionLabel = $"{m.WidthPixels}\u00d7{m.HeightPixels}{primary}",
                Alias           = _settingsService.ResolveMonitorName(m.MonitorId, "") == m.FriendlyName
                    ? ""
                    : _settingsService.ResolveMonitorName(m.MonitorId, ""),
            });
        }
        MonitorList.ItemsSource = _monitorRows;
    }

    private void OnMonitorEditClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.Tag is not MonitorRow row) return;

        // Cancel any other row currently being edited
        foreach (var r in _monitorRows)
            if (r != row) r.IsEditing = false;

        row.EditAlias   = row.Alias;   // seed the edit box with the current value
        row.IsEditing   = true;

        // Focus the TextBox on the next layout pass
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            if (MonitorList.ItemContainerGenerator.ContainerFromItem(row) is
                System.Windows.Controls.ContentPresenter cp)
            {
                var tb = FindChild<System.Windows.Controls.TextBox>(cp);
                tb?.Focus();
                tb?.SelectAll();
            }
        });
    }

    private void OnMonitorAliasSaveClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.Tag is not MonitorRow row) return;
        CommitMonitorAlias(row);
    }

    private void OnMonitorAliasCancelClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.Tag is not MonitorRow row) return;
        row.IsEditing = false;
    }

    private void OnMonitorAliasEditKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox tb) return;
        if (tb.Tag is not MonitorRow row) return;

        if (e.Key == Key.Enter)  { CommitMonitorAlias(row); e.Handled = true; }
        if (e.Key == Key.Escape) { row.IsEditing = false;   e.Handled = true; }
    }

    private void CommitMonitorAlias(MonitorRow row)
    {
        string? alias = string.IsNullOrWhiteSpace(row.EditAlias) ? null : row.EditAlias.Trim();
        row.Alias     = alias ?? "";
        row.IsEditing = false;
        _settingsService.SetMonitorAlias(row.MonitorId, alias);
    }

    /// <summary>Walks the visual tree to find a child of type <typeparamref name="T"/>.</summary>
    private static T? FindChild<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var result = FindChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    // ── Refresh ──────────────────────────────────────────────────────────────

    private void Refresh()
    {
        var all = _workspaceService.GetAllWorkspaces();
        var ordered = GetOrderedWorkspaces(all);
        string? defaultName = _settingsService.Settings.DefaultWorkspaceName;

        var wsRows = new List<WorkspaceRow>();
        for (int i = 0; i < ordered.Count; i++)
        {
            var ws = ordered[i];
            wsRows.Add(new WorkspaceRow
            {
                Source    = ws,
                Position  = i + 1,
                IsDefault = !string.IsNullOrEmpty(defaultName)
                    && ws.Name.Equals(defaultName, StringComparison.OrdinalIgnoreCase),
            });
        }

        WorkspacesList.ItemsSource = wsRows;
        WorkspaceCountText.Text    = $"{wsRows.Count} workspace{(wsRows.Count == 1 ? "" : "s")} saved";
        WorkspacesEmpty.Visibility = wsRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Returns workspaces in the user's preferred order.
    /// Names in <see cref="AppSettings.WorkspaceOrder"/> come first (in order),
    /// followed by any remaining workspaces sorted by save date.
    /// </summary>
    private List<WorkspaceSnapshot> GetOrderedWorkspaces(List<WorkspaceSnapshot> all)
    {
        var order = _settingsService.Settings.WorkspaceOrder;
        if (order == null || order.Count == 0)
            return all.OrderByDescending(w => w.SavedAt).ToList();

        var result = new List<WorkspaceSnapshot>();
        foreach (var name in order)
        {
            var ws = all.FirstOrDefault(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (ws != null) result.Add(ws);
        }
        // Append workspaces not listed in the order
        foreach (var ws in all.OrderByDescending(w => w.SavedAt))
        {
            if (!result.Any(r => r.Name.Equals(ws.Name, StringComparison.OrdinalIgnoreCase)))
                result.Add(ws);
        }
        return result;
    }

    /// <summary>Persists the current display order into settings.</summary>
    private void PersistWorkspaceOrder()
    {
        if (WorkspacesList.ItemsSource is not List<WorkspaceRow> rows) return;
        _settingsService.Settings.WorkspaceOrder = rows.Select(r => r.Name).ToList();
        _settingsService.Save();
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

    private async void OnSaveNewWorkspace(object sender, RoutedEventArgs e)
    {
        var windowPreview = await Task.Run(() => _workspaceService.GetWindowPreviewForDialog());
        var dialog = new SaveWorkspaceDialog(windowPreview, _settingsService) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        // Read all dialog properties on the UI thread before entering Task.Run.
        var name            = dialog.WorkspaceName;
        var saveFiles       = dialog.SaveFiles;
        var selectedWindows = dialog.SelectedWindows;

        // Show progress window when file detection is enabled (can take several seconds).
        SaveProgressWindow? progressWindow = null;
        if (saveFiles)
        {
            progressWindow = new SaveProgressWindow(name) { Owner = this };
            progressWindow.Show();
        }

        var progress = progressWindow != null
            ? new Progress<SaveProgressReport>(r => progressWindow.ApplyReport(r))
            : (IProgress<SaveProgressReport>?)null;

        IsEnabled = false;
        try
        {
            await Task.Run(() => _workspaceService.TakeSnapshot(name, saveFiles,
                selectedWindows: selectedWindows, progress: progress));
            Refresh();
        }
        catch (Exception ex)
        {
            AppLogger.Error("OnSaveNewWorkspace: TakeSnapshot failed", ex);
            System.Windows.MessageBox.Show(
                $"Failed to save workspace: {ex.Message}",
                "WindowAnchor",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
        finally
        {
            IsEnabled = true;
            progressWindow?.Close();
        }
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

        var switchWs = new System.Windows.Controls.MenuItem { Header = "Switch to Workspace" };
        switchWs.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowSwap24 };
        switchWs.Click += (_, _) => DoSwitchWorkspace(row);
        menu.Items.Add(switchWs);

        // Only offer Selective Restore when the workspace has more than one monitor
        if (row.Source.Monitors.Count > 1)
        {
            var restoreSelective = new System.Windows.Controls.MenuItem { Header = "Restore Selected Monitors…" };
            restoreSelective.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.DesktopCheckmark24 };
            restoreSelective.Click += (_, _) => DoSelectiveRestore(row);
            menu.Items.Add(restoreSelective);
        }

        var viewWindows = new System.Windows.Controls.MenuItem { Header = "View & Edit Windows…" };
        viewWindows.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.AppsList24 };
        viewWindows.Click += (_, _) => DoViewWindows(row);
        menu.Items.Add(viewWindows);

        menu.Items.Add(new Separator());

        // ── Reorder: Move Up / Move Down ─────────────────────────────────
        if (row.Position > 1)
        {
            var moveUp = new System.Windows.Controls.MenuItem { Header = "Move Up" };
            moveUp.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowUp24 };
            moveUp.Click += (_, _) => MoveWorkspace(row, -1);
            menu.Items.Add(moveUp);
        }

        if (WorkspacesList.ItemsSource is List<WorkspaceRow> rows && row.Position < rows.Count)
        {
            var moveDown = new System.Windows.Controls.MenuItem { Header = "Move Down" };
            moveDown.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowDown24 };
            moveDown.Click += (_, _) => MoveWorkspace(row, +1);
            menu.Items.Add(moveDown);
        }

        menu.Items.Add(new Separator());

        // ── Set / remove default workspace ───────────────────────────────
        if (row.IsDefault)
        {
            var clearDefault = new System.Windows.Controls.MenuItem { Header = "Remove as Default" };
            clearDefault.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.StarOff24 };
            clearDefault.Click += (_, _) =>
            {
                _settingsService.Settings.DefaultWorkspaceName = null;
                _settingsService.Save();
                Refresh();
            };
            menu.Items.Add(clearDefault);
        }
        else
        {
            var setDefault = new System.Windows.Controls.MenuItem { Header = "Set as Default" };
            setDefault.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Star24 };
            setDefault.Click += (_, _) =>
            {
                _settingsService.Settings.DefaultWorkspaceName = row.Name;
                _settingsService.Save();
                Refresh();
            };
            menu.Items.Add(setDefault);
        }

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

    private void MoveWorkspace(WorkspaceRow row, int direction)
    {
        if (WorkspacesList.ItemsSource is not List<WorkspaceRow> rows) return;
        int oldIdx = rows.IndexOf(row);
        int newIdx = oldIdx + direction;
        if (newIdx < 0 || newIdx >= rows.Count) return;

        // Swap in the list
        (rows[oldIdx], rows[newIdx]) = (rows[newIdx], rows[oldIdx]);

        // Update positions
        for (int i = 0; i < rows.Count; i++)
            rows[i].Position = i + 1;

        // Rebind to force UI update
        WorkspacesList.ItemsSource = null;
        WorkspacesList.ItemsSource = rows;

        PersistWorkspaceOrder();
    }

    private void DoSelectiveRestore(WorkspaceRow row)
    {
        string currentFp = _workspaceService.GetCurrentMonitorFingerprint();
        bool mismatch = currentFp != row.Source.MonitorFingerprint;

        var dlg = new SelectiveRestoreDialog(row.Source, mismatch, _settingsService) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.SelectedMonitorIds is { Count: > 0 } ids)
            _ = _coordinator.RestoreWorkspaceSelectiveAsync(row.Source, ids);
    }

    private void DoSwitchWorkspace(WorkspaceRow row)
    {
        var result = System.Windows.MessageBox.Show(
            $"Switch to \u201c{row.Name}\u201d?\n\n" +
            "All open windows will be asked to close. Apps with unsaved work will " +
            "prompt you to save before closing.\n\n" +
            "The workspace will be restored once every window has closed.",
            "Switch Workspace",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Question);
        if (result != System.Windows.MessageBoxResult.OK) return;

        _ = _coordinator.SwitchWorkspaceAsync(row.Source);
    }

    private void DoViewWindows(WorkspaceRow row)
    {
        var dlg = new WorkspaceWindowsDialog(row.Source, _storageService, _settingsService) { Owner = this };
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
}

/// <summary>Inline dialog to view and remove individual windows in a saved workspace.</summary>
internal sealed class WorkspaceWindowsDialog : FluentWindow
{
    private readonly WorkspaceSnapshot _snapshot;
    private readonly StorageService    _storageService;
    private readonly SettingsService?  _settingsService;
    private readonly StackPanel        _listPanel;

    public WorkspaceWindowsDialog(WorkspaceSnapshot snapshot, StorageService storageService, SettingsService? settingsService = null)
    {
        _snapshot        = snapshot;
        _storageService  = storageService;
        _settingsService = settingsService;

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
        if (_snapshot.Monitors.Count > 0)
        {
            foreach (var (monitor, entries) in _snapshot.EntriesByMonitor())
            {
                var entriesList = entries.ToList();
                if (entriesList.Count == 0) continue;

                var header = new System.Windows.Controls.TextBlock
                {
                    Text       = $"{(_settingsService?.ResolveMonitorName(monitor.MonitorId, monitor.FriendlyName) ?? monitor.FriendlyName)}  ({entriesList.Count} window{(entriesList.Count == 1 ? "" : "s")})",
                    FontSize   = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["SystemAccentColorPrimaryBrush"],
                    Margin     = new Thickness(12, 8, 0, 2),
                };
                _listPanel.Children.Add(header);

                foreach (var entry in entriesList)
                    AddEntryRow(entry);
            }
        }
        else
        {
            foreach (var entry in _snapshot.Entries.ToList())
                AddEntryRow(entry);
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

    private void AddEntryRow(WorkspaceEntry entry)
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
}
