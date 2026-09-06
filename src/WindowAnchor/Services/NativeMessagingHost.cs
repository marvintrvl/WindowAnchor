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
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<JsonDocument>> Pending = new();

    public static void Run()
    {
#pragma warning disable CA2000 // Chromium owns these process-lifetime standard streams.
        Stream input = Console.OpenStandardInput();
        Stream output = Console.OpenStandardOutput();
#pragma warning restore CA2000
        _ = Task.Run(() => PipeServerLoop(output));
        while (true)
        {
            using JsonDocument? request = NativeMessagingFraming.ReadMessage(input);
            if (request == null) return;
            HandleBrowserMessage(request, output);
        }
    }

    private static void HandleBrowserMessage(JsonDocument document, Stream output)
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
            WriteMessage(output, new
            {
                type = "response",
                requestId,
                ok = true,
                protocolVersion = BrowserSessionBridge.ProtocolVersion
            });
    }

    private static async Task PipeServerLoop(Stream output)
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    BrowserSessionBridge.PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync().ConfigureAwait(false);
#pragma warning disable CA2000 // Both wrappers are disposed by using declarations; the pipe owns the stream.
                using var reader = new StreamReader(
                    server,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 1024,
                    leaveOpen: true);
                using var writer = new StreamWriter(
                    server,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 1024,
                    leaveOpen: true)
                {
                    AutoFlush = true
                };
#pragma warning restore CA2000
                string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var request = JsonDocument.Parse(line);
                string requestId = request.RootElement.GetProperty("requestId").GetString() ?? Guid.NewGuid().ToString("N");
                var waiter = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
                Pending[requestId] = waiter;
                WriteMessage(output, request.RootElement.GetRawText());

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
                AppLogger.Debug(
                    "native_messaging.pipe_request_failed",
                    "A native-messaging pipe request failed",
                    ex,
                    LogField.Public("errorCategory", "native_messaging_pipe"));
            }
        }
    }

    private static void WriteMessage(Stream output, object message)
        => WriteMessage(output, JsonSerializer.Serialize(message));

    private static void WriteMessage(Stream output, string json)
        => NativeMessagingFraming.TryWriteMessage(output, json);
}
