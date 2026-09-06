using System;
using System.Windows;
using Wpf.Ui.Controls;

namespace WindowAnchor.UI;

/// <summary>Reusable first-run guide and permanent in-app reference.</summary>
public partial class HelpReferenceWindow : FluentWindow
{
    internal HelpReferenceWindow(bool firstRun = false)
    {
        InitializeComponent();
        if (!firstRun)
        {
            SaveWorkspaceButton.Content = "Save current workspace";
            return;
        }

        Title = "Welcome to WindowAnchor";
        GuideTitleText.Text = "Welcome to WindowAnchor";
        GuideSubtitleText.Text =
            "WindowAnchor runs in the notification area. Start here, then reopen this guide " +
            "from the tray or Settings whenever you need it.";
        FirstRunBadge.Visibility = Visibility.Visible;
        CloseButton.Content = "Dismiss";
    }

    internal event EventHandler? SaveWorkspaceRequested;

    internal event EventHandler? SettingsRequested;

    private void OnSaveWorkspaceClick(object sender, RoutedEventArgs e)
    {
        Close();
        SaveWorkspaceRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        Close();
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
