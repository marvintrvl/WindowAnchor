using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using Wpf.Ui.Controls;
using WindowAnchor.Models;

namespace WindowAnchor.UI;

public partial class SaveWorkspaceDialog : FluentWindow
{
    // ── Public results (read after DialogResult = true) ───────────────────────
    public string           WorkspaceName     => WorkspaceNameInput.Text.Trim();
    public bool             SaveFiles         => SaveFilesCheckBox.IsChecked == true;
    public HashSet<string>? SelectedMonitorIds
    {
        get
        {
            if (_monitorItems.Count == 0) return null;   // no monitors → save all
            var selected = _monitorItems
                .Where(m => m.IsSelected)
                .Select(m => m.MonitorId)
                .ToHashSet();
            // if every monitor is selected, return null (means "all") so callers
            // don't need to build a filter set
            return selected.Count == _monitorItems.Count ? null : selected;
        }
    }

    // ── View-model ────────────────────────────────────────────────────────────

    public sealed class MonitorCheckItem : INotifyPropertyChanged
    {
        public string MonitorId      { get; init; } = "";
        public string DisplayHeader  { get; init; } = "";
        public string ResolutionLabel{ get; init; } = "";
        public string WindowCountLabel { get; init; } = "";

        private bool _isSelected = true;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    private readonly List<MonitorCheckItem> _monitorItems = new();

    // ── Constructor ───────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the Save Workspace dialog.
    /// </summary>
    /// <param name="monitors">
    ///   Monitors to show as checkboxes.  Each tuple carries the
    ///   <see cref="MonitorInfo"/> and the number of windows currently on that monitor.
    ///   Pass an empty list (or omit) when the caller cannot enumerate monitors.
    /// </param>
    public SaveWorkspaceDialog(IEnumerable<(MonitorInfo Monitor, int WindowCount)>? monitors = null)
    {
        InitializeComponent();

        if (monitors != null)
        {
            foreach (var (mon, count) in monitors)
            {
                string primaryTag = mon.IsPrimary ? " (Primary)" : "";
                _monitorItems.Add(new MonitorCheckItem
                {
                    MonitorId       = mon.MonitorId,
                    DisplayHeader   = $"Monitor {mon.Index + 1}: {mon.FriendlyName}{primaryTag}",
                    ResolutionLabel = $"{mon.WidthPixels}\u00d7{mon.HeightPixels}",
                    WindowCountLabel= $"{count} window{(count == 1 ? "" : "s")}",
                });
            }
        }

        MonitorList.ItemsSource = _monitorItems;
        Loaded += (_, _) => WorkspaceNameInput.Focus();
    }

    // ── Button handlers ───────────────────────────────────────────────────────

    private void OnSave(object sender, RoutedEventArgs e)   => TryCommit();
    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) TryCommit();
        if (e.Key == System.Windows.Input.Key.Escape) Close();
    }

    private void OnSelectAll  (object sender, RoutedEventArgs e) => SetAll(true);
    private void OnDeselectAll(object sender, RoutedEventArgs e) => SetAll(false);

    private void SetAll(bool value)
    {
        foreach (var item in _monitorItems) item.IsSelected = value;
    }

    private void TryCommit()
    {
        if (string.IsNullOrWhiteSpace(WorkspaceNameInput.Text))
        {
            System.Windows.MessageBox.Show(
                "Please enter a workspace name.", "Name Required",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        if (_monitorItems.Count > 0 && _monitorItems.All(m => !m.IsSelected))
        {
            System.Windows.MessageBox.Show(
                "Please select at least one monitor to save.", "No Monitor Selected",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }
}
