using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Implements the Chromium native-messaging host and local app bridge.</summary>
public static class NativeMessagingHost
{
    private const int MaxMessageBytes = 1024 * 1024;
    private static readonly object OutputLock = new();
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<JsonDocument>> Pending = new();

    public static void Run()
    {
        _ = Task.Run(PipeServerLoop);
        while (true)
        {
            JsonDocument? request = ReadMessage(Console.OpenStandardInput());
            if (request == null) return;
            HandleBrowserMessage(request);
        }
    }

    private static void HandleBrowserMessage(JsonDocument document)
    {
        var root = document.RootElement;
        string type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() ?? "" : "";
        string requestId = root.TryGetProperty("requestId", out var idElement) ? idElement.GetString() ?? "" : "";

        if (type == "response" && !string.IsNullOrWhiteSpace(requestId) && Pending.TryRemove(requestId, out var waiter))
        {
            waiter.SetResult(JsonDocument.Parse(root.GetRawText()));
            return;
        }

        if (type == "ping")
            WriteMessage(new { type = "response", requestId, ok = true, protocolVersion = 1 });
    }

    private static async Task PipeServerLoop()
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    BrowserSessionBridge.PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync().ConfigureAwait(false);
                using var reader = new StreamReader(server);
                using var writer = new StreamWriter(server) { AutoFlush = true };
                string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var request = JsonDocument.Parse(line);
                string requestId = request.RootElement.GetProperty("requestId").GetString() ?? Guid.NewGuid().ToString("N");
                var waiter = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
                Pending[requestId] = waiter;
                WriteMessage(request.RootElement.GetRawText());

                using var timeout = new CancellationTokenSource(5000);
                try
                {
                    using JsonDocument response = await waiter.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
                    await writer.WriteLineAsync(response.RootElement.GetRawText()).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Pending.TryRemove(requestId, out _);
                    await writer.WriteLineAsync("{\"ok\":false,\"error\":\"Browser extension timed out.\"}").ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"Native messaging pipe request failed: {ex.Message}");
            }
        }
    }

    private static JsonDocument? ReadMessage(Stream input)
    {
        Span<byte> header = stackalloc byte[4];
        if (ReadExactly(input, header) != 4) return null;
        int length = BitConverter.ToInt32(header);
        if (length < 0 || length > MaxMessageBytes) return null;
        byte[] payload = new byte[length];
        if (ReadExactly(input, payload) != length) return null;
        return JsonDocument.Parse(payload);
    }

    private static int ReadExactly(Stream input, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = input.Read(buffer[total..]);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private static void WriteMessage(object message)
        => WriteMessage(JsonSerializer.Serialize(message));

    private static void WriteMessage(string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        if (payload.Length > MaxMessageBytes) return;
        lock (OutputLock)
        {
            Stream output = Console.OpenStandardOutput();
            output.Write(BitConverter.GetBytes(payload.Length));
            output.Write(payload);
            output.Flush();
        }
    }
}
