using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.UI;

/// <summary>Dialog to view and remove individual windows in a saved workspace.</summary>
internal sealed class WorkspaceWindowsDialog : Wpf.Ui.Controls.FluentWindow
{
    private static readonly IReadOnlyList<RestoreModeOption> RestoreModes =
    [
        new(RestoreModeKind.Resume, "Resume"),
        new(RestoreModeKind.Repair, "Repair"),
        new(RestoreModeKind.MoveExisting, "Move existing"),
        new(RestoreModeKind.LaunchFresh, "Launch fresh"),
        new(RestoreModeKind.ExactSwitch, "Exact switch"),
        new(RestoreModeKind.PreviewOnly, "Preview only")
    ];

    private static readonly IReadOnlyList<EntryPolicyOption> EntryPolicies =
    [
        new(EntryRestorePolicy.WorkspaceDefault, "Workspace default"),
        new(EntryRestorePolicy.ReuseExisting, "Reuse existing"),
        new(EntryRestorePolicy.LaunchIfMissing, "Launch if missing"),
        new(EntryRestorePolicy.AlwaysLaunchNew, "Always launch new"),
        new(EntryRestorePolicy.NeverLaunch, "Never launch"),
        new(EntryRestorePolicy.NeverClose, "Never close"),
        new(EntryRestorePolicy.IgnoreDuringSwitch, "Ignore during switch")
    ];

    private readonly WorkspaceSnapshot _snapshot;
    private readonly StorageService    _storageService;
    private readonly SettingsService?  _settingsService;
    private readonly StackPanel        _listPanel;

    public WorkspaceWindowsDialog(
        WorkspaceSnapshot snapshot,
        StorageService storageService,
        SettingsService? settingsService = null)
    {
        _snapshot        = snapshot;
        _storageService  = storageService;
        _settingsService = settingsService;

        Title  = $"{snapshot.Name} — Saved Windows";
        Width  = 820;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titleBar = new Wpf.Ui.Controls.TitleBar
        {
            Title        = $"{snapshot.Name} — Saved Windows",
            ShowMinimize = false,
            ShowMaximize = false,
        };
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        var modeGrid = new Grid { Margin = new Thickness(16, 10, 16, 8) };
        modeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        modeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        var modeText = new StackPanel();
        modeText.Children.Add(new TextBlock
        {
            Text = "Default restore mode",
            FontWeight = FontWeights.SemiBold
        });
        modeText.Children.Add(new TextBlock
        {
            Text = "Used by tray actions and restore hotkeys. Exact switch can close unrelated windows.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextFillColorTertiaryBrush"]
        });
        modeGrid.Children.Add(modeText);
        var modePicker = new ComboBox
        {
            ItemsSource = RestoreModes,
            DisplayMemberPath = nameof(RestoreModeOption.Label),
            SelectedValuePath = nameof(RestoreModeOption.Value),
            SelectedValue = snapshot.DefaultRestoreMode,
            MinWidth = 190,
            VerticalAlignment = VerticalAlignment.Center
        };
        modePicker.SelectionChanged += (_, _) =>
        {
            if (modePicker.SelectedValue is not RestoreModeKind selected ||
                selected == _snapshot.DefaultRestoreMode)
            {
                return;
            }

            _snapshot.DefaultRestoreMode = selected;
            _storageService.SaveWorkspace(_snapshot);
        };
        Grid.SetColumn(modePicker, 1);
        modeGrid.Children.Add(modePicker);

        // Column headers
        var headerGrid = new Grid { Margin = new Thickness(16, 10, 16, 4) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        AddHeaderCell(headerGrid, "Application", 0);
        AddHeaderCell(headerGrid, "Window Title", 1);
        AddHeaderCell(headerGrid, "Restore Policy", 2);
        Grid.SetRow(headerGrid, 1);

        var headerWrap = new StackPanel();
        headerWrap.Children.Add(modeGrid);
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

        var footer = new Border
        {
            Padding = new Thickness(16, 10, 16, 16),
            Child = new TextBlock
            {
                Text = "Entry policies override the selected workspace mode. “Always launch new” " +
                       "falls back to a safe existing match when an app has no reliable multi-window launch contract.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextFillColorTertiaryBrush"],
            },
        };
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
        Loaded += (_, _) => RebuildList();
    }

    private static void AddHeaderCell(Grid grid, string text, int column)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        Grid.SetColumn(label, column);
        grid.Children.Add(label);
    }

    private void RebuildList()
    {
        _listPanel.Children.Clear();
        if (_snapshot.Monitors.Count > 0)
        {
            foreach (var (monitor, entries) in _snapshot.EntriesByMonitor())
            {
                var entriesList = entries.ToList();
                if (entriesList.Count == 0)
                    continue;

                var header = new TextBlock
                {
                    Text = $"{(_settingsService?.ResolveMonitorName(monitor.MonitorId, monitor.FriendlyName) ?? monitor.FriendlyName)}  ({entriesList.Count} window{(entriesList.Count == 1 ? "" : "s")})",
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["SystemAccentColorPrimaryBrush"],
                    Margin = new Thickness(12, 8, 0, 2),
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
            _listPanel.Children.Add(new TextBlock
            {
                Text = "No windows remaining in this workspace.",
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 24, 0, 24),
                Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextFillColorTertiaryBrush"],
            });
        }
    }

    private void AddEntryRow(WorkspaceEntry entry)
    {
        var row = new Grid { Margin = new Thickness(4, 1, 4, 1) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });

        var nameStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 6, 4, 6)
        };
        nameStack.Children.Add(new TextBlock
        {
            Text = entry.ProcessName,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextFillColorPrimaryBrush"],
        });
        if (!string.IsNullOrEmpty(entry.FilePath))
        {
            nameStack.Children.Add(new TextBlock
            {
                Text = System.IO.Path.GetFileName(entry.FilePath),
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextFillColorTertiaryBrush"],
            });
        }
        Grid.SetColumn(nameStack, 0);
        row.Children.Add(nameStack);

        var title = new TextBlock
        {
            Text = entry.Position.TitleSnippet,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextFillColorSecondaryBrush"],
            Margin = new Thickness(0, 0, 4, 0),
            ToolTip = entry.Position.TitleSnippet,
        };
        Grid.SetColumn(title, 1);
        row.Children.Add(title);

        var policyPicker = new ComboBox
        {
            ItemsSource = EntryPolicies,
            DisplayMemberPath = nameof(EntryPolicyOption.Label),
            SelectedValuePath = nameof(EntryPolicyOption.Value),
            SelectedValue = entry.RestorePolicy,
            Margin = new Thickness(4, 3, 8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Override restore behavior for this saved entry"
        };
        policyPicker.SelectionChanged += (_, _) =>
        {
            if (policyPicker.SelectedValue is not EntryRestorePolicy selected ||
                selected == entry.RestorePolicy)
            {
                return;
            }

            entry.RestorePolicy = selected;
            _storageService.SaveWorkspace(_snapshot);
        };
        Grid.SetColumn(policyPicker, 2);
        row.Children.Add(policyPicker);

        var removeButton = new Wpf.Ui.Controls.Button
        {
            Icon = new Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = Wpf.Ui.Controls.SymbolRegular.Delete24,
                FontSize = 14
            },
            Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
            Padding = new Thickness(6),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Remove from workspace",
        };
        var captured = entry;
        removeButton.Click += (_, _) =>
        {
            _snapshot.Entries.Remove(captured);
            _storageService.SaveWorkspace(_snapshot);
            RebuildList();
        };
        Grid.SetColumn(removeButton, 3);
        row.Children.Add(removeButton);

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

    private sealed record RestoreModeOption(RestoreModeKind Value, string Label);
    private sealed record EntryPolicyOption(EntryRestorePolicy Value, string Label);
}
