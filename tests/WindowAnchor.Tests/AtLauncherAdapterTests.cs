using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class AtLauncherAdapterTests
{
    private const string Javaw = @"C:\Users\test\AppData\Roaming\ATLauncher\jre\bin\javaw.exe";
    private const string Launcher = @"C:\Users\test\AppData\Roaming\ATLauncher\ATLauncher.exe";

    [Fact]
    public void Old_javaw_entries_launch_the_native_launcher_once_and_wait_for_console()
    {
        WorkspaceEntry main = Entry("ATLauncher");
        WorkspaceEntry console = Entry("ATLauncher Console");
        RestorePlan plan = RestorePlanner.Build(
            Snapshot(main, console),
            new RestoreLiveInventory
            {
                Resources =
                [
                    new(0, RestoreResourceKind.Executable, RestoreResourceAvailability.Available, Javaw),
                    new(0, RestoreResourceKind.AppAdapterLauncher, RestoreResourceAvailability.Available, Launcher),
                    new(1, RestoreResourceKind.Executable, RestoreResourceAvailability.Available, Javaw),
                    new(1, RestoreResourceKind.AppAdapterLauncher, RestoreResourceAvailability.Available, Launcher)
                ]
            },
            new RestoreMonitorTopology { Monitors = [Monitor()] },
            RestoreMode.Resume);

        RestoreAction launch = Assert.Single(plan.Entries[0].Actions,
            action => action.Kind == RestoreActionKind.LaunchApplication);
        Assert.Equal(Launcher, launch.Target);
        Assert.Contains(plan.Entries[0].Actions,
            action => action.Kind == RestoreActionKind.AwaitWindowAppearance);
        Assert.DoesNotContain(plan.Entries[1].Actions,
            action => RestoreExecutionSupport.IsLaunch(action.Kind));
        Assert.Single(plan.Entries[1].Actions,
            action => action.Kind == RestoreActionKind.AwaitWindowAppearance);
        Assert.Equal(RestorePlanEntryOutcome.AwaitingRunningApplication, plan.Entries[1].Outcome);
        Assert.Equal(AtLauncherAdapter.AdapterName, plan.Entries[0].SavedIdentity.AppAdapterIdentity);
    }

    private static WorkspaceEntry Entry(string title) => new()
    {
        ExecutablePath = Javaw,
        ProcessName = "javaw",
        WindowClassName = "SunAwtFrame",
        MonitorId = "primary",
        Position = new WindowRecord
        {
            ExecutablePath = Javaw,
            ProcessName = "javaw",
            ClassName = "SunAwtFrame",
            TitleSnippet = title,
            MonitorId = "primary",
            NormalRight = 800,
            NormalBottom = 600,
            SavedDpi = 96
        }
    };

    private static WorkspaceSnapshot Snapshot(params WorkspaceEntry[] entries) => new()
    {
        Name = "ATLauncher",
        MonitorFingerprint = "test",
        SavedAt = DateTime.UnixEpoch,
        Monitors = [new MonitorInfo
        {
            MonitorId = "primary", Index = 0, WidthPixels = 1920,
            HeightPixels = 1080, Dpi = 96, IsPrimary = true
        }],
        Entries = entries.ToList()
    };

    private static RestoreMonitor Monitor() =>
        new("primary", 0, 0, 0, 1920, 1080, 96, true);
}
