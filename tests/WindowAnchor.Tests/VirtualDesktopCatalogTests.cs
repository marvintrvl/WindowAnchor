using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class VirtualDesktopCatalogTests
{
    [Fact]
    public void Preserves_shell_order_custom_names_and_current_desktop()
    {
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        var catalog = VirtualDesktopCatalog.Parse(
            first.ToByteArray().Concat(second.ToByteArray()).ToArray(), second.ToByteArray(),
            id => id == second ? "Research" : null);
        Assert.Equal(new VirtualDesktopInfo(first, "Desktop 1", false), catalog[0]);
        Assert.Equal(new VirtualDesktopInfo(second, "Research", true), catalog[1]);
    }

    [Fact]
    public void Malformed_missing_duplicate_and_empty_ids_fail_softly()
    {
        Guid id = Guid.NewGuid();
        foreach (byte[]? bytes in new byte[]?[] { null, [], new byte[17], new byte[16],
                     id.ToByteArray().Concat(id.ToByteArray()).ToArray(), new byte[16400] })
            Assert.Empty(VirtualDesktopCatalog.Parse(bytes, null, _ => null));
        Assert.False(Assert.Single(VirtualDesktopCatalog.Parse(id.ToByteArray(), [1], _ => " ")).IsCurrent);
    }
}
