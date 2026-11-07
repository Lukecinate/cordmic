namespace CordMic.Core;

/// <summary>WAVEFORMATEX / WAVEFORMATEXTENSIBLE blobs stored in PKEY_AudioEngine_DeviceFormat.</summary>
public static class WaveFormatBlob
{
    public const int RateHz = 48000;
    public static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    public static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    public static byte[] PcmStereo48k(int bitsPerSample)
    {
        if (bitsPerSample is not (16 or 24))
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample), "only 16- or 24-bit PCM is allowed");
        int block = 2 * (bitsPerSample / 8);
        var buf = new byte[40];
        WriteU16(buf, 0, 0xFFFE);
        WriteU16(buf, 2, 2);
        WriteU32(buf, 4, RateHz);
        WriteU32(buf, 8, (uint)(RateHz * block));
        WriteU16(buf, 12, (ushort)block);
        WriteU16(buf, 14, (ushort)bitsPerSample);
        WriteU16(buf, 16, 22);
        WriteU16(buf, 18, (ushort)bitsPerSample);
        WriteU32(buf, 20, 3); // SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT
        SubtypePcm.ToByteArray().CopyTo(buf, 24);
        return buf;
    }

    public static bool TryParse(ReadOnlySpan<byte> blob, out int channels, out int rate, out int bits, out bool ieeeFloat)
    {
        channels = 0; rate = 0; bits = 0; ieeeFloat = false;
        if (blob.Length < 16) return false;
        int tag = ReadU16(blob, 0);
        channels = ReadU16(blob, 2);
        rate = (int)ReadU32(blob, 4);
        bits = ReadU16(blob, 14);
        if (tag == 1) { ieeeFloat = false; return channels > 0 && rate > 0 && bits > 0; }
        if (tag == 3) { ieeeFloat = true; return channels > 0 && rate > 0 && bits > 0; }
        if (tag == 0xFFFE)
        {
            if (blob.Length < 40) return false;
            int valid = ReadU16(blob, 18);
            if (valid > 0) bits = valid;
            var sub = blob.Slice(24, 16).ToArray();
            var guid = new Guid(sub);
            ieeeFloat = guid == SubtypeIeeeFloat;
            if (guid != SubtypePcm && guid != SubtypeIeeeFloat) return false;
            return channels > 0 && rate > 0 && bits > 0;
        }
        return false;
    }

    /// <summary>16-bit or 24-bit PCM, 48 kHz, 2 channels. Float and other rates/channel counts are not optimal.</summary>
    public static bool IsOptimalStereo48k(ReadOnlySpan<byte> blob) =>
        TryParse(blob, out int ch, out int rate, out int bits, out bool ieeeFloat)
        && !ieeeFloat && rate == RateHz && ch == 2 && bits is 16 or 24;

    public static string Describe(ReadOnlySpan<byte> blob)
    {
        if (!TryParse(blob, out int ch, out int rate, out int bits, out bool ieeeFloat))
            return blob.Length == 0 ? "(empty)" : $"unparsed {blob.Length} bytes";
        return $"{bits}-bit {(ieeeFloat ? "float" : "PCM")} {rate} Hz {ch}ch";
    }

    static void WriteU16(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
    static void WriteU32(byte[] b, int o, uint v)
    {
        b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
    }
    static int ReadU16(ReadOnlySpan<byte> b, int o) => b[o] | (b[o + 1] << 8);
    static uint ReadU32(ReadOnlySpan<byte> b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
}
