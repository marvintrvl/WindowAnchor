using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class LogicalPathAliasTests
{
    [Fact]
    public void Nested_aliases_choose_the_most_specific_matching_root()
    {
        var aliases = new Dictionary<string, string>
        {
            ["PROJECTS"] = @"D:\Work",
            ["WINDOWANCHOR"] = @"D:\Work\WindowAnchor"
        };

        string? logical = LogicalPathAliasResolver.ToLogicalPath(
            @"D:\Work\WindowAnchor\src\WindowAnchor.sln", aliases);

        Assert.Equal("${WINDOWANCHOR}\\src\\WindowAnchor.sln", logical);
        Assert.Equal(@"D:\Work\WindowAnchor\src\WindowAnchor.sln",
            LogicalPathAliasResolver.Resolve(logical, aliases));
    }

    [Fact]
    public void Unmapped_or_malformed_logical_paths_stay_unresolved_without_changing_absolute_fallbacks()
    {
        Assert.Null(LogicalPathAliasResolver.Resolve("${OFFLINE}\\report.docx",
            new Dictionary<string, string> { ["PROJECTS"] = @"D:\Projects" }));
        Assert.Null(LogicalPathAliasResolver.Resolve("not-a-logical-path",
            new Dictionary<string, string> { ["PROJECTS"] = @"D:\Projects" }));
    }

    [Fact]
    public void Alias_settings_round_trip_and_reject_invalid_names_or_relative_roots()
    {
        using var directory = new TestDirectory();
        string settingsPath = Path.Combine(directory.Path, "settings.json");
        var service = new SettingsService(settingsPath, new StorageService(directory.Path));

        service.SetLogicalPathAlias("projects", @"D:\Projects");
        var reloaded = new SettingsService(settingsPath, new StorageService(directory.Path));

        Assert.Equal(@"D:\Projects", reloaded.Settings.LogicalPathAliases!["PROJECTS"]);
        Assert.Throws<ArgumentException>(() => service.SetLogicalPathAlias("not valid", @"D:\Projects"));
        Assert.Throws<ArgumentException>(() => service.SetLogicalPathAlias("PROJECTS", "relative"));
    }

    [Fact]
    public void Restore_observation_prefers_the_existing_absolute_path_before_its_alias()
    {
        var resources = new FakeRestoreResourceBoundary { DefaultAvailability = RestoreResourceAvailability.Available };
        RestoreResourceObservation observation = ObserveExecutable(resources,
            @"Z:\Offline\editor.exe", "${PROJECTS}\\editor.exe", @"D:\Projects");

        Assert.Equal(@"Z:\Offline\editor.exe", observation.ResolvedTarget);
        Assert.Single(resources.Observations);
    }

    [Fact]
    public void Restore_observation_uses_alias_when_the_absolute_path_is_missing()
    {
        var resources = new FakeRestoreResourceBoundary { DefaultAvailability = RestoreResourceAvailability.Missing };
        resources.AvailabilityByTarget[@"D:\Projects\editor.exe"] = RestoreResourceAvailability.Available;

        RestoreResourceObservation observation = ObserveExecutable(resources,
            @"Z:\Offline\editor.exe", "${PROJECTS}\\editor.exe", @"D:\Projects");

        Assert.Equal(RestoreResourceAvailability.Available, observation.Availability);
        Assert.Equal(@"D:\Projects\editor.exe", observation.ResolvedTarget);
        Assert.Equal([@"Z:\Offline\editor.exe", @"D:\Projects\editor.exe"],
            resources.Observations.Select(item => item.Target));
    }

    [Fact]
    public void Unresolved_alias_falls_back_to_the_existing_missing_resource_outcome()
    {
        var resources = new FakeRestoreResourceBoundary { DefaultAvailability = RestoreResourceAvailability.Missing };

        RestoreResourceObservation observation = ObserveExecutable(resources,
            @"Z:\Offline\editor.exe", "${OFFLINE}\\editor.exe", @"D:\Projects");

        Assert.Equal(RestoreResourceAvailability.Missing, observation.Availability);
        Assert.Empty(observation.ResolvedTarget);
        Assert.Single(resources.Observations);
    }

    [Fact]
    public async Task Capture_retains_absolute_paths_and_records_portable_aliases()
    {
        using var directory = new TestDirectory();
        var storage = new StorageService(directory.Path);
        var settings = new SettingsService(Path.Combine(directory.Path, "settings.json"), storage);
        settings.SetLogicalPathAlias("PROJECTS", @"D:\Projects");
        var monitor = new FakeMonitorInventory
        {
            Monitors = [new MonitorInfo { MonitorId = "primary", Index = 0, IsPrimary = true }]
        };
        var service = new WorkspaceService(
            storage,
            new FakeWindowInventory(),
            new RecordingWindowMutation(),
            monitor,
            new JumpListService(),
            settingsService: settings);
        var window = new WindowRecord
        {
            ExecutablePath = @"D:\Projects\Tools\editor.exe",
            ProcessName = "editor",
            ClassName = "EditorWindow",
            MonitorId = "primary",
            NormalRight = 800,
            NormalBottom = 600
        };

        WorkspaceCaptureResult result = await service.CaptureWorkspaceAsync(
            "Portable workspace",
            saveFiles: false,
            selectedWindows: [window],
            captureBrowserSessions: false);

        WorkspaceEntry entry = Assert.Single(result.Snapshot.Entries);
        Assert.Equal(@"D:\Projects\Tools\editor.exe", entry.ExecutablePath);
        Assert.Equal("${PROJECTS}\\Tools\\editor.exe", entry.LogicalExecutablePath);
    }

    private static RestoreResourceObservation ObserveExecutable(
        FakeRestoreResourceBoundary resources,
        string absolute,
        string logical,
        string mappedRoot)
    {
        using var directory = new TestDirectory();
        var storage = new StorageService(directory.Path);
        var settings = new SettingsService(Path.Combine(directory.Path, "settings.json"), storage);
        settings.SetLogicalPathAlias("PROJECTS", mappedRoot);
        var builder = new RestoreObservationBuilder(
            new FakeWindowInventory(),
            new FakeMonitorInventory(),
            resources,
            new FakePackagedAppResolver(),
            new WebAppService(),
            settings,
            browserSessionConnector: null);
        var snapshot = new WorkspaceSnapshot
        {
            Entries =
            [
                new WorkspaceEntry
                {
                    ExecutablePath = absolute,
                    LogicalExecutablePath = logical,
                    ProcessName = "editor",
                    Position = new WindowRecord { ExecutablePath = absolute, ProcessName = "editor" }
                }
            ]
        };

        return Assert.Single(builder.Build(snapshot,
            new Dictionary<IntPtr, (uint Pid, WindowRecord Record)>()).Inventory.Resources);
    }
}
