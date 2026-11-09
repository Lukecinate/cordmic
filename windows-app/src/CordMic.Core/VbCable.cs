namespace CordMic.Core;

/// <summary>
/// Official VB-CABLE Basic package pin. CordMic never ships the driver; it only
/// downloads this URL over HTTPS and checks the hash (and, on Windows, the signer)
/// before launching VB-Audio's own setup.
/// </summary>
public static class VbCable
{
    public const string DownloadUrl = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack46.zip";
    public const string PageUrl = "https://vb-audio.com/Cable/";
    public const string FileName = "VBCABLE_Driver_Pack46.zip";
    public const string Sha256 = "bc44e8974191a5a2427d6c979e2dab08dbb09925f3f435dfa2f0ba3e45b6eb90";
    public const string SetupX64 = "VBCABLE_Setup_x64.exe";
    public const long ExpectedSize = 1_324_680;

    public static readonly string[] TrustedSignerNeedles = { "VB-Audio", "Vincent Burel", "V.Burel" };

    public static bool HashMatches(ReadOnlySpan<byte> zip) => AppUpdate.HashMatches(zip, Sha256);

    public static bool HashMatchesFile(string path) => AppUpdate.HashMatchesFile(path, Sha256);

    public static bool SignerLooksTrusted(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return false;
        foreach (var n in TrustedSignerNeedles)
            if (subject.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
