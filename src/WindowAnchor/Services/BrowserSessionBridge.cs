using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Requests browser-session operations from the persistent native-messaging host.</summary>
public sealed class BrowserSessionBridge : IBrowserSessionConnector
{
    public const string PipeName = "WindowAnchor.BrowserBridge";
    // The native host applies its own 5-second extension timeout. Allow that structured response
    // to arrive before treating the pipe itself as timed out.
    private const int TimeoutMs = 6000;

    public async Task<BrowserCaptureResult> CaptureAsync(
        string workspaceName, IEnumerable<string> selectedBrowserTitles, CancellationToken ct = default)
    {
        try
        {
            using var response = await SendAsync(new
            {
                type = "capture",
                workspaceName,
                selectedBrowserTitles,
                requestId = Guid.NewGuid().ToString("N")
            }, ct).ConfigureAwait(false);

            return ParseCaptureResponse(response.RootElement);
        }
        catch (TimeoutException ex)
        {
            AppLogger.Debug(
                "browser_session.capture_timed_out",
                "Browser extension capture timed out",
                ex,
                LogField.Workspace("workspaceName", workspaceName),
                LogField.Public("errorCategory", "browser_capture_timeout"));
            return BrowserCaptureResult.Empty(BrowserCaptureStatus.TimedOut, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.Debug(
                "browser_session.capture_unavailable",
                "Browser extension capture was unavailable",
                ex,
                LogField.Workspace("workspaceName", workspaceName),
                LogField.Public("errorCategory", "browser_capture_unavailable"));
            return BrowserCaptureResult.Empty(BrowserCaptureStatus.Unavailable, ex.Message);
        }
        catch (JsonException ex)
        {
            AppLogger.Debug(
                "browser_session.capture_invalid",
                "Browser extension returned invalid capture data",
                ex,
                LogField.Workspace("workspaceName", workspaceName),
                LogField.Public("errorCategory", "browser_capture_invalid"));
            return BrowserCaptureResult.Empty(BrowserCaptureStatus.Failed, ex.Message);
        }
    }

    public async Task<bool> RestoreAsync(string workspaceName, List<BrowserSession> sessions, CancellationToken ct = default)
    {
        try
        {
            using var response = await SendAsync(new
            {
                type = "restore",
                workspaceName,
                sessions,
                requestId = Guid.NewGuid().ToString("N")
            }, ct).ConfigureAwait(false);
            return response.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean();
        }
        catch (Exception ex) when (
            ex is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            AppLogger.Debug(
                "browser_session.restore_unavailable",
                "Browser extension restore was unavailable",
                ex,
                LogField.Workspace("workspaceName", workspaceName),
                LogField.Public("errorCategory", "browser_restore_unavailable"));
            return false;
        }
    }

    internal static BrowserCaptureResult ParseCaptureResponse(JsonElement root)
    {
        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
        {
            string detail = root.TryGetProperty("error", out var error)
                ? error.GetString() ?? "Browser session capture failed."
                : "Browser session capture failed.";
            return BrowserCaptureResult.Empty(
                detail.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                    ? BrowserCaptureStatus.TimedOut
                    : BrowserCaptureStatus.Failed,
                detail);
        }

        if (!root.TryGetProperty("sessions", out var sessions))
            return BrowserCaptureResult.Captured(new List<BrowserSession>());
        return BrowserCaptureResult.Captured(
            JsonSerializer.Deserialize<List<BrowserSession>>(sessions.GetRawText()) ?? new());
    }

    private static async Task<JsonDocument> SendAsync(object request, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(request);
        using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeoutMs);
        try
        {
            await client.ConnectAsync(Timeout.Infinite, timeout.Token).ConfigureAwait(false);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            using var reader = new StreamReader(client);
            await writer.WriteLineAsync(json).ConfigureAwait(false);
            string? line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line)) throw new IOException("Browser host returned no response.");
            return JsonDocument.Parse(line);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Browser host did not respond within {TimeoutMs} ms.",
                ex);
        }
    }
}
