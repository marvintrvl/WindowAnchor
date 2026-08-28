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
public sealed class BrowserSessionBridge
{
    public const string PipeName = "WindowAnchor.BrowserBridge";
    private const int TimeoutMs = 5000;

    public async Task<List<BrowserSession>> CaptureAsync(
        string workspaceName, IEnumerable<string> selectedBrowserTitles, CancellationToken ct = default)
    {
        using var response = await SendAsync(new
        {
            type = "capture",
            workspaceName,
            selectedBrowserTitles,
            requestId = Guid.NewGuid().ToString("N")
        }, ct).ConfigureAwait(false);

        if (!response.RootElement.TryGetProperty("sessions", out var sessions))
            return new List<BrowserSession>();
        return JsonSerializer.Deserialize<List<BrowserSession>>(sessions.GetRawText()) ?? new();
    }

    public async Task<bool> RestoreAsync(string workspaceName, List<BrowserSession> sessions, CancellationToken ct = default)
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

    private static async Task<JsonDocument> SendAsync(object request, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(request);
        using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await client.ConnectAsync(TimeoutMs, ct).ConfigureAwait(false);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            using var reader = new StreamReader(client);
            await writer.WriteLineAsync(json).ConfigureAwait(false);
            string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line)) throw new IOException("Browser host returned no response.");
            return JsonDocument.Parse(line);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
        {
            AppLogger.Debug($"Browser extension unavailable: {ex.Message}");
            return JsonDocument.Parse("{\"ok\":false,\"sessions\":[]}");
        }
    }
}
