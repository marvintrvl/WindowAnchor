using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WindowAnchor.Services;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;

namespace WindowAnchor.UI;

/// <summary>Non-technical failure summary with a privacy-safe diagnostics copy action.</summary>
internal sealed class RestoreDiagnosticsDialog : FluentWindow
{
    private readonly RestoreDiagnosticsReport _report;
    private readonly TextBlock _copyStatus;

    internal RestoreDiagnosticsDialog(
        RestoreDiagnosticsReport report,
        string title,
        Window? owner = null)
    {
        _report = report ?? throw new ArgumentNullException(nameof(report));
        Owner = owner;
        Title = title;
        Width = 560;
        Height = 430;
        MinWidth = 460;
        MinHeight = 320;
        WindowStartupLocation = owner is null
            ? WindowStartupLocation.CenterScreen
            : WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Margin = new Thickness(24, 20, 24, 12) };
        header.Children.Add(new TextBlock
        {
            Text = "Restore needs attention",
            FontWeight = FontWeights.SemiBold,
            FontSize = 16,
            Foreground = Brush("TextFillColorPrimaryBrush", Brushes.Black)
        });
        header.Children.Add(new TextBlock
        {
            Text = report.Summary.Message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 0),
            Foreground = Brush("TextFillColorSecondaryBrush", Brushes.DimGray)
        });
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var details = new TextBox
        {
            Text = BuildDetails(report),
            IsReadOnly = true,
            IsTabStop = false,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Brush("TextFillColorPrimaryBrush", Brushes.Black),
            Margin = new Thickness(24, 0, 24, 12),
            Padding = new Thickness(0)
        };
        Grid.SetRow(details, 1);
        root.Children.Add(details);

        var footer = new Grid { Margin = new Thickness(24, 0, 24, 20) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _copyStatus = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("TextFillColorTertiaryBrush", Brushes.Gray)
        };
        footer.Children.Add(_copyStatus);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var copy = new Button
        {
            Content = "Copy diagnostics",
            MinWidth = 128,
            Padding = new Thickness(14, 5, 14, 5),
            Margin = new Thickness(0, 0, 8, 0)
        };
        copy.Click += (_, _) => CopyDiagnostics();
        buttons.Children.Add(copy);
        var close = new Button
        {
            Content = "Close",
            MinWidth = 88,
            Padding = new Thickness(14, 5, 14, 5),
            IsDefault = true
        };
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
    }

    private void CopyDiagnostics()
    {
        try
        {
            Clipboard.SetText(_report.ToRedactedJson());
            _copyStatus.Text = "Diagnostics copied.";
        }
        catch
        {
            _copyStatus.Text = "WindowAnchor could not copy diagnostics.";
        }
    }

    private static string BuildDetails(RestoreDiagnosticsReport report)
    {
        string[] entries = report.Entries
            .Where(entry => entry.FinalStatus is
                RestoreExecutionEntryStatus.Blocked or
                RestoreExecutionEntryStatus.Cancelled or
                RestoreExecutionEntryStatus.Stale or
                RestoreExecutionEntryStatus.Failed)
            .Take(8)
            .Select(entry =>
                $"Entry {entry.EntryIndex + 1}: {entry.FinalStatus} " +
                $"({string.Join(", ", entry.DiagnosticCodes.Take(3))})" +
                $"{Environment.NewLine}{entry.Message}")
            .ToArray();
        if (entries.Length == 0)
            return "The restore reported an incomplete result. Copy diagnostics for the structured details.";
        return string.Join(Environment.NewLine + Environment.NewLine, entries);
    }

    private static Brush Brush(string resourceKey, Brush fallback) =>
        Application.Current?.Resources.Contains(resourceKey) == true
            ? Application.Current.Resources[resourceKey] as Brush ?? fallback
            : fallback;
}
