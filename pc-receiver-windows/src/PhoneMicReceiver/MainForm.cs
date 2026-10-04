using System.ComponentModel;
using System.Diagnostics;
using PhoneMic.Core;

namespace PhoneMicReceiver;

public sealed class MainForm : Form
{
    private readonly Settings _settings = Settings.Load();
    private ReceiverEngine? _engine;
    private bool _exiting;
    private long _lastPackets, _lastBytes, _lastTick;

    private readonly ComboBox _cmbDevice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    private readonly Button _btnRefresh = new() { Text = "Refresh", AutoSize = true };
    private readonly LinkLabel _lnkCable = new()
    {
        AutoSize = true,
        Text = "VB-CABLE not found. Install it (free) so Windows apps can use the phone as a microphone.",
    };
    private readonly NumericUpDown _numPort = new() { Minimum = 1, Maximum = 65535, Width = 80 };
    private readonly NumericUpDown _numBuffer = new() { Minimum = 10, Maximum = 500, Increment = 10, Width = 80 };
    private readonly Button _btnStartStop = new() { Text = "Start listening", Size = new Size(160, 36) };
    private readonly Button _btnFirewall = new() { Text = "Allow through Windows Firewall…", AutoSize = true, Height = 36 };
    private readonly Label _lblStatus = new()
    {
        AutoSize = true,
        MaximumSize = new Size(590, 0),
        Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
        Text = "Stopped",
    };
    private readonly Label _lblStats = new() { AutoSize = true, MaximumSize = new Size(590, 0), ForeColor = SystemColors.GrayText };
    private readonly ProgressBar _pbLevel = new() { Minimum = 0, Maximum = 100, Size = new Size(590, 14) };
    private readonly Label _lblHint = new()
    {
        AutoSize = true,
        MaximumSize = new Size(590, 0),
        ForeColor = SystemColors.GrayText,
        Text = "Then choose \"CABLE Output\" as the microphone in Windows Sound settings, Discord, Zoom or OBS. " +
               "On the phone: open PhoneMic, tap Find PC, pick this computer, tap Start microphone.",
    };
    private readonly TextBox _txtLog = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Font = new Font("Consolas", 9f),
        BackColor = SystemColors.Window,
        Dock = DockStyle.Fill,
    };
    private readonly CheckBox _chkAutoStart = new() { Text = "Start listening when the app opens", AutoSize = true };
    private readonly CheckBox _chkTray = new() { Text = "Keep running in the tray when the window is closed", AutoSize = true };
    private readonly NotifyIcon _tray = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    public MainForm()
    {
        Text = "PhoneMic Receiver";
        Icon = AppIcon.Create();
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ClientSize = new Size(624, 660);

        BuildLayout();
        LoadSettingsIntoUi();
        WireEvents();
        SetupTray();
        PopulateDevices();
        AppendLog("PhoneMic Receiver ready.");
    }

    // ------------------------------------------------------------------ layout

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(16, 12, 16, 12),
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        int row = 0;

        void Add(Control c, bool fill = false)
        {
            root.RowCount = row + 1;
            root.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            root.Controls.Add(c, 0, row);
            row++;
        }

        Add(Section("Output device"));
        Add(Row(_cmbDevice, _btnRefresh));
        Add(_lnkCable);
        Add(Row(FieldLabel("Port"), _numPort, FieldLabel("   Buffer (ms)"), _numBuffer,
            FieldLabel("lower = less delay, higher = fewer dropouts")));

        Add(Section("Status"));
        Add(_lblStatus);
        Add(_lblStats);
        Add(_pbLevel);
        Add(_lblHint);
        Add(Row(_btnStartStop, _btnFirewall));

        Add(Section("Log"));
        Add(_txtLog, fill: true);
        Add(_chkAutoStart);
        Add(_chkTray);

        Controls.Add(root);
    }

    private static Label Section(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 10f),
        Margin = new Padding(0, 10, 0, 4),
    };

    private static Label FieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Padding = new Padding(0, 5, 0, 0),
        ForeColor = SystemColors.ControlText,
    };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 2, 0, 2),
        };
        foreach (var c in controls)
        {
            c.Margin = new Padding(0, 3, 8, 3);
            panel.Controls.Add(c);
        }
        return panel;
    }

    // ------------------------------------------------------------------ settings

    private void LoadSettingsIntoUi()
    {
        _numPort.Value = Math.Clamp(_settings.Port, 1, 65535);
        _numBuffer.Value = Math.Clamp(_settings.BufferMs, 10, 500);
        _chkAutoStart.Checked = _settings.AutoStart;
        _chkTray.Checked = _settings.MinimizeToTray;
    }

    private void SaveUiToSettings()
    {
        _settings.Port = (int)_numPort.Value;
        _settings.BufferMs = (int)_numBuffer.Value;
        _settings.AutoStart = _chkAutoStart.Checked;
        _settings.MinimizeToTray = _chkTray.Checked;
        _settings.DeviceId = SelectedDeviceId();
        _settings.Save();
    }

    private string? SelectedDeviceId() => (_cmbDevice.SelectedItem as OutputDevice)?.Id;

    // ------------------------------------------------------------------ events

    private void WireEvents()
    {
        Load += (_, _) =>
        {
            if (_chkAutoStart.Checked) StartEngine();
        };
        _btnRefresh.Click += (_, _) => PopulateDevices();
        _lnkCable.LinkClicked += (_, _) => OpenUrl("https://vb-audio.com/Cable/");
        _btnStartStop.Click += (_, _) =>
        {
            if (_engine is null) StartEngine();
            else StopEngine();
        };
        _btnFirewall.Click += (_, _) => AddFirewallRule();
        _cmbDevice.SelectedIndexChanged += (_, _) =>
        {
            var id = SelectedDeviceId();
            _settings.DeviceId = id;
            _settings.Save();
            _engine?.ChangeDevice(id);
        };
        _numBuffer.ValueChanged += (_, _) =>
        {
            _settings.BufferMs = (int)_numBuffer.Value;
            _engine?.SetBufferMs(_settings.BufferMs);
        };
        _chkAutoStart.CheckedChanged += (_, _) => SaveUiToSettings();
        _chkTray.CheckedChanged += (_, _) => SaveUiToSettings();
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
        FormClosing += OnFormClosing;
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized && _chkTray.Checked) Hide();
        };
    }

    private void PopulateDevices()
    {
        List<OutputDevice> devices;
        try
        {
            devices = AudioDevices.List();
        }
        catch (Exception ex)
        {
            AppendLog("Cannot list audio devices: " + ex.Message);
            return;
        }

        _cmbDevice.BeginUpdate();
        _cmbDevice.Items.Clear();
        foreach (var d in devices) _cmbDevice.Items.Add(d);
        _cmbDevice.EndUpdate();

        bool hasCable = devices.Any(d => d.IsVirtualCable);
        _lnkCable.Visible = !hasCable;

        int idx = devices.FindIndex(d => d.Id == _settings.DeviceId);
        if (idx < 0) idx = devices.FindIndex(d => d.IsVirtualCable);
        if (idx < 0)
        {
            var def = AudioDevices.DefaultId();
            idx = devices.FindIndex(d => d.Id == def);
        }
        if (idx < 0 && devices.Count > 0) idx = 0;
        _cmbDevice.SelectedIndex = idx;

        if (devices.Count == 0) AppendLog("No audio output devices found.");
        else if (!hasCable) AppendLog("No VB-CABLE found: the phone will play through the selected speakers. Install VB-CABLE to use it as a microphone.");
        else AppendLog($"Using virtual cable: {devices.First(d => d.IsVirtualCable).Name}");
    }

    private void StartEngine()
    {
        SaveUiToSettings();
        var engine = new ReceiverEngine(_settings, SelectedDeviceId());
        engine.Log += AppendLog;
        try
        {
            engine.Start();
        }
        catch (Exception ex)
        {
            engine.Dispose();
            AppendLog("Cannot start: " + ex.Message);
            MessageBox.Show(this,
                $"Cannot listen on port {_settings.Port}:\n{ex.Message}\n\nIs another receiver already running?",
                "PhoneMic Receiver", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        _engine = engine;
        _lastPackets = 0;
        _lastBytes = 0;
        _lastTick = Environment.TickCount64;
        _btnStartStop.Text = "Stop";
        _numPort.Enabled = false;
        RefreshStatus();
    }

    private void StopEngine()
    {
        _engine?.Dispose();
        _engine = null;
        _btnStartStop.Text = "Start listening";
        _numPort.Enabled = true;
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var e = _engine;
        if (e is null)
        {
            _lblStatus.Text = "Stopped";
            _lblStatus.ForeColor = SystemColors.GrayText;
            _lblStats.Text = "";
            _pbLevel.Value = 0;
            _tray.Text = "PhoneMic Receiver: stopped";
            return;
        }

        var s = e.Stats;
        long now = Environment.TickCount64;
        long packets = Interlocked.Read(ref s.Packets);
        long bytes = Interlocked.Read(ref s.Bytes);
        long lastPacket = Interlocked.Read(ref s.LastPacketTicks);
        double dt = Math.Max(1, now - _lastTick) / 1000.0;
        double pps = (packets - _lastPackets) / dt;
        double kbps = (bytes - _lastBytes) * 8 / dt / 1000.0;
        _lastPackets = packets;
        _lastBytes = bytes;
        _lastTick = now;

        bool receiving = lastPacket != 0 && now - lastPacket < 3000;
        string status;
        Color color;
        if (lastPacket == 0)
        {
            status = $"Listening on port {e.Port} as \"{e.Name}\". Waiting for the phone…";
            color = SystemColors.ControlText;
        }
        else if (!receiving)
        {
            status = $"Phone stopped sending {(now - lastPacket) / 1000} s ago (last: {s.LastSender})";
            color = Color.DarkOrange;
        }
        else if (e.DeviceError is not null)
        {
            status = $"Receiving from {s.LastSender}, but the output device failed: {e.DeviceError}";
            color = Color.Firebrick;
        }
        else
        {
            status = $"Receiving from {s.LastSender}: {e.PhoneRate} Hz {(e.PhoneChannels == 2 ? "stereo" : "mono")} → {e.DeviceInfo}";
            color = Color.SeaGreen;
        }
        _lblStatus.Text = status;
        _lblStatus.ForeColor = color;
        _lblStats.Text = receiving
            ? $"{pps:F0} pkt/s · {kbps:F0} kbit/s · buffer {e.BufferedMs:F0} ms · lost {s.Lost} · late {s.Late} · underruns {s.Underruns} · trims {s.Trims}"
            : $"total {packets} packets · lost {s.Lost} · underruns {s.Underruns}";
        _pbLevel.Value = receiving ? Math.Clamp((int)((e.LevelDb + 60f) / 60f * 100f), 0, 100) : 0;
        _tray.Text = receiving ? "PhoneMic Receiver: receiving" : "PhoneMic Receiver: waiting for phone";
    }

    // ------------------------------------------------------------------ tray

    private void SetupTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show window", null, (_, _) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _tray.Icon = Icon;
        _tray.Text = "PhoneMic Receiver";
        _tray.ContextMenuStrip = menu;
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowWindow();
    }

    private void ShowWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_exiting && _chkTray.Checked && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _tray.ShowBalloonTip(2500, "PhoneMic Receiver", "Still running in the tray. Right-click the icon to exit.", ToolTipIcon.Info);
            return;
        }
        SaveUiToSettings();
        _timer.Stop();
        _engine?.Dispose();
        _engine = null;
        _tray.Visible = false;
        _tray.Dispose();
    }

    // ------------------------------------------------------------------ helpers

    private void AddFirewallRule()
    {
        string exe = Environment.ProcessPath ?? Application.ExecutablePath;
        var psi = new ProcessStartInfo("netsh",
            $"advfirewall firewall add rule name=\"PhoneMic Receiver\" dir=in action=allow program=\"{exe}\" enable=yes profile=any")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi);
            p?.WaitForExit(15000);
            AppendLog(p is { ExitCode: 0 } ? "Firewall rule added for this program." : $"netsh finished with code {p?.ExitCode}.");
        }
        catch (Win32Exception)
        {
            AppendLog("Firewall rule not added (administrator approval declined).");
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }

    private void AppendLog(string message)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action<string>(AppendLog), message);
            }
            catch (InvalidOperationException)
            {
                // window is going away
            }
            return;
        }
        if (_txtLog.Lines.Length > 400) _txtLog.Lines = _txtLog.Lines.Skip(200).ToArray();
        _txtLog.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
    }
}
