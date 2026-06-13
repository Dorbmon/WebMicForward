using System.Diagnostics;
using System.Text.Json;
using WebMicForward.Core;

namespace WebMicForward.Gui;

public sealed class MainForm : Form
{
    private const string VbCableUrl = "https://vb-audio.com/Cable/";

    private readonly TextBox _serverBox = new() { Text = "wss://example.workers.dev/ws" };
    private readonly TextBox _roomBox = new() { Text = "default" };
    private readonly TextBox _tokenBox = new() { UseSystemPasswordChar = true };
    private readonly NumericUpDown _latencyBox = new() { Minimum = 20, Maximum = 1000, Value = 80, Increment = 10 };
    private readonly ComboBox _deviceCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _deviceStatusLabel = new() { AutoSize = true };
    private readonly Label _statusLabel = new() { AutoSize = true, Text = "Idle" };
    private readonly Label _statsLabel = new() { AutoSize = true, Text = "No audio received yet." };
    private readonly TextBox _logBox = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Height = 150
    };
    private readonly Button _refreshButton = new() { Text = "Refresh devices" };
    private readonly Button _installButton = new() { Text = "Install / download virtual cable" };
    private readonly Button _soundSettingsButton = new() { Text = "Open sound settings" };
    private readonly Button _openWebButton = new() { Text = "Open web page" };
    private readonly Button _copyWebButton = new() { Text = "Copy web URL" };
    private readonly Button _startButton = new() { Text = "Start receiver" };
    private readonly Button _stopButton = new() { Text = "Stop", Enabled = false };

    private CancellationTokenSource? _receiverCancellation;
    private AudioSink? _sink;
    private Task? _receiverTask;

    public MainForm()
    {
        Text = "Web Mic Forward";
        Width = 820;
        Height = 700;
        MinimumSize = new Size(720, 620);
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        WireEvents();
        LoadSettings();
        RefreshDevices();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveSettings();
        StopReceiver();
        base.OnFormClosing(e);
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 5
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var title = new Label
        {
            Text = "Web Mic Forward",
            Font = new Font(Font.FontFamily, 18, FontStyle.Bold),
            AutoSize = true
        };
        root.Controls.Add(title);

        root.Controls.Add(BuildInstallGroup());
        root.Controls.Add(BuildConfigGroup());
        root.Controls.Add(BuildActionGroup());
        root.Controls.Add(BuildLogGroup());
    }

    private Control BuildInstallGroup()
    {
        var group = new GroupBox
        {
            Text = "1. Virtual microphone device",
            Dock = DockStyle.Top,
            Padding = new Padding(12),
            AutoSize = true
        };

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            AutoSize = true
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        panel.Controls.Add(_deviceStatusLabel, 0, 0);
        panel.Controls.Add(_installButton, 1, 0);
        panel.Controls.Add(_soundSettingsButton, 2, 0);
        group.Controls.Add(panel);
        return group;
    }

    private Control BuildConfigGroup()
    {
        var group = new GroupBox
        {
            Text = "2. Connection and output",
            Dock = DockStyle.Top,
            Padding = new Padding(12),
            AutoSize = true
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 4,
            AutoSize = true
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        AddLabeledControl(grid, "Worker WebSocket URL", _serverBox, 0, 0, 3);
        AddLabeledControl(grid, "Room", _roomBox, 0, 1);
        AddLabeledControl(grid, "Token", _tokenBox, 2, 1);
        AddLabeledControl(grid, "Output device", _deviceCombo, 0, 2, 3);
        AddLabeledControl(grid, "Latency ms", _latencyBox, 0, 3);
        grid.Controls.Add(_refreshButton, 2, 3);
        grid.Controls.Add(_copyWebButton, 3, 3);

        group.Controls.Add(grid);
        return group;
    }

    private Control BuildActionGroup()
    {
        var group = new GroupBox
        {
            Text = "3. Run",
            Dock = DockStyle.Top,
            Padding = new Padding(12),
            AutoSize = true
        };

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            AutoSize = true
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        panel.Controls.Add(_startButton, 0, 0);
        panel.Controls.Add(_stopButton, 1, 0);
        panel.Controls.Add(_openWebButton, 2, 0);
        panel.Controls.Add(_statusLabel, 3, 0);
        group.Controls.Add(panel);
        return group;
    }

    private Control BuildLogGroup()
    {
        var group = new GroupBox
        {
            Text = "Status",
            Dock = DockStyle.Fill,
            Padding = new Padding(12)
        };

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        panel.Controls.Add(_statsLabel, 0, 0);
        panel.Controls.Add(_logBox, 0, 1);
        group.Controls.Add(panel);
        return group;
    }

    private static void AddLabeledControl(
        TableLayoutPanel grid,
        string label,
        Control control,
        int column,
        int row,
        int controlColumnSpan = 1)
    {
        var labelControl = new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 8, 8, 4)
        };
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(0, 4, 12, 4);
        grid.Controls.Add(labelControl, column, row);
        grid.Controls.Add(control, column + 1, row);
        if (controlColumnSpan > 1)
        {
            grid.SetColumnSpan(control, controlColumnSpan);
        }
    }

    private void WireEvents()
    {
        _refreshButton.Click += (_, _) => RefreshDevices();
        _installButton.Click += (_, _) => OpenExternal(VbCableUrl);
        _soundSettingsButton.Click += (_, _) => OpenExternal("ms-settings:sound");
        _openWebButton.Click += (_, _) => OpenExternal(BuildOptions().BuildWebPageUri().ToString());
        _copyWebButton.Click += (_, _) =>
        {
            Clipboard.SetText(BuildOptions().BuildWebPageUri().ToString());
            AppendLog("Copied web URL.");
        };
        _startButton.Click += (_, _) => StartReceiver();
        _stopButton.Click += (_, _) => StopReceiver();
    }

    private void RefreshDevices()
    {
        var devices = AudioDeviceService.GetRenderDevices();
        _deviceCombo.DataSource = devices.ToList();

        var likelyCable = AudioDeviceService.FindLikelyVirtualCable(devices);
        if (likelyCable is not null)
        {
            _deviceCombo.SelectedItem = likelyCable;
            _deviceStatusLabel.Text = $"Virtual cable found: {likelyCable.FriendlyName}";
            _deviceStatusLabel.ForeColor = Color.ForestGreen;
            return;
        }

        _deviceStatusLabel.Text = "No obvious virtual cable render endpoint found. Install VB-CABLE, then refresh devices.";
        _deviceStatusLabel.ForeColor = Color.DarkOrange;
    }

    private async void StartReceiver()
    {
        if (_receiverCancellation is not null)
        {
            return;
        }

        SaveSettings();

        try
        {
            var options = BuildOptions();
            _sink = AudioSink.Create(options);
            _sink.Start();

            var receiver = new AudioReceiver(options, _sink);
            receiver.Event += OnReceiverEvent;

            _receiverCancellation = new CancellationTokenSource();
            SetRunningState(true);
            AppendLog($"Output device: {_sink.DeviceName}");
            AppendLog($"WebSocket: {options.BuildWebSocketUri()}");

            _receiverTask = Task.Run(() => receiver.RunAsync(_receiverCancellation.Token));
            await _receiverTask;
        }
        catch (OperationCanceledException)
        {
            AppendLog("Stopped.");
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Web Mic Forward", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _receiverCancellation?.Dispose();
            _receiverCancellation = null;
            _sink?.Dispose();
            _sink = null;
            _receiverTask = null;
            SetRunningState(false);
        }
    }

    private void StopReceiver()
    {
        _receiverCancellation?.Cancel();
    }

    private ClientOptions BuildOptions()
    {
        var selectedDevice = _deviceCombo.SelectedItem as AudioDeviceInfo;
        return new ClientOptions
        {
            Server = _serverBox.Text.Trim(),
            Room = _roomBox.Text.Trim(),
            Token = string.IsNullOrWhiteSpace(_tokenBox.Text) ? null : _tokenBox.Text.Trim(),
            DeviceSelector = selectedDevice?.Selector,
            LatencyMilliseconds = (int)_latencyBox.Value
        };
    }

    private void OnReceiverEvent(object? sender, ReceiverEvent evt)
    {
        if (IsDisposed)
        {
            return;
        }

        BeginInvoke(() =>
        {
            if (evt.Kind == "stats")
            {
                _statsLabel.Text = evt.Message;
            }
            else
            {
                AppendLog(evt.Message);
                _statusLabel.Text = evt.Message;
            }
        });
    }

    private void SetRunningState(bool running)
    {
        _startButton.Enabled = !running;
        _stopButton.Enabled = running;
        _serverBox.Enabled = !running;
        _roomBox.Enabled = !running;
        _tokenBox.Enabled = !running;
        _latencyBox.Enabled = !running;
        _deviceCombo.Enabled = !running;
        _refreshButton.Enabled = !running;
        _statusLabel.Text = running ? "Running" : "Idle";
    }

    private void AppendLog(string message)
    {
        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }

    private void LoadSettings()
    {
        try
        {
            var path = SettingsPath;
            if (!File.Exists(path))
            {
                return;
            }

            var settings = JsonSerializer.Deserialize<GuiSettings>(File.ReadAllText(path));
            if (settings is null)
            {
                return;
            }

            _serverBox.Text = settings.Server;
            _roomBox.Text = settings.Room;
            _tokenBox.Text = settings.Token ?? "";
            _latencyBox.Value = Math.Clamp(settings.LatencyMilliseconds, (int)_latencyBox.Minimum, (int)_latencyBox.Maximum);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not load settings: {ex.Message}");
        }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var settings = new GuiSettings
            {
                Server = _serverBox.Text.Trim(),
                Room = _roomBox.Text.Trim(),
                Token = string.IsNullOrWhiteSpace(_tokenBox.Text) ? null : _tokenBox.Text.Trim(),
                LatencyMilliseconds = (int)_latencyBox.Value
            };
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            AppendLog($"Could not save settings: {ex.Message}");
        }
    }

    private static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WebMicForward", "gui-settings.json");

    private static void OpenExternal(string target)
    {
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    private sealed class GuiSettings
    {
        public string Server { get; set; } = "wss://example.workers.dev/ws";
        public string Room { get; set; } = "default";
        public string? Token { get; set; }
        public int LatencyMilliseconds { get; set; } = 80;
    }
}
