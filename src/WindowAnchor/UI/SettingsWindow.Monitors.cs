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

public partial class SettingsWindow
{
    private readonly List<MonitorRow> _monitorRows = new();

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

}
