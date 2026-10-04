using System.Buffers.Binary;
using System.Text;

namespace PhoneMic.Core;

/// <summary>
/// PhoneMic wire format, identical to the Android app and the Python receiver (see PROTOCOL.md).
/// 16-byte big-endian header: magic "PM", version, type, seq(u32), sampleRate(u32), channels, bits, payloadLen(u16).
/// </summary>
public static class Pmic
{
    public const byte Magic0 = 0x50, Magic1 = 0x4D, Version = 1;
    public const int HeaderSize = 16;
    public const int DefaultPort = 48111;
    public const int MaxPayload = 1400;
    public const int MaxPacket = HeaderSize + MaxPayload;

    public const byte TypeAudio = 1, TypeDiscover = 2, TypeAnnounce = 3, TypePing = 4, TypePong = 5, TypeBye = 6;

    /// <summary>Payload length declared by a header, or -1 when the bytes are not a PhoneMic header.</summary>
    public static int PayloadLength(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderSize || header[0] != Magic0 || header[1] != Magic1 || header[2] != Version) return -1;
        return (header[14] << 8) | header[15];
    }

    public static bool TryParse(ReadOnlySpan<byte> data, out Packet packet)
    {
        packet = null!;
        int len = PayloadLength(data);
        if (len < 0 || data.Length < HeaderSize + len) return false;
        packet = new Packet(
            data[3],
            BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4)),
            (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8, 4)),
            data[12],
            data[13],
            data.Slice(HeaderSize, len).ToArray());
        return true;
    }

    public static byte[] Pack(byte type, uint seq, int sampleRate, int channels, int bits, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 0xFFFF) throw new ArgumentException("payload too large", nameof(payload));
        var buf = new byte[HeaderSize + payload.Length];
        buf[0] = Magic0;
        buf[1] = Magic1;
        buf[2] = Version;
        buf[3] = type;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), seq);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8, 4), (uint)sampleRate);
        buf[12] = (byte)channels;
        buf[13] = (byte)bits;
        buf[14] = (byte)(payload.Length >> 8);
        buf[15] = (byte)payload.Length;
        payload.CopyTo(buf.AsSpan(HeaderSize));
        return buf;
    }

    public static byte[] Control(byte type, string text = "") => Pack(type, 0, 0, 0, 0, Encoding.UTF8.GetBytes(text));
}

public sealed record Packet(byte Type, uint Seq, int SampleRate, int Channels, int Bits, byte[] Payload)
{
    public string Text => Encoding.UTF8.GetString(Payload);
}
