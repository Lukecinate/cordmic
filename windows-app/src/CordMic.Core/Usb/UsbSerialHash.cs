using System.Security.Cryptography;
using System.Text;

namespace CordMic.Core.Usb;

/// <summary>Salts and hashes a USB serial so reports never store the raw value.</summary>
public static class UsbSerialHash
{
    public const int HexChars = 16;

    public static string Hash(string? rawSerial, string? saltHex)
    {
        if (string.IsNullOrWhiteSpace(rawSerial) || string.IsNullOrWhiteSpace(saltHex)) return "";
        byte[] salt;
        try { salt = Convert.FromHexString(saltHex); }
        catch (FormatException) { return ""; }
        if (salt.Length == 0) return "";
        byte[] id = Encoding.UTF8.GetBytes(rawSerial.Trim());
        byte[] data = new byte[salt.Length + id.Length];
        Buffer.BlockCopy(salt, 0, data, 0, salt.Length);
        Buffer.BlockCopy(id, 0, data, salt.Length, id.Length);
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()[..HexChars];
    }

    public static bool LooksLikeRawSerial(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        // A hashed serial is exactly 16 lowercase hex chars. Anything with the shape of a
        // typical USB serial (longer alphanumeric, or containing separators) is treated as raw.
        string v = value.Trim();
        if (v.Length == HexChars && v.All(c => char.IsAsciiHexDigit(c))) return false;
        return v.Length >= 4;
    }
}
