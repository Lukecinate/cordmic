using System.Diagnostics;
using System.Net.Http;
using CordMic.Core;

namespace CordMic.App;

/// <summary>
/// A downloaded, hash-verified installer. <see cref="Handle"/> is a read-only handle that denies writes and deletes,
/// so the bytes that were hashed are the bytes that run. Dispose releases it and removes the staging directory.
/// </summary>
internal sealed class StagedSetup : IDisposable
{
    public StagedSetup(string tag, string path, string dir, FileStream handle) { Tag = tag; Path = path; Dir = dir; Handle = handle; }
    public string Tag { get; }
    public string Path { get; }
    public string Dir { get; }
    public FileStream Handle { get; }
    private bool _keepFiles;

    /// <summary>Releases the handle but leaves the file for the running installer (it reads itself while it runs).</summary>
    public void ReleaseForRunningInstaller() { _keepFiles = true; Dispose(); }

    public void Dispose()
    {
        try { Handle.Dispose(); } catch (Exception) { }
        if (!_keepFiles) SecureFiles.TryDelete(Dir);
    }
}

/// <summary>Downloads a verified CordMic installer from GitHub Releases and launches it silently.</summary>
internal static class UpdateClient
{
    private static readonly HttpClient Http = Create();

    private static HttpClient Create()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd(AppUpdate.UserAgent);
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    public static async Task<AppUpdate.ReleaseInfo> FetchLatestAsync(CancellationToken ct = default)
    {
        var json = await Http.GetStringAsync(AppUpdate.LatestApiUrl, ct).ConfigureAwait(false);
        return AppUpdate.ParseLatestJson(json);
    }

    /// <summary>
    /// 0.6.1 (audit M1): downloads into a new private directory under %LOCALAPPDATA%\CordMic\staging, then re-opens
    /// the file read-only (write/delete sharing denied) and checks SHA-256 on that handle. The caller keeps the
    /// returned <see cref="StagedSetup"/> until the installer has started.
    /// </summary>
    public static async Task<StagedSetup> DownloadVerifiedSetupAsync(AppUpdate.ReleaseInfo info, string currentVersion, CancellationToken ct = default)
    {
        if (AppUpdate.IsDowngrade(info.Tag, currentVersion))
            throw new InvalidOperationException("Refusing to downgrade to " + info.Tag);
        if (!AppUpdate.IsNewer(info.Tag, currentVersion))
            throw new InvalidOperationException("Already up to date (" + currentVersion + ")");
        if (string.IsNullOrEmpty(info.SetupUrl) || string.IsNullOrEmpty(info.SetupName))
            throw new InvalidOperationException("Release has no Windows installer");
        if (string.IsNullOrEmpty(info.SumsUrl))
            throw new InvalidOperationException("Release has no SHA256SUMS asset");

        var sumsText = await Http.GetStringAsync(info.SumsUrl, ct).ConfigureAwait(false);
        var sums = AppUpdate.ParseSha256Sums(sumsText);
        var expected = AppUpdate.HashForAsset(sums, info.SetupName)
            ?? throw new InvalidOperationException("SHA256SUMS has no entry for " + info.SetupName);

        CleanOldStaging();
        var dir = SecureFiles.CreatePrivateDirectory(Paths.Staging, "update-");
        try
        {
            var dest = Path.Combine(dir, AppUpdate.SafeAssetFileName(info.SetupName, "CordMic-Setup.exe"));
            await using (var input = await Http.GetStreamAsync(info.SetupUrl, ct).ConfigureAwait(false))
                await SecureFiles.WriteNewAsync(dest, input, ct).ConfigureAwait(false);
            FileStream handle;
            try { handle = SecureFiles.OpenLockedVerified(dest, expected); }
            catch (InvalidDataException) { throw new InvalidOperationException("Installer SHA-256 mismatch; aborting"); }
            return new StagedSetup(info.Tag, dest, dir, handle);
        }
        catch
        {
            SecureFiles.TryDelete(dir);
            throw;
        }
    }

    /// <summary>
    /// Starts the staged installer silently (it relaunches CordMic when it finishes, also after a failure) and keeps
    /// the verified handle open until the installer has opened itself.
    /// </summary>
    public static async Task LaunchSilentAsync(StagedSetup setup)
    {
        var psi = new ProcessStartInfo(setup.Path)
        {
            Arguments = "/S",
            UseShellExecute = true,
        };
        // ShellExecute shows the UAC prompt and returns once the elevated process exists; run it off the UI thread.
        using var p = await Task.Run(() => Process.Start(psi)).ConfigureAwait(true)
            ?? throw new InvalidOperationException("installer did not start");
        try { await Task.Run(() => p.WaitForExit(2000)).ConfigureAwait(true); } catch (Exception) { }
        setup.ReleaseForRunningInstaller();
    }

    /// <summary>Removes staging directories left by earlier runs (older than one day).</summary>
    private static void CleanOldStaging()
    {
        try
        {
            if (!Directory.Exists(Paths.Staging)) return;
            foreach (var d in Directory.GetDirectories(Paths.Staging))
            {
                try
                {
                    if (DateTime.UtcNow - Directory.GetCreationTimeUtc(d) > TimeSpan.FromDays(1)) SecureFiles.TryDelete(d);
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
    }
}
