using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WindowAnchor.Models;

namespace WindowAnchor.UI;

/// <summary>Confirms one profile and directory per visible Windows Terminal tab.</summary>
internal sealed class TerminalTabsDialog : Window
{
    private readonly ObservableCollection<TerminalTab> _tabs;
    private readonly DataGrid _grid;
    private TerminalTab? _activeTab;

    internal IReadOnlyList<TerminalTab> Tabs => _tabs.ToList();
    internal int ActiveIndex => _activeTab is null ? 0 : Math.Max(0, _tabs.IndexOf(_activeTab));

    internal TerminalTabsDialog(WindowRecord record)
    {
        Title = "Windows Terminal tabs";
        Width = 790;
        Height = 440;
        MinWidth = 600;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _tabs = new ObservableCollection<TerminalTab>(record.TerminalTabs.Select(tab => new TerminalTab
        {
            TitleHint = tab.TitleHint,
            Profile = tab.Profile,
            StartingDirectory = tab.StartingDirectory
        }));
        if (_tabs.Count == 0) _tabs.Add(new TerminalTab());
        _activeTab = _tabs[Math.Clamp(record.TerminalActiveTabIndex, 0, _tabs.Count - 1)];

        var root = new DockPanel { Margin = new Thickness(18) };
        Content = root;
        var intro = new TextBlock
        {
            Text = "Confirm each tab in left-to-right order. Tracked PowerShell tabs are prefilled. " +
                   "For other tabs, enter the Terminal profile name or GUID and its current directory. " +
                   "A blank profile uses Terminal's default.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(intro, Dock.Top);
        root.Children.Add(intro);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        Button add = MakeButton("Add tab", () =>
        {
            int index = _grid!.SelectedItem is TerminalTab selected
                ? _tabs.IndexOf(selected) + 1 : _tabs.Count;
            var tab = new TerminalTab();
            _tabs.Insert(index, tab);
            _grid.SelectedItem = tab;
            UpdateActiveTitle();
        });
        Button setup = MakeButton("PowerShell setup", () =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://github.com/marvintrvl/WindowAnchor#windows-terminal-integration",
                    UseShellExecute = true
                });
            }
            catch (Exception)
            {
                MessageBox.Show(this,
                    "Open the Windows Terminal integration section in the WindowAnchor README.",
                    "Setup guide", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        });
        Button remove = MakeButton("Remove selected", () =>
        {
            if (_grid!.SelectedItem is TerminalTab tab && _tabs.Count > 1)
            {
                _tabs.Remove(tab);
                if (ReferenceEquals(_activeTab, tab)) _activeTab = _tabs[0];
                UpdateActiveTitle();
            }
        });
        Button active = MakeButton("Set active", () =>
        {
            if (_grid!.SelectedItem is TerminalTab tab)
            {
                _activeTab = tab;
                UpdateActiveTitle();
            }
        });
        Button up = MakeButton("↑", () => MoveSelected(-1));
        Button down = MakeButton("↓", () => MoveSelected(1));
        Button cancel = MakeButton("Cancel", () => Close());
        Button save = MakeButton("Use these tabs", Save);
        foreach (Button button in new[] { setup, add, remove, up, down, active, cancel, save }) buttons.Children.Add(button);

        _grid = new DataGrid
        {
            ItemsSource = _tabs,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserSortColumns = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionMode = DataGridSelectionMode.Single
        };
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Visible tab title (hint)",
            Binding = new Binding(nameof(TerminalTab.TitleHint)),
            IsReadOnly = true,
            Width = new DataGridLength(2, DataGridLengthUnitType.Star)
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Profile name / GUID",
            Binding = new Binding(nameof(TerminalTab.Profile)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = new DataGridLength(2, DataGridLengthUnitType.Star)
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Current directory",
            Binding = new Binding(nameof(TerminalTab.StartingDirectory)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = new DataGridLength(3, DataGridLengthUnitType.Star)
        });
        root.Children.Add(_grid);
        UpdateActiveTitle();
    }

    private static Button MakeButton(string label, Action action)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 5, 10, 5) };
        button.Click += (_, _) => action();
        return button;
    }

    private void Save()
    {
        _grid.CommitEdit(DataGridEditingUnit.Cell, true);
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        if (_tabs.Any(tab => string.IsNullOrWhiteSpace(tab.StartingDirectory) ||
            !Directory.Exists(tab.StartingDirectory.Trim())))
        {
            MessageBox.Show(this,
                "Every tab needs an existing directory. Check the paths before saving. " +
                "If you don't want to save Terminal tabs, turn off 'Save open files' instead.",
                "Terminal directory required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        foreach (TerminalTab tab in _tabs)
        {
            tab.Profile = tab.Profile.Trim();
            tab.StartingDirectory = tab.StartingDirectory.Trim();
        }
        DialogResult = true;
    }

    private void MoveSelected(int offset)
    {
        if (_grid.SelectedItem is not TerminalTab tab) return;
        int from = _tabs.IndexOf(tab);
        int to = from + offset;
        if (to < 0 || to >= _tabs.Count) return;
        _tabs.Move(from, to);
        _grid.SelectedItem = tab;
        UpdateActiveTitle();
    }

    private void UpdateActiveTitle() => Title = $"Windows Terminal tabs — active tab {ActiveIndex + 1}";
}
