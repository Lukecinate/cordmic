using System.Buffers.Binary;
using System.Text;

namespace CordMic.Core.Transport;

/// <summary>CONTROL payload for the first HELLO each way. Extra trailing bytes are ignored.</summary>
public readonly struct HelloPayload
{
    public const ushort Magic = 0x4C48; // "HL" little-endian on the wire as 48 4C
    public const int MinSize = 12;

    public HelloPayload(byte frameVersion, ushort maxPayload, uint capabilities, string appVersion)
    {
        FrameVersion = frameVersion;
        MaxPayload = maxPayload;
        Capabilities = capabilities;
        AppVersion = appVersion ?? "";
    }

    public byte FrameVersion { get; }
    public ushort MaxPayload { get; }
    public uint Capabilities { get; }
    public string AppVersion { get; }

    public byte[] Encode()
    {
        byte[] ver = Encoding.UTF8.GetBytes(AppVersion);
        if (ver.Length > 64) ver = ver.AsSpan(0, 64).ToArray();
        var dst = new byte[MinSize + ver.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(dst, Magic);
        dst[2] = FrameVersion;
        dst[3] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(dst.AsSpan(4), MaxPayload);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.AsSpan(6), Capabilities);
        BinaryPrimitives.WriteUInt16LittleEndian(dst.AsSpan(10), (ushort)ver.Length);
        ver.CopyTo(dst.AsSpan(MinSize));
        return dst;
    }

    public static bool TryParse(ReadOnlySpan<byte> src, out HelloPayload hello)
    {
        hello = default;
        if (src.Length < MinSize) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(src) != Magic) return false;
        byte fv = src[2];
        ushort max = BinaryPrimitives.ReadUInt16LittleEndian(src[4..]);
        uint caps = BinaryPrimitives.ReadUInt32LittleEndian(src[6..]);
        ushort n = BinaryPrimitives.ReadUInt16LittleEndian(src[10..]);
        if (n > 64) return false;
        string app = "";
        if (n > 0 && src.Length >= MinSize + n)
            app = Encoding.UTF8.GetString(src.Slice(MinSize, n));
        hello = new HelloPayload(fv, max, caps, app);
        return true;
    }
}
