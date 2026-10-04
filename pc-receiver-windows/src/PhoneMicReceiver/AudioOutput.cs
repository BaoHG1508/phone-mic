using NAudio.CoreAudioApi;
using NAudio.Wave;
using PhoneMic.Core;

namespace PhoneMicReceiver;

public sealed record OutputDevice(string Id, string Name)
{
    public bool IsVirtualCable => Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase);
    public override string ToString() => Name;
}

public static class AudioDevices
{
    public static List<OutputDevice> List()
    {
        var list = new List<OutputDevice>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                list.Add(new OutputDevice(device.ID, device.FriendlyName));
            }
            finally
            {
                device.Dispose();
            }
        }
        return list;
    }

    public static string? DefaultId()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.ID;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Plays the jitter buffer into one output device. WASAPI shared mode at the device's mix rate
/// (lowest latency); falls back to the classic WaveOut API when WASAPI refuses the format.
/// </summary>
public sealed class AudioPlayer : IWaveProvider, IDisposable
{
    private readonly JitterBuffer _jitter;
    private IWavePlayer? _out;
    private MMDevice? _device;

    public WaveFormat WaveFormat { get; }
    public int Rate => WaveFormat.SampleRate;
    public int Channels => WaveFormat.Channels;
    public string DeviceName { get; private set; } = "";
    public string Backend { get; private set; } = "";

    public event Action<Exception?>? Stopped;

    private AudioPlayer(JitterBuffer jitter, WaveFormat format)
    {
        _jitter = jitter;
        WaveFormat = format;
    }

    public static AudioPlayer Open(string deviceId, JitterBuffer jitter, int latencyMs)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(deviceId);
        var mix = device.AudioClient.MixFormat;
        int channels = Math.Clamp(mix.Channels, 1, 8);
        var format = new WaveFormat(mix.SampleRate, 16, channels);

        // The audio thread starts pulling as soon as Play() runs, so the buffer must know the format first.
        jitter.Configure(format.SampleRate, format.Channels);

        var player = new AudioPlayer(jitter, format) { _device = device, DeviceName = device.FriendlyName };
        try
        {
            var wasapi = new WasapiOut(device, AudioClientShareMode.Shared, true, latencyMs);
            wasapi.PlaybackStopped += (_, e) => player.Stopped?.Invoke(e.Exception);
            wasapi.Init(player);
            wasapi.Play();
            player._out = wasapi;
            player.Backend = "WASAPI";
        }
        catch (Exception wasapiError)
        {
            var waveOut = new WaveOutEvent
            {
                DeviceNumber = FindWaveOutIndex(device.FriendlyName),
                DesiredLatency = Math.Max(60, latencyMs * 3),
                NumberOfBuffers = 3,
            };
            waveOut.PlaybackStopped += (_, e) => player.Stopped?.Invoke(e.Exception);
            waveOut.Init(player);
            waveOut.Play();
            player._out = waveOut;
            player.Backend = $"WaveOut (WASAPI refused: {wasapiError.GetType().Name})";
        }
        return player;
    }

    /// <summary>WaveOut numbers devices differently and truncates names to 31 chars; match by prefix.</summary>
    private static int FindWaveOutIndex(string friendlyName)
    {
        for (int i = 0; i < WaveOut.DeviceCount; i++)
        {
            var caps = WaveOut.GetCapabilities(i);
            if (!string.IsNullOrEmpty(caps.ProductName) && friendlyName.StartsWith(caps.ProductName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1; // default device
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        _jitter.Pull(buffer.AsSpan(offset, count));
        return count;
    }

    public void Dispose()
    {
        try { _out?.Stop(); } catch { }
        try { _out?.Dispose(); } catch { }
        _out = null;
        try { _device?.Dispose(); } catch { }
        _device = null;
    }
}
