using System.Text.Json;
using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class BrowserSessionBridgeTests
{
    [Fact]
    public async Task Restore_propagates_caller_cancellation()
    {
        var bridge = new BrowserSessionBridge();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            bridge.RestoreAsync("cancelled", new List<BrowserSession>(), cancellation.Token));
    }

    [Theory]
    [InlineData(
        "{\"ok\":false,\"error\":\"Browser extension timed out.\"}",
        BrowserCaptureStatus.TimedOut)]
    [InlineData(
        "{\"ok\":false,\"error\":\"Extension capture failed.\"}",
        BrowserCaptureStatus.Failed)]
    [InlineData(
        "{\"ok\":true,\"sessions\":[]}",
        BrowserCaptureStatus.Captured)]
    public void Capture_response_maps_native_host_outcome(string json, BrowserCaptureStatus expected)
    {
        using var response = JsonDocument.Parse(json);

        BrowserCaptureResult result = BrowserSessionBridge.ParseCaptureResponse(response.RootElement);

        Assert.Equal(expected, result.Status);
        Assert.Empty(result.Sessions);
    }
}
