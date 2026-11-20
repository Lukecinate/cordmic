namespace CordMic.Core.Transport;

public enum UsbFrameType : byte
{
    Audio = 1,
    Control = 2,
    Bye = 3,
    Keepalive = 4,
}

public readonly struct UsbFrame
{
    public UsbFrame(UsbFrameType type, uint seq, byte[] payload)
    {
        Type = type;
        Seq = seq;
        Payload = payload ?? Array.Empty<byte>();
    }

    public UsbFrameType Type { get; }
    public uint Seq { get; }
    public byte[] Payload { get; }

    public bool IsAudio => Type == UsbFrameType.Audio;
    public bool IsControl => Type == UsbFrameType.Control;
}

public enum FrameDecodeStatus
{
    Valid = 0,
    NeedMore = 1,
    BadHeader = 2,
    BadCrc = 3,
    UnsupportedVersion = 4,
    UnknownType = 5, // valid CRC and length; payload skipped, still a syncable frame
}

public readonly struct FrameDecodeResult
{
    public FrameDecodeResult(FrameDecodeStatus status, UsbFrame? frame, int consumed, byte version)
    {
        Status = status;
        Frame = frame;
        Consumed = consumed;
        Version = version;
    }

    public FrameDecodeStatus Status { get; }
    public UsbFrame? Frame { get; }
    public int Consumed { get; }
    public byte Version { get; }
}
