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

    /// <summary>
    /// Bits 0–11 match 0.4.2. Bits 12–14 are optional status added in 0.5.0 (0 on older senders):
    /// noise cancellation, AGC, capture path is AAudio. Receivers that do not know them ignore them.
    /// </summary>
    public static ushort MakeFlags(int codec = CodecPcm16, int channels = 1, int sampleRate = 48000,
        bool noiseCancellation = false, bool agc = false, bool aaudio = false)
    {
        if (channels < 1 || channels > 16) throw new ArgumentOutOfRangeException(nameof(channels));
        int v = (codec & 0x0F) | ((channels - 1) << 4) | (RateToCode(sampleRate) << 8);
        if (noiseCancellation) v |= 1 << 12;
        if (agc) v |= 1 << 13;
        if (aaudio) v |= 1 << 14;
        return (ushort)v;
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
    public bool NoiseCancellation => (Flags & 0x1000) != 0;
    public bool Agc => (Flags & 0x2000) != 0;
    public bool AAudio => (Flags & 0x4000) != 0;
}

/// <summary>
/// Optional 4-byte payloads on PING (PC → phone) and PONG (phone → PC), added in 0.5.0.
/// Version stays 1. A 0.4.2 peer ignores the payload; a missing or non-magic payload is "no status".
/// PING: magic, flags (bit0 NC, bit1 AGC), gain 0–24, change counter (0 = do not override the phone).
/// PONG: magic, flags (bit0 NC, bit1 AGC, bit2 AAudio, bit3 exclusive, bit4 low-latency), gain 0–24, engine (1 AAudio, 2 AudioRecord).
/// </summary>
public static class LinkControl
{
    public const byte Magic = 0xC5;
    public const int Size = 4;

    public static void WritePing(Span<byte> dst, bool noiseCancellation, bool agc, int gainDb, byte changeSeq)
    {
        if (dst.Length < Size) throw new ArgumentException("buffer too small", nameof(dst));
        dst[0] = Magic;
        dst[1] = (byte)((noiseCancellation ? 1 : 0) | (agc ? 2 : 0));
        dst[2] = (byte)Math.Clamp(gainDb, 0, VoiceChain.MaxGainDb);
        dst[3] = changeSeq;
    }

    public static bool TryReadPing(ReadOnlySpan<byte> src, out bool noiseCancellation, out bool agc, out int gainDb, out byte changeSeq)
    {
        noiseCancellation = false; agc = false; gainDb = 0; changeSeq = 0;
        if (src.Length < Size || src[0] != Magic) return false;
        noiseCancellation = (src[1] & 1) != 0;
        agc = (src[1] & 2) != 0;
        gainDb = Math.Clamp((int)src[2], 0, VoiceChain.MaxGainDb);
        changeSeq = src[3];
        return true;
    }

    /// <summary>Writes the status block and returns how many bytes were used (4, plus the device name).</summary>
    public static int WritePong(Span<byte> dst, bool noiseCancellation, bool agc, bool aaudio, bool exclusive, bool lowLatency, int gainDb, string? deviceName = null)
    {
        if (dst.Length < Size) throw new ArgumentException("buffer too small", nameof(dst));
        dst[0] = Magic;
        int f = (noiseCancellation ? 1 : 0) | (agc ? 2 : 0) | (aaudio ? 4 : 0) | (exclusive ? 8 : 0) | (lowLatency ? 16 : 0);
        dst[1] = (byte)f;
        dst[2] = (byte)Math.Clamp(gainDb, 0, VoiceChain.MaxGainDb);
        dst[3] = (byte)(aaudio ? 1 : 2);
        int n = 0;
        if (!string.IsNullOrEmpty(deviceName))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(deviceName);
            n = Math.Min(bytes.Length, Math.Max(0, dst.Length - Size));
            bytes.AsSpan(0, n).CopyTo(dst[Size..]);
        }
        return Size + n;
    }

    public static bool TryReadPong(ReadOnlySpan<byte> src, out PhoneLink phone)
    {
        phone = default;
        if (src.Length < Size || src[0] != Magic) return false;
        int f = src[1];
        string name = "";
        if (src.Length > Size)
        {
            int n = src.Length - Size;
            while (n > 0 && src[Size + n - 1] == 0) n--;
            name = System.Text.Encoding.UTF8.GetString(src.Slice(Size, n)).Trim();
            if (name.Length > 40) name = name[..40];
        }
        phone = new PhoneLink(true, (f & 1) != 0, (f & 2) != 0, (f & 4) != 0 || src[3] == 1,
            (f & 8) != 0, (f & 16) != 0, Math.Clamp((int)src[2], 0, VoiceChain.MaxGainDb), name);
        return true;
    }
}

/// <summary>What the phone last reported about its capture path and voice chain. <see cref="Known"/> is false for a 0.4.2 peer.</summary>
public readonly record struct PhoneLink(bool Known, bool NoiseCancellation, bool Agc, bool AAudio, bool Exclusive, bool LowLatency, int GainDb, string Name = "")
{
    public string Mode =>
        !Known ? "?" :
        !AAudio ? "AudioRecord" :
        Exclusive ? "AAudio-excl" :
        LowLatency ? "AAudio-ll" : "AAudio";

    public string NoiseText => !Known ? "?" : NoiseCancellation ? "on" : "off";
    public string AgcText => !Known ? "?" : Agc ? "on" : "off";
    public string GainText => !Known || GainDb < 0 ? "?" : $"+{GainDb}dB";
}
