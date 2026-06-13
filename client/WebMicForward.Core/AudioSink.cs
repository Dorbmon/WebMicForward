using System.Buffers.Binary;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WebMicForward.Core;

public sealed class AudioSink : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    private readonly BufferedWaveProvider _provider;
    private readonly WasapiOut _output;
    private readonly MMDevice _device;
    private int _volumePercent;

    private AudioSink(MMDevice device, int latencyMilliseconds, int volumePercent)
    {
        _device = device;
        DeviceName = device.FriendlyName;
        RenderLatencyMilliseconds = latencyMilliseconds;
        VolumePercent = volumePercent;
        _provider = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, Channels))
        {
            BufferDuration = TimeSpan.FromSeconds(3),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
        _output = new WasapiOut(device, AudioClientShareMode.Shared, true, latencyMilliseconds);
        _output.Init(_provider);
    }

    public string DeviceName { get; }

    public int RenderLatencyMilliseconds { get; }

    public int VolumePercent
    {
        get => Volatile.Read(ref _volumePercent);
        set => Volatile.Write(ref _volumePercent, Math.Clamp(value, 0, 500));
    }

    public int BufferedMilliseconds => (int)_provider.BufferedDuration.TotalMilliseconds;

    public static AudioSink Create(ClientOptions options)
    {
        var device = ResolveRenderDevice(options.DeviceSelector);
        return new AudioSink(device, options.LatencyMilliseconds, options.VolumePercent);
    }

    public void Start() => _output.Play();

    public void AddPcm(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length % 2 != 0)
        {
            return;
        }

        var samples = ApplyGain(pcm);
        _provider.AddSamples(samples, 0, samples.Length);
    }

    public void AddMonoPcmAsStereo(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length % 2 != 0)
        {
            return;
        }

        var stereo = new byte[pcm.Length * 2];
        var volumePercent = VolumePercent;
        for (var sourceOffset = 0; sourceOffset < pcm.Length; sourceOffset += 2)
        {
            var value = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(sourceOffset, 2));
            value = ScaleSample(value, volumePercent);
            var targetOffset = sourceOffset * 2;
            BinaryPrimitives.WriteInt16LittleEndian(stereo.AsSpan(targetOffset, 2), value);
            BinaryPrimitives.WriteInt16LittleEndian(stereo.AsSpan(targetOffset + 2, 2), value);
        }

        _provider.AddSamples(stereo, 0, stereo.Length);
    }

    public void Dispose()
    {
        _output.Stop();
        _output.Dispose();
        _device.Dispose();
    }

    private byte[] ApplyGain(ReadOnlySpan<byte> pcm)
    {
        var volumePercent = VolumePercent;
        var output = pcm.ToArray();
        if (volumePercent == 100)
        {
            return output;
        }

        for (var offset = 0; offset < output.Length; offset += 2)
        {
            var value = BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(offset, 2));
            BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(offset, 2), ScaleSample(value, volumePercent));
        }

        return output;
    }

    private static short ScaleSample(short value, int volumePercent)
    {
        if (volumePercent == 100)
        {
            return value;
        }

        var scaled = (int)Math.Round(value * (volumePercent / 100d));
        return (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
    }

    private static MMDevice ResolveRenderDevice(string? selector)
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        if (devices.Count == 0)
        {
            throw new InvalidOperationException("No active render devices found.");
        }

        if (string.IsNullOrWhiteSpace(selector))
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        if (int.TryParse(selector, out var index) && index >= 0 && index < devices.Count)
        {
            return devices[index];
        }

        foreach (var device in devices)
        {
            if (device.ID.Equals(selector, StringComparison.OrdinalIgnoreCase) ||
                device.FriendlyName.Contains(selector, StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }
        }

        throw new InvalidOperationException($"No render device matched '{selector}'. Run --list-devices.");
    }
}
