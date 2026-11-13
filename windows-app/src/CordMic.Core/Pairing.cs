using System.Security.Cryptography;
using System.Text;

namespace CordMic.Core;

/// <summary>
/// Optional pairing code (0.6.1). Off by default: a phone on the USB tethering link is accepted automatically.
/// When the user turns it on, the PC shows a 6-digit code that is entered once in the phone app. A phone becomes the
/// peer only after it answers a fresh random challenge:
/// <list type="bullet">
/// <item>PC → phone <see cref="Protocol.TPairChallenge"/>: 16-byte nonce.</item>
/// <item>phone → PC <see cref="Protocol.TPairProof"/>: the nonce, then the first 16 bytes of
/// HMAC-SHA256(key, "CordMic-pair-proof-v1" ‖ nonce), where key = PBKDF2-SHA256(code, "CordMic-pair-v1", 20000, 32).</item>
/// </list>
/// The nonce is single-use, so a captured proof cannot be replayed, and PBKDF2 makes guessing the code from a
/// captured exchange slow. Android implements the same derivation (Pairing.kt); a shared test vector keeps them equal.
/// </summary>
public static class Pairing
{
    public const int CodeLength = 6;
    public const int NonceSize = 16;
    public const int MacSize = 16;
    public const int ProofSize = NonceSize + MacSize;
    public const int KeySize = 32;
    public const int Iterations = 20_000;
    private static readonly byte[] Salt = Encoding.ASCII.GetBytes("CordMic-pair-v1");
    private static readonly byte[] Label = Encoding.ASCII.GetBytes("CordMic-pair-proof-v1");

    public static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public static bool IsValidCode(string? code)
    {
        if (code is null || code.Length != CodeLength) return false;
        foreach (char c in code) if (c is < '0' or > '9') return false;
        return true;
    }

    public static byte[] DeriveKey(string code)
    {
        if (!IsValidCode(code)) throw new ArgumentException("pairing code must be 6 digits", nameof(code));
        return Rfc2898DeriveBytes.Pbkdf2(Encoding.ASCII.GetBytes(code), Salt, Iterations, HashAlgorithmName.SHA256, KeySize);
    }

    public static byte[] NewNonce() => RandomNumberGenerator.GetBytes(NonceSize);

    /// <summary>Writes nonce ‖ mac (<see cref="ProofSize"/> bytes) into <paramref name="dst"/>.</summary>
    public static void WriteProof(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, Span<byte> dst)
    {
        if (nonce.Length != NonceSize) throw new ArgumentException("bad nonce", nameof(nonce));
        if (dst.Length < ProofSize) throw new ArgumentException("buffer too small", nameof(dst));
        nonce.CopyTo(dst);
        Mac(key, nonce, dst.Slice(NonceSize, MacSize));
    }

    /// <summary>Constant-time check of a proof payload against the outstanding nonce.</summary>
    public static bool Verify(ReadOnlySpan<byte> key, ReadOnlySpan<byte> expectedNonce, ReadOnlySpan<byte> proof)
    {
        if (key.Length != KeySize || expectedNonce.Length != NonceSize || proof.Length < ProofSize) return false;
        Span<byte> mac = stackalloc byte[MacSize];
        Mac(key, expectedNonce, mac);
        bool nonceOk = CryptographicOperations.FixedTimeEquals(proof[..NonceSize], expectedNonce);
        bool macOk = CryptographicOperations.FixedTimeEquals(proof.Slice(NonceSize, MacSize), mac);
        return nonceOk & macOk;
    }

    private static void Mac(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, Span<byte> dst16)
    {
        Span<byte> msg = stackalloc byte[Label.Length + NonceSize];
        Label.CopyTo(msg);
        nonce.CopyTo(msg[Label.Length..]);
        Span<byte> full = stackalloc byte[32];
        HMACSHA256.HashData(key, msg, full);
        full[..MacSize].CopyTo(dst16);
    }
}
