using System.Net;
using System.Net.Sockets;

namespace PhoneMic.Core;

public sealed record AudioPacket(string Sender, uint Seq, int SampleRate, int Channels, byte[] Pcm);

/// <summary>
/// Listens for PhoneMic packets on UDP and TCP (same port), answers discovery and keep-alives,
/// and raises <see cref="AudioReceived"/> for every audio frame. Thread-safe; events fire on network threads.
/// </summary>
public sealed class ReceiverServer : IDisposable
{
    public int Port { get; }
    public string Name { get; }
    public Stats Stats { get; }

    public event Action<string>? Log;
    public event Action<AudioPacket>? AudioReceived;
    public event Action<string>? SenderChanged;

    private volatile bool _running;
    private Socket? _udp;
    private TcpListener? _tcp;
    private readonly List<Thread> _threads = new();

    public ReceiverServer(int port, string name, Stats? stats = null)
    {
        Port = port;
        Name = name;
        Stats = stats ?? new Stats();
    }

    public void Start()
    {
        if (_running) return;
        _udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try { _udp.ReceiveBufferSize = 1 << 20; } catch (SocketException) { }
        _udp.Bind(new IPEndPoint(IPAddress.Any, Port));
        _udp.ReceiveTimeout = 500;

        _tcp = new TcpListener(IPAddress.Any, Port);
        _tcp.Server.NoDelay = true;
        _tcp.Start(4);

        _running = true;
        Stats.Reset();
        StartThread(UdpLoop, "pmic-udp");
        StartThread(TcpAcceptLoop, "pmic-tcp-accept");
        Log?.Invoke($"Listening on UDP+TCP port {Port} as '{Name}'");
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        try { _udp?.Close(); } catch { }
        try { _tcp?.Stop(); } catch { }
        foreach (var t in _threads) t.Join(2000);
        _threads.Clear();
        _udp = null;
        _tcp = null;
        Log?.Invoke("Stopped listening");
    }

    public void Dispose() => Stop();

    private void StartThread(ThreadStart body, string name)
    {
        var t = new Thread(body) { IsBackground = true, Name = name };
        _threads.Add(t);
        t.Start();
    }

    // ------------------------------------------------------------------ dispatch

    private void Handle(Packet pkt, string sender, Action<byte[]> reply)
    {
        switch (pkt.Type)
        {
            case Pmic.TypeAudio:
                if (pkt.Bits != 16 || pkt.Channels is < 1 or > 2 || pkt.SampleRate is < 8000 or > 192000) return;
                if (sender != Stats.LastSender)
                {
                    Stats.LastSender = sender;
                    Log?.Invoke($"Audio from {sender}: {pkt.SampleRate} Hz {(pkt.Channels == 2 ? "stereo" : "mono")}");
                    SenderChanged?.Invoke(sender);
                }
                Interlocked.Increment(ref Stats.Packets);
                Interlocked.Add(ref Stats.Bytes, pkt.Payload.Length);
                Interlocked.Exchange(ref Stats.LastPacketTicks, Environment.TickCount64);
                AudioReceived?.Invoke(new AudioPacket(sender, pkt.Seq, pkt.SampleRate, pkt.Channels, pkt.Payload));
                break;
            case Pmic.TypePing:
                reply(Pmic.Control(Pmic.TypePong, Name));
                break;
            case Pmic.TypeDiscover:
                Log?.Invoke($"Discovery request from {sender} ({(pkt.Text.Length > 0 ? pkt.Text : "phone")})");
                reply(Pmic.Control(Pmic.TypeAnnounce, Name));
                break;
            case Pmic.TypeBye:
                Log?.Invoke($"{sender} stopped streaming");
                break;
        }
    }

    // ------------------------------------------------------------------ UDP

    private void UdpLoop()
    {
        var sock = _udp!;
        var buf = new byte[Pmic.MaxPacket];
        EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
        while (_running)
        {
            int n;
            try
            {
                n = sock.ReceiveFrom(buf, ref ep);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
            {
                continue;
            }
            catch (SocketException)
            {
                if (_running) Thread.Sleep(50);
                continue;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            if (!Pmic.TryParse(buf.AsSpan(0, n), out var pkt)) continue;
            var from = (IPEndPoint)ep;
            Handle(pkt, $"{from.Address} (UDP)", reply =>
            {
                try { sock.SendTo(reply, from); } catch (SocketException) { } catch (ObjectDisposedException) { }
            });
        }
    }

    // ------------------------------------------------------------------ TCP

    private void TcpAcceptLoop()
    {
        var listener = _tcp!;
        while (_running)
        {
            TcpClient client;
            try
            {
                client = listener.AcceptTcpClient();
            }
            catch (SocketException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                break;
            }
            var t = new Thread(() => TcpClientLoop(client)) { IsBackground = true, Name = "pmic-tcp-client" };
            t.Start();
        }
    }

    private void TcpClientLoop(TcpClient client)
    {
        string label;
        try
        {
            var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
            label = $"{remote.Address} (TCP)";
        }
        catch
        {
            label = "? (TCP)";
        }
        Log?.Invoke($"TCP connection from {label}");
        var sendLock = new object();
        try
        {
            client.NoDelay = true;
            using var stream = client.GetStream();
            stream.ReadTimeout = 10000;

            void Reply(byte[] b)
            {
                lock (sendLock)
                {
                    try { stream.Write(b, 0, b.Length); } catch (IOException) { } catch (ObjectDisposedException) { }
                }
            }

            var header = new byte[Pmic.HeaderSize];
            while (_running)
            {
                if (!ReadExact(stream, header, header.Length)) break;
                int len = Pmic.PayloadLength(header);
                if (len < 0)
                {
                    Log?.Invoke($"{label}: bad packet header, closing");
                    break;
                }
                var whole = new byte[Pmic.HeaderSize + len];
                Array.Copy(header, whole, Pmic.HeaderSize);
                if (len > 0 && !ReadExact(stream, whole, len, Pmic.HeaderSize)) break;
                if (Pmic.TryParse(whole, out var pkt)) Handle(pkt, label, Reply);
            }
        }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            try { client.Close(); } catch { }
            Log?.Invoke($"{label} disconnected");
        }
    }

    private bool ReadExact(NetworkStream stream, byte[] buf, int count, int offset = 0)
    {
        int got = 0;
        while (got < count)
        {
            int n;
            try
            {
                n = stream.Read(buf, offset + got, count - got);
            }
            catch (IOException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut })
            {
                if (!_running) return false;
                continue;
            }
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }
}
