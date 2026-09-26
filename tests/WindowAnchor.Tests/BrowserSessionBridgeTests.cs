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
        "{\"ok\":true,\"protocolVersion\":2,\"sessions\":[]}",
        BrowserCaptureStatus.Captured)]
    public void Capture_response_maps_native_host_outcome(string json, BrowserCaptureStatus expected)
    {
        using var response = JsonDocument.Parse(json);

        BrowserCaptureResult result = BrowserSessionBridge.ParseCaptureResponse(response.RootElement);

        Assert.Equal(expected, result.Status);
        Assert.Empty(result.Sessions);
    }

    [Fact]
    public void Capture_response_rejects_an_incompatible_protocol_version()
    {
        using var response = JsonDocument.Parse("{\"ok\":true,\"protocolVersion\":1,\"sessions\":[]}");

        BrowserCaptureResult result = BrowserSessionBridge.ParseCaptureResponse(response.RootElement);

        Assert.Equal(BrowserCaptureStatus.Failed, result.Status);
        Assert.Contains("protocol", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"ok\":true,\"protocolVersion\":2}", true)]
    [InlineData("{\"ok\":true,\"protocolVersion\":1}", false)]
    [InlineData("{\"ok\":false,\"protocolVersion\":2}", false)]
    public void Restore_response_requires_the_current_protocol(string json, bool expected)
    {
        using var response = JsonDocument.Parse(json);

        Assert.Equal(expected, BrowserSessionBridge.ParseRestoreResponse(response.RootElement));
    }

    [Fact]
    public void Restore_response_extracts_only_version_compatible_conflict_session_ids()
    {
        using var response = JsonDocument.Parse("""
            {"ok":true,"protocolVersion":2,"results":[
              {"status":"conflict","sessionId":"first"},
              {"status":"opened","sessionId":"second"},
              {"status":"conflict","sessionId":"first"}
            ]}
            """);

        Assert.Equal(["first"], BrowserSessionBridge.GetConflictSessionIds(response.RootElement));
    }

    [Theory]
    [InlineData("firefox", BrowserSessionBridge.FirefoxPipeName)]
    [InlineData("Firefox.exe", BrowserSessionBridge.FirefoxPipeName)]
    [InlineData("chrome", BrowserSessionBridge.ChromiumPipeName)]
    [InlineData("edge", BrowserSessionBridge.ChromiumPipeName)]
    [InlineData("", BrowserSessionBridge.ChromiumPipeName)]
    public void Browser_family_selects_a_dedicated_native_host_pipe(string browser, string expected)
    {
        Assert.Equal(expected, BrowserSessionBridge.PipeNameForBrowser(browser));
    }

    [Fact]
    public void Capture_aggregation_combines_successful_browser_families()
    {
        BrowserCaptureResult result = BrowserSessionBridge.AggregateCaptureResults([
            BrowserCaptureResult.Captured([new BrowserSession { Browser = "chrome" }]),
            BrowserCaptureResult.Captured([new BrowserSession { Browser = "firefox" }])]);

        Assert.Equal(BrowserCaptureStatus.Captured, result.Status);
        Assert.Equal(2, result.Sessions.Count);
    }

    [Fact]
    public void Capture_aggregation_retains_partial_data_and_reports_an_unavailable_family()
    {
        BrowserCaptureResult result = BrowserSessionBridge.AggregateCaptureResults([
            BrowserCaptureResult.Captured([new BrowserSession { Browser = "chrome" }]),
            BrowserCaptureResult.Empty(BrowserCaptureStatus.Unavailable, "Firefox connector unavailable.")]);

        Assert.Equal(BrowserCaptureStatus.Unavailable, result.Status);
        Assert.Single(result.Sessions);
        Assert.Contains("Firefox", result.Detail, StringComparison.OrdinalIgnoreCase);
    }
}
