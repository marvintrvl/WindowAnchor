using System;
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
    // ── Public results (read after DialogResult = true) ───────────────────
    public string WorkspaceName  => WorkspaceNameInput.Text.Trim();
    public bool   SaveFiles      => SaveFilesCheckBox.IsChecked == true;

    /// <summary>
    /// Returns the list of <see cref="WindowRecord"/>s the user checked.
    /// Pass this to <see cref="Services.WorkspaceService.TakeSnapshot"/> as <c>selectedWindows</c>.
    /// </summary>
    public List<WindowRecord> SelectedWindows =>
        _monitorGroups
            .SelectMany(g => g.Windows)
            .Where(w => w.IsSelected)
            .Select(w => w.Record)
            .ToList();

    // ── Smart-exclusion lists ─────────────────────────────────────────────

    /// <summary>Process names that are auto-unchecked by default (password managers etc.).</summary>
    private static readonly HashSet<string> AutoExcludeProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "keepass", "keepassxc", "1password", "bitwarden", "lastpass",
        "dashlane", "keeper", "roboform", "enpass",
    };

    /// <summary>Title substrings that indicate a private / incognito window.</summary>
    private static readonly string[] PrivateTitlePatterns = new[]
    {
        "InPrivate",          // Edge
        "Incognito",          // Chrome / Brave
        "Private Browsing",   // Firefox
        "Private Window",     // Opera
    };

    // ── View-models ───────────────────────────────────────────────────────

    public sealed class MonitorWindowGroup
    {
        public string MonitorHeader { get; init; } = "";
        public List<WindowCheckItem> Windows { get; init; } = new();
    }

    public sealed class WindowCheckItem : INotifyPropertyChanged
    {
        public WindowRecord Record       { get; init; } = null!;
        public string       DisplayName  { get; init; } = "";
        public string       TitleSnippet { get; init; } = "";

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

    private readonly List<MonitorWindowGroup> _monitorGroups = new();

    // ── Constructor ───────────────────────────────────────────────────────

    /// <param name="windowData">
    ///   Monitor + windows data returned by
    ///   <see cref="Services.WorkspaceService.GetWindowPreviewForDialog"/>.
    /// </param>
    /// <param name="settingsService">
    ///   Optional; when supplied, monitor aliases are used instead of hardware names.
    /// </param>
    public SaveWorkspaceDialog(
        List<(MonitorInfo Monitor, List<WindowRecord> Windows)> windowData,
        Services.SettingsService? settingsService = null)
    {
        InitializeComponent();

        foreach (var (mon, windows) in windowData)
        {
            string primaryTag = mon.IsPrimary ? " (Primary)" : "";
            string monName = settingsService?.ResolveMonitorName(mon.MonitorId, mon.FriendlyName)
                             ?? mon.FriendlyName;
            var group = new MonitorWindowGroup
            {
                MonitorHeader = $"Monitor {mon.Index + 1}: {monName}{primaryTag}  \u2014  " +
                                $"{mon.WidthPixels}\u00d7{mon.HeightPixels}  ({windows.Count} window{(windows.Count == 1 ? "" : "s")})",
                Windows = windows.Select(w => new WindowCheckItem
                {
                    Record       = w,
                    DisplayName  = w.ProcessName,
                    TitleSnippet = w.TitleSnippet,
                    IsSelected   = !ShouldAutoExclude(w),
                }).ToList(),
            };
            _monitorGroups.Add(group);
        }

        WindowGroupList.ItemsSource = _monitorGroups;
        Loaded += (_, _) => WorkspaceNameInput.Focus();
    }

    // ── Smart exclusion ───────────────────────────────────────────────────

    private static bool ShouldAutoExclude(WindowRecord w)
    {
        // Password managers
        if (AutoExcludeProcesses.Contains(w.ProcessName))
            return true;

        // Incognito / private browser windows
        foreach (var pattern in PrivateTitlePatterns)
        {
            if (w.TitleSnippet.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    // ── Button handlers ───────────────────────────────────────────────────

    private void OnSave(object sender, RoutedEventArgs e)   => TryCommit();
    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) TryCommit();
        if (e.Key == System.Windows.Input.Key.Escape) Close();
    }

    private void OnSelectAllWindows(object sender, RoutedEventArgs e) => SetAllWindows(true);
    private void OnDeselectAllWindows(object sender, RoutedEventArgs e) => SetAllWindows(false);

    private void SetAllWindows(bool value)
    {
        foreach (var g in _monitorGroups)
            foreach (var w in g.Windows)
                w.IsSelected = value;
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

        bool anySelected = _monitorGroups.Any(g => g.Windows.Any(w => w.IsSelected));
        if (!anySelected)
        {
            System.Windows.MessageBox.Show(
                "Please select at least one window to save.", "No Windows Selected",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }
}
