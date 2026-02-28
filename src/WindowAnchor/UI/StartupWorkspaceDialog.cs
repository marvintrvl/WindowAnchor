using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using WindowAnchor.Models;

namespace WindowAnchor.UI;

/// <summary>
/// Shown on startup when <see cref="StartupBehavior.AskUser"/> is configured.
/// Lets the user pick a workspace to restore (or skip).
/// </summary>
internal sealed class StartupWorkspaceDialog : FluentWindow
{
    private readonly System.Windows.Controls.ListBox _listBox;

    /// <summary>The workspace the user chose, or <c>null</c> if they clicked Skip / closed.</summary>
    public WorkspaceSnapshot? SelectedWorkspace { get; private set; }

    public StartupWorkspaceDialog(IReadOnlyList<WorkspaceSnapshot> workspaces)
    {
        Title                      = "WindowAnchor — Restore Workspace";
        Width                      = 440;
        SizeToContent              = SizeToContent.Height;
        MaxHeight                  = 520;
        ResizeMode                 = ResizeMode.NoResize;
        WindowStartupLocation     = WindowStartupLocation.CenterScreen;
        ExtendsContentIntoTitleBar = true;
        ShowInTaskbar              = true;
        Topmost                    = true;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());

        var titleBar = new TitleBar
        {
            Title        = "Restore Workspace",
            ShowMinimize = false,
            ShowMaximize = false,
        };
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        var content = new StackPanel { Margin = new Thickness(20, 14, 20, 20) };
        Grid.SetRow(content, 1);
        root.Children.Add(content);

        content.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "Choose a workspace to restore:",
            FontSize   = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res("TextFillColorPrimaryBrush"),
            Margin     = new Thickness(0, 0, 0, 12),
        });

        _listBox = new System.Windows.Controls.ListBox
        {
            MaxHeight  = 280,
            Margin     = new Thickness(0, 0, 0, 16),
            BorderBrush = Res("CardStrokeColorDefaultBrush"),
        };

        foreach (var ws in workspaces.OrderByDescending(w => w.SavedAt))
        {
            var item = new System.Windows.Controls.ListBoxItem
            {
                Tag     = ws,
                Padding = new Thickness(12, 8, 12, 8),
                Content = new StackPanel
                {
                    Children =
                    {
                        new System.Windows.Controls.TextBlock
                        {
                            Text       = ws.Name,
                            FontSize   = 13,
                            FontWeight = FontWeights.Medium,
                            Foreground = Res("TextFillColorPrimaryBrush"),
                        },
                        new System.Windows.Controls.TextBlock
                        {
                            Text       = $"{ws.Entries.Count} windows  \u2022  saved {ws.SavedAt.ToLocalTime():g}",
                            FontSize   = 11,
                            Foreground = Res("TextFillColorTertiaryBrush"),
                            Margin     = new Thickness(0, 2, 0, 0),
                        },
                    },
                },
            };
            item.MouseDoubleClick += (_, _) => DoRestore();
            _listBox.Items.Add(item);
        }

        if (_listBox.Items.Count > 0) _listBox.SelectedIndex = 0;
        content.Children.Add(_listBox);

        // ── Buttons ─────────────────────────────────────────────────────
        var btnRow = new StackPanel
        {
            Orientation         = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };

        var skipBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Skip",
            Margin  = new Thickness(0, 0, 8, 0),
        };
        skipBtn.Click += (_, _) => { DialogResult = false; };

        var restoreBtn = new Wpf.Ui.Controls.Button
        {
            Content    = "Restore",
            Appearance = ControlAppearance.Primary,
        };
        restoreBtn.Click += (_, _) => DoRestore();

        btnRow.Children.Add(skipBtn);
        btnRow.Children.Add(restoreBtn);
        content.Children.Add(btnRow);

        Content = root;
    }

    private void DoRestore()
    {
        if (_listBox.SelectedItem is System.Windows.Controls.ListBoxItem { Tag: WorkspaceSnapshot ws })
        {
            SelectedWorkspace = ws;
            DialogResult = true;
        }
    }

    private static System.Windows.Media.Brush Res(string key) =>
        (System.Windows.Media.Brush)System.Windows.Application.Current.Resources[key];
}
