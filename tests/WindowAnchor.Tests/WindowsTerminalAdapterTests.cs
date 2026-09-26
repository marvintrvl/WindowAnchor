using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using WindowAnchor.Models;
using WindowAnchor.Services;
using Xunit;

namespace WindowAnchor.Tests;

public sealed class WindowsTerminalAdapterTests
{
    [Fact]
    public void Capture_keeps_each_tab_profile_directory_and_active_index()
    {
        var adapter = new WindowsTerminalAdapter();
        var window = new WindowRecord
        {
            ProcessName = "WindowsTerminal",
            ExecutablePath = @"C:\WindowsApps\WindowsTerminal.exe",
            TerminalTabs =
            [
                new TerminalTab { Profile = "{11111111-1111-1111-1111-111111111111}", StartingDirectory = @"C:\One", TitleHint = "secret title" },
                new TerminalTab { Profile = "{22222222-2222-2222-2222-222222222222}", StartingDirectory = @"C:\Two" }
            ],
            TerminalActiveTabIndex = 1
        };
        var context = new AppAdapterCaptureContext(window, true,
            new CaptureResourceSearchBudget(false, TimeSpan.Zero, default), null, 1, 1, false);

        WorkspaceEntry saved = Assert.IsType<WorkspaceEntry>(adapter.TryCapture(context));

        Assert.Equal("TERMINAL_TABS", saved.FileSource);
        Assert.Equal(2, saved.TerminalTabs.Count);
        Assert.Equal(@"C:\One", saved.TerminalTabs[0].StartingDirectory);
        Assert.Equal(@"C:\Two", saved.TerminalTabs[1].StartingDirectory);
        Assert.Equal(1, saved.TerminalActiveTabIndex);
        Assert.Empty(saved.TerminalTabs[0].TitleHint);
    }

    [Fact]
    public void Launch_builds_one_new_window_with_two_distinct_tabs_and_directories()
    {
        var entry = new WorkspaceEntry
        {
            ProcessName = "WindowsTerminal",
            TerminalTabs =
            [
                new TerminalTab { Profile = "PowerShell", StartingDirectory = @"C:\First Folder\" },
                new TerminalTab { Profile = "Ubuntu", StartingDirectory = @"D:\Second" }
            ],
            TerminalActiveTabIndex = 1
        };
        var adapter = new WindowsTerminalAdapter();
        var resources = new Dictionary<(int, RestoreResourceKind), RestoreResourceObservation>
        {
            [(0, RestoreResourceKind.TerminalLauncher)] = new(0,
                RestoreResourceKind.TerminalLauncher, RestoreResourceAvailability.Available,
                @"C:\Users\Test\AppData\Local\Microsoft\WindowsApps\wt.exe")
        };
        var context = Context(entry, resources);

        Assert.True(adapter.TryPlanLaunch(context, out RestoreLaunchDecision decision));
        RestoreAction action = Assert.Single(decision.Actions);
        Assert.Equal(RestoreActionKind.LaunchApplication, action.Kind);
        Assert.Equal(resources[(0, RestoreResourceKind.TerminalLauncher)].ResolvedTarget, action.Target);
        Assert.True(action.UseShellExecute);
        Assert.StartsWith("-w -1 new-tab", action.Arguments);
        Assert.Contains("new-tab --profile \"PowerShell\" --startingDirectory \"C:\\First Folder\\\\\"", action.Arguments);
        Assert.Contains(" ; new-tab --profile \"Ubuntu\" --startingDirectory \"D:\\Second\"", action.Arguments);
        Assert.EndsWith(" ; focus-tab -t 1", action.Arguments);
        Assert.Equal(2, action.TerminalDirectories?.Count);
    }

    [Fact]
    public void Missing_wt_alias_blocks_launch_instead_of_reporting_stale_later()
    {
        var entry = new WorkspaceEntry
        {
            ProcessName = "WindowsTerminal",
            TerminalTabs = [new TerminalTab { StartingDirectory = @"C:\Projects" }]
        };
        var resources = new Dictionary<(int, RestoreResourceKind), RestoreResourceObservation>
        {
            [(0, RestoreResourceKind.TerminalLauncher)] = new(0,
                RestoreResourceKind.TerminalLauncher, RestoreResourceAvailability.Missing)
        };

        Assert.True(new WindowsTerminalAdapter().TryPlanLaunch(Context(entry, resources),
            out RestoreLaunchDecision decision));
        Assert.Empty(decision.Actions);
        Assert.NotEmpty(decision.BlockingErrors);
    }

    [Fact]
    public void Missing_terminal_directory_is_caught_in_preview_and_preflight()
    {
        var entry = new WorkspaceEntry
        {
            ProcessName = "WindowsTerminal",
            TerminalTabs = [new TerminalTab { StartingDirectory = @"Z:\MissingProject" }]
        };
        var resources = new Dictionary<(int, RestoreResourceKind), RestoreResourceObservation>
        {
            [(0, RestoreResourceKind.TerminalDirectories)] = new(0,
                RestoreResourceKind.TerminalDirectories, RestoreResourceAvailability.Missing),
            [(0, RestoreResourceKind.TerminalLauncher)] = new(0,
                RestoreResourceKind.TerminalLauncher, RestoreResourceAvailability.Available, @"C:\wt.exe")
        };
        Assert.True(new WindowsTerminalAdapter().TryPlanLaunch(Context(entry, resources),
            out RestoreLaunchDecision blocked));
        Assert.Empty(blocked.Actions);

        using var directory = new TestDirectory();
        string launcher = Path.Combine(directory.Path, "wt.exe");
        File.WriteAllText(launcher, "");
        var action = new RestoreAction(0, RestoreActionKind.LaunchApplication, null,
            launcher, "", false, null, "terminal")
        {
            TerminalDirectories = [@"Z:\MissingProject"]
        };
        Assert.Equal(RestoreResourceAvailability.Missing,
            new FileSystemRestoreResourceBoundary().Revalidate(action).Availability);
    }

    [Fact]
    public void Wt_alias_preflight_is_limited_to_approved_terminal_session_actions()
    {
        var action = new RestoreAction(0, RestoreActionKind.LaunchApplication, null,
            "wt.exe", "-w -1 new-tab --startingDirectory \"C:\\Projects\"",
            true, null, "terminal")
        {
            TerminalDirectories = [Environment.CurrentDirectory]
        };
        var boundary = new FileSystemRestoreResourceBoundary();

        Assert.Equal(RestoreResourceAvailability.Available,
            boundary.Revalidate(action).Availability);
        Assert.Equal(RestoreResourceAvailability.Missing,
            boundary.Revalidate(action with { TerminalDirectories = null }).Availability);
    }

    [Fact]
    public void Prompt_report_is_mapped_by_session_marker()
    {
        using var directory = new TestDirectory();
        Guid session = Guid.NewGuid();
        string file = Path.Combine(directory.Path, session.ToString("N") + ".json");
        File.WriteAllText(file,
            "{\"sessionId\":\"" + session.ToString("D") +
            "\",\"profileId\":\"33333333-3333-3333-3333-333333333333\",\"directory\":\"C:\\\\Projects\",\"updatedAtUtc\":\"" +
            DateTime.UtcNow.ToString("O") + "\"}");

        TerminalTab tab = Assert.IsType<TerminalTab>(TerminalSessionTracker.Read(
            session.ToString("N")[..12], directory.Path));
        Assert.Equal(@"C:\Projects", tab.StartingDirectory);
        Assert.Equal("{33333333-3333-3333-3333-333333333333}", tab.Profile);
    }

    [Fact]
    public void V9_workspace_migrates_without_inventing_terminal_tabs()
    {
        var snapshot = new WorkspaceSnapshot
        {
            SchemaVersion = 9,
            Name = "Old terminal capture",
            Entries = [new WorkspaceEntry { ProcessName = "WindowsTerminal" }]
        };
        snapshot.EnsureLayoutVariants();
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        MigratedDocument<WorkspaceSnapshot> migrated = WorkspaceSchemaMigrator.Migrate(
            JsonSerializer.Serialize(snapshot, options), "old.workspace.json", options);

        Assert.True(migrated.WasMigrated);
        Assert.Equal(10, migrated.Value.SchemaVersion);
        Assert.Empty(Assert.Single(migrated.Value.Entries).TerminalTabs);
    }

    private static AppAdapterLaunchContext Context(WorkspaceEntry entry,
        IReadOnlyDictionary<(int, RestoreResourceKind), RestoreResourceObservation> resources) =>
        new(0, entry, false, false, false, false,
            Array.Empty<RunningApplicationIdentity>(), new HashSet<string>(), resources,
            new RestoreTargetPlacement("monitor", 0, RestoreMonitorMappingKind.ExactStableId,
                0, 0, 800, 600, 1, 96, 96, false));
}
