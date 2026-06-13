namespace WebMicForward.Core;

public sealed class ClientOptions
{
    public string Server { get; init; } = "ws://localhost:8787/ws";
    public string Room { get; init; } = "default";
    public string? Token { get; init; }
    public string? DeviceSelector { get; init; }
    public int LatencyMilliseconds { get; init; } = 80;
    public int VolumePercent { get; init; } = 100;
    public bool ListDevices { get; init; }
    public bool ShowHelp { get; init; }

    public Uri BuildWebSocketUri()
    {
        var builder = new UriBuilder(Server);
        if (builder.Scheme == Uri.UriSchemeHttps)
        {
            builder.Scheme = "wss";
        }
        else if (builder.Scheme == Uri.UriSchemeHttp)
        {
            builder.Scheme = "ws";
        }

        if (string.IsNullOrWhiteSpace(builder.Path) || builder.Path == "/")
        {
            builder.Path = "/ws";
        }

        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(builder.Query))
        {
            query.Add(builder.Query.TrimStart('?'));
        }

        query.Add($"room={Uri.EscapeDataString(Room)}");
        query.Add("role=client");
        if (!string.IsNullOrWhiteSpace(Token))
        {
            query.Add($"token={Uri.EscapeDataString(Token)}");
        }

        builder.Query = string.Join("&", query);
        return builder.Uri;
    }

    public Uri BuildWebPageUri()
    {
        var builder = new UriBuilder(Server);
        if (builder.Scheme == "wss")
        {
            builder.Scheme = Uri.UriSchemeHttps;
        }
        else if (builder.Scheme == "ws")
        {
            builder.Scheme = Uri.UriSchemeHttp;
        }

        builder.Path = "/";

        var query = new List<string> { $"room={Uri.EscapeDataString(Room)}" };
        if (!string.IsNullOrWhiteSpace(Token))
        {
            query.Add($"token={Uri.EscapeDataString(Token)}");
        }

        builder.Query = string.Join("&", query);
        return builder.Uri;
    }

    public static ClientOptions Parse(string[] args)
    {
        var options = new MutableClientOptions();

        for (var i = 0; i < args.Length; i += 1)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--help":
                case "-h":
                    options.ShowHelp = true;
                    break;
                case "--list-devices":
                    options.ListDevices = true;
                    break;
                case "--server":
                    options.Server = ReadValue(args, ref i, arg);
                    break;
                case "--room":
                    options.Room = ReadValue(args, ref i, arg);
                    break;
                case "--token":
                    options.Token = ReadValue(args, ref i, arg);
                    break;
                case "--device":
                    options.DeviceSelector = ReadValue(args, ref i, arg);
                    break;
                case "--latency-ms":
                    if (!int.TryParse(ReadValue(args, ref i, arg), out var latency) || latency < 20 || latency > 1000)
                    {
                        throw new ArgumentException("--latency-ms must be between 20 and 1000.");
                    }
                    options.LatencyMilliseconds = latency;
                    break;
                case "--volume-percent":
                    if (!int.TryParse(ReadValue(args, ref i, arg), out var volume) || volume < 0 || volume > 500)
                    {
                        throw new ArgumentException("--volume-percent must be between 0 and 500.");
                    }
                    options.VolumePercent = volume;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{arg}'. Run --help.");
            }
        }

        return options.ToImmutable();
    }

    private static string ReadValue(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{name} requires a value.");
        }

        index += 1;
        return args[index];
    }

    private sealed class MutableClientOptions
    {
        public string Server { get; set; } = "ws://localhost:8787/ws";
        public string Room { get; set; } = "default";
        public string? Token { get; set; }
        public string? DeviceSelector { get; set; }
        public int LatencyMilliseconds { get; set; } = 80;
        public int VolumePercent { get; set; } = 100;
        public bool ListDevices { get; set; }
        public bool ShowHelp { get; set; }

        public ClientOptions ToImmutable() => new()
        {
            Server = Server,
            Room = Room,
            Token = Token,
            DeviceSelector = DeviceSelector,
            LatencyMilliseconds = LatencyMilliseconds,
            VolumePercent = VolumePercent,
            ListDevices = ListDevices,
            ShowHelp = ShowHelp
        };
    }
}
