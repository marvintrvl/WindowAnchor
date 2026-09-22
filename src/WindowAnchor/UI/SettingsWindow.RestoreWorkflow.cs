using System.Windows;
using System.Windows.Controls;

namespace WindowAnchor.UI;

public partial class SettingsWindow
{
    private void InitialiseRestoreWorkflowUI()
    {
        _suppressToggle = true;
        RestorePreviewToggle.IsChecked = _settingsService.Settings.ShowRestorePreview;
        RestoreCheckpointToggle.IsChecked = _settingsService.Settings.CreateRestoreCheckpoints;
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
