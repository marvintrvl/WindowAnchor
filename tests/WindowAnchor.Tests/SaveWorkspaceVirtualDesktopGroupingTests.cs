using WindowAnchor.Models;
using WindowAnchor.UI;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class SaveWorkspaceVirtualDesktopGroupingTests
{
    [Fact]
    public void Catalog_order_and_names_win_over_current_desktop_and_guid_order()
    {
        WindowRecord current = Window("editor", "Notes", CurrentDesktopId, true);
        WindowRecord inactive = Window("browser", "Research", InactiveDesktopId, false);
        var groups = SaveWorkspaceDialog.BuildGroups([(Monitor(), [current, inactive])], desktops:
            [new(Guid.Parse(InactiveDesktopId), "Work", false),
             new(Guid.Parse(CurrentDesktopId), "Desktop 2", true)]);
        Assert.StartsWith("Work  ·", groups[0].GroupHeader);
        Assert.StartsWith("Desktop 2 (Current)", groups[1].GroupHeader);
        Assert.Same(inactive, Assert.Single(groups[0].Windows).Record);
        Assert.Same(current, Assert.Single(groups[1].Windows).Record);
    }

    [Fact]
    public void Empty_second_desktop_is_shown_and_unavailable_membership_is_not_lost()
    {
        WindowRecord current = Window("editor", "Notes", CurrentDesktopId, true);
        WindowRecord unknown = Window("other", "Unknown", "", true);
        var groups = SaveWorkspaceDialog.BuildGroups([(Monitor(), [current, unknown])], desktops:
            [new(Guid.Parse(CurrentDesktopId), "Desktop 1", true),
             new(Guid.Parse(InactiveDesktopId), "Desktop 2", false)]);
        Assert.Equal(3, groups.Count);
        Assert.Contains("Desktop 2  ·  No capturable windows", groups[1].GroupHeader);
        Assert.Empty(groups[1].Windows);
        Assert.Same(unknown, Assert.Single(groups[2].Windows).Record);
    }

    [Fact]
    public void Stale_catalog_does_not_drop_new_desktop_windows()
    {
        WindowRecord inactive = Window("browser", "Research", InactiveDesktopId, false);
        var groups = SaveWorkspaceDialog.BuildGroups([(Monitor(), [inactive])], desktops:
            [new(Guid.Parse(CurrentDesktopId), "Desktop 1", true)]);
        Assert.Same(inactive, Assert.Single(groups.SelectMany(group => group.Windows)).Record);
    }

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
