using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using WindowAnchor.Services;

namespace WindowAnchor.UI;

/// <summary>Non-modal, cancellable progress surface for restore and switch transactions.</summary>
internal sealed class RestoreProgressWindow : FluentWindow
{
    private readonly System.Windows.Controls.ProgressBar _progressBar;
    private readonly System.Windows.Controls.TextBlock _counterText;
    private readonly System.Windows.Controls.TextBlock _messageText;
    private readonly System.Windows.Controls.TextBlock _detailText;
    private readonly System.Windows.Controls.TextBlock _elapsedText;
    private readonly System.Windows.Controls.Button _cancelButton;
    private readonly DispatcherTimer _elapsedTimer;
    private readonly Stopwatch _operationTimer = Stopwatch.StartNew();
    private readonly Stopwatch _sinceLastReport = Stopwatch.StartNew();
    private TimeSpan? _reportedElapsed;
    private TimeSpan? _reportedTimeout;
    private bool _operationActive = true;
    private bool _cancelRequested;

    internal RestoreProgressWindow(string workspaceName, bool isSwitch, Window? owner = null)
    {
        Owner = owner;
        Title = isSwitch ? "Switching Workspace…" : "Restoring Workspace…";
        Width = 480;
        Height = 245;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = owner is null
            ? WindowStartupLocation.CenterScreen
            : WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        ShowInTaskbar = false;
        Topmost = true;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());

        var titleBar = new TitleBar
        {
            Title = isSwitch ? "Switching Workspace" : "Restoring Workspace",
            ShowClose = true,
            ShowMinimize = false,
            ShowMaximize = false
        };
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        var content = new Grid { Margin = new Thickness(24, 12, 24, 18) };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(content, 1);
        root.Children.Add(content);

        var header = new System.Windows.Controls.TextBlock
        {
            Text = $"{(isSwitch ? "Switching to" : "Restoring")} “{workspaceName}”…",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res("TextFillColorPrimaryBrush"),
            Margin = new Thickness(0, 0, 0, 10),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(header, 0);
        content.Children.Add(header);

        var progressRow = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        progressRow.ColumnDefinitions.Add(new ColumnDefinition());
        progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _progressBar = new System.Windows.Controls.ProgressBar
        {
            IsIndeterminate = true,
            Height = 5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        progressRow.Children.Add(_progressBar);
        _counterText = new System.Windows.Controls.TextBlock
        {
            MinWidth = 48,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Res("TextFillColorTertiaryBrush")
        };
        Grid.SetColumn(_counterText, 1);
        progressRow.Children.Add(_counterText);
        Grid.SetRow(progressRow, 1);
        content.Children.Add(progressRow);

        _messageText = new System.Windows.Controls.TextBlock
        {
            Text = "Creating recovery checkpoint",
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Foreground = Res("TextFillColorPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 3)
        };
        Grid.SetRow(_messageText, 2);
        content.Children.Add(_messageText);

        _detailText = new System.Windows.Controls.TextBlock
        {
            Text = "Capturing the current desktop before making changes.",
            FontSize = 11,
            Foreground = Res("TextFillColorTertiaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(_detailText, 3);
        content.Children.Add(_detailText);

        var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _elapsedText = new System.Windows.Controls.TextBlock
        {
            FontSize = 11,
            Foreground = Res("TextFillColorTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        footer.Children.Add(_elapsedText);
        _cancelButton = new System.Windows.Controls.Button
        {
            Content = new System.Windows.Controls.TextBlock { Text = "Cancel" },
            MinWidth = 88,
            Padding = new Thickness(14, 5, 14, 5)
        };
        _cancelButton.Click += (_, _) => RequestCancellation();
        Grid.SetColumn(_cancelButton, 1);
        footer.Children.Add(_cancelButton);
        Grid.SetRow(footer, 4);
        content.Children.Add(footer);

        Content = root;
        Closing += OnClosing;
        _elapsedTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _elapsedTimer.Tick += (_, _) => RefreshElapsedText();
        _elapsedTimer.Start();
    }

    internal event EventHandler? CancelRequested;

    internal void ApplyReport(RestoreProgressReport report)
    {
        if (!_operationActive)
            return;

        if (report.Total > 0)
        {
            _progressBar.IsIndeterminate = false;
            _progressBar.Value = Math.Clamp((double)report.Current / report.Total * 100.0, 0, 100);
            _counterText.Text = $"{report.Current} / {report.Total}";
        }
        else
        {
            _progressBar.IsIndeterminate = true;
            _counterText.Text = "";
        }

        _messageText.Text = report.Message;
        _detailText.Text = report.Detail;
        _reportedElapsed = report.Elapsed;
        _reportedTimeout = report.Timeout;
        _sinceLastReport.Restart();
        RefreshElapsedText();
    }

    internal void CompleteAndClose()
    {
        _operationActive = false;
        _elapsedTimer.Stop();
        Close();
    }

    private void RefreshElapsedText()
    {
        TimeSpan elapsed = _reportedElapsed is { } reported
            ? reported + _sinceLastReport.Elapsed
            : _operationTimer.Elapsed;
        _elapsedText.Text = FormatTiming(elapsed, _reportedTimeout);
    }

    private void RequestCancellation()
    {
        if (_cancelRequested)
            return;
        _cancelRequested = true;
        _cancelButton.IsEnabled = false;
        _messageText.Text = "Cancelling safely…";
        _detailText.Text = "WindowAnchor will stop before the next operation boundary.";
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_operationActive)
            return;
        e.Cancel = true;
        RequestCancellation();
    }

    private static string FormatTiming(TimeSpan? elapsed, TimeSpan? timeout)
    {
        if (elapsed is null)
            return "";
        string value = $"Elapsed {FormatDuration(elapsed.Value)}";
        return timeout is null ? value : $"{value} · limit {FormatDuration(timeout.Value)}";
    }

    private static string FormatDuration(TimeSpan value) =>
        value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss")
            : value.ToString(@"m\:ss");

    private static System.Windows.Media.Brush Res(string key) =>
        (System.Windows.Media.Brush)System.Windows.Application.Current.Resources[key];
}
