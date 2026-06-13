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

    private AudioSink(MMDevice device, int latencyMilliseconds)
    {
        _device = device;
        DeviceName = device.FriendlyName;
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

    public int BufferedMilliseconds => (int)_provider.BufferedDuration.TotalMilliseconds;

    public static AudioSink Create(ClientOptions options)
    {
        var device = ResolveRenderDevice(options.DeviceSelector);
        return new AudioSink(device, options.LatencyMilliseconds);
    }

    public void Start() => _output.Play();

    public void AddPcm(ReadOnlySpan<byte> pcm)
    {
        _provider.AddSamples(pcm.ToArray(), 0, pcm.Length);
    }

    public void AddMonoPcmAsStereo(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length % 2 != 0)
        {
            return;
        }

        var stereo = new byte[pcm.Length * 2];
        for (var sourceOffset = 0; sourceOffset < pcm.Length; sourceOffset += 2)
        {
            var value = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(sourceOffset, 2));
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
