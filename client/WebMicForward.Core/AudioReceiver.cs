using System.Net.WebSockets;
using System.Text;

namespace WebMicForward.Core;

public sealed class AudioReceiver(ClientOptions options, AudioSink sink)
{
    private readonly ReceiverStats _stats = new();
    private DateTimeOffset _nextStatsEvent = DateTimeOffset.MinValue;

    public event EventHandler<ReceiverEvent>? Event;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(1);

        while (!cancellationToken.IsCancellationRequested)
        {
            using var webSocket = new ClientWebSocket();
            webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

            try
            {
                Publish("connecting", $"Connecting to {options.BuildWebSocketUri()}");
                await webSocket.ConnectAsync(options.BuildWebSocketUri(), cancellationToken);
                backoff = TimeSpan.FromSeconds(1);
                Publish("connected", "Connected.");
                await SendTextAsync(webSocket, "{\"type\":\"client-ready\"}", cancellationToken);
                await ReceiveLoopAsync(webSocket, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (WebSocketException ex)
            {
                Publish("disconnected", $"Disconnected: {ex.Message}");
            }
            catch (IOException ex)
            {
                Publish("disconnected", $"Disconnected: {ex.Message}");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            Publish("reconnecting", $"Reconnecting in {backoff.TotalSeconds:0}s...");
            await Task.Delay(backoff, cancellationToken);
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 15));
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];

        while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var messageBuffer = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", cancellationToken);
                    return;
                }

                if (result.Count > 0)
                {
                    messageBuffer.Write(buffer, 0, result.Count);
                }
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                ProcessAudioPacket(messageBuffer.GetBuffer().AsSpan(0, (int)messageBuffer.Length));
            }
            else if (result.MessageType == WebSocketMessageType.Text)
            {
                ProcessControlMessage(messageBuffer.GetBuffer().AsSpan(0, (int)messageBuffer.Length));
            }
        }
    }

    private void ProcessAudioPacket(ReadOnlySpan<byte> packet)
    {
        if (LegacyAudioPacket.TryRead(packet, out var legacyPayload, out var legacyChannels, out var legacyError))
        {
            if (legacyChannels == 1)
            {
                sink.AddMonoPcmAsStereo(legacyPayload);
            }
            else
            {
                sink.AddPcm(legacyPayload);
            }

            _stats.Frames += 1;
            _stats.Bytes += legacyPayload.Length;
            PublishStats();
            return;
        }

        if (RealtimeAdapterPacket.TryRead(packet, out var adapterPayload, out _, out _, out var adapterError))
        {
            sink.AddPcm(adapterPayload);
            _stats.Frames += 1;
            _stats.Bytes += adapterPayload.Length;
            PublishStats();
            return;
        }

        var error = legacyError.Length > 0 ? legacyError : adapterError;
        _stats.InvalidPackets += 1;
        if (_stats.InvalidPackets <= 3)
        {
            Publish("invalid-packet", $"Invalid packet: {error}");
        }
    }

    private void ProcessControlMessage(ReadOnlySpan<byte> payload)
    {
        var text = Encoding.UTF8.GetString(payload);
        if (text.Contains("\"type\":\"peers\"", StringComparison.Ordinal))
        {
            _stats.LastPeerMessage = text;
        }
    }

    private void PublishStats()
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _nextStatsEvent)
        {
            return;
        }

        _nextStatsEvent = now.AddSeconds(1);
        Publish("stats", $"frames={_stats.Frames} received={FormatBytes(_stats.Bytes)} buffered={sink.BufferedMilliseconds}ms invalid={_stats.InvalidPackets}");
    }

    private void Publish(string kind, string message) =>
        Event?.Invoke(this, new ReceiverEvent(kind, message, _stats.Clone(), sink.BufferedMilliseconds));

    private static async Task SendTextAsync(ClientWebSocket webSocket, string text, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        await webSocket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024d:0.0} KB";
        }

        return $"{bytes / 1024d / 1024d:0.00} MB";
    }
}

public sealed record ReceiverEvent(string Kind, string Message, ReceiverStats Stats, int BufferedMilliseconds);

public sealed class ReceiverStats
{
    public long Frames { get; set; }
    public long Bytes { get; set; }
    public long InvalidPackets { get; set; }
    public string LastPeerMessage { get; set; } = "";

    internal ReceiverStats Clone() => new()
    {
        Frames = Frames,
        Bytes = Bytes,
        InvalidPackets = InvalidPackets,
        LastPeerMessage = LastPeerMessage
    };
}
