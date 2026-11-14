using System.IO.Compression;
using System.Security.Cryptography;

namespace CordMic.Core.Updates;

/// <summary>Decides whether this copy runs from the installer's folder or from an unpacked portable zip.</summary>
public static class InstallMode
{
    /// <summary>File shipped in the portable zip (0.7.0+). Its presence always means portable.</summary>
    public const string PortableMarker = "portable.txt";
    /// <summary>Written by the NSIS installer next to CordMic.exe.</summary>
    public const string Uninstaller = "Uninstall.exe";

    /// <param name="exeDir">Folder of the running CordMic.exe.</param>
    /// <param name="fileExists">File probe (injected for tests).</param>
    /// <param name="registeredInstallDir">InstallLocation from the CordMic uninstall registry key, or null.</param>
    public static InstallKind Detect(string exeDir, Func<string, bool> fileExists, string? registeredInstallDir)
    {
        if (fileExists(Path.Combine(exeDir, PortableMarker))) return InstallKind.Portable;
        if (!string.IsNullOrWhiteSpace(registeredInstallDir) && SamePath(registeredInstallDir, exeDir)) return InstallKind.Installed;
        // An installed copy always has the uninstaller next to it; the 0.6.x portable zips (no marker) never do.
        return fileExists(Path.Combine(exeDir, Uninstaller)) ? InstallKind.Installed : InstallKind.Portable;
    }

    public static bool SamePath(string a, string b)
    {
        static string N(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        try { return string.Equals(N(a), N(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal); }
        catch (Exception) { return false; }
    }

    /// <summary>True when a new file can be created (and removed) in <paramref name="dir"/> without elevation.</summary>
    public static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".cordmic-write-test-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception) { return false; }
    }
}

/// <summary>
/// Arguments of the portable update helper (<c>CordMic.exe --apply-portable-update &lt;dir&gt; --pid N --started T</c>).
/// <see cref="StartedUtcTicks"/> (0.7.1, audit M6) is the start time of the process to wait for, so a reused PID is
/// not mistaken for CordMic; 0 means unknown (a 0.7.0 caller).
/// </summary>
public sealed record PortableUpdateArgs(string TargetDir, int WaitPid, long StartedUtcTicks = 0)
{
    public const string Verb = "--apply-portable-update";

    public static bool Mentions(IReadOnlyList<string> args) => args.Any(a => a.Equals(Verb, StringComparison.OrdinalIgnoreCase));

    public string[] ToArgs()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var a = new List<string> { Verb, TargetDir, "--pid", WaitPid.ToString(inv) };
        if (StartedUtcTicks > 0) { a.Add("--started"); a.Add(StartedUtcTicks.ToString(inv)); }
        return a.ToArray();
    }

    /// <summary>True when a process with the awaited PID is the one that was meant (start times within one second).</summary>
    public static bool IsSameProcess(long expectedStartUtcTicks, DateTime actualStartUtc) =>
        expectedStartUtcTicks <= 0 || Math.Abs(actualStartUtc.ToUniversalTime().Ticks - expectedStartUtcTicks) < TimeSpan.TicksPerSecond;

    public static bool TryParse(IReadOnlyList<string> args, out PortableUpdateArgs? parsed)
    {
        parsed = null;
        int i = -1;
        for (int k = 0; k < args.Count; k++) if (args[k].Equals(Verb, StringComparison.OrdinalIgnoreCase)) { i = k; break; }
        if (i < 0 || i + 1 >= args.Count) return false;
        var target = args[i + 1];
        if (string.IsNullOrWhiteSpace(target) || target.StartsWith("--", StringComparison.Ordinal) || !Path.IsPathFullyQualified(target)) return false;
        int pid = 0;
        long started = 0;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        for (int k = i + 2; k + 1 < args.Count; k++)
        {
            if (args[k].Equals("--pid", StringComparison.OrdinalIgnoreCase) && !int.TryParse(args[k + 1], System.Globalization.NumberStyles.None, inv, out pid)) return false;
            if (args[k].Equals("--started", StringComparison.OrdinalIgnoreCase) && !long.TryParse(args[k + 1], System.Globalization.NumberStyles.None, inv, out started)) return false;
        }
        parsed = new PortableUpdateArgs(target, pid, started);
        return true;
    }
}

public enum SwapOutcome { Updated, RolledBack, NothingToDo }

public sealed record SwapResult(SwapOutcome Outcome, int FilesReplaced, string? Error);

/// <summary>
/// Portable self-update. <see cref="ExtractVerified"/> unpacks the zip from the same locked handle that was hashed,
/// rejecting entries that would land outside the folder. <see cref="Apply"/> replaces the files of the portable folder
/// one by one, moving each old file into a backup folder first; on any error every change is undone, so the folder is
/// either fully old or fully new. User data is not in this folder (settings and logs live under %APPDATA% and
/// %LOCALAPPDATA%), and files that the new version does not ship are left alone.
/// </summary>
public static class PortableSwap
{
    public const string ExeName = "CordMic.exe";
    public const string BackupPrefix = ".update-backup-";

    /// <summary>Unpacks <paramref name="verified"/> (read from position 0) into the new directory <paramref name="destDir"/>.</summary>
    public static int ExtractVerified(FileStream verified, string destDir, long maxTotalBytes = UpdateEngine.MaxAssetBytes * 2)
    {
        verified.Position = 0;
        Directory.CreateDirectory(destDir);
        var root = Path.GetFullPath(destDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        int files = 0;
        long total = 0;
        using (var zip = new ZipArchive(verified, ZipArchiveMode.Read, leaveOpen: true))
        {
            foreach (var e in zip.Entries)
            {
                var name = e.FullName.Replace('\\', '/');
                if (name.Length == 0 || name.EndsWith('/')) continue;
                if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(p => p == ".."))
                    throw new UpdateException(UpdateError.InstallFailed, "the update archive has an unsafe path: " + e.FullName);
                var dst = Path.GetFullPath(Path.Combine(destDir, name.Replace('/', Path.DirectorySeparatorChar)));
                if (!dst.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new UpdateException(UpdateError.InstallFailed, "the update archive has an unsafe path: " + e.FullName);
                if (total + e.Length > maxTotalBytes) throw new UpdateException(UpdateError.TooLarge, "the update archive is too large");
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                using (var src = e.Open())
                using (var fs = new FileStream(dst, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    // Audit L4: count the bytes actually written; the size in the zip header is only a claim.
                    var buf = new byte[81920];
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        total += n;
                        if (total > maxTotalBytes) throw new UpdateException(UpdateError.TooLarge, "the update archive is too large");
                        fs.Write(buf, 0, n);
                    }
                }
                files++;
            }
        }
        verified.Position = 0;
        if (!File.Exists(Path.Combine(destDir, ExeName)))
            throw new UpdateException(UpdateError.InstallFailed, "the update archive has no " + ExeName);
        return files;
    }

    /// <summary>
    /// Copies every file of <paramref name="sourceDir"/> over <paramref name="targetDir"/>, keeping the replaced files in
    /// a backup folder until all copies succeeded. <paramref name="beforeFile"/> is a test hook called before each file.
    /// </summary>
    public static SwapResult Apply(string sourceDir, string targetDir, Action<string>? log = null, Action<string>? beforeFile = null,
        int retries = 10, TimeSpan? retryDelay = null)
    {
        var delay = retryDelay ?? TimeSpan.FromMilliseconds(300);
        var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(sourceDir, f))
            .OrderBy(r => r.Equals(ExeName, StringComparison.OrdinalIgnoreCase) ? 1 : 0) // the exe last
            .ThenBy(r => r, StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0) return new SwapResult(SwapOutcome.NothingToDo, 0, null);
        var backup = Path.Combine(targetDir, BackupPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant());
        Directory.CreateDirectory(backup);
        var replaced = new List<string>();
        var added = new List<string>();
        try
        {
            foreach (var rel in files)
            {
                beforeFile?.Invoke(rel);
                var dst = Path.Combine(targetDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                if (File.Exists(dst))
                {
                    var bak = Path.Combine(backup, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(bak)!);
                    Retry(() => File.Move(dst, bak), retries, delay);
                    replaced.Add(rel);
                }
                else added.Add(rel);
                Retry(() => File.Copy(Path.Combine(sourceDir, rel), dst, overwrite: false), retries, delay);
            }
        }
        catch (Exception e)
        {
            log?.Invoke($"portable update: {e.Message}; restoring the previous version");
            foreach (var rel in added)
                try { var d = Path.Combine(targetDir, rel); if (File.Exists(d)) Retry(() => File.Delete(d), retries, delay); } catch (Exception) { }
            foreach (var rel in replaced)
            {
                try
                {
                    var d = Path.Combine(targetDir, rel);
                    if (File.Exists(d)) Retry(() => File.Delete(d), retries, delay);
                    Retry(() => File.Move(Path.Combine(backup, rel), d), retries, delay);
                }
                catch (Exception re) { log?.Invoke($"portable update: could not restore {rel}: {re.Message}"); }
            }
            TryDeleteIfEmpty(backup);
            return new SwapResult(SwapOutcome.RolledBack, 0, e.Message);
        }
        SecureFiles.TryDelete(backup);
        log?.Invoke($"portable update: replaced {replaced.Count} files, added {added.Count}");
        return new SwapResult(SwapOutcome.Updated, files.Count, null);
    }

    /// <summary>
    /// 0.7.1 (audit M10): a new, not yet existing unpack folder inside the staging folder for each install attempt, so
    /// a retry after a failed start never collides with files from the previous attempt.
    /// </summary>
    public static string NewUnpackDir(string stagedDir)
    {
        for (int i = 0; i < 8; i++)
        {
            var d = Path.Combine(stagedDir, "new-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant());
            if (!Directory.Exists(d) && !File.Exists(d)) return d;
        }
        throw new IOException("could not pick an unpack folder");
    }

    /// <summary>
    /// 0.7.1 (audit M4/L8): removes <c>.update-backup-*</c> folders in a portable folder that are older than one hour
    /// (or dated more than a day ahead). Returns how many were removed.
    /// </summary>
    public static int CleanBackups(string targetDir, DateTime? nowUtc = null)
    {
        int removed = 0;
        try
        {
            var now = nowUtc ?? DateTime.UtcNow;
            foreach (var d in Directory.GetDirectories(targetDir, BackupPrefix + "*"))
            {
                try
                {
                    if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    if (UpdateEngine.IsStale(Directory.GetCreationTimeUtc(d), now)) { SecureFiles.TryDelete(d); if (!Directory.Exists(d)) removed++; }
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
        return removed;
    }

    /// <summary>
    /// Waits (bounded) until <paramref name="path"/> can be opened for writing with no sharing, i.e. the old CordMic
    /// has really let go of it. Returns false after <paramref name="timeout"/>.
    /// </summary>
    public static bool WaitUntilWritable(string path, TimeSpan timeout, TimeSpan? poll = null)
    {
        var step = poll ?? TimeSpan.FromMilliseconds(250);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                return true;
            }
            catch (FileNotFoundException) { return true; }
            catch (Exception) when (sw.Elapsed < timeout) { Thread.Sleep(step); }
            catch (Exception) { return false; }
        }
    }

    private static void TryDeleteIfEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any())
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception) { }
    }

    private static void Retry(Action a, int retries, TimeSpan delay)
    {
        for (int i = 0; ; i++)
        {
            try { a(); return; }
            catch (Exception e) when ((e is IOException || e is UnauthorizedAccessException) && i < retries && e is not FileNotFoundException && e is not DirectoryNotFoundException)
            {
                Thread.Sleep(delay);
            }
        }
    }
}
