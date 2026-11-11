namespace CordMic.Core;

/// <summary>Who last wrote a synced setting. Used only to ignore our own echo.</summary>
public enum SyncOrigin : byte { Pc = 0, Phone = 1 }

/// <summary>Settings that both sides can change while connected.</summary>
public enum SyncField : byte
{
    Noise = 0,
    Clarity = 1,
    Agc = 2,
    Gain = 3,
    FrontFocus = 4,
    FrontFocusAr = 5,
}

/// <summary>Per-setting revision: a wrapping sequence and the side that last wrote it.</summary>
public readonly record struct FieldRev(byte Seq, SyncOrigin Origin)
{
    public static readonly FieldRev None = new(0, SyncOrigin.Pc);
}

/// <summary>
/// Last-write-wins sync for live settings. A higher sequence always wins; equal
/// sequences are treated as an echo or a simultaneous edit and left untouched
/// (no snap-back, no ping-pong). Sequence 0 means "unspecified" and never overrides.
/// </summary>
public static class SettingsSync
{
    public const byte Magic = 0xC6;
    public const byte Version = 1;
    public const int FieldCount = 6;
    public const int Size = 10;
    public const byte FlagFrontFocusAr = 1 << 0;
    public const byte FlagSpeechLow = 1 << 1;
    public const byte FlagPeerSync = 1 << 2;
    public const byte FlagVoiceClarity = 1 << 3;
    /// <summary>PING only (0.5.7): ask the phone for the diagnostics block. Older phones ignore the bit.</summary>
    public const byte FlagWantDiag = 1 << 4;
    /// <summary>PING only (0.6.0): the PC runs Studio noise reduction, so the phone should bypass its suppressor, platform
    /// noise suppression, gate, AGC and pause expander (user gain, voice clarity and the limiter stay). Older phones ignore it.</summary>
    public const byte FlagStudio = 1 << 5;

    public static byte NextSeq(byte local, byte remote)
    {
        int n = Math.Max(local, remote) + 1;
        return n is <= 0 or > 255 ? (byte)1 : (byte)n;
    }

    /// <summary>True when <paramref name="incoming"/> is strictly ahead of <paramref name="local"/> on the 8-bit ring.</summary>
    public static bool SeqAhead(byte incoming, byte local)
    {
        if (incoming == 0) return false;
        if (local == 0) return incoming != 0;
        return (byte)(incoming - local) is > 0 and < 128;
    }

    /// <summary>
    /// Apply a remote revision only when it is newer. Same sequence (echo or conflict)
    /// and an in-progress local edit are ignored so the UI cannot snap back.
    /// </summary>
    public static bool ShouldApply(FieldRev local, FieldRev remote, bool localBusy = false)
    {
        if (localBusy) return false;
        if (remote.Seq == 0) return false;
        return SeqAhead(remote.Seq, local.Seq);
    }

    public static void Write(Span<byte> dst, byte flags2, ReadOnlySpan<FieldRev> revs)
    {
        if (dst.Length < Size) throw new ArgumentException("buffer too small", nameof(dst));
        dst[0] = Magic;
        dst[1] = Version;
        dst[2] = flags2;
        byte origins = 0;
        for (int i = 0; i < FieldCount; i++)
        {
            var r = i < revs.Length ? revs[i] : default;
            dst[4 + i] = r.Seq;
            if (r.Origin == SyncOrigin.Phone) origins |= (byte)(1 << i);
        }
        dst[3] = origins;
    }

    public static bool TryRead(ReadOnlySpan<byte> src, out byte flags2, out FieldRev[] revs)
    {
        flags2 = 0;
        revs = Array.Empty<FieldRev>();
        if (src.Length < Size || src[0] != Magic || src[1] != Version) return false;
        flags2 = src[2];
        byte origins = src[3];
        revs = new FieldRev[FieldCount];
        for (int i = 0; i < FieldCount; i++)
            revs[i] = new FieldRev(src[4 + i], (origins & (1 << i)) != 0 ? SyncOrigin.Phone : SyncOrigin.Pc);
        return true;
    }

    /// <summary>
    /// Reads a trailing sync block from the bytes after the classic 4-byte PONG header.
    /// Returns the UTF-8 name length (separator NUL stripped) when a block is present.
    /// </summary>
    public static bool TryReadTrailing(ReadOnlySpan<byte> afterClassic, out byte flags2, out FieldRev[] revs, out int nameLength)
    {
        flags2 = 0;
        revs = Array.Empty<FieldRev>();
        nameLength = afterClassic.Length;
        if (afterClassic.Length < Size) return false;
        int start = afterClassic.Length - Size;
        if (!TryRead(afterClassic.Slice(start), out flags2, out revs)) return false;
        nameLength = start;
        if (nameLength > 0 && afterClassic[nameLength - 1] == 0) nameLength--;
        return true;
    }
}

/// <summary>Mutable per-setting clocks kept by each peer for the current session.</summary>
public sealed class SyncClock
{
    public FieldRev[] Revs { get; } = new FieldRev[SettingsSync.FieldCount];

    public FieldRev this[SyncField field] => Revs[(int)field];

    public void Bump(SyncField field, SyncOrigin origin)
    {
        int i = (int)field;
        var cur = Revs[i];
        Revs[i] = new FieldRev(SettingsSync.NextSeq(cur.Seq, cur.Seq), origin);
    }

    public void ApplyRemote(SyncField field, FieldRev remote) => Revs[(int)field] = remote;

    /// <summary>PC session start: sequence 1 so the first PING applies saved desktop settings.</summary>
    public void SeedPcDefaults()
    {
        for (int i = 0; i < Revs.Length; i++)
            Revs[i] = new FieldRev(1, SyncOrigin.Pc);
    }
}
