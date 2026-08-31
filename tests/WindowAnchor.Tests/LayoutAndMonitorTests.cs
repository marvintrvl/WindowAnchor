using WindowAnchor.Native;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class LayoutAndMonitorTests
{
    [Fact]
    public void Mixed_dpi_rectangle_is_scaled_without_physical_monitor_hardware()
    {
        var saved = new NativeMethodsWindow.Rect
        {
            Left = -1920,
            Top = 120,
            Right = -960,
            Bottom = 1080
        };

        var scaled = WindowService.ScaleCoordsForDpi(saved, savedDpi: 96, targetDpi: 144);

        Assert.Equal(-2880, scaled.Left);
        Assert.Equal(180, scaled.Top);
        Assert.Equal(-1440, scaled.Right);
        Assert.Equal(1620, scaled.Bottom);
    }

    [Theory]
    [InlineData(96, 96)]
    [InlineData(0, 144)]
    public void Dpi_mapping_preserves_coordinates_when_scaling_is_not_applicable(
        uint savedDpi,
        uint targetDpi)
    {
        var saved = new NativeMethodsWindow.Rect
        {
            Left = 100,
            Top = 200,
            Right = 900,
            Bottom = 800
        };

        var mapped = WindowService.ScaleCoordsForDpi(saved, savedDpi, targetDpi);

        Assert.Equal(saved.Left, mapped.Left);
        Assert.Equal(saved.Top, mapped.Top);
        Assert.Equal(saved.Right, mapped.Right);
        Assert.Equal(saved.Bottom, mapped.Bottom);
    }

    [Fact]
    public void Monitor_fingerprint_is_stable_across_inventory_order()
    {
        string first = MonitorService.ComputeFingerprint(["ABCD:EF01:1", "1234:5678:0"]);
        string second = MonitorService.ComputeFingerprint(["1234:5678:0", "ABCD:EF01:1"]);

        Assert.Equal(first, second);
        Assert.Matches("^[0-9a-f]{8}$", first);
        Assert.Equal("no_monitors", MonitorService.ComputeFingerprint([]));
        Assert.NotEqual(first, MonitorService.ComputeFingerprint(["1234:5678:0"]));
    }
}
