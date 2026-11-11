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
        => WritePing(dst, noiseCancellation, agc, gainDb, changeSeq, extended: false, voiceClarity: false,
            mode: noiseCancellation ? NoiseMode.Light : NoiseMode.Off);

    /// <summary>
    /// PING payload. Bit 0 = any NC, bit 1 = AGC (read by 0.5.0–0.5.2).
    /// When <paramref name="extended"/> is set (bit 5), bit 2 is voice clarity, bits 3–4 are
    /// <see cref="NoiseMode"/>, and bit 6 is front focus. Older phones ignore the extra bits.
    /// </summary>
    public static void WritePing(Span<byte> dst, bool noiseCancellation, bool agc, int gainDb, byte changeSeq,
        bool extended, bool voiceClarity, NoiseMode mode, bool frontFocus = false, bool frontFocusAudioRecord = false)
    {
        if (dst.Length < Size) throw new ArgumentException("buffer too small", nameof(dst));
        int f = (noiseCancellation ? 1 : 0) | (agc ? 2 : 0);
        if (extended)
            f |= 32 | (voiceClarity ? 4 : 0) | (((int)mode & 3) << 3) | (frontFocus ? 64 : 0)
                | (frontFocusAudioRecord ? 128 : 0);
        dst[0] = Magic;
        dst[1] = (byte)f;
        dst[2] = (byte)Math.Clamp(gainDb, 0, VoiceChain.MaxGainDb);
        dst[3] = changeSeq;
    }

    public static bool TryReadPing(ReadOnlySpan<byte> src, out bool noiseCancellation, out bool agc, out int gainDb, out byte changeSeq)
        => TryReadPing(src, out noiseCancellation, out agc, out gainDb, out changeSeq, out _, out _, out _, out _);

    public static bool TryReadPing(ReadOnlySpan<byte> src, out bool noiseCancellation, out bool agc, out int gainDb,
        out byte changeSeq, out bool extended, out bool voiceClarity, out NoiseMode mode)
        => TryReadPing(src, out noiseCancellation, out agc, out gainDb, out changeSeq, out extended, out voiceClarity, out mode, out _);

    public static bool TryReadPing(ReadOnlySpan<byte> src, out bool noiseCancellation, out bool agc, out int gainDb,
        out byte changeSeq, out bool extended, out bool voiceClarity, out NoiseMode mode, out bool frontFocus)
        => TryReadPing(src, out noiseCancellation, out agc, out gainDb, out changeSeq, out extended, out voiceClarity,
            out mode, out frontFocus, out _);

    public static bool TryReadPing(ReadOnlySpan<byte> src, out bool noiseCancellation, out bool agc, out int gainDb,
        out byte changeSeq, out bool extended, out bool voiceClarity, out NoiseMode mode, out bool frontFocus,
        out bool frontFocusAudioRecord)
    {
        noiseCancellation = false; agc = false; gainDb = 0; changeSeq = 0;
        extended = false; voiceClarity = false; mode = NoiseMode.Off; frontFocus = false; frontFocusAudioRecord = false;
        if (src.Length < Size || src[0] != Magic) return false;
        int f = src[1];
        noiseCancellation = (f & 1) != 0;
        agc = (f & 2) != 0;
        gainDb = Math.Clamp((int)src[2], 0, VoiceChain.MaxGainDb);
        changeSeq = src[3];
        extended = (f & 32) != 0;
        voiceClarity = (f & 4) != 0;
        frontFocus = extended && (f & 64) != 0;
        frontFocusAudioRecord = extended && (f & 128) != 0;
        int stored = (f >> 3) & 3;
        if (extended)
            mode = stored == 2 ? NoiseMode.Strong : stored == 1 ? NoiseMode.Light : NoiseMode.Off;
        else
            mode = noiseCancellation ? NoiseMode.Light : NoiseMode.Off;
        return true;
    }

    /// <summary>Writes the status block and returns how many bytes were used (4, plus the device name).</summary>
    /// <summary>
    /// Byte 3: bits 0–1 engine (1 AAudio, 2 AudioRecord), bits 2–3 dir (0 off, 1 front, 2 unsupported),
    /// bits 4–7 mic count 0–15. Older readers still see AAudio from flags bit 2.
    /// Flags bit 5 is front-focus requested/on.
    /// </summary>
    public static int WritePong(Span<byte> dst, bool noiseCancellation, bool agc, bool aaudio, bool exclusive, bool lowLatency, int gainDb, string? deviceName = null, NoiseMode mode = NoiseMode.Off, bool frontFocus = false, int dirCode = 0, int micCount = 0, bool frontFocusAudioRecord = false, bool speechLow = false, FieldRev[]? sync = null, bool voiceClarity = true, PhoneDiag? diag = null)
    {
        if (dst.Length < Size) throw new ArgumentException("buffer too small", nameof(dst));
        dst[0] = Magic;
        int f = (noiseCancellation ? 1 : 0) | (agc ? 2 : 0) | (aaudio ? 4 : 0) | (exclusive ? 8 : 0) | (lowLatency ? 16 : 0)
            | (frontFocus ? 32 : 0) | (((int)mode & 3) << 6);
        dst[1] = (byte)f;
        dst[2] = (byte)Math.Clamp(gainDb, 0, VoiceChain.MaxGainDb);
        int engine = aaudio ? 1 : 2;
        dst[3] = (byte)((engine & 3) | ((dirCode & 3) << 2) | ((Math.Clamp(micCount, 0, 15) & 15) << 4));
        int n = 0;
        if (!string.IsNullOrEmpty(deviceName))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(deviceName);
            int room = dst.Length - Size - (sync != null ? 1 + SettingsSync.Size + (diag != null ? PhoneDiag.Size : 0) : 0);
            n = Math.Min(bytes.Length, Math.Max(0, room));
            bytes.AsSpan(0, n).CopyTo(dst[Size..]);
        }
        int used = Size + n;
        if (sync != null && dst.Length - used >= 1 + SettingsSync.Size)
        {
            dst[used] = 0;
            used++;
            if (diag is { } d && dst.Length - used >= PhoneDiag.Size + SettingsSync.Size)
            {
                d.Write(dst.Slice(used, PhoneDiag.Size));
                used += PhoneDiag.Size;
            }
            byte flags2 = SettingsSync.FlagPeerSync;
            if (frontFocusAudioRecord) flags2 |= SettingsSync.FlagFrontFocusAr;
            if (speechLow) flags2 |= SettingsSync.FlagSpeechLow;
            if (voiceClarity) flags2 |= SettingsSync.FlagVoiceClarity;
            SettingsSync.Write(dst.Slice(used, SettingsSync.Size), flags2, sync);
            used += SettingsSync.Size;
        }
        return used;
    }

    public static bool TryReadPong(ReadOnlySpan<byte> src, out PhoneLink phone)
    {
        phone = default;
        if (src.Length < Size || src[0] != Magic) return false;
        int f = src[1];
        string name = "";
        bool syncCapable = false, frontAr = false, speechLow = false, voiceClarity = true;
        FieldRev[]? revs = null;
        PhoneDiag? diag = null;
        if (src.Length > Size)
        {
            var after = src[Size..];
            int n = after.Length;
            if (SettingsSync.TryReadTrailing(after, out var flags2, out var got, out int nameLen))
            {
                syncCapable = true;
                revs = got;
                frontAr = (flags2 & SettingsSync.FlagFrontFocusAr) != 0;
                speechLow = (flags2 & SettingsSync.FlagSpeechLow) != 0;
                voiceClarity = (flags2 & SettingsSync.FlagVoiceClarity) != 0;
                n = nameLen;
                // 0.5.7: name, NUL, diagnostics, sync. A peer that did not send the block leaves the legacy layout.
                if (n >= PhoneDiag.Size && PhoneDiag.TryRead(after.Slice(n - PhoneDiag.Size, PhoneDiag.Size), out var d)
                    && (n == PhoneDiag.Size || after[n - PhoneDiag.Size - 1] == 0))
                {
                    diag = d;
                    n -= PhoneDiag.Size;
                    if (n > 0) n--;
                }
            }
            else
            {
                while (n > 0 && after[n - 1] == 0) n--;
            }
            if (n > 0) name = System.Text.Encoding.UTF8.GetString(after[..n]).Trim();
            if (name.Length > 40) name = name[..40];
        }
        int stored = (f >> 6) & 3;
        var ncMode = stored == 2 ? NoiseMode.Strong : stored == 1 ? NoiseMode.Light : ((f & 1) != 0 ? NoiseMode.Light : NoiseMode.Off);
        int packed = src[3];
        int dirCode = (packed >> 2) & 3;
        int mics = (packed >> 4) & 15;
        phone = new PhoneLink(true, (f & 1) != 0, (f & 2) != 0, (f & 4) != 0 || (packed & 3) == 1,
            (f & 8) != 0, (f & 16) != 0, Math.Clamp((int)src[2], 0, VoiceChain.MaxGainDb), name, ncMode, stored,
            (f & 32) != 0, dirCode, mics, frontAr, speechLow, syncCapable, revs, voiceClarity, diag);
        return true;
    }
}

/// <summary>What the phone last reported about its capture path and voice chain. <see cref="Known"/> is false for a 0.4.2 peer.</summary>
public readonly record struct PhoneLink(bool Known, bool NoiseCancellation, bool Agc, bool AAudio, bool Exclusive, bool LowLatency, int GainDb, string Name = "", NoiseMode Noise = NoiseMode.Off, int NoiseBits = 0, bool FrontFocus = false, int DirCode = 0, int MicCount = 0, bool FrontFocusAudioRecord = false, bool SpeechLow = false, bool SyncCapable = false, FieldRev[]? SyncRevs = null, bool VoiceClarity = true, PhoneDiag? Diag = null)
{
    public string Mode =>
        !Known ? "?" :
        !AAudio ? "AudioRecord" :
        Exclusive ? "AAudio-excl" :
        LowLatency ? "AAudio-ll" : "AAudio";

    public string NoiseText =>
        !Known ? "?" :
        NoiseBits == 2 ? "strong" :
        NoiseBits == 1 ? "light" :
        NoiseCancellation ? "on" : "off";
    public string AgcText => !Known ? "?" : Agc ? "on" : "off";
    public string GainText => !Known || GainDb < 0 ? "?" : $"+{GainDb}dB";
    public string DirText =>
        !Known ? "?" :
        DirCode == 1 ? "front" :
        DirCode == 2 ? "unsupported" :
        FrontFocus ? "on" : "off";
    public string MicText => !Known || MicCount <= 0 ? "-" : MicCount.ToString();
    /// <summary>Phone background level before its user gain; "~" marks the initial guess before a pause was measured.</summary>
    public string FloorText => Diag is { } d ? $"{(d.FloorKnown ? "" : "~")}{d.FloorDbfs}dBFS" : "-";
    public string GateText => Diag is { } d ? (d.Gated ? "closed" : "open") : "-";
    public string AgcGainText => Diag is { } d ? (d.Agc ? $"{d.AgcGainDb:+0.0;-0.0;0.0}dB" : "off") : "-";
    public string ExpanderText => Diag is { } d ? $"{d.ExpanderDb:0.0}dB" : "-";
    /// <summary>0.6.0: Android reports that another app has silenced CordMic's capture.</summary>
    public bool Silenced => Diag is { Silenced: true };
    /// <summary>0.6.0: the phone confirmed it bypassed its own suppression for PC Studio NR.</summary>
    public bool StudioBypass => Diag is { StudioBypass: true };
    /// <summary>0.6.0: the phone suspects hidden device noise suppression on its capture (logged only).</summary>
    public bool NsSuspected => Diag is { NsSuspected: true };
    public string DetectedSummary => DeviceTune.FormatDetected(Name, AAudio, Exclusive, LowLatency, MicCount, DirCode, FrontFocus);
}

/// <summary>
/// 0.5.7 phone diagnostics carried in a PONG only after the PC set <see cref="SettingsSync.FlagWantDiag"/>.
/// Layout: magic 0xC7, floor dBFS (int8), AGC gain in 0.5 dB (int8), flags (bit0 gated, bit1 AGC on,
/// bit2 floor measured), expander gain in 0.5 dB (int8), check byte = xor(bytes 0–4) ^ 0x5A.
/// 0.6.0 adds flags bit3 (Android reports the capture as silenced by another app) and bit4 (the phone has bypassed
/// its noise suppression, gate, AGC and expander because the PC asked for Studio) and bit5 (room tone fell sharply
/// after start with no speech, so the device may apply hidden noise suppression; logged only). 0.5.7 readers ignore them.
/// It sits between the name NUL and the sync block, so a reader without it sees an unchanged name.
/// </summary>
public readonly record struct PhoneDiag(int FloorDbfs, bool Gated, bool Agc, double AgcGainDb, double ExpanderDb, bool FloorKnown, bool Silenced = false, bool StudioBypass = false, bool NsSuspected = false)
{
    public const byte Magic = 0xC7;
    public const int Size = 6;

    public void Write(Span<byte> dst)
    {
        dst[0] = Magic;
        dst[1] = (byte)(sbyte)Math.Clamp(FloorDbfs, -127, 0);
        dst[2] = (byte)(sbyte)Math.Clamp((int)Math.Round(AgcGainDb * 2), -127, 127);
        dst[3] = (byte)((Gated ? 1 : 0) | (Agc ? 2 : 0) | (FloorKnown ? 4 : 0) | (Silenced ? 8 : 0) | (StudioBypass ? 16 : 0) | (NsSuspected ? 32 : 0));
        dst[4] = (byte)(sbyte)Math.Clamp((int)Math.Round(ExpanderDb * 2), -127, 0);
        dst[5] = Check(dst);
    }

    static byte Check(ReadOnlySpan<byte> b) => (byte)(b[0] ^ b[1] ^ b[2] ^ b[3] ^ b[4] ^ 0x5A);

    public static bool TryRead(ReadOnlySpan<byte> src, out PhoneDiag diag)
    {
        diag = default;
        if (src.Length < Size || src[0] != Magic || src[5] != Check(src)) return false;
        int f = src[3];
        diag = new PhoneDiag((sbyte)src[1], (f & 1) != 0, (f & 2) != 0, (sbyte)src[2] / 2.0, (sbyte)src[4] / 2.0, (f & 4) != 0, (f & 8) != 0, (f & 16) != 0, (f & 32) != 0);
        return true;
    }
}
