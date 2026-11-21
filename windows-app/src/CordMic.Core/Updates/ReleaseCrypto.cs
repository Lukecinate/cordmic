using System.Security.Cryptography;

namespace CordMic.Core.Updates;

/// <summary>ECDSA P-256 helpers shared by the verifier and <c>cordmic-keytool</c>.</summary>
public static class ReleaseCrypto
{
    public const int Pbkdf2Iterations = 600_000;
    public const string AlgorithmName = "ECDSA-P256-SHA256";

    public static ECDsa CreateKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public static byte[] ExportSpki(ECDsa key) => key.ExportSubjectPublicKeyInfo();

    public static string SpkiBase64(ECDsa key) => Convert.ToBase64String(ExportSpki(key));

    /// <summary>First 8 bytes of SHA-256 of the SPKI, lowercase hex (16 characters).</summary>
    public static string KeyId(ReadOnlySpan<byte> spki)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(spki, hash);
        return Convert.ToHexString(hash[..8]).ToLowerInvariant();
    }

    public static string KeyIdFromBase64(string spkiBase64) => KeyId(Convert.FromBase64String(spkiBase64));

    public static byte[] EncryptPkcs8(ECDsa key, string passphrase)
    {
        var pbe = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, Pbkdf2Iterations);
        return key.ExportEncryptedPkcs8PrivateKey(passphrase, pbe);
    }

    public static ECDsa DecryptPkcs8(ReadOnlySpan<byte> p8, string passphrase)
    {
        var key = CreateKey();
        key.ImportEncryptedPkcs8PrivateKey(passphrase, p8, out _);
        return key;
    }

    public static byte[] SignDer(ECDsa key, ReadOnlySpan<byte> data) =>
        key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    public static bool VerifyDer(ReadOnlySpan<byte> spki, ReadOnlySpan<byte> data, ReadOnlySpan<byte> der)
    {
        using var key = CreateKey();
        key.ImportSubjectPublicKeyInfo(spki, out _);
        return key.VerifyData(data, der, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    public static bool PassphraseStrongEnough(string? passphrase)
    {
        if (string.IsNullOrEmpty(passphrase)) return false;
        var words = passphrase.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 6 && words.All(w => w.Length >= 3)) return true;
        return passphrase.Length >= 20;
    }
}
