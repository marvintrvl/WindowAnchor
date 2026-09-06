using System.Windows;

namespace WindowAnchor.UI;

public partial class SettingsWindow
{
    private void InitialiseRestoreWorkflowUI()
    {
        _suppressToggle = true;
        RestorePreviewToggle.IsChecked = _settingsService.Settings.ShowRestorePreview;
        RestoreCheckpointToggle.IsChecked = _settingsService.Settings.CreateRestoreCheckpoints;
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
}
