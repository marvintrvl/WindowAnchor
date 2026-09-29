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
    /// Pass this to <see cref="Services.WorkspaceService.CaptureWorkspaceAsync"/> as <c>selectedWindows</c>.
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
        public string GroupHeader { get; init; } = "";
        public List<WindowCheckItem> Windows { get; init; } = new();
    }

    public sealed class WindowCheckItem : INotifyPropertyChanged
    {
        public WindowRecord Record       { get; init; } = null!;
        public string       DisplayName  { get; init; } = "";
        public string       TitleSnippet { get; init; } = "";
        public bool IsTerminal => Record.ProcessName.Equals("windowsterminal", StringComparison.OrdinalIgnoreCase);
        public string TerminalSummary => IsTerminal
            ? $"{Record.TerminalTabs.Count} detected tab(s) · configure profiles and directories"
            : "";

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
        Services.SettingsService? settingsService = null,
        IReadOnlyList<Services.VirtualDesktopInfo>? desktops = null)
    {
        InitializeComponent();

        _monitorGroups.AddRange(BuildGroups(windowData, settingsService, desktops));

        WindowGroupList.ItemsSource = _monitorGroups;
        Loaded += (_, _) => WorkspaceNameInput.Focus();
    }

    internal static List<MonitorWindowGroup> BuildGroups(
        List<(MonitorInfo Monitor, List<WindowRecord> Windows)> windowData,
        Services.SettingsService? settingsService = null,
        IReadOnlyList<Services.VirtualDesktopInfo>? desktops = null)
    {
        WindowRecord[] allWindows = windowData.SelectMany(item => item.Windows).ToArray();
        bool showVirtualDesktops = desktops?.Count > 1 || allWindows.Any(window =>
            window.IsOnCurrentVirtualDesktop == false &&
            Guid.TryParse(window.VirtualDesktopId, out _));

        if (!showVirtualDesktops)
        {
            return windowData.Select(item => CreateGroup(
                item.Monitor,
                item.Windows,
                desktopLabel: null,
                settingsService)).ToList();
        }

        string? currentDesktopId = allWindows.FirstOrDefault(window =>
            window.IsOnCurrentVirtualDesktop == true &&
            Guid.TryParse(window.VirtualDesktopId, out _))?.VirtualDesktopId;
        string[] desktopIds = allWindows
            .Select(window => window.VirtualDesktopId)
            .Where(id => Guid.TryParse(id, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var desktopLabels = new List<(string Id, string Label)>();
        if (desktops is not null)
            desktopLabels.AddRange(desktops.Select(desktop => (
                desktop.Id.ToString("D"), desktop.Name + (desktop.IsCurrent ? " (Current)" : ""))));
        if (!string.IsNullOrWhiteSpace(currentDesktopId) &&
            !desktopLabels.Any(desktop => string.Equals(desktop.Id, currentDesktopId, StringComparison.OrdinalIgnoreCase)))
            desktopLabels.Add((currentDesktopId, "Current virtual desktop"));
        int inactiveIndex = 0;
        int observedIndex = 0;
        foreach (string desktopId in desktopIds.Where(id => !string.Equals(
                     id,
                     currentDesktopId,
                     StringComparison.OrdinalIgnoreCase)))
        {
            if (desktopLabels.Any(desktop => string.Equals(desktop.Id, desktopId, StringComparison.OrdinalIgnoreCase)))
                continue;
            bool inactive = allWindows.Any(window =>
                string.Equals(window.VirtualDesktopId, desktopId, StringComparison.OrdinalIgnoreCase) &&
                window.IsOnCurrentVirtualDesktop == false);
            desktopLabels.Add((
                desktopId,
                inactive
                    ? $"Inactive virtual desktop {++inactiveIndex}"
                    : $"Observed virtual desktop {++observedIndex}"));
        }

        var groups = new List<MonitorWindowGroup>();
        foreach ((string desktopId, string desktopLabel) in desktopLabels)
        {
            int groupCountBefore = groups.Count;
            foreach ((MonitorInfo monitor, List<WindowRecord> windows) in windowData)
            {
                List<WindowRecord> desktopWindows = windows.Where(window => string.Equals(
                    window.VirtualDesktopId,
                    desktopId,
                    StringComparison.OrdinalIgnoreCase)).ToList();
                if (desktopWindows.Count > 0)
                {
                    groups.Add(CreateGroup(
                        monitor,
                        desktopWindows,
                        desktopLabel,
                        settingsService));
                }
            }
            if (groups.Count == groupCountBefore)
                groups.Add(new MonitorWindowGroup { GroupHeader = $"{desktopLabel}  ·  No capturable windows" });
        }

        foreach ((MonitorInfo monitor, List<WindowRecord> windows) in windowData)
        {
            List<WindowRecord> monitorWindows = windows
                .Where(window => !Guid.TryParse(window.VirtualDesktopId, out _))
                .ToList();
            if (monitorWindows.Count > 0)
                groups.Add(CreateGroup(monitor, monitorWindows, "Desktop unavailable", settingsService));
        }

        return groups;
    }

    private static MonitorWindowGroup CreateGroup(
        MonitorInfo monitor,
        List<WindowRecord> windows,
        string? desktopLabel,
        Services.SettingsService? settingsService)
    {
        string primaryTag = monitor.IsPrimary ? " (Primary)" : "";
        string monitorName = settingsService?.ResolveMonitorName(
            monitor.MonitorId,
            monitor.FriendlyName) ?? monitor.FriendlyName;
        string monitorHeader = $"Monitor {monitor.Index + 1}: {monitorName}{primaryTag}  \u2014  " +
            $"{monitor.WidthPixels}\u00d7{monitor.HeightPixels}  " +
            $"({windows.Count} window{(windows.Count == 1 ? "" : "s")})";
        return new MonitorWindowGroup
        {
            GroupHeader = desktopLabel is null
                ? monitorHeader
                : $"{desktopLabel}  ·  {monitorHeader}",
            Windows = windows.Select(window => new WindowCheckItem
            {
                Record = window,
                DisplayName = string.IsNullOrEmpty(window.DisplayName)
                    ? window.ProcessName
                    : window.DisplayName,
                TitleSnippet = window.TitleSnippet,
                IsSelected = !ShouldAutoExclude(window),
            }).ToList(),
        };
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

    private void OnConfigureTerminal(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: WindowCheckItem item }) return;
        var dialog = new TerminalTabsDialog(item.Record) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        item.Record.TerminalTabs = dialog.Tabs.ToList();
        item.Record.TerminalActiveTabIndex = dialog.ActiveIndex;
    }

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

        if (SaveFiles)
        {
            foreach (WindowCheckItem item in _monitorGroups.SelectMany(g => g.Windows)
                         .Where(w => w.IsSelected && w.IsTerminal))
            {
                if (item.Record.TerminalTabs.Count > 0 &&
                    item.Record.TerminalTabs.All(tab =>
                        !string.IsNullOrWhiteSpace(tab.StartingDirectory) &&
                        System.IO.Directory.Exists(tab.StartingDirectory)))
                    continue;
                var tabsDialog = new TerminalTabsDialog(item.Record) { Owner = this };
                if (tabsDialog.ShowDialog() != true) return;
                item.Record.TerminalTabs = tabsDialog.Tabs.ToList();
                item.Record.TerminalActiveTabIndex = tabsDialog.ActiveIndex;
            }
        }

        DialogResult = true;
        Close();
    }
}
