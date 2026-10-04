using System.Runtime.InteropServices;

namespace PhoneMic.Core;

/// <summary>Streaming linear-interpolation resampler for interleaved 16-bit PCM.</summary>
public sealed class Resampler
{
    private readonly double _ratio;
    private readonly int _channels;
    private readonly float[] _tail;
    private double _pos;
    private bool _primed;

    public Resampler(int sourceRate, int targetRate, int channels)
    {
        _ratio = sourceRate / (double)targetRate;
        _channels = channels;
        _tail = new float[channels];
    }

    public short[] Process(ReadOnlySpan<short> input)
    {
        int inFrames = input.Length / _channels;
        if (inFrames == 0) return Array.Empty<short>();

        int total = inFrames + (_primed ? 1 : 0);
        var x = new float[total * _channels];
        int offset = 0;
        if (_primed)
        {
            Array.Copy(_tail, 0, x, 0, _channels);
            offset = _channels;
        }
        _primed = true;
        for (int i = 0; i < inFrames * _channels; i++) x[offset + i] = input[i];

        int last = total - 1;
        Array.Copy(x, last * _channels, _tail, 0, _channels);
        if (last < 1) return Array.Empty<short>();
        if (_pos > last)
        {
            _pos -= last;
            return Array.Empty<short>();
        }

        int nOut = (int)Math.Floor((last - _pos) / _ratio) + 1;
        var y = new short[nOut * _channels];
        double idx = _pos;
        for (int n = 0; n < nOut; n++, idx += _ratio)
        {
            int i0 = (int)Math.Floor(idx);
            if (i0 > last - 1) i0 = last - 1;
            double frac = idx - i0;
            for (int c = 0; c < _channels; c++)
            {
                double v = x[i0 * _channels + c] * (1.0 - frac) + x[(i0 + 1) * _channels + c] * frac;
                y[n * _channels + c] = (short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue);
            }
        }
        _pos = idx - last;
        return y;
    }
}

public static class ChannelMixer
{
    /// <summary>Duplicate a mono signal or average a stereo one to reach <paramref name="outChannels"/>.</summary>
    public static short[] Convert(ReadOnlySpan<short> pcm, int inChannels, int outChannels)
    {
        if (inChannels == outChannels) return pcm.ToArray();
        int frames = pcm.Length / inChannels;
        var outp = new short[frames * outChannels];
        for (int f = 0; f < frames; f++)
        {
            int sum = 0;
            for (int c = 0; c < inChannels; c++) sum += pcm[f * inChannels + c];
            short mono = (short)(sum / inChannels);
            if (inChannels == 1)
            {
                for (int c = 0; c < outChannels; c++) outp[f * outChannels + c] = pcm[f];
            }
            else
            {
                for (int c = 0; c < outChannels; c++) outp[f * outChannels + c] = mono;
            }
        }
        return outp;
    }
}

/// <summary>Converts the phone's PCM (rate/channels) to the audio device's format.</summary>
public sealed class StreamAdapter
{
    private readonly Resampler? _resampler;
    private readonly int _inChannels;
    private readonly int _outChannels;

    public bool IsPassThrough => _resampler is null && _inChannels == _outChannels;

    public StreamAdapter(int inRate, int inChannels, int outRate, int outChannels)
    {
        _inChannels = inChannels;
        _outChannels = outChannels;
        _resampler = inRate != outRate ? new Resampler(inRate, outRate, inChannels) : null;
    }

    public byte[] Convert(ReadOnlySpan<byte> pcmLittleEndian16)
    {
        if (IsPassThrough) return pcmLittleEndian16.ToArray();
        ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(pcmLittleEndian16);
        samples = samples[..(samples.Length - samples.Length % _inChannels)];
        short[] work = _resampler is null ? samples.ToArray() : _resampler.Process(samples);
        if (_inChannels != _outChannels) work = ChannelMixer.Convert(work, _inChannels, _outChannels);
        return MemoryMarshal.AsBytes(work.AsSpan()).ToArray();
    }
}

public static class Level
{
    /// <summary>RMS level of little-endian 16-bit PCM in dBFS, clamped to [-96, 0].</summary>
    public static float Dbfs(ReadOnlySpan<byte> pcm)
    {
        ReadOnlySpan<short> s = MemoryMarshal.Cast<byte, short>(pcm);
        if (s.Length == 0) return -96f;
        double sum = 0;
        foreach (short v in s) sum += (double)v * v;
        double rms = Math.Sqrt(sum / s.Length);
        if (rms < 1.0) return -96f;
        return (float)Math.Clamp(20.0 * Math.Log10(rms / 32768.0), -96.0, 0.0);
    }
}
