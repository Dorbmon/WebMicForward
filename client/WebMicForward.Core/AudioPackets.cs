using System.Buffers.Binary;

namespace WebMicForward.Core;

internal readonly ref struct TimestampedAudioPacket
{
    private const int HeaderLength = 24;
    private const uint Magic = 0x32464d57; // WMF2, little endian.

    public static bool TryRead(
        ReadOnlySpan<byte> packet,
        out ReadOnlySpan<byte> payload,
        out ushort channels,
        out uint sequenceNumber,
        out ulong capturedUnixMilliseconds,
        out string error)
    {
        payload = default;
        channels = 0;
        sequenceNumber = 0;
        capturedUnixMilliseconds = 0;
        error = "";

        if (packet.Length < HeaderLength)
        {
            error = "packet shorter than 24-byte header";
            return false;
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(packet[..4]);
        if (magic != Magic)
        {
            error = "not a WMF2 packet";
            return false;
        }

        sequenceNumber = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(4, 4));
        var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(8, 4));
        channels = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(12, 2));
        var samples = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(14, 2));
        capturedUnixMilliseconds = BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(16, 8));

        if (sampleRate != AudioSink.SampleRate)
        {
            error = $"unsupported sample rate {sampleRate}";
            return false;
        }

        if (channels is not (1 or AudioSink.Channels))
        {
            error = $"unsupported channel count {channels}";
            return false;
        }

        var expectedLength = HeaderLength + samples * channels * 2;
        if (packet.Length != expectedLength)
        {
            error = $"bad length {packet.Length}, expected {expectedLength}";
            return false;
        }

        payload = packet[HeaderLength..];
        return true;
    }
}

internal readonly ref struct LegacyAudioPacket
{
    private const int HeaderLength = 16;
    private const uint Magic = 0x31464d57; // WMF1, little endian.

    public static bool TryRead(
        ReadOnlySpan<byte> packet,
        out ReadOnlySpan<byte> payload,
        out ushort channels,
        out string error)
    {
        payload = default;
        channels = 0;
        error = "";

        if (packet.Length < HeaderLength)
        {
            error = "packet shorter than 16-byte header";
            return false;
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(packet[..4]);
        if (magic != Magic)
        {
            error = "not a WMF1 packet";
            return false;
        }

        var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(8, 4));
        channels = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(12, 2));
        var samples = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(14, 2));

        if (sampleRate != AudioSink.SampleRate)
        {
            error = $"unsupported sample rate {sampleRate}";
            return false;
        }

        if (channels is not (1 or AudioSink.Channels))
        {
            error = $"unsupported channel count {channels}";
            return false;
        }

        var expectedLength = HeaderLength + samples * channels * 2;
        if (packet.Length != expectedLength)
        {
            error = $"bad length {packet.Length}, expected {expectedLength}";
            return false;
        }

        payload = packet[HeaderLength..];
        return true;
    }
}

internal readonly ref struct RealtimeAdapterPacket
{
    public static bool TryRead(
        ReadOnlySpan<byte> packet,
        out ReadOnlySpan<byte> payload,
        out uint sequenceNumber,
        out uint timestamp,
        out string error)
    {
        payload = default;
        sequenceNumber = 0;
        timestamp = 0;
        error = "";

        var offset = 0;
        while (offset < packet.Length)
        {
            if (!TryReadVarint(packet, ref offset, out var key))
            {
                error = "bad protobuf field key";
                return false;
            }

            var fieldNumber = (int)(key >> 3);
            var wireType = (int)(key & 0x7);

            switch (wireType)
            {
                case 0:
                    if (!TryReadVarint(packet, ref offset, out var value))
                    {
                        error = $"bad varint field {fieldNumber}";
                        return false;
                    }

                    if (fieldNumber == 1)
                    {
                        sequenceNumber = (uint)value;
                    }
                    else if (fieldNumber == 2)
                    {
                        timestamp = (uint)value;
                    }
                    break;

                case 2:
                    if (!TryReadVarint(packet, ref offset, out var length) ||
                        length > int.MaxValue ||
                        offset + (int)length > packet.Length)
                    {
                        error = $"bad length-delimited field {fieldNumber}";
                        return false;
                    }

                    if (fieldNumber == 5)
                    {
                        payload = packet.Slice(offset, (int)length);
                    }
                    offset += (int)length;
                    break;

                default:
                    error = $"unsupported protobuf wire type {wireType}";
                    return false;
            }
        }

        if (payload.IsEmpty)
        {
            error = "protobuf packet missing payload field";
            return false;
        }

        if (payload.Length % (AudioSink.Channels * 2) != 0)
        {
            error = $"PCM payload length {payload.Length} is not aligned to stereo int16 samples";
            return false;
        }

        return true;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> packet, ref int offset, out ulong value)
    {
        value = 0;
        var shift = 0;

        while (offset < packet.Length && shift <= 63)
        {
            var b = packet[offset];
            offset += 1;
            value |= (ulong)(b & 0x7f) << shift;

            if ((b & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
        }

        return false;
    }
}
