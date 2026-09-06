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
            DefaultWorkspaceCombo.Items.Add(new ComboBoxItem { Content = ws.Name, Tag = ws.WorkspaceId });
            if (ws.WorkspaceId.Equals(
                    _settingsService.Settings.DefaultWorkspaceId,
                    StringComparison.OrdinalIgnoreCase))
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
            _settingsService.Settings.DefaultWorkspaceId = item.Tag as string;
            _settingsService.Save();
        }
    }

    // ── Hotkey UI ────────────────────────────────────────────────────────────

}
