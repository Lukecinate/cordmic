namespace CordMic.Core.Transport;

public sealed class TransportHealth
{
    public long FramesIn;
    public long FramesOut;
    public long AudioDroppedOld;
    public long ControlDropped;
    public long CrcErrors;
    public long HeaderErrors;
    public long SequenceGaps;
    public long WriterTimeouts;
    public long ReaderTimeouts;
    public long BytesIn;
    public long BytesOut;
    public int LastRttMs = -1;
    public bool HelloOk;
    public bool LinkLost;
    public string? LinkLostReason;

    public TransportHealth Snapshot() => (TransportHealth)MemberwiseClone();
}
