using System.Text;
using System.Text.Json;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class NativeMessagingFramingTests
{
    [Fact]
    public void Multiple_messages_round_trip_without_reopening_streams()
    {
        using var stream = new MemoryStream();
        Assert.True(NativeMessagingFraming.TryWriteMessage(stream, "{\"id\":1}"));
        Assert.True(NativeMessagingFraming.TryWriteMessage(stream, "{\"id\":2}"));
        stream.Position = 0;

        using JsonDocument first = NativeMessagingFraming.ReadMessage(stream)!;
        using JsonDocument second = NativeMessagingFraming.ReadMessage(stream)!;

        Assert.Equal(1, first.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(2, second.RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public void Truncated_frame_is_reported_as_disconnect()
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(10).Concat(Encoding.UTF8.GetBytes("{}" )).ToArray());

        Assert.Null(NativeMessagingFraming.ReadMessage(stream));
    }

    [Fact]
    public void Oversized_frame_is_rejected_without_writing()
    {
        using var stream = new MemoryStream();
        string payload = new('x', NativeMessagingFraming.MaxMessageBytes + 1);

        Assert.False(NativeMessagingFraming.TryWriteMessage(stream, payload));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task Concurrent_responses_remain_individually_framed()
    {
        using var stream = new MemoryStream();
        Task[] writers = Enumerable.Range(0, 64)
            .Select(id => Task.Run(() =>
                Assert.True(NativeMessagingFraming.TryWriteMessage(
                    stream,
                    $"{{\"id\":{id}}}"))))
            .ToArray();
        await Task.WhenAll(writers);
        stream.Position = 0;

        var observed = new HashSet<int>();
        while (NativeMessagingFraming.ReadMessage(stream) is { } message)
        {
            using (message)
                observed.Add(message.RootElement.GetProperty("id").GetInt32());
        }

        Assert.Equal(Enumerable.Range(0, 64), observed.OrderBy(id => id));
    }
}
