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
        var receivedAt = DateTimeOffset.UtcNow;
        if (TimestampedAudioPacket.TryRead(packet, out var timestampedPayload, out var timestampedChannels, out _, out var capturedUnixMilliseconds, out var timestampedError))
        {
            if (timestampedChannels == 1)
            {
                sink.AddMonoPcmAsStereo(timestampedPayload);
            }
            else
            {
                sink.AddPcm(timestampedPayload);
            }

            RecordAudioFrame(timestampedPayload.Length, "WMF2", receivedAt, capturedUnixMilliseconds);
            PublishStats();
            return;
        }

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

            RecordAudioFrame(legacyPayload.Length, "WMF1", receivedAt);
            PublishStats();
            return;
        }

        if (RealtimeAdapterPacket.TryRead(packet, out var adapterPayload, out _, out _, out var adapterError))
        {
            sink.AddPcm(adapterPayload);
            RecordAudioFrame(adapterPayload.Length, "SFU", receivedAt);
            PublishStats();
            return;
        }

        var error = timestampedError != "not a WMF2 packet" && timestampedError.Length > 0
            ? timestampedError
            : legacyError.Length > 0 ? legacyError : adapterError;
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
        UpdateLatencyStats();
        Publish("stats", $"frames={_stats.Frames} received={FormatBytes(_stats.Bytes)} volume={sink.VolumePercent}% {FormatLatency()} invalid={_stats.InvalidPackets}");
    }

    private void Publish(string kind, string message) =>
        Event?.Invoke(this, new ReceiverEvent(kind, message, _stats.Clone(), sink.BufferedMilliseconds));

    private void RecordAudioFrame(int payloadLength, string packetSource, DateTimeOffset receivedAt, ulong? capturedUnixMilliseconds = null)
    {
        _stats.Frames += 1;
        _stats.Bytes += payloadLength;
        _stats.PacketSource = packetSource;

        if (capturedUnixMilliseconds is not { } captured)
        {
            _stats.ReceiveLatencyMilliseconds = null;
            return;
        }

        var maxUnixMilliseconds = (ulong)DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();
        if (captured > maxUnixMilliseconds)
        {
            _stats.ReceiveLatencyMilliseconds = null;
            return;
        }

        var capturedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)captured);
        var receiveLatency = (int)Math.Round((receivedAt - capturedAt).TotalMilliseconds);
        _stats.ReceiveLatencyMilliseconds = receiveLatency is >= -1000 and <= 60000
            ? Math.Max(0, receiveLatency)
            : null;
    }

    private void UpdateLatencyStats()
    {
        _stats.BufferedMilliseconds = sink.BufferedMilliseconds;
        _stats.RenderLatencyMilliseconds = sink.RenderLatencyMilliseconds;
        _stats.EstimatedLatencyMilliseconds = _stats.ReceiveLatencyMilliseconds is { } receiveLatency
            ? receiveLatency + _stats.BufferedMilliseconds + _stats.RenderLatencyMilliseconds
            : null;
    }

    private string FormatLatency()
    {
        var buffer = _stats.BufferedMilliseconds;
        var render = _stats.RenderLatencyMilliseconds;

        if (_stats.ReceiveLatencyMilliseconds is { } receiveLatency &&
            _stats.EstimatedLatencyMilliseconds is { } estimatedLatency)
        {
            return $"latency≈{estimatedLatency}ms (source={_stats.PacketSource} rx={receiveLatency}ms buffer={buffer}ms render≈{render}ms)";
        }

        return $"local≥{buffer + render}ms (source={_stats.PacketSource} no-rx-timestamp buffer={buffer}ms render≈{render}ms)";
    }

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
    public string PacketSource { get; set; } = "unknown";
    public int? ReceiveLatencyMilliseconds { get; set; }
    public int BufferedMilliseconds { get; set; }
    public int RenderLatencyMilliseconds { get; set; }
    public int? EstimatedLatencyMilliseconds { get; set; }

    internal ReceiverStats Clone() => new()
    {
        Frames = Frames,
        Bytes = Bytes,
        InvalidPackets = InvalidPackets,
        LastPeerMessage = LastPeerMessage,
        PacketSource = PacketSource,
        ReceiveLatencyMilliseconds = ReceiveLatencyMilliseconds,
        BufferedMilliseconds = BufferedMilliseconds,
        RenderLatencyMilliseconds = RenderLatencyMilliseconds,
        EstimatedLatencyMilliseconds = EstimatedLatencyMilliseconds
    };
}
