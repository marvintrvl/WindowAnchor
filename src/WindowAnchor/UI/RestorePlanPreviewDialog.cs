using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Controls;
using WindowAnchor.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace WindowAnchor.UI;

/// <summary>
/// Displays an immutable restore plan and derives an approved copy when entries are disabled.
/// Matching and resource discovery never run in this window.
/// </summary>
internal sealed class RestorePlanPreviewDialog : FluentWindow
{
    private readonly RestorePlan _previewPlan;
    private readonly Dictionary<int, System.Windows.Controls.CheckBox> _entryControls = new();
    private readonly Wpf.Ui.Controls.Button _restoreButton;
    private readonly System.Windows.Controls.TextBlock _selectionSummary;
    private readonly Border _blockingNotice;
    private readonly StackPanel _globalActionsPanel;
    private readonly Dictionary<int, StackPanel> _entryActionPanels = new();
    private readonly Dictionary<int, System.Windows.Controls.TextBlock> _entryOutcomeLabels = new();
    private readonly Dictionary<int, Border> _entryCards = new();

    internal RestorePlan? ApprovedPlan { get; private set; }

    internal RestorePlanPreviewDialog(RestorePlan previewPlan)
    {
        _previewPlan = previewPlan ?? throw new ArgumentNullException(nameof(previewPlan));
        RestorePlanPreview preview = RestorePlanPreviewBuilder.Build(previewPlan);

        Title = $"Review restore: {preview.WorkspaceName}";
        Width = 780;
        Height = 720;
        MinWidth = 640;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        ShowInTaskbar = false;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        KeyboardNavigation.SetTabNavigation(root, KeyboardNavigationMode.Continue);

        var titleBar = new TitleBar
        {
            Title = $"Review restore: {preview.WorkspaceName}",
            ShowMinimize = false,
            ShowMaximize = false
        };
        AutomationProperties.SetName(titleBar, $"Restore plan preview for {preview.WorkspaceName}");
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        var body = new Grid { Margin = new Thickness(22, 14, 22, 20) };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var heading = new System.Windows.Controls.TextBlock
        {
            Text = "Review what WindowAnchor will change",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextFillColorPrimaryBrush", Brushes.Black)
        };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        body.Children.Add(heading);

        var introduction = new System.Windows.Controls.TextBlock
        {
            Text = $"Mode: {ModeLabel(preview.Mode)} · {preview.Entries.Count} saved entries. " +
                   "Clear an entry to remove its actions from the approved plan.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 12),
            Foreground = Brush("TextFillColorSecondaryBrush", Brushes.DimGray)
        };
        Grid.SetRow(introduction, 1);
        body.Children.Add(introduction);

        var notices = new StackPanel();
        Border destructiveNotice = Notice(
            preview.DestructiveSummary,
            preview.DestructiveActionCount > 0
                ? Brush("SystemFillColorCautionBrush", Brushes.DarkOrange)
                : Brush("SystemFillColorSuccessBrush", Brushes.SeaGreen),
            preview.DestructiveActionCount > 0
                ? Brush("SystemFillColorCautionBackgroundBrush", Brushes.LemonChiffon)
                : Brush("SystemFillColorSuccessBackgroundBrush", Brushes.Honeydew));
        AutomationProperties.SetName(
            destructiveNotice,
            $"Destructive action summary. {preview.DestructiveSummary}");
        notices.Children.Add(destructiveNotice);

        _blockingNotice = Notice(
            "Blocking errors must be resolved or their entries disabled before restore.",
            Brush("SystemFillColorCriticalBrush", Brushes.Firebrick),
            Brush("SystemFillColorCriticalBackgroundBrush", Brushes.MistyRose));
        _blockingNotice.Margin = new Thickness(0, 8, 0, 0);
        _blockingNotice.Visibility = preview.BlockingErrorCount > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        AutomationProperties.SetName(
            _blockingNotice,
            "Blocking errors are present. Disable affected entries before restoring.");
        notices.Children.Add(_blockingNotice);
        Grid.SetRow(notices, 2);
        body.Children.Add(notices);

        var entryPanel = new StackPanel { Margin = new Thickness(0, 12, 0, 10) };
        int tabIndex = 0;
        foreach (RestorePlanPreviewEntry entry in preview.Entries)
        {
            Border card = BuildEntryCard(entry, tabIndex++);
            entryPanel.Children.Add(card);
        }
        var scroll = new ScrollViewer
        {
            Content = entryPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false
        };
        AutomationProperties.SetName(scroll, "Restore plan entries");
        Grid.SetRow(scroll, 3);
        body.Children.Add(scroll);

        _globalActionsPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        Grid.SetRow(_globalActionsPanel, 4);
        body.Children.Add(_globalActionsPanel);

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _selectionSummary = new System.Windows.Controls.TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("TextFillColorSecondaryBrush", Brushes.DimGray)
        };
        AutomationProperties.SetLiveSetting(_selectionSummary, AutomationLiveSetting.Polite);
        footer.Children.Add(_selectionSummary);

        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        var cancelButton = new Wpf.Ui.Controls.Button
        {
            Content = "Cancel",
            IsCancel = true,
            Margin = new Thickness(0, 0, 8, 0),
            TabIndex = tabIndex++
        };
        AutomationProperties.SetName(cancelButton, "Cancel restore");
        cancelButton.Click += (_, _) =>
        {
            ApprovedPlan = null;
            DialogResult = false;
        };

        _restoreButton = new Wpf.Ui.Controls.Button
        {
            Content = "Restore selected",
            Appearance = ControlAppearance.Primary,
            IsDefault = true,
            TabIndex = tabIndex
        };
        AutomationProperties.SetName(_restoreButton, "Approve plan and restore selected entries");
        AutomationProperties.SetHelpText(
            _restoreButton,
            "Executes the exact actions shown in this preview after stale-plan validation.");
        _restoreButton.Click += (_, _) =>
        {
            ApprovedPlan = DeriveCurrentPlan();
            if (!ApprovedPlan.CanExecute || ApprovedPlan.Actions.Count == 0) return;
            DialogResult = true;
        };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(_restoreButton);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 5);
        body.Children.Add(footer);

        Content = root;
        RefreshApprovalState();
        Loaded += (_, _) =>
            _entryControls.Values.FirstOrDefault(control => control.IsEnabled)?.Focus();
    }

    private Border BuildEntryCard(RestorePlanPreviewEntry entry, int tabIndex)
    {
        Brush borderBrush = entry.IsBlocking
            ? Brush("SystemFillColorCriticalBrush", Brushes.Firebrick)
            : entry.Outcome == RestorePreviewOutcomeKind.Ambiguous
                ? Brush("SystemFillColorCautionBrush", Brushes.DarkOrange)
                : Brush("CardStrokeColorDefaultBrush", Brushes.LightGray);
        var card = new Border
        {
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(entry.IsBlocking ? 2 : 1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Background = Brush("CardBackgroundFillColorDefaultBrush", Brushes.White)
        };
        _entryCards[entry.EntryIndex] = card;
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var checkBox = new System.Windows.Controls.CheckBox
        {
            IsChecked = entry.IsInitiallyEnabled,
            IsEnabled = entry.Outcome is not (RestorePreviewOutcomeKind.Skipped or
                RestorePreviewOutcomeKind.Cancelled),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 3, 10, 0),
            TabIndex = tabIndex,
            Tag = entry.EntryIndex
        };
        AutomationProperties.SetName(checkBox, $"Include {entry.DisplayName}");
        AutomationProperties.SetHelpText(checkBox, entry.AccessibilityLabel);
        checkBox.Checked += (_, _) => RefreshApprovalState();
        checkBox.Unchecked += (_, _) => RefreshApprovalState();
        _entryControls[entry.EntryIndex] = checkBox;
        row.Children.Add(checkBox);

        var details = new StackPanel();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new System.Windows.Controls.TextBlock
        {
            Text = entry.DisplayName,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Brush("TextFillColorPrimaryBrush", Brushes.Black)
        };
        header.Children.Add(name);
        var outcome = new System.Windows.Controls.TextBlock
        {
            Text = entry.OutcomeLabel,
            FontWeight = FontWeights.SemiBold,
            Foreground = OutcomeBrush(entry.Outcome),
            Margin = new Thickness(12, 0, 0, 0)
        };
        AutomationProperties.SetName(outcome, $"Outcome: {entry.OutcomeLabel}");
        _entryOutcomeLabels[entry.EntryIndex] = outcome;
        Grid.SetColumn(outcome, 1);
        header.Children.Add(outcome);
        details.Children.Add(header);

        details.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = entry.TargetLabel,
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 4),
            Foreground = Brush("TextFillColorTertiaryBrush", Brushes.Gray)
        });
        var actionPanel = new StackPanel();
        _entryActionPanels[entry.EntryIndex] = actionPanel;
        details.Children.Add(actionPanel);

        Grid.SetColumn(details, 1);
        row.Children.Add(details);
        card.Child = row;
        AutomationProperties.SetName(card, entry.AccessibilityLabel);
        return card;
    }

    private void RefreshApprovalState()
    {
        if (_restoreButton is null || _selectionSummary is null || _blockingNotice is null) return;
        RestorePlan approved = DeriveCurrentPlan();
        int selected = approved.Entries.Count(entry =>
            entry.Outcome is not (RestorePlanEntryOutcome.Excluded or RestorePlanEntryOutcome.Cancelled));
        int blockers = approved.BlockingErrors.Count;
        _selectionSummary.Text = blockers > 0
            ? $"{selected} selected · {blockers} blocking error{(blockers == 1 ? "" : "s")}"
            : $"{selected} selected · ready to restore";
        _blockingNotice.Visibility = blockers > 0 ? Visibility.Visible : Visibility.Collapsed;
        _restoreButton.IsEnabled = approved.CanExecute && approved.Actions.Count > 0;
        RenderEntryActions(approved);
        RenderGlobalActions(approved);
    }

    private void RenderEntryActions(RestorePlan approved)
    {
        RestorePlanPreview projection = RestorePlanPreviewBuilder.Build(approved);
        foreach (RestorePlanPreviewEntry entry in projection.Entries)
        {
            if (!_entryActionPanels.TryGetValue(entry.EntryIndex, out StackPanel? panel))
                continue;
            if (_entryOutcomeLabels.TryGetValue(
                    entry.EntryIndex,
                    out System.Windows.Controls.TextBlock? outcome))
            {
                outcome.Text = entry.OutcomeLabel;
                outcome.Foreground = OutcomeBrush(entry.Outcome);
                AutomationProperties.SetName(outcome, $"Outcome: {entry.OutcomeLabel}");
            }
            if (_entryCards.TryGetValue(entry.EntryIndex, out Border? card))
            {
                card.BorderBrush = entry.IsBlocking
                    ? Brush("SystemFillColorCriticalBrush", Brushes.Firebrick)
                    : entry.Outcome == RestorePreviewOutcomeKind.Ambiguous
                        ? Brush("SystemFillColorCautionBrush", Brushes.DarkOrange)
                        : Brush("CardStrokeColorDefaultBrush", Brushes.LightGray);
                card.BorderThickness = new Thickness(entry.IsBlocking ? 2 : 1);
            }
            panel.Children.Clear();
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = entry.Explanation,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 3),
                Foreground = Brush("TextFillColorTertiaryBrush", Brushes.Gray)
            });
            foreach (RestorePlanPreviewAction action in entry.Actions)
            {
                var actionText = new System.Windows.Controls.TextBlock
                {
                    Text = $"• {action.Label} — {action.Explanation}",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = action.IsDestructive
                        ? Brush("SystemFillColorCautionBrush", Brushes.DarkOrange)
                        : Brush("TextFillColorSecondaryBrush", Brushes.DimGray),
                    FontWeight = action.IsDestructive ? FontWeights.SemiBold : FontWeights.Normal
                };
                AutomationProperties.SetName(
                    actionText,
                    $"Planned action. {action.Label}. {action.Explanation}");
                panel.Children.Add(actionText);
            }
            foreach (string warning in entry.Warnings)
                panel.Children.Add(IssueText($"Warning: {warning}", isBlocking: false));
            foreach (string error in entry.BlockingErrors)
                panel.Children.Add(IssueText($"Blocking: {error}", isBlocking: true));
        }
    }

    private void RenderGlobalActions(RestorePlan approved)
    {
        _globalActionsPanel.Children.Clear();
        foreach (RestorePlanPreviewAction action in
                 RestorePlanPreviewBuilder.Build(approved).GlobalActions)
        {
            var text = new System.Windows.Controls.TextBlock
            {
                Text = $"Workspace action · {action.Label}",
                TextWrapping = TextWrapping.Wrap,
                FontWeight = action.IsDestructive ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = action.IsDestructive
                    ? Brush("SystemFillColorCautionBrush", Brushes.DarkOrange)
                    : Brush("TextFillColorSecondaryBrush", Brushes.DimGray)
            };
            AutomationProperties.SetName(
                text,
                $"Workspace action. {action.Label}. {action.Explanation}");
            _globalActionsPanel.Children.Add(text);
        }
    }

    private RestorePlan DeriveCurrentPlan()
    {
        int[] disabled = _entryControls
            .Where(item => item.Value.IsEnabled && item.Value.IsChecked != true)
            .Select(item => item.Key)
            .ToArray();
        return RestorePlanner.DeriveApprovedPlan(_previewPlan, disabled);
    }

    private static Border Notice(string text, Brush border, Brush background) => new()
    {
        BorderBrush = border,
        BorderThickness = new Thickness(1),
        Background = background,
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(10, 7, 10, 7),
        Child = new System.Windows.Controls.TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextFillColorPrimaryBrush", Brushes.Black)
        }
    };

    private static System.Windows.Controls.TextBlock IssueText(string text, bool isBlocking) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 11,
        Margin = new Thickness(0, 3, 0, 0),
        FontWeight = isBlocking ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = isBlocking
            ? Brush("SystemFillColorCriticalBrush", Brushes.Firebrick)
            : Brush("SystemFillColorCautionBrush", Brushes.DarkOrange)
    };

    private static Brush OutcomeBrush(RestorePreviewOutcomeKind outcome) => outcome switch
    {
        RestorePreviewOutcomeKind.Missing =>
            Brush("SystemFillColorCriticalBrush", Brushes.Firebrick),
        RestorePreviewOutcomeKind.Ambiguous or RestorePreviewOutcomeKind.Adapted =>
            Brush("SystemFillColorCautionBrush", Brushes.DarkOrange),
        RestorePreviewOutcomeKind.Exact =>
            Brush("SystemFillColorSuccessBrush", Brushes.SeaGreen),
        _ => Brush("TextFillColorSecondaryBrush", Brushes.DimGray)
    };

    private static Brush Brush(string resourceKey, Brush fallback) =>
        System.Windows.Application.Current?.Resources.Contains(resourceKey) == true
            ? System.Windows.Application.Current.Resources[resourceKey] as Brush ?? fallback
            : fallback;

    private static string ModeLabel(RestoreModeKind mode) => mode switch
    {
        RestoreModeKind.Standard => "Restore",
        RestoreModeKind.Selective => "Selective restore",
        RestoreModeKind.AlignAndMinimize => "Align and minimize others",
        _ => mode.ToString()
    };
}
