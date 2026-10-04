using System.Diagnostics;

namespace PhoneMic.Core;

/// <summary>
/// FIFO of device-format 16-bit PCM with pre-buffering, packet-loss concealment, a hard latency cap
/// and gentle latency tightening. Push() is called by network threads, Pull() by the audio thread.
/// Mirrors the Python receiver's JitterBuffer so both behave the same.
/// </summary>
public sealed class JitterBuffer
{
    public const int SlackMs = 30;          // tolerated steady-state excess above target before tightening
    public double HoldSeconds { get; set; } = 3.0;

    private readonly Stats _stats;
    private readonly object _lock = new();
    private byte[] _buf = new byte[1 << 16];
    private int _head;      // read index
    private int _count;     // bytes stored
    private bool _filling = true;
    private uint? _expected;
    private int _rate = 48000;
    private int _frameBytes = 2;
    private long _highSince = -1;

    public int TargetMs { get; set; }
    public int MaxMs { get; set; }
    public bool IsFilling { get { lock (_lock) return _filling; } }

    public JitterBuffer(Stats stats, int targetMs = 40, int maxMs = 250)
    {
        _stats = stats;
        TargetMs = targetMs;
        MaxMs = maxMs;
    }

    /// <summary>Set the (device) sample format and flush.</summary>
    public void Configure(int rate, int channels)
    {
        lock (_lock)
        {
            _rate = rate;
            _frameBytes = 2 * channels;
            _head = 0;
            _count = 0;
            _filling = true;
            _expected = null;
            _highSince = -1;
        }
    }

    public double BufferedMs
    {
        get { lock (_lock) return _count / (double)_frameBytes / _rate * 1000.0; }
    }

    public void ResetSequence()
    {
        lock (_lock) _expected = null;
    }

    public void Push(uint seq, ReadOnlySpan<byte> pcm)
    {
        lock (_lock)
        {
            if (_expected is uint expected)
            {
                uint gap = unchecked(seq - expected);
                if (gap == 0)
                {
                }
                else if (gap <= 50)
                {
                    // Lost packets: conceal with silence so timing stays intact.
                    _stats.Lost += gap;
                    WriteZeros((int)gap * pcm.Length);
                }
                else if (gap >= uint.MaxValue - 50)
                {
                    // Late or duplicate packet: already concealed, drop it.
                    _stats.Late++;
                    return;
                }
                else
                {
                    // Sender restarted or huge jump: resynchronise.
                    _head = 0;
                    _count = 0;
                    _filling = true;
                }
            }
            _expected = unchecked(seq + 1);
            Write(pcm);

            int maxBytes = MsToBytes(MaxMs);
            if (_count > maxBytes)
            {
                int excess = _count - MsToBytes(TargetMs);
                excess -= excess % _frameBytes;
                Discard(excess);
                _stats.Trims++;
            }
        }
    }

    /// <summary>Fill <paramref name="dest"/> completely (silence while pre-buffering or on underrun).</summary>
    public void Pull(Span<byte> dest)
    {
        lock (_lock)
        {
            if (_filling)
            {
                if (_count >= MsToBytes(TargetMs)) _filling = false;
                else
                {
                    dest.Clear();
                    return;
                }
            }
            if (_count >= dest.Length)
            {
                Read(dest);
                Tighten();
                return;
            }
            // Underrun: play what we have, pad with silence, then re-buffer.
            int have = _count;
            Read(dest[..have]);
            dest[have..].Clear();
            _filling = true;
            _stats.Underruns++;
        }
    }

    private void Tighten()
    {
        int excess = _count - MsToBytes(TargetMs + SlackMs);
        if (excess <= 0)
        {
            _highSince = -1;
            return;
        }
        long now = Stopwatch.GetTimestamp();
        if (_highSince < 0)
        {
            _highSince = now;
        }
        else if ((now - _highSince) / (double)Stopwatch.Frequency >= HoldSeconds)
        {
            int drop = _count - MsToBytes(TargetMs);
            drop -= drop % _frameBytes;
            Discard(drop);
            _stats.Trims++;
            _highSince = -1;
        }
    }

    private int MsToBytes(int ms) => Math.Max(_frameBytes, (int)((long)_rate * ms / 1000) * _frameBytes);

    // ---- ring buffer primitives (caller holds the lock) ----

    private void Write(ReadOnlySpan<byte> src)
    {
        EnsureCapacity(_count + src.Length);
        int tail = (_head + _count) % _buf.Length;
        int first = Math.Min(src.Length, _buf.Length - tail);
        src[..first].CopyTo(_buf.AsSpan(tail));
        src[first..].CopyTo(_buf.AsSpan(0));
        _count += src.Length;
    }

    private void WriteZeros(int n)
    {
        EnsureCapacity(_count + n);
        int tail = (_head + _count) % _buf.Length;
        int first = Math.Min(n, _buf.Length - tail);
        Array.Clear(_buf, tail, first);
        Array.Clear(_buf, 0, n - first);
        _count += n;
    }

    private void Read(Span<byte> dst)
    {
        int first = Math.Min(dst.Length, _buf.Length - _head);
        _buf.AsSpan(_head, first).CopyTo(dst);
        _buf.AsSpan(0, dst.Length - first).CopyTo(dst[first..]);
        _head = (_head + dst.Length) % _buf.Length;
        _count -= dst.Length;
    }

    private void Discard(int n)
    {
        _head = (_head + n) % _buf.Length;
        _count -= n;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _buf.Length) return;
        var nb = new byte[Math.Max(needed, _buf.Length * 2)];
        int first = Math.Min(_count, _buf.Length - _head);
        Array.Copy(_buf, _head, nb, 0, first);
        Array.Copy(_buf, 0, nb, first, _count - first);
        _buf = nb;
        _head = 0;
    }
}
