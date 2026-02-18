using System.Windows;
using Wpf.Ui.Controls;

namespace WindowAnchor.UI;

public partial class SaveWorkspaceDialog : FluentWindow
{
    public string WorkspaceName => WorkspaceNameInput.Text.Trim();

    public SaveWorkspaceDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => WorkspaceNameInput.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e) => TryCommit();

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) TryCommit();
    }

    private void TryCommit()
    {
        if (string.IsNullOrWhiteSpace(WorkspaceNameInput.Text))
        {
            System.Windows.MessageBox.Show("Please enter a workspace name.", "Name Required",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
        Close();
    }
}
