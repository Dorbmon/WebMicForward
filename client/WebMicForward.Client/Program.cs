using WebMicForward.Core;

var options = ClientOptions.Parse(args);
if (options.ShowHelp)
{
    PrintUsage();
    return 0;
}

if (options.ListDevices)
{
    PrintDevices();
    return 0;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

try
{
    using var sink = AudioSink.Create(options);
    sink.Start();

    Console.WriteLine($"Output device : {sink.DeviceName}");
    Console.WriteLine($"WebSocket     : {options.BuildWebSocketUri()}");
    Console.WriteLine("Press Ctrl+C to stop.");

    var receiver = new AudioReceiver(options, sink);
    receiver.Event += (_, evt) =>
    {
        if (evt.Kind == "stats")
        {
            Console.Write($"\r{evt.Message}   ");
        }
        else
        {
            Console.WriteLine(evt.Message);
        }
    };
    await receiver.RunAsync(shutdown.Token);
}
catch (OperationCanceledException)
{
    // Normal shutdown.
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

return 0;

static void PrintDevices()
{
    Console.WriteLine("Render devices:");
    foreach (var device in AudioDeviceService.GetRenderDevices())
    {
        Console.WriteLine(device.DisplayName);
        Console.WriteLine($"      {device.Id}");
    }
}

static void PrintUsage()
{
    Console.WriteLine("""
WebMicForward.Client

Usage:
  WebMicForward.Client --server wss://example.workers.dev/ws --room my-room --device "CABLE Input"

Options:
  --server <url>       WebSocket or Worker URL. Default: ws://localhost:8787/ws
  --room <name>        Room name. Default: default
  --token <token>      Optional token when ROOM_TOKEN is configured on Worker
  --device <selector>  Render device index, ID, or name substring
  --latency-ms <n>     WASAPI render latency, 20-1000. Default: 80
  --list-devices       List active render devices
  --help               Show this help

For a virtual microphone, install a virtual audio cable and choose its render endpoint,
for example --device "CABLE Input". Other apps then select the paired recording endpoint.
The client accepts both legacy WMF1 mono PCM packets and Cloudflare Realtime
WebSocket adapter protobuf packets carrying 48 kHz stereo PCM.
""");
}
