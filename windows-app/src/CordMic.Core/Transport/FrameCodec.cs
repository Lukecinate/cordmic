using System.Buffers.Binary;

namespace CordMic.Core.Transport;

/// <summary>
/// USB bulk framing (plan section 7). Wire-v1 UDP packets travel as the payload of AUDIO/CONTROL frames.
/// </summary>
public static class FrameCodec
{
    public const int HeaderSize = 14;
    public const int MaxPayload = 4096;
    public const byte Version = 1;
    public const byte Magic0 = (byte)'C';
    public const byte Magic1 = (byte)'M';

    public static int MaxFrameSize => HeaderSize + MaxPayload;

    public static bool TryWrite(Span<byte> dest, UsbFrameType type, uint seq, ReadOnlySpan<byte> payload, out int written)
    {
        written = 0;
        if (payload.Length > MaxPayload) return false;
        int n = HeaderSize + payload.Length;
        if (dest.Length < n) return false;
        dest[0] = Magic0;
        dest[1] = Magic1;
        dest[2] = Version;
        dest[3] = (byte)type;
        BinaryPrimitives.WriteUInt16LittleEndian(dest[4..], (ushort)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[6..], seq);
        uint crc = Crc32C.Compute(dest[..10], payload);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[10..], crc);
        payload.CopyTo(dest[HeaderSize..]);
        written = n;
        return true;
    }

    public static byte[] Encode(UsbFrameType type, uint seq, ReadOnlySpan<byte> payload)
    {
        var buf = new byte[HeaderSize + payload.Length];
        if (!TryWrite(buf, type, seq, payload, out _))
            throw new ArgumentOutOfRangeException(nameof(payload));
        return buf;
    }

    /// <summary>
    /// Inspects <paramref name="src"/> starting at 0. Never throws. On a bad header or CRC,
    /// <see cref="FrameDecodeResult.Consumed"/> is 1 so the caller can skip a byte and resync.
    /// Length is validated before any payload slice longer than <see cref="MaxPayload"/> is accepted.
    /// </summary>
    public static FrameDecodeResult TryDecode(ReadOnlySpan<byte> src)
    {
        if (src.Length < 2) return new(FrameDecodeStatus.NeedMore, null, 0, 0);
        if (src[0] != Magic0 || src[1] != Magic1)
            return new(FrameDecodeStatus.BadHeader, null, 1, 0);
        if (src.Length < HeaderSize) return new(FrameDecodeStatus.NeedMore, null, 0, 0);
        byte ver = src[2];
        byte typ = src[3];
        ushort len = BinaryPrimitives.ReadUInt16LittleEndian(src[4..]);
        if (len > MaxPayload)
            return new(FrameDecodeStatus.BadHeader, null, 1, ver);
        int total = HeaderSize + len;
        if (src.Length < total) return new(FrameDecodeStatus.NeedMore, null, 0, ver);
        uint seq = BinaryPrimitives.ReadUInt32LittleEndian(src[6..]);
        uint crc = BinaryPrimitives.ReadUInt32LittleEndian(src[10..]);
        uint got = Crc32C.Compute(src[..10], src.Slice(HeaderSize, len));
        if (got != crc)
            return new(FrameDecodeStatus.BadCrc, null, 1, ver);
        if (ver != Version)
            return new(FrameDecodeStatus.UnsupportedVersion, new UsbFrame((UsbFrameType)typ, seq, src.Slice(HeaderSize, len).ToArray()), total, ver);
        if (typ is < 1 or > 4)
            return new(FrameDecodeStatus.UnknownType, new UsbFrame((UsbFrameType)typ, seq, src.Slice(HeaderSize, len).ToArray()), total, ver);
        return new(FrameDecodeStatus.Valid, new UsbFrame((UsbFrameType)typ, seq, src.Slice(HeaderSize, len).ToArray()), total, ver);
    }
}
