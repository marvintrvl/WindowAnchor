using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using WindowAnchor.Models;
using WindowAnchor.Services;
using TextBlock = System.Windows.Controls.TextBlock;

namespace WindowAnchor.UI;

/// <summary>Collects explicit, local-only metadata choices before exporting one workspace.</summary>
internal sealed class WorkspaceExportDialog : FluentWindow
{
    private readonly RadioButton _exactBackup;
    private readonly RadioButton _portableRedacted;
    private readonly CheckBox _layout;
    private readonly CheckBox _applicationIdentities;
    private readonly CheckBox _filesAndFolders;
    private readonly CheckBox _browserUrls;
    private readonly CheckBox _machineIdentifiers;
    private readonly CheckBox _logicalAliases;

    internal WorkspaceExportOptions? Options { get; private set; }

    internal WorkspaceExportDialog(WorkspaceSnapshot workspace)
    {
        Title = "Export Workspace";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        ShowInTaskbar = false;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        var titleBar = new TitleBar { Title = "Export Workspace", ShowMinimize = false, ShowMaximize = false };
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        var content = new StackPanel { Margin = new Thickness(20, 16, 20, 20) };
        Grid.SetRow(content, 1);
        root.Children.Add(content);
        content.Children.Add(new TextBlock
        {
            Text = $"Create a local copy of “{workspace.Name}”. Settings, credentials, cookies, document contents, and native browser-host data are never exported.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });

        _exactBackup = new RadioButton
        {
            Content = "Exact backup — keep the selected local restore metadata",
            GroupName = "exportMode",
            IsChecked = true,
            Margin = new Thickness(0, 2, 0, 2)
        };
        _portableRedacted = new RadioButton
        {
            Content = "Portable / redacted — remove local absolute paths by default",
            GroupName = "exportMode",
            Margin = new Thickness(0, 2, 0, 10)
        };
        _exactBackup.Checked += (_, _) => ApplyDefaults(WorkspaceExportOptions.ExactBackup());
        _portableRedacted.Checked += (_, _) => ApplyDefaults(WorkspaceExportOptions.PortableRedacted());
        content.Children.Add(_exactBackup);
        content.Children.Add(_portableRedacted);

        content.Children.Add(new TextBlock
        {
            Text = "Include in this file",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 5)
        });
        _layout = AddOption(content, "Layout and geometry", "Monitor arrangement, placement, and saved window size.");
        _applicationIdentities = AddOption(content, "Application identities", "Executable, process, class, and supported app metadata.");
        _filesAndFolders = AddOption(content, "Files and folders", "Open document, Explorer, Terminal, and editor locations.");
        _browserUrls = AddOption(content, "Browser URLs and tabs", "Dedicated browser locations and connector tab sessions.");
        _machineIdentifiers = AddOption(content, "Machine identifiers", "Monitor, profile, and virtual-desktop identifiers.");
        _logicalAliases = AddOption(content, "Logical path aliases", "Portable ${ALIAS} path forms, never the alias mapping itself.");

        content.Children.Add(new TextBlock
        {
            Text = "The receiving device never receives your WindowAnchor settings or alias roots; configure aliases there if needed.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72,
            FontSize = 12,
            Margin = new Thickness(0, 10, 0, 14)
        });
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var cancel = new Wpf.Ui.Controls.Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => Close();
        var export = new Wpf.Ui.Controls.Button { Content = "Choose destination", Appearance = ControlAppearance.Primary };
        export.Click += (_, _) => Commit();
        buttons.Children.Add(cancel);
        buttons.Children.Add(export);
        content.Children.Add(buttons);
        Content = root;

        ApplyDefaults(WorkspaceExportOptions.ExactBackup());
    }

    private static CheckBox AddOption(Panel parent, string label, string detail)
    {
        var checkBox = new CheckBox
        {
            IsChecked = true,
            Margin = new Thickness(0, 3, 0, 3),
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = label, FontWeight = FontWeights.Medium },
                    new TextBlock { Text = detail, Opacity = 0.72, FontSize = 11, TextWrapping = TextWrapping.Wrap }
                }
            }
        };
        parent.Children.Add(checkBox);
        return checkBox;
    }

    private void ApplyDefaults(WorkspaceExportOptions defaults)
    {
        _layout.IsChecked = defaults.IncludeLayout;
        _applicationIdentities.IsChecked = defaults.IncludeApplicationIdentities;
        _filesAndFolders.IsChecked = defaults.IncludeFilesAndFolders;
        _browserUrls.IsChecked = defaults.IncludeBrowserUrls;
        _machineIdentifiers.IsChecked = defaults.IncludeMachineIdentifiers;
        _logicalAliases.IsChecked = defaults.IncludeLogicalPathAliases;
    }

    private void Commit()
    {
        Options = new WorkspaceExportOptions
        {
            Mode = _portableRedacted.IsChecked == true
                ? WorkspaceExportMode.PortableRedacted
                : WorkspaceExportMode.ExactBackup,
            IncludeLayout = _layout.IsChecked == true,
            IncludeApplicationIdentities = _applicationIdentities.IsChecked == true,
            IncludeFilesAndFolders = _filesAndFolders.IsChecked == true,
            IncludeBrowserUrls = _browserUrls.IsChecked == true,
            IncludeMachineIdentifiers = _machineIdentifiers.IsChecked == true,
            IncludeLogicalPathAliases = _logicalAliases.IsChecked == true
        };
        DialogResult = true;
    }
}

/// <summary>Presents a staged import and requires an explicit copy action before any storage write.</summary>
internal sealed class WorkspaceImportPreviewDialog : FluentWindow
{
    internal WorkspaceImportPreviewDialog(WorkspaceImportPreview preview)
    {
        Title = "Import Workspace";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        ShowInTaskbar = false;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        var titleBar = new TitleBar { Title = "Import Workspace", ShowMinimize = false, ShowMaximize = false };
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);
        var content = new StackPanel { Margin = new Thickness(20, 16, 20, 20) };
        Grid.SetRow(content, 1);
        root.Children.Add(content);

        string mode = preview.Mode == WorkspaceExportMode.ExactBackup ? "Exact backup" : "Portable / redacted";
        content.Children.Add(new TextBlock
        {
            Text = $"“{preview.Workspace.Name}”\n{preview.Workspace.Entries.Count} saved window{(preview.Workspace.Entries.Count == 1 ? "" : "s")} · {mode}",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        if (preview.WasMigrated)
        {
            content.Children.Add(new TextBlock
            {
                Text = "This older export will be migrated to the current local schema before it is saved.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });
        }
        content.Children.Add(new TextBlock
        {
            Text = ConflictMessage(preview.ConflictKind),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Wpf.Ui.Controls.Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => Close();
        var import = new Wpf.Ui.Controls.Button { Content = "Import as Copy", Appearance = ControlAppearance.Primary };
        import.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(import);
        content.Children.Add(buttons);
        Content = root;
    }

    private static string ConflictMessage(WorkspaceImportConflictKind conflict) => conflict switch
    {
        WorkspaceImportConflictKind.None => "The file is valid. Importing adds it as a new local workspace.",
        WorkspaceImportConflictKind.Name => "A workspace with this name already exists. Importing creates a distinct copy with an “Imported” suffix.",
        WorkspaceImportConflictKind.WorkspaceId => "A workspace with this stable ID already exists. Importing creates a new ID and keeps the existing workspace unchanged.",
        _ => "A workspace with this name and stable ID already exists. Importing creates a new ID and an “Imported” name; the existing workspace is never overwritten."
    };
}
