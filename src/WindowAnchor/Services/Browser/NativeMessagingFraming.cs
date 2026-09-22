using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace WindowAnchor.Services;

/// <summary>Length-prefixed Chromium native-messaging framing with no stream ownership.</summary>
internal static class NativeMessagingFraming
{
    internal const int MaxMessageBytes = 1024 * 1024;
    private static readonly object WriteSync = new();

    internal static JsonDocument? ReadMessage(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Span<byte> header = stackalloc byte[4];
        if (!ReadExactly(input, header, out int headerBytes) || headerBytes != 4)
            return null;

        int length = BitConverter.ToInt32(header);
        if (length < 0 || length > MaxMessageBytes)
            return null;

        byte[] payload = new byte[length];
        if (!ReadExactly(input, payload, out int payloadBytes) || payloadBytes != length)
            return null;
        return JsonDocument.Parse(payload);
    }

    internal static bool TryWriteMessage(Stream output, string json)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(json);
        byte[] payload = Encoding.UTF8.GetBytes(json);
        if (payload.Length > MaxMessageBytes)
            return false;

        lock (WriteSync)
        {
            output.Write(BitConverter.GetBytes(payload.Length));
            output.Write(payload);
            output.Flush();
        }
        return true;
    }

    private static bool ReadExactly(Stream input, Span<byte> buffer, out int bytesRead)
    {
        bytesRead = 0;
        while (bytesRead < buffer.Length)
        {
            int read = input.Read(buffer[bytesRead..]);
            if (read == 0)
                return false;
            bytesRead += read;
        }
        return true;
    }

    private static bool ReadExactly(Stream input, byte[] buffer, out int bytesRead)
    {
        bytesRead = 0;
        while (bytesRead < buffer.Length)
        {
            int read = input.Read(buffer, bytesRead, buffer.Length - bytesRead);
            if (read == 0)
                return false;
            bytesRead += read;
        }
        return true;
    }
}
