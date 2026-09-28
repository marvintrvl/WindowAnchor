using System.Windows;
using System.Windows.Controls;
using WindowAnchor.Models;

namespace WindowAnchor.UI;

public partial class SettingsWindow
{
    private void InitialiseRestoreWorkflowUI()
    {
        _suppressToggle = true;
        RestorePreviewToggle.IsChecked = _settingsService.Settings.ShowRestorePreview;
        RestoreCheckpointToggle.IsChecked = _settingsService.Settings.CreateRestoreCheckpoints;
        VirtualDesktopAssociationToggle.IsChecked =
            _settingsService.Settings.EnableVirtualDesktopAssociation;
        TemporaryDisplayRecoveryModeCombo.SelectedValue =
            _settingsService.Settings.TemporaryDisplayRecoveryMode.ToString();
        BrowserTabRestorePolicyCombo.SelectedIndex = (int)_settingsService.Settings.BrowserTabRestorePolicy;
        double threshold = _settingsService.Settings.MinimumVisibleWindowAreaRatio;
        RescueVisibilityThresholdCombo.SelectedIndex = threshold <= 0.10 ? 0 : threshold >= 0.50 ? 2 : 1;
        _suppressToggle = false;
    }

    private void OnRestoreWorkflowToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;

        _settingsService.Settings.ShowRestorePreview =
            RestorePreviewToggle.IsChecked.GetValueOrDefault();
        _settingsService.Settings.CreateRestoreCheckpoints =
            RestoreCheckpointToggle.IsChecked.GetValueOrDefault();
        _settingsService.Settings.EnableVirtualDesktopAssociation =
            VirtualDesktopAssociationToggle.IsChecked.GetValueOrDefault();
        _settingsService.Save();
    }

    private void OnBrowserTabRestorePolicyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressToggle || BrowserTabRestorePolicyCombo.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string value || !Enum.TryParse(value, out BrowserTabRestorePolicy policy))
            return;

        _settingsService.Settings.BrowserTabRestorePolicy = policy;
        _settingsService.Save();
    }

    private void OnTemporaryDisplayRecoveryModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressToggle || TemporaryDisplayRecoveryModeCombo.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string value || !Enum.TryParse(value, out TemporaryDisplayRecoveryMode mode))
        {
            return;
        }

        _settingsService.Settings.TemporaryDisplayRecoveryMode = mode;
        _settingsService.Save();
    }

    private void OnRescueVisibilityThresholdChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressToggle || RescueVisibilityThresholdCombo.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string value || !double.TryParse(
                value,
                System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture,
                out double threshold))
            return;

        _settingsService.Settings.MinimumVisibleWindowAreaRatio = threshold;
        _settingsService.Save();
    }
}
