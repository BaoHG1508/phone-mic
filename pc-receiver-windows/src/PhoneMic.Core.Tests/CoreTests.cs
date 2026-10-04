using System.Net;
using System.Net.Sockets;
using PhoneMic.Core;
using Xunit;

namespace PhoneMic.Core.Tests;

public class ProtocolTests
{
    [Fact]
    public void AudioHeaderMatchesSharedVector()
    {
        // Same vector as the Kotlin PmicTest and the Python test_protocol.py.
        var pcm = new byte[] { 0x01, 0x00, 0xFF, 0x7F };
        var bytes = Pmic.Pack(Pmic.TypeAudio, 258, 48000, 1, 16, pcm);
        Assert.Equal("504d0101" + "00000102" + "0000bb80" + "01" + "10" + "0004" + "0100ff7f", Convert.ToHexString(bytes).ToLowerInvariant());
    }

    [Fact]
    public void RoundTrip()
    {
        var pcm = Enumerable.Range(0, 960).Select(i => (byte)(i * 7)).ToArray();
        var raw = Pmic.Pack(Pmic.TypeAudio, uint.MaxValue, 44100, 2, 16, pcm);
        Assert.True(Pmic.TryParse(raw, out var pkt));
        Assert.Equal(Pmic.TypeAudio, pkt.Type);
        Assert.Equal(uint.MaxValue, pkt.Seq);
        Assert.Equal(44100, pkt.SampleRate);
        Assert.Equal(2, pkt.Channels);
        Assert.Equal(16, pkt.Bits);
        Assert.Equal(pcm, pkt.Payload);
        Assert.Equal(pcm.Length, Pmic.PayloadLength(raw));
    }

    [Fact]
    public void ControlText()
    {
        var raw = Pmic.Control(Pmic.TypeAnnounce, "Gaming-PC ✓");
        Assert.True(Pmic.TryParse(raw, out var pkt));
        Assert.Equal(Pmic.TypeAnnounce, pkt.Type);
        Assert.Equal(0u, pkt.Seq);
        Assert.Equal("Gaming-PC ✓", pkt.Text);
    }

    [Fact]
    public void RejectsGarbage()
    {
        Assert.False(Pmic.TryParse(new byte[3], out _));
        var bad = Pmic.Control(Pmic.TypePing);
        bad[0] = (byte)'X';
        Assert.False(Pmic.TryParse(bad, out _));
        Assert.Equal(-1, Pmic.PayloadLength(bad));
        var truncated = Pmic.Control(Pmic.TypeAnnounce, "hello");
        Assert.False(Pmic.TryParse(truncated.AsSpan(0, truncated.Length - 2), out _));
    }
}

public class JitterBufferTests
{
    private static JitterBuffer Make(int targetMs = 40, int maxMs = 250)
    {
        var jb = new JitterBuffer(new Stats(), targetMs, maxMs);
        jb.Configure(48000, 1);
        return jb;
    }

    private static byte[] Frame(short value, int ms = 10)
    {
        var s = new short[48000 * ms / 1000];
        Array.Fill(s, value);
        return System.Runtime.InteropServices.MemoryMarshal.AsBytes(s.AsSpan()).ToArray();
    }

    private static short[] Pull(JitterBuffer jb, int bytes)
    {
        var buf = new byte[bytes];
        jb.Pull(buf);
        return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(buf).ToArray();
    }

    [Fact]
    public void PrebuffersThenPlays()
    {
        var jb = Make(40);
        jb.Push(0, Frame(1));
        Assert.All(Pull(jb, 960), v => Assert.Equal(0, v)); // still filling, nothing consumed
        for (uint seq = 1; seq < 4; seq++) jb.Push(seq, Frame((short)(seq + 1)));
        Assert.All(Pull(jb, 960), v => Assert.Equal(1, v));
        Assert.Equal(30.0, jb.BufferedMs, 3);
    }

    [Fact]
    public void LossConcealedWithSilence()
    {
        var stats = new Stats();
        var jb = new JitterBuffer(stats, 10, 250);
        jb.Configure(48000, 1);
        jb.Push(0, Frame(5));
        jb.Push(2, Frame(7)); // seq 1 lost
        Assert.Equal(1, stats.Lost);
        Pull(jb, 960);
        Assert.All(Pull(jb, 960), v => Assert.Equal(0, v));
        Assert.All(Pull(jb, 960), v => Assert.Equal(7, v));
    }

    [Fact]
    public void LatePacketDropped()
    {
        var stats = new Stats();
        var jb = new JitterBuffer(stats, 10, 250);
        jb.Configure(48000, 1);
        jb.Push(5, Frame(1));
        jb.Push(6, Frame(2));
        jb.Push(4, Frame(9));
        Assert.Equal(1, stats.Late);
        Assert.Equal(20.0, jb.BufferedMs, 3);
    }

    [Fact]
    public void LatencyCapTrimsOldAudio()
    {
        var stats = new Stats();
        var jb = new JitterBuffer(stats, 40, 100);
        jb.Configure(48000, 1);
        for (uint seq = 0; seq < 15; seq++) jb.Push(seq, Frame((short)seq)); // 150 ms without a pull
        Assert.Equal(1, stats.Trims);
        Assert.True(jb.BufferedMs <= 100.0);
        Assert.True(Pull(jb, 960)[0] > 0); // oldest frames dropped
    }

    [Fact]
    public void UnderrunRefills()
    {
        var stats = new Stats();
        var jb = new JitterBuffer(stats, 10, 250);
        jb.Configure(48000, 1);
        jb.Push(0, Frame(3));
        Pull(jb, 960);
        Assert.All(Pull(jb, 960), v => Assert.Equal(0, v));
        Assert.Equal(1, stats.Underruns);
        Assert.True(jb.IsFilling);
    }

    [Fact]
    public void PersistentExcessIsTightenedOnce()
    {
        var stats = new Stats();
        var jb = new JitterBuffer(stats, 40, 500) { HoldSeconds = 0.05 };
        jb.Configure(48000, 1);
        for (uint seq = 0; seq < 20; seq++) jb.Push(seq, Frame(1)); // 200 ms queued
        Pull(jb, 960);
        Assert.Equal(0, stats.Trims);
        Thread.Sleep(80);
        Pull(jb, 960);
        Assert.Equal(1, stats.Trims);
        Assert.True(jb.BufferedMs <= 40.01);
        Pull(jb, 960);
        Assert.Equal(1, stats.Trims);
    }

    [Fact]
    public void RingBufferWrapsCorrectly()
    {
        var jb = Make(10, 5000);
        // Push far more than the initial 64 KiB capacity in odd-sized chunks and read back in order.
        uint seq = 0;
        var expected = new List<short>();
        for (int i = 0; i < 300; i++)
        {
            var s = new short[333];
            for (int k = 0; k < s.Length; k++) s[k] = (short)(i * 7 + k);
            expected.AddRange(s);
            jb.Push(seq++, System.Runtime.InteropServices.MemoryMarshal.AsBytes(s.AsSpan()));
            if (i % 3 == 2) Pull(jb, 2 * 500); // drain a little as we go
        }
        // Drain everything and compare against the expected sequence (prefix removed by earlier pulls).
        var got = new List<short>();
        int pulledBefore = 100 * 500; // 100 pulls of 500 samples
        while (jb.BufferedMs > 0) got.AddRange(Pull(jb, 2 * 111));
        var tail = got.TakeWhile((_, idx) => idx < expected.Count - pulledBefore).ToArray();
        Assert.Equal(expected.Skip(pulledBefore).Take(tail.Length), tail);
    }
}

public class DspTests
{
    [Fact]
    public void ResamplerRatioAndContinuity()
    {
        var rs = new Resampler(48000, 44100, 1);
        var sig = new short[48000];
        for (int i = 0; i < sig.Length; i++) sig[i] = (short)(10000 * Math.Sin(2 * Math.PI * 440 * i / 48000.0));
        var outp = new List<short>();
        for (int i = 0; i < sig.Length; i += 480) outp.AddRange(rs.Process(sig.AsSpan(i, 480)));
        Assert.InRange(outp.Count, 44098, 44102);
        double maxStep = 0;
        for (int i = 1; i < outp.Count; i++) maxStep = Math.Max(maxStep, Math.Abs(outp[i] - outp[i - 1]));
        Assert.True(maxStep < 10000 * 2 * Math.PI * 440 / 44100 * 1.5, $"max step {maxStep}");
    }

    [Fact]
    public void ChannelConversion()
    {
        Assert.Equal(new short[] { 1, 1, 2, 2, 3, 3 }, ChannelMixer.Convert(new short[] { 1, 2, 3 }, 1, 2));
        Assert.Equal(new short[] { 3, 0 }, ChannelMixer.Convert(new short[] { 2, 4, -2, 2 }, 2, 1));
    }

    [Fact]
    public void AdapterPassThroughAndConvert()
    {
        var same = new StreamAdapter(48000, 1, 48000, 1);
        Assert.True(same.IsPassThrough);
        var bytes = new byte[] { 1, 0, 2, 0 };
        Assert.Equal(bytes, same.Convert(bytes));

        var up = new StreamAdapter(48000, 1, 48000, 2);
        Assert.Equal(new byte[] { 1, 0, 1, 0, 2, 0, 2, 0 }, up.Convert(bytes));
    }

    [Fact]
    public void LevelMeter()
    {
        Assert.Equal(-96f, Level.Dbfs(new byte[200]));
        var loud = new short[200];
        for (int i = 0; i < loud.Length; i++) loud[i] = i % 2 == 0 ? short.MaxValue : short.MinValue;
        Assert.Equal(0f, Level.Dbfs(System.Runtime.InteropServices.MemoryMarshal.AsBytes(loud.AsSpan())), 2);
        var half = new short[200];
        for (int i = 0; i < half.Length; i++) half[i] = (short)(i % 2 == 0 ? 16384 : -16384);
        Assert.Equal(-6.02f, Level.Dbfs(System.Runtime.InteropServices.MemoryMarshal.AsBytes(half.AsSpan())), 1);
    }
}

public class ServerTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public void UdpAudioDiscoveryAndPing()
    {
        int port = FreePort();
        using var server = new ReceiverServer(port, "TestPC");
        var received = new List<AudioPacket>();
        var gotAudio = new ManualResetEventSlim();
        server.AudioReceived += p => { lock (received) received.Add(p); gotAudio.Set(); };
        server.Start();

        using var udp = new UdpClient();
        udp.Client.ReceiveTimeout = 2000;
        var target = new IPEndPoint(IPAddress.Loopback, port);

        udp.Send(Pmic.Control(Pmic.TypeDiscover, "test"), target);
        var ep = new IPEndPoint(IPAddress.Any, 0);
        Assert.True(Pmic.TryParse(udp.Receive(ref ep), out var announce));
        Assert.Equal(Pmic.TypeAnnounce, announce.Type);
        Assert.Equal("TestPC", announce.Text);

        udp.Send(Pmic.Control(Pmic.TypePing), target);
        Assert.True(Pmic.TryParse(udp.Receive(ref ep), out var pong));
        Assert.Equal(Pmic.TypePong, pong.Type);

        var pcm = new byte[960];
        udp.Send(Pmic.Pack(Pmic.TypeAudio, 7, 48000, 1, 16, pcm), target);
        Assert.True(gotAudio.Wait(2000));
        Assert.Single(received);
        Assert.Equal(7u, received[0].Seq);
        Assert.Equal(48000, received[0].SampleRate);
        Assert.Equal(1, server.Stats.Packets);
        Assert.Contains("(UDP)", server.Stats.LastSender);
    }

    [Fact]
    public void TcpFramingAndPong()
    {
        int port = FreePort();
        using var server = new ReceiverServer(port, "TestPC");
        int audioCount = 0;
        var gotThree = new ManualResetEventSlim();
        server.AudioReceived += _ => { if (Interlocked.Increment(ref audioCount) == 3) gotThree.Set(); };
        server.Start();

        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        using var stream = client.GetStream();
        stream.ReadTimeout = 2000;
        // Three audio packets and a ping written in one burst: the framer must split them by payloadLen.
        var burst = new List<byte>();
        for (uint seq = 0; seq < 3; seq++) burst.AddRange(Pmic.Pack(Pmic.TypeAudio, seq, 16000, 1, 16, new byte[640]));
        burst.AddRange(Pmic.Control(Pmic.TypePing));
        stream.Write(burst.ToArray());

        var header = new byte[Pmic.HeaderSize];
        stream.ReadExactly(header);
        int len = Pmic.PayloadLength(header);
        Assert.True(len >= 0);
        var payload = new byte[len];
        stream.ReadExactly(payload);
        Assert.True(Pmic.TryParse(header.Concat(payload).ToArray(), out var pong));
        Assert.Equal(Pmic.TypePong, pong.Type);
        Assert.True(gotThree.Wait(2000));
        Assert.Equal(3, server.Stats.Packets);
    }
}
