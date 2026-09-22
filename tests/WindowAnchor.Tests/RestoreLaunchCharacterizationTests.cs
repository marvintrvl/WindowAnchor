using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WindowAnchor.Models;
using WindowAnchor.Services;
using Xunit;

namespace WindowAnchor.Tests;

/// <summary>Locks current launch behavior before future adapter-policy migrations.</summary>
public class RestoreLaunchCharacterizationTests
{
    [Fact]
    public void Pwa_uses_an_available_shortcut_before_its_executable_fallback()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\chrome.exe", "chrome");
        entry.IsWebApp = true;
        entry.AppUserModelId = "Chrome.App.calendar";
        entry.WebAppShortcutPath = @"C:\Users\Test\Desktop\Calendar.lnk";
        entry.WebAppLaunchTarget = @"C:\Apps\chrome_proxy.exe";

        RestoreAction action = LaunchAction(entry,
            new RestoreResourceObservation(0, RestoreResourceKind.WebAppShortcut, RestoreResourceAvailability.Available,
                @"C:\Users\Test\Desktop\Calendar.lnk"));

        Assert.Equal(RestoreActionKind.LaunchWebApp, action.Kind);
        Assert.Equal(entry.WebAppShortcutPath, action.Target);
        Assert.True(action.UseShellExecute);
        Assert.Equal("", action.Arguments);
    }

    [Fact]
    public void Pwa_uses_its_saved_executable_and_arguments_when_shortcut_is_missing()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\chrome.exe", "chrome");
        entry.IsWebApp = true;
        entry.AppUserModelId = "Chrome.App.calendar";
        entry.WebAppShortcutPath = @"C:\Users\Test\Desktop\Calendar.lnk";
        entry.WebAppLaunchTarget = @"C:\Apps\chrome_proxy.exe";
        entry.WebAppLaunchArguments = "--profile-directory=Work --app-id=calendar";

        RestoreAction action = LaunchAction(entry,
            new(0, RestoreResourceKind.WebAppShortcut, RestoreResourceAvailability.Missing),
            new RestoreResourceObservation(0, RestoreResourceKind.Executable, RestoreResourceAvailability.Available,
                entry.WebAppLaunchTarget));

        Assert.Equal(RestoreActionKind.LaunchWebApp, action.Kind);
        Assert.Equal(entry.WebAppLaunchTarget, action.Target);
        Assert.Equal(entry.WebAppLaunchArguments, action.Arguments);
        Assert.False(action.UseShellExecute);
    }

    [Fact]
    public void Dedicated_browser_window_reopens_the_saved_url_in_a_new_window()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\brave.exe", "brave");
        entry.IsDedicatedBrowserWindow = true;
        entry.BrowserUrl = "https://charts.example.test/workspace";

        RestoreAction action = LaunchAction(entry,
            new RestoreResourceObservation(0, RestoreResourceKind.Executable, RestoreResourceAvailability.Available,
                entry.ExecutablePath));

        Assert.Equal(RestoreActionKind.LaunchDedicatedBrowser, action.Kind);
        Assert.Equal($"--new-window \"{entry.BrowserUrl}\"", action.Arguments);
        Assert.False(action.UseShellExecute);
    }

    [Fact]
    public void Explorer_folder_capture_preserves_the_folder_as_the_launch_target()
    {
        var adapter = new ExplorerFolderAdapter();
        var window = new WindowRecord
        {
            ExecutablePath = @"C:\Windows\explorer.exe",
            ProcessName = "explorer",
            FolderPath = @"C:\Projects\WindowAnchor",
            ClassName = "CabinetWClass"
        };

        WorkspaceEntry entry = Assert.IsType<WorkspaceEntry>(adapter.TryCapture(new AppAdapterCaptureContext(
            window,
            SaveFiles: true,
            new CaptureResourceSearchBudget(false, TimeSpan.Zero, default),
            Progress: null,
            ResourceProgressCurrent: 1,
            ResourceProgressTotal: 1,
            BuildFullJumpListCache: false)));

        Assert.Equal("EXPLORER_FOLDER", entry.FileSource);
        Assert.Equal(window.FolderPath, entry.FilePath);
        Assert.Equal(window.FolderPath, entry.LaunchArg);
    }

    [Theory]
    [InlineData("Code", false)]
    [InlineData("Cursor", true)]
    public void Workspace_launches_preserve_current_editor_specific_behavior(
        string processName,
        bool usesRegisteredHandler)
    {
        WorkspaceEntry entry = Entry($@"C:\Apps\{processName}.exe", processName);
        entry.LaunchArg = @"C:\Projects\WindowAnchor\WindowAnchor.code-workspace";

        RestoreAction action = LaunchAction(entry,
            new(0, RestoreResourceKind.LaunchTarget, RestoreResourceAvailability.Available, entry.LaunchArg),
            new RestoreResourceObservation(0, RestoreResourceKind.Executable,
                RestoreResourceAvailability.Available, entry.ExecutablePath));

        Assert.Equal(RestoreActionKind.OpenResource, action.Kind);
        Assert.Equal(usesRegisteredHandler, action.UseShellExecute);
        Assert.Equal(usesRegisteredHandler ? entry.LaunchArg : entry.ExecutablePath, action.Target);
        Assert.Equal(usesRegisteredHandler ? "" : $"\"{entry.LaunchArg}\"", action.Arguments);
    }

    [Fact]
    public void Msix_entry_uses_the_stable_app_user_model_id_activation()
    {
        WorkspaceEntry entry = Entry(
            @"C:\Program Files\WindowsApps\Contoso.Editor_1.0_x64__abc\Editor.exe",
            "Editor");
        entry.AppUserModelId = "Contoso.Editor_abc!App";

        RestoreAction action = LaunchAction(entry);

        Assert.Equal(RestoreActionKind.ActivatePackagedApplication, action.Kind);
        Assert.Equal("explorer.exe", action.Target);
        Assert.Equal($"shell:AppsFolder\\{entry.AppUserModelId}", action.Arguments);
        Assert.True(action.UseShellExecute);
    }

    [Fact]
    public void Generic_win32_entry_launches_its_observed_executable()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\writer.exe", "writer");

        RestoreAction action = LaunchAction(entry,
            new RestoreResourceObservation(0, RestoreResourceKind.Executable,
                RestoreResourceAvailability.Available, entry.ExecutablePath));

        Assert.Equal(RestoreActionKind.LaunchApplication, action.Kind);
        Assert.Equal(entry.ExecutablePath, action.Target);
        Assert.True(action.UseShellExecute);
    }

    [Fact]
    public void Unavailable_document_resource_blocks_only_its_entry()
    {
        WorkspaceEntry unavailable = Entry(@"C:\Apps\writer.exe", "writer");
        unavailable.LaunchArg = @"Z:\Unavailable\Draft.docx";
        WorkspaceEntry available = Entry(@"C:\Apps\notes.exe", "notes");
        RestorePlan plan = Plan(unavailable, available,
            new(0, RestoreResourceKind.LaunchTarget, RestoreResourceAvailability.Missing),
            new(1, RestoreResourceKind.Executable, RestoreResourceAvailability.Available, available.ExecutablePath));

        Assert.Equal(RestorePlanEntryOutcome.Blocked, plan.Entries[0].Outcome);
        Assert.Contains(plan.Entries[0].BlockingErrors,
            issue => issue.Code == RestorePlanIssueCode.MissingResource);
        Assert.Equal(RestorePlanEntryOutcome.LaunchRequired, plan.Entries[1].Outcome);
        Assert.Contains(plan.Entries[1].Actions, action => action.Kind == RestoreActionKind.LaunchApplication);
    }

    private static RestoreAction LaunchAction(WorkspaceEntry entry, params RestoreResourceObservation[] resources) =>
        Assert.Single(Plan(entry, resources).Entries.Single().Actions, action =>
            action.Kind is RestoreActionKind.LaunchWebApp or RestoreActionKind.LaunchDedicatedBrowser or
                RestoreActionKind.OpenResource or RestoreActionKind.ActivatePackagedApplication or
                RestoreActionKind.LaunchApplication);

    private static RestorePlan Plan(WorkspaceEntry entry, params RestoreResourceObservation[] resources) =>
        Plan([entry], resources);

    private static RestorePlan Plan(
        WorkspaceEntry first,
        WorkspaceEntry second,
        params RestoreResourceObservation[] resources) => Plan([first, second], resources);

    private static RestorePlan Plan(
        IReadOnlyList<WorkspaceEntry> entries,
        IReadOnlyList<RestoreResourceObservation> resources) => RestorePlanner.Build(
            new WorkspaceSnapshot
            {
                WorkspaceId = "characterization",
                Name = "Characterization",
                Entries = entries.ToList()
            },
            new RestoreLiveInventory { Resources = resources },
            new RestoreMonitorTopology
            {
                Monitors = [new RestoreMonitor("primary", 0, 0, 0, 1920, 1080, 96, true)]
            },
            RestoreMode.Standard);

    private static WorkspaceEntry Entry(string executablePath, string processName) => new()
    {
        ExecutablePath = executablePath,
        ProcessName = processName,
        WindowClassName = "CharacterizationWindow",
        MonitorId = "primary",
        Position = new WindowRecord
        {
            ExecutablePath = executablePath,
            ProcessName = processName,
            ClassName = "CharacterizationWindow",
            MonitorId = "primary",
            SavedDpi = 96,
            NormalRight = 800,
            NormalBottom = 600,
            ShowCmd = 1
        }
    };
}
