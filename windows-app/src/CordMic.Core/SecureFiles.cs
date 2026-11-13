using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace CordMic.Core;

/// <summary>
/// Download staging for files that are launched elevated (CordMic update, VB-CABLE setup). Closes the
/// check-then-use window: each run writes into a new randomly named directory (on Windows with an explicit ACL for
/// the current user, SYSTEM and Administrators, inheritance off), and the file is then re-opened read-only with
/// <see cref="FileShare.Read"/> so nobody can write, rename or delete it while the hash is computed on that same
/// handle and the child process is started. The caller keeps the returned handle open until the child has started.
/// </summary>
public static class SecureFiles
{
    /// <summary>Creates <c>parent\&lt;prefix&gt;&lt;random&gt;</c>. Never reuses an existing directory.</summary>
    public static string CreatePrivateDirectory(string parent, string prefix = "run-")
    {
        Directory.CreateDirectory(parent);
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var dir = Path.Combine(parent, prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant());
            if (Directory.Exists(dir) || File.Exists(dir)) continue;
            if (OperatingSystem.IsWindows()) CreateWithAcl(dir);
            else
            {
                Directory.CreateDirectory(dir);
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("staging directory is a reparse point");
            return dir;
        }
        throw new IOException("could not create a private staging directory");
    }

    [SupportedOSPlatform("windows")]
    private static void CreateWithAcl(string dir)
    {
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var who = new List<SecurityIdentifier> { new(WellKnownSidType.LocalSystemSid, null), new(WellKnownSidType.BuiltinAdministratorsSid, null) };
        if (WindowsIdentity.GetCurrent().User is { } me) who.Insert(0, me);
        foreach (var sid in who)
            sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        // Fails if the name already exists, so a pre-planted directory or junction is never adopted.
        var di = new DirectoryInfo(dir);
        if (di.Exists) throw new IOException("staging directory already exists");
        di.Create(sec);
    }

    /// <summary>Writes <paramref name="source"/> to a new file (fails if the name exists) and flushes it.</summary>
    public static async Task WriteNewAsync(string path, Stream source, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await source.CopyToAsync(fs, ct).ConfigureAwait(false);
        await fs.FlushAsync(ct).ConfigureAwait(false);
    }

    public static void WriteNew(string path, ReadOnlySpan<byte> data)
    {
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        fs.Write(data);
        fs.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Opens <paramref name="path"/> read-only, denying writers and deleters, hashes the content through that handle
    /// and returns the handle positioned at 0. Throws (and closes the handle) when the SHA-256 differs.
    /// </summary>
    public static FileStream OpenLockedVerified(string path, string expectedSha256Hex)
    {
        var fs = OpenLocked(path);
        try
        {
            var got = Sha256Hex(fs);
            if (!string.Equals(got, expectedSha256Hex.Trim().ToLowerInvariant(), StringComparison.Ordinal))
                throw new InvalidDataException($"SHA-256 mismatch for {Path.GetFileName(path)}");
            return fs;
        }
        catch { fs.Dispose(); throw; }
    }

    /// <summary>Read-only handle that denies write and delete sharing; refuses reparse points.</summary>
    public static FileStream OpenLocked(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException($"{Path.GetFileName(path)} is a reparse point");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    /// <summary>SHA-256 of the whole stream (lower-case hex); rewinds to 0 before and after.</summary>
    public static string Sha256Hex(FileStream fs)
    {
        fs.Position = 0;
        var hash = SHA256.HashData(fs);
        fs.Position = 0;
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Best-effort recursive delete of a staging directory (handles must be closed first).</summary>
    public static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (Exception) { }
    }
}

/// <summary>A set of locked handles released together, after the child process has started or exited.</summary>
public sealed class LockedFiles : IDisposable
{
    private readonly List<FileStream> _handles = new();
    public IReadOnlyList<FileStream> Handles => _handles;
    public void Add(FileStream fs) => _handles.Add(fs);
    public void Dispose()
    {
        foreach (var h in _handles) { try { h.Dispose(); } catch (Exception) { } }
        _handles.Clear();
    }
}
