using NAudio.CoreAudioApi;

namespace WebMicForward.Core;

public sealed record AudioDeviceInfo(
    int Index,
    string Id,
    string FriendlyName,
    bool IsDefault,
    bool LooksLikeVirtualCable)
{
    public string Selector => Index.ToString();
    public string DisplayName => $"{(IsDefault ? "* " : "  ")}[{Index}] {FriendlyName}";

    public override string ToString() => DisplayName;
}

public static class AudioDeviceService
{
    private static readonly string[] VirtualCableHints =
    [
        "CABLE Input",
        "VB-Audio",
        "Virtual Cable",
        "Virtual Audio Cable",
        "Voicemeeter Input"
    ];

    public static IReadOnlyList<AudioDeviceInfo> GetRenderDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        var result = new List<AudioDeviceInfo>();

        for (var i = 0; i < devices.Count; i += 1)
        {
            var device = devices[i];
            result.Add(new AudioDeviceInfo(
                i,
                device.ID,
                device.FriendlyName,
                device.ID == defaultDevice.ID,
                LooksLikeVirtualCable(device.FriendlyName)));
        }

        return result;
    }

    public static AudioDeviceInfo? FindLikelyVirtualCable(IReadOnlyList<AudioDeviceInfo> devices) =>
        devices.FirstOrDefault(device => device.LooksLikeVirtualCable);

    public static bool LooksLikeVirtualCable(string deviceName) =>
        VirtualCableHints.Any(hint => deviceName.Contains(hint, StringComparison.OrdinalIgnoreCase));
}
