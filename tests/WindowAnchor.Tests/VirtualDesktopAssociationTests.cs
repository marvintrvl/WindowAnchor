using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class VirtualDesktopAssociationTests
{
    [Fact]
    public void Mapping_preserves_surviving_ids_and_uses_saved_order_for_recreated_desktops()
    {
        Guid savedFirst = Guid.NewGuid();
        Guid savedSecond = Guid.NewGuid();
        Guid recreated = Guid.NewGuid();
        var saved = new[]
        {
            new SavedVirtualDesktop { DesktopId = savedFirst.ToString("D"), Index = 0 },
            new SavedVirtualDesktop { DesktopId = savedSecond.ToString("D"), Index = 1 }
        };

        IReadOnlyDictionary<Guid, Guid> map =
            VirtualDesktopAssociationService.MapSavedDesktops(saved, [savedFirst, recreated]);

        Assert.Equal(savedFirst, map[savedFirst]);
        Assert.Equal(recreated, map[savedSecond]);
    }

    [Fact]
    public void Mapping_does_not_reassign_a_surviving_later_desktop_by_index()
    {
        Guid deletedFirst = Guid.NewGuid();
        Guid survivingSecond = Guid.NewGuid();
        Guid recreated = Guid.NewGuid();
        var saved = new[]
        {
            new SavedVirtualDesktop { DesktopId = deletedFirst.ToString("D"), Index = 0 },
            new SavedVirtualDesktop { DesktopId = survivingSecond.ToString("D"), Index = 1 }
        };

        IReadOnlyDictionary<Guid, Guid> map =
            VirtualDesktopAssociationService.MapSavedDesktops(saved, [survivingSecond, recreated]);

        Assert.Equal(recreated, map[deletedFirst]);
        Assert.Equal(survivingSecond, map[survivingSecond]);
    }

    [Fact]
    public void Mapping_ignores_extra_current_desktops()
    {
        Guid savedId = Guid.NewGuid();
        Guid extra = Guid.NewGuid();
        var saved = new[]
        {
            new SavedVirtualDesktop { DesktopId = savedId.ToString("D"), Index = 0 }
        };

        IReadOnlyDictionary<Guid, Guid> map =
            VirtualDesktopAssociationService.MapSavedDesktops(saved, [savedId, extra]);

        Assert.Single(map);
        Assert.Equal(savedId, map[savedId]);
    }
}
