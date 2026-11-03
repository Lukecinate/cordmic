using System.Buffers.Binary;

namespace CordMic.Core;

/// <summary>
/// CordMic wire protocol. Byte-for-byte identical to windows-receiver/cordmic_receiver/protocol.py
/// and android/.../Protocol.kt. 20-byte little-endian header:
/// 0 magic "UM" | 2 version(1) | 3 type | 4 seq u32 | 8 ts_us u64 | 16 frames u16 | 18 flags u16
/// flags: bits 0-3 codec (0 = PCM s16le), bits 4-7 channels-1, bits 8-11 rate code (0=48k,1=44.1k,2=16k)
/// </summary>
public static class Protocol
{
    public const int HeaderSize = 20;
    public const byte Version = 1;
    public const int DefaultPort = 50005;
    public const byte TAudio = 1, TDiscover = 2, THello = 3, TPing = 4, TPong = 5, TBye = 6;
    public const int CodecPcm16 = 0;

    public static int RateFromCode(int code) => code switch { 0 => 48000, 1 => 44100, 2 => 16000, _ => 48000 };

    public static int RateToCode(int rate) => rate switch
    {
        48000 => 0, 44100 => 1, 16000 => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(rate)),
    };

    public static ushort MakeFlags(int codec = CodecPcm16, int channels = 1, int sampleRate = 48000)
    {
        if (channels < 1 || channels > 16) throw new ArgumentOutOfRangeException(nameof(channels));
        return (ushort)((codec & 0x0F) | ((channels - 1) << 4) | (RateToCode(sampleRate) << 8));
    }

    public static void WriteHeader(Span<byte> dst, byte type, uint seq, ulong tsUs, ushort frames = 0, ushort flags = 0)
    {
        if (dst.Length < HeaderSize) throw new ArgumentException("buffer too small", nameof(dst));
        dst[0] = (byte)'U'; dst[1] = (byte)'M'; dst[2] = Version; dst[3] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(dst[4..], seq);
        BinaryPrimitives.WriteUInt64LittleEndian(dst[8..], tsUs);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[16..], frames);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[18..], flags);
    }

    public static byte[] Encode(byte type, uint seq, ulong tsUs, ReadOnlySpan<byte> payload = default,
                                ushort frames = 0, ushort flags = 0)
    {
        var b = new byte[HeaderSize + payload.Length];
        WriteHeader(b, type, seq, tsUs, frames, flags);
        payload.CopyTo(b.AsSpan(HeaderSize));
        return b;
    }

    /// <summary>Parses a datagram. Returns false (with a reason) for anything Python would reject.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, out PacketHeader h, out string? error)
    {
        h = default; error = null;
        if (data.Length < HeaderSize) { error = "short packet"; return false; }
        if (data[0] != (byte)'U' || data[1] != (byte)'M') { error = "bad magic"; return false; }
        if (data[2] != Version) { error = $"unsupported version {data[2]}"; return false; }
        h = new PacketHeader(
            data[3],
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
            BinaryPrimitives.ReadUInt64LittleEndian(data[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[16..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[18..]),
            data.Length - HeaderSize);
        if (h.Type == TAudio && h.Codec == CodecPcm16 && h.PayloadLength != h.Frames * h.Channels * 2)
        {
            error = "payload length does not match frame count"; return false;
        }
        return true;
    }

    /// <summary>Signed distance a - b on the 32-bit wrapping sequence space (same as Python seq_diff).</summary>
    public static long SeqDiff(uint a, uint b)
    {
        long d = (long)(uint)(a - b);
        return d >= (1L << 31) ? d - (1L << 32) : d;
    }
}

public readonly record struct PacketHeader(byte Type, uint Seq, ulong TsUs, ushort Frames, ushort Flags, int PayloadLength)
{
    public int Codec => Flags & 0x0F;
    public int Channels => ((Flags >> 4) & 0x0F) + 1;
    public int SampleRate => Protocol.RateFromCode((Flags >> 8) & 0x0F);
}
