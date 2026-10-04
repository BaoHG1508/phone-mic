namespace PhoneMic.Core;

/// <summary>Counters shared between the network threads, the audio thread and the UI (read racily for display).</summary>
public sealed class Stats
{
    public long Packets;
    public long Bytes;
    public long Lost;
    public long Late;
    public long Underruns;
    public long Trims;
    public volatile string LastSender = "";
    public long LastPacketTicks; // Environment.TickCount64 of the last audio packet

    public void Reset()
    {
        Packets = Bytes = Lost = Late = Underruns = Trims = 0;
        LastSender = "";
        LastPacketTicks = 0;
    }
}
