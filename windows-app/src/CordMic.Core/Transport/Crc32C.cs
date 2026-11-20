namespace CordMic.Core.Transport;

/// <summary>CRC-32C (Castagnoli, reflected polynomial 0x82F63B78). Used by the USB frame header.</summary>
public static class Crc32C
{
    private static readonly uint[] Table = Build();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    public static uint Compute(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte x in a) crc = Table[(crc ^ x) & 0xFF] ^ (crc >> 8);
        foreach (byte x in b) crc = Table[(crc ^ x) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    private static uint[] Build()
    {
        const uint poly = 0x82F63B78;
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
            t[i] = crc;
        }
        return t;
    }
}
