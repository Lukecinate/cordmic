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

    /// <summary>
    /// 0.6.1 (audit M1): extracts a hash-pinned VB-CABLE zip (already verified by the caller) into a private staging
    /// directory without a check-then-use window. Each entry is written to a new file, re-opened read-only with
    /// write and delete sharing denied, and compared (SHA-256) with the bytes from the zip through that handle. The
    /// handles stay in <paramref name="locks"/> until the caller has finished with the setup. Entry names that leave
    /// the directory (absolute, "..", drive letters) are rejected. Returns the path of <see cref="SetupX64"/>.
    /// </summary>
    public static string ExtractLocked(byte[] zipBytes, string dir, LockedFiles locks, string setupName = SetupX64)
    {
        string root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string? setup = null;
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(zipBytes, writable: false), System.IO.Compression.ZipArchiveMode.Read);
        foreach (var e in zip.Entries)
        {
            if (string.IsNullOrEmpty(e.Name)) continue; // directory entry
            string rel = e.FullName.Replace('\\', '/');
            if (rel.StartsWith('/') || rel.Contains(':') || rel.Split('/').Any(p => p == ".."))
                throw new InvalidDataException("unsafe entry name in VB-CABLE zip: " + e.FullName);
            string target = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("unsafe entry name in VB-CABLE zip: " + e.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            byte[] data;
            using (var es = e.Open())
            using (var ms = new MemoryStream())
            {
                es.CopyTo(ms);
                data = ms.ToArray();
            }
            SecureFiles.WriteNew(target, data);
            string expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();
            locks.Add(SecureFiles.OpenLockedVerified(target, expected));
            if (string.Equals(rel, setupName, StringComparison.OrdinalIgnoreCase)) setup = target;
        }
        return setup ?? throw new InvalidDataException("Official zip did not contain " + setupName);
    }
}
