using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using WindowAnchor.Models;

namespace WindowAnchor.UI;

/// <summary>
/// Lets the user choose which monitors to include when restoring a workspace.
/// Set <see cref="Owner"/> before calling <see cref="Window.ShowDialog"/>.
/// On a <c>true</c> result, read <see cref="SelectedMonitorIds"/> for the chosen set.
/// </summary>
internal sealed class SelectiveRestoreDialog : FluentWindow
{
    private readonly List<(System.Windows.Controls.CheckBox Cb, string MonitorId)> _checkRows = new();

    /// <summary>
    /// The monitor IDs the user selected, or <c>null</c> when the dialog was cancelled.
    /// An empty set means the user deselected everything (caller should treat as no-op).
    /// </summary>
    public HashSet<string>? SelectedMonitorIds { get; private set; }

    public SelectiveRestoreDialog(
        WorkspaceSnapshot snapshot,
        bool fingerprintMismatch,
        Services.SettingsService? settingsService = null)
    {
        Title                      = $"Restore: {snapshot.Name}";
        Width                      = 460;
        SizeToContent              = SizeToContent.Height;
        ResizeMode                 = ResizeMode.NoResize;
        WindowStartupLocation      = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        ShowInTaskbar              = false;

        // ── Layout ────────────────────────────────────────────────────────
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // TitleBar
        root.RowDefinitions.Add(new RowDefinition());                             // content

        var titleBar = new TitleBar
        {
            Title        = $"Restore: {snapshot.Name}",
            ShowMinimize = false,
            ShowMaximize = false,
        };
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        var content = new StackPanel { Margin = new Thickness(20, 16, 20, 20) };
        Grid.SetRow(content, 1);
        root.Children.Add(content);

        // ── Fingerprint mismatch warning ──────────────────────────────────
        if (fingerprintMismatch)
        {
            System.Windows.Media.Brush warnBg =
                TryRes("SystemFillColorCautionBackgroundBrush") ??
                TryRes("CardBackgroundFillColorDefaultBrush") ??
                System.Windows.Media.Brushes.Transparent;
            System.Windows.Media.Brush warnBorder =
                TryRes("SystemFillColorCautionBrush") ??
                TryRes("CardStrokeColorDefaultBrush") ??
                System.Windows.Media.Brushes.Transparent;

            var warn = new Border
            {
                Background      = warnBg,
                BorderBrush     = warnBorder,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Padding         = new Thickness(12, 8, 12, 10),
                Margin          = new Thickness(0, 0, 0, 14),
                Child           = new System.Windows.Controls.TextBlock
                {
                    Text         = "\u26A0\uFE0F  The current monitor configuration doesn\u2019t match " +
                                   "this workspace. Windows may restore to unexpected positions.",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize     = 12,
                    Foreground   = Res("TextFillColorPrimaryBrush"),
                },
            };
            content.Children.Add(warn);
        }

        // ── Section label ─────────────────────────────────────────────────
        content.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "Choose which monitors to restore:",
            FontSize   = 13,
            FontWeight = FontWeights.Medium,
            Foreground = Res("TextFillColorPrimaryBrush"),
            Margin     = new Thickness(0, 0, 0, 10),
        });

        // ── Monitor checkboxes ────────────────────────────────────────────
        foreach (var monitor in snapshot.Monitors)
        {
            int    count = snapshot.Entries.Count(e => e.MonitorId == monitor.MonitorId);
            string res   = $"{monitor.WidthPixels}\u00D7{monitor.HeightPixels}";
            string flags = monitor.IsPrimary ? "  \u2022  Primary" : "";
            string displayName = settingsService?.ResolveMonitorName(monitor.MonitorId, monitor.FriendlyName)
                                 ?? monitor.FriendlyName;

            var cb = new System.Windows.Controls.CheckBox
            {
                IsChecked = true,
                Margin    = new Thickness(0, 3, 0, 3),
                Content   = new StackPanel
                {
                    Children =
                    {
                        new System.Windows.Controls.TextBlock
                        {
                            Text       = displayName,
                            FontSize   = 13,
                            FontWeight = FontWeights.Medium,
                            Foreground = Res("TextFillColorPrimaryBrush"),
                        },
                        new System.Windows.Controls.TextBlock
                        {
                            Text       = $"{res}{flags}  \u2014  {count} window{(count == 1 ? "" : "s")}",
                            FontSize   = 11,
                            Margin     = new Thickness(0, 1, 0, 0),
                            Foreground = Res("TextFillColorTertiaryBrush"),
                        },
                    },
                },
            };
            content.Children.Add(cb);
            _checkRows.Add((cb, monitor.MonitorId));
        }

        // ── Separator ─────────────────────────────────────────────────────
        content.Children.Add(new Separator { Margin = new Thickness(0, 14, 0, 12), Opacity = 0.4 });

        // ── Buttons ───────────────────────────────────────────────────────
        var btnRow = new StackPanel
        {
            Orientation         = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };

        var cancelBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Cancel",
            Margin  = new Thickness(0, 0, 8, 0),
        };
        cancelBtn.Click += (_, _) => { SelectedMonitorIds = null; DialogResult = false; };

        var restoreBtn = new Wpf.Ui.Controls.Button
        {
            Content    = "Restore Selected",
            Appearance = ControlAppearance.Primary,
        };
        restoreBtn.Click += (_, _) =>
        {
            SelectedMonitorIds = new HashSet<string>(
                _checkRows.Where(r => r.Cb.IsChecked == true).Select(r => r.MonitorId));
            DialogResult = true;
        };

        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(restoreBtn);
        content.Children.Add(btnRow);

        Content = root;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static System.Windows.Media.Brush Res(string key) =>
        (System.Windows.Media.Brush)System.Windows.Application.Current.Resources[key];

    private static System.Windows.Media.Brush? TryRes(string key) =>
        System.Windows.Application.Current.Resources.Contains(key)
            ? System.Windows.Application.Current.Resources[key] as System.Windows.Media.Brush
            : null;
}
