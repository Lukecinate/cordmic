namespace CordMic.Core;

/// <summary>
/// 0.8.0 M0: optional trailing block on a PONG, sent only when the PC set
/// <see cref="SettingsSync.FlagWantPhoneKey"/>. 16 random bytes, not a hardware serial.
/// Layout: magic 0xC8, version 1, 16-byte id. A 0.7.x PC never asks for it, so older readers never see it.
/// </summary>
public static class PhoneIdBlock
{
    public const byte Magic = 0xC8;
    public const byte Version = 1;
    public const int IdLength = 16;
    public const int Size = 2 + IdLength;

    public static int Write(Span<byte> dst, ReadOnlySpan<byte> id)
    {
        if (dst.Length < Size) return 0;
        if (id.Length != IdLength) return 0;
        bool any = false;
        foreach (byte b in id) if (b != 0) { any = true; break; }
        if (!any) return 0;
        dst[0] = Magic;
        dst[1] = Version;
        id.CopyTo(dst[2..]);
        return Size;
    }

    public static bool TryRead(ReadOnlySpan<byte> src, out byte[] id)
    {
        id = Array.Empty<byte>();
        if (src.Length < Size || src[0] != Magic || src[1] != Version) return false;
        id = src.Slice(2, IdLength).ToArray();
        // All-zero is treated as missing (a phone that could not allocate an id).
        bool any = false;
        foreach (byte b in id) if (b != 0) { any = true; break; }
        if (!any) { id = Array.Empty<byte>(); return false; }
        return true;
    }
}
