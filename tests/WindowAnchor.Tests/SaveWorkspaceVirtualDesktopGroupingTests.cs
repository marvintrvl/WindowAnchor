using WindowAnchor.Models;
using WindowAnchor.UI;

namespace WindowAnchor.Tests;

public class SaveWorkspaceVirtualDesktopGroupingTests
{
    [Fact]
    public void Inactive_desktop_windows_are_grouped_separately_from_current_desktop_windows()
    {
        var monitor = Monitor();
        WindowRecord current = Window("editor", "Current notes", CurrentDesktopId, true);
        WindowRecord inactive = Window("browser", "Research", InactiveDesktopId, false);

        List<SaveWorkspaceDialog.MonitorWindowGroup> groups =
            SaveWorkspaceDialog.BuildGroups([(monitor, [current, inactive])]);

        Assert.Equal(2, groups.Count);
        Assert.Contains("Current virtual desktop", groups[0].GroupHeader);
        Assert.Same(current, Assert.Single(groups[0].Windows).Record);
        Assert.Contains("Inactive virtual desktop 1", groups[1].GroupHeader);
        Assert.Same(inactive, Assert.Single(groups[1].Windows).Record);
    }

    [Fact]
    public void One_observed_desktop_keeps_the_compact_monitor_only_header()
    {
        var monitor = Monitor();
        WindowRecord current = Window("editor", "Current notes", CurrentDesktopId, true);

        SaveWorkspaceDialog.MonitorWindowGroup group = Assert.Single(
            SaveWorkspaceDialog.BuildGroups([(monitor, [current])]));

        Assert.StartsWith("Monitor 1:", group.GroupHeader);
        Assert.DoesNotContain("virtual desktop", group.GroupHeader, StringComparison.OrdinalIgnoreCase);
    }

    private const string CurrentDesktopId = "11111111-1111-4111-8111-111111111111";
    private const string InactiveDesktopId = "22222222-2222-4222-8222-222222222222";

    private static MonitorInfo Monitor() => new()
    {
        MonitorId = "primary",
        FriendlyName = "Main Monitor",
        Index = 0,
        WidthPixels = 3840,
        HeightPixels = 2160,
        IsPrimary = true
    };

    private static WindowRecord Window(
        string processName,
        string title,
        string desktopId,
        bool current) => new()
    {
        ProcessName = processName,
        DisplayName = processName,
        TitleSnippet = title,
        MonitorId = "primary",
        VirtualDesktopId = desktopId,
        IsOnCurrentVirtualDesktop = current
    };
}
