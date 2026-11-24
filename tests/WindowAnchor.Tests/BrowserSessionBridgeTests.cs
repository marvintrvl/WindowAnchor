using System.IO;
using System.IO.Pipes;
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

    [Fact]
    public void Capture_response_reads_the_connectors_camel_case_session_data()
    {
        using var response = JsonDocument.Parse("""
            {"ok":true,"protocolVersion":2,"sessions":[{
              "browser":"chrome","profileKey":"profile-1","browserWindowId":"42",
              "activeTitle":"Example","left":120,"width":1000,"state":"maximized",
              "tabs":[{"url":"https://example.com/","index":1,"active":true,"pinned":true,"groupIndex":2}],
              "groups":[{"index":2,"title":"Work","color":"blue","collapsed":true}]
            }]}
            """);

        BrowserCaptureResult result = BrowserSessionBridge.ParseCaptureResponse(response.RootElement);

        Assert.Equal(BrowserCaptureStatus.Captured, result.Status);
        BrowserSession session = Assert.Single(result.Sessions);
        Assert.Equal("chrome", session.Browser);
        Assert.Equal("profile-1", session.ProfileKey);
        Assert.Equal("42", session.BrowserWindowId);
        Assert.Equal("Example", session.ActiveTitle);
        Assert.Equal(120, session.Left);
        Assert.Equal(1000, session.Width);
        Assert.Equal("maximized", session.State);
        BrowserTab tab = Assert.Single(session.Tabs);
        Assert.Equal("https://example.com/", tab.Url);
        Assert.Equal(1, tab.Index);
        Assert.True(tab.Active);
        Assert.True(tab.Pinned);
        Assert.Equal(2, tab.GroupIndex);
        BrowserTabGroup group = Assert.Single(session.Groups);
        Assert.Equal(2, group.Index);
        Assert.Equal("Work", group.Title);
        Assert.Equal("blue", group.Color);
        Assert.True(group.Collapsed);
    }

    [Theory]
    [InlineData("{\"ok\":true,\"protocolVersion\":2,\"results\":[{\"status\":\"opened\"}]}", true)]
    [InlineData("{\"ok\":true,\"protocolVersion\":2,\"results\":[{\"status\":\"conflict\"}]}", true)]
    [InlineData("{\"ok\":true,\"protocolVersion\":2,\"results\":[]}", false)]
    [InlineData("{\"ok\":true,\"protocolVersion\":2,\"results\":[{\"status\":\"skipped\"}]}", false)]
    [InlineData("{\"ok\":true,\"protocolVersion\":2,\"results\":[{\"status\":\"profileUnavailable\"}]}", false)]
    [InlineData("{\"ok\":true,\"protocolVersion\":1}", false)]
    [InlineData("{\"ok\":false,\"protocolVersion\":2}", false)]
    public void Restore_response_requires_a_compatible_result_for_each_session(string json, bool expected)
    {
        using var response = JsonDocument.Parse(json);

        Assert.Equal(expected, BrowserSessionBridge.ParseRestoreResponse(response.RootElement, 1));
    }

    [Fact]
    public async Task Restore_request_sends_session_data_in_the_connectors_camel_case_format()
    {
        string pipe = $"WindowAnchor.Tests.{Guid.NewGuid():N}";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task listening = server.WaitForConnectionAsync(timeout.Token);
        Task<JsonDocument> restore = BrowserSessionBridge.SendRestoreRequestAsync(
            pipe, "test workspace", [new BrowserSession
            {
                BrowserSessionId = "session-1",
                ProfileKey = "profile-1",
                RestorePolicy = BrowserTabRestorePolicy.OpenDuplicate,
                Left = 120,
                State = "maximized",
                Tabs = [new BrowserTab { Url = "https://example.com/", Pinned = true, GroupIndex = 2 }],
                Groups = [new BrowserTabGroup { Index = 2, Title = "Work" }]
            }], timeout.Token);

        await listening;
        using var reader = new StreamReader(server, leaveOpen: true);
        using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };
        string? line = await reader.ReadLineAsync(timeout.Token);
        await writer.WriteLineAsync("""{"ok":true,"protocolVersion":2,"results":[{"status":"opened"}]}""");
        using JsonDocument response = await restore;
        Assert.True(BrowserSessionBridge.ParseRestoreResponse(response.RootElement, 1));

        using JsonDocument request = JsonDocument.Parse(Assert.IsType<string>(line));
        Assert.Equal("restore", request.RootElement.GetProperty("type").GetString());
        JsonElement session = Assert.Single(request.RootElement.GetProperty("sessions").EnumerateArray());
        Assert.Equal("session-1", session.GetProperty("browserSessionId").GetString());
        Assert.Equal("profile-1", session.GetProperty("profileKey").GetString());
        Assert.Equal(1, session.GetProperty("restorePolicy").GetInt32());
        Assert.Equal(120, session.GetProperty("left").GetInt32());
        Assert.Equal("maximized", session.GetProperty("state").GetString());
        JsonElement tab = Assert.Single(session.GetProperty("tabs").EnumerateArray());
        Assert.Equal("https://example.com/", tab.GetProperty("url").GetString());
        Assert.True(tab.GetProperty("pinned").GetBoolean());
        Assert.Equal(2, tab.GetProperty("groupIndex").GetInt32());
        JsonElement group = Assert.Single(session.GetProperty("groups").EnumerateArray());
        Assert.Equal(2, group.GetProperty("index").GetInt32());
        Assert.Equal("Work", group.GetProperty("title").GetString());
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
