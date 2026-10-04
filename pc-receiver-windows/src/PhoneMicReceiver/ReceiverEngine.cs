using PhoneMic.Core;

namespace PhoneMicReceiver;

/// <summary>Wires the network server to the audio device: format tracking, conversion, jitter buffer.</summary>
public sealed class ReceiverEngine : IDisposable
{
    private readonly Stats _stats = new();
    private readonly JitterBuffer _jitter;
    private readonly ReceiverServer _server;
    private readonly object _lock = new();
    private AudioPlayer? _player;
    private StreamAdapter? _adapter;
    private long _lastLevelTicks;

    public int LatencyMs { get; set; } = 20;
    public string? DeviceId { get; private set; }
    public int PhoneRate { get; private set; }
    public int PhoneChannels { get; private set; }
    public string DeviceInfo { get; private set; } = "";
    public string? DeviceError { get; private set; }
    public float LevelDb { get; private set; } = -96f;

    public Stats Stats => _stats;
    public double BufferedMs => _jitter.BufferedMs;
    public int Port => _server.Port;
    public string Name => _server.Name;

    public event Action<string>? Log;

    public ReceiverEngine(Settings settings, string? deviceId)
    {
        DeviceId = deviceId;
        _jitter = new JitterBuffer(_stats, settings.BufferMs, settings.MaxBufferMs);
        _server = new ReceiverServer(settings.Port, string.IsNullOrWhiteSpace(settings.Name) ? Environment.MachineName : settings.Name!, _stats);
        _server.Log += m => Log?.Invoke(m);
        _server.SenderChanged += _ => _jitter.ResetSequence();
        _server.AudioReceived += OnAudio;
    }

    public void Start() => _server.Start();

    public void Stop()
    {
        _server.Stop();
        lock (_lock)
        {
            ClosePlayerLocked();
            PhoneRate = 0;
            PhoneChannels = 0;
        }
    }

    public void SetBufferMs(int ms) => _jitter.TargetMs = ms;

    /// <summary>Switch output device; takes effect immediately if audio is flowing.</summary>
    public void ChangeDevice(string? deviceId)
    {
        lock (_lock)
        {
            if (deviceId == DeviceId) return;
            DeviceId = deviceId;
            if (PhoneRate > 0)
            {
                int rate = PhoneRate, ch = PhoneChannels;
                PhoneRate = 0;
                ReopenLocked(rate, ch);
            }
        }
    }

    private void OnAudio(AudioPacket p)
    {
        StreamAdapter? adapter;
        lock (_lock)
        {
            if (p.SampleRate != PhoneRate || p.Channels != PhoneChannels) ReopenLocked(p.SampleRate, p.Channels);
            adapter = _adapter;
        }
        long now = Environment.TickCount64;
        if (now - _lastLevelTicks >= 50)
        {
            LevelDb = Level.Dbfs(p.Pcm);
            _lastLevelTicks = now;
        }
        if (adapter is null) return; // device could not be opened; keep counting packets for the UI
        _jitter.Push(p.Seq, adapter.Convert(p.Pcm));
    }

    private void ReopenLocked(int rate, int channels)
    {
        ClosePlayerLocked();
        PhoneRate = rate;
        PhoneChannels = channels;
        DeviceError = null;
        try
        {
            if (string.IsNullOrEmpty(DeviceId)) throw new InvalidOperationException("no output device selected");
            var player = AudioPlayer.Open(DeviceId, _jitter, LatencyMs);
            player.Stopped += ex =>
            {
                if (ex is not null) Log?.Invoke($"Playback stopped: {ex.Message}");
            };
            _player = player;
            _adapter = new StreamAdapter(rate, channels, player.Rate, player.Channels);
            DeviceInfo = $"{player.DeviceName} ({player.Rate} Hz, {player.Channels} ch, {player.Backend})";
            Log?.Invoke($"Phone sends {rate} Hz {(channels == 2 ? "stereo" : "mono")} -> {DeviceInfo}");
        }
        catch (Exception ex)
        {
            _adapter = null;
            DeviceError = ex.Message;
            DeviceInfo = "";
            Log?.Invoke($"Cannot open output device: {ex.Message}");
        }
    }

    private void ClosePlayerLocked()
    {
        _player?.Dispose();
        _player = null;
        _adapter = null;
    }

    public void Dispose()
    {
        Stop();
        _server.Dispose();
    }
}
