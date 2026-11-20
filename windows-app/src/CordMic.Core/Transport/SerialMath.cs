namespace CordMic.Core.Transport;

/// <summary>RFC 1982 serial comparison for 32-bit sequence numbers.</summary>
public static class SerialMath
{
    public static int Compare(uint a, uint b)
    {
        if (a == b) return 0;
        uint d = a - b;
        return d < 0x80000000u ? 1 : -1;
    }

    public static uint Distance(uint later, uint earlier) => later - earlier;

    public static bool IsNext(uint got, uint expected) => got == expected;

    public static bool IsGap(uint got, uint expected) =>
        got != expected && Compare(got, expected) > 0;
}
