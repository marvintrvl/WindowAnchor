using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using WindowAnchor.Services;

namespace WindowAnchor.UI;

/// <summary>
/// Non-modal progress window shown while <see cref="WorkspaceService.TakeSnapshot"/> runs on a
/// background thread.  Open with <c>.Show()</c> before awaiting <c>Task.Run</c>; close with
/// <c>.Close()</c> in the <c>finally</c> block when the task finishes.
/// Route progress updates through <c>new Progress&lt;SaveProgressReport&gt;(r =&gt; window.ApplyReport(r))</c>
/// so that marshalling to the UI thread happens automatically.
/// </summary>
internal sealed class SaveProgressWindow : FluentWindow
{
    private readonly System.Windows.Controls.ProgressBar _progressBar;
    private readonly System.Windows.Controls.TextBlock   _counterText;
    private readonly System.Windows.Controls.TextBlock   _appNameText;
    private readonly System.Windows.Controls.TextBlock   _detailText;

    public SaveProgressWindow(string workspaceName)
    {
        Title                      = "Saving Workspace\u2026";
        Width                      = 430;
        Height                     = 190;
        ResizeMode                 = ResizeMode.NoResize;
        WindowStartupLocation      = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        ShowInTaskbar              = false;
        Topmost                    = true;

        // ── Layout ────────────────────────────────────────────────────────
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // TitleBar
        root.RowDefinitions.Add(new RowDefinition());                             // content

        var titleBar = new TitleBar
        {
            Title        = "Saving Workspace",
            ShowClose    = false,
            ShowMinimize = false,
            ShowMaximize = false,
        };
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        var content = new StackPanel { Margin = new Thickness(24, 14, 24, 18) };
        Grid.SetRow(content, 1);
        root.Children.Add(content);

        // ── "Saving 'Name'…" header ───────────────────────────────────────
        content.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text         = $"Saving \u201c{workspaceName}\u201d\u2026",
            FontSize     = 14,
            FontWeight   = FontWeights.SemiBold,
            Foreground   = Res("TextFillColorPrimaryBrush"),
            Margin       = new Thickness(0, 0, 0, 10),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        // ── Progress bar row (bar + "X / Y" counter) ──────────────────────
        var pbRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        pbRow.ColumnDefinitions.Add(new ColumnDefinition());
        pbRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _progressBar = new System.Windows.Controls.ProgressBar
        {
            IsIndeterminate   = true,
            Height            = 5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 10, 0),
        };
        Grid.SetColumn(_progressBar, 0);
        pbRow.Children.Add(_progressBar);

        _counterText = new System.Windows.Controls.TextBlock
        {
            Text              = "",
            FontSize          = 11,
            MinWidth          = 48,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground        = Res("TextFillColorTertiaryBrush"),
        };
        Grid.SetColumn(_counterText, 1);
        pbRow.Children.Add(_counterText);

        content.Children.Add(pbRow);

        // ── Current app name ──────────────────────────────────────────────
        _appNameText = new System.Windows.Controls.TextBlock
        {
            Text         = "Building file detection cache\u2026",
            FontSize     = 13,
            FontWeight   = FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground   = Res("TextFillColorPrimaryBrush"),
            Margin       = new Thickness(0, 0, 0, 2),
        };
        content.Children.Add(_appNameText);

        // ── Window title / stage detail ───────────────────────────────────
        _detailText = new System.Windows.Controls.TextBlock
        {
            Text         = "",
            FontSize     = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground   = Res("TextFillColorTertiaryBrush"),
        };
        content.Children.Add(_detailText);

        Content = root;
    }

    /// <summary>
    /// Updates the progress UI.  Must be called on the UI thread — use
    /// <c>new Progress&lt;SaveProgressReport&gt;(r =&gt; window.ApplyReport(r))</c>
    /// so marshalling happens automatically.
    /// </summary>
    public void ApplyReport(SaveProgressReport r)
    {
        if (r.Total > 0)
        {
            _progressBar.IsIndeterminate = false;
            _progressBar.Value           = (double)r.Current / r.Total * 100.0;
            _counterText.Text            = $"{r.Current}\u202f/\u202f{r.Total}";
        }
        else
        {
            _progressBar.IsIndeterminate = true;
            _counterText.Text            = "";
        }

        _appNameText.Text = r.AppName;
        _detailText.Text  = r.Detail;
    }

    // ── Helper ────────────────────────────────────────────────────────────

    private static System.Windows.Media.Brush Res(string key) =>
        (System.Windows.Media.Brush)System.Windows.Application.Current.Resources[key];
}
