namespace CordMic.Core;

/// <summary>
/// 1.1.1 optional trailing block: signed mic gain in 0.1 dB units.
/// Magic 0xC9, version 1, int16 little-endian tenths (−240..+240).
/// Older peers ignore it when it is placed after the sync block on a PING, or (on a PONG)
/// only when the phone has seen this block on a PING — so a 1.1.0 PC still parses the
/// classic PONG layout. The legacy gain byte stays 0–24 (negative sent as 0).
/// </summary>
public static class GainExt
{
    public const byte Magic = 0xC9;
    public const byte Version = 1;
    public const int Size = 4;

    public static int Write(Span<byte> dst, double gainDb)
    {
        if (dst.Length < Size) return 0;
        dst[0] = Magic;
        dst[1] = Version;
        short tenths = (short)Math.Clamp((int)Math.Round(VoiceChain.ClampGain(gainDb) * 10, MidpointRounding.ToEven), -240, 240);
        dst[2] = (byte)(tenths & 0xFF);
        dst[3] = (byte)((tenths >> 8) & 0xFF);
        return Size;
    }

    public static bool TryRead(ReadOnlySpan<byte> src, out double gainDb)
    {
        gainDb = 0;
        if (src.Length < Size || src[0] != Magic || src[1] != Version) return false;
        short tenths = (short)(src[2] | (src[3] << 8));
        if (tenths < -240 || tenths > 240) return false;
        gainDb = VoiceChain.ClampGain(tenths / 10.0);
        return true;
    }
}
