using System.Diagnostics;
using System.Net.Http;
using CordMic.Core;

namespace CordMic.App;

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

    public static async Task<string> DownloadVerifiedSetupAsync(AppUpdate.ReleaseInfo info, string currentVersion, CancellationToken ct = default)
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

        var dest = Path.Combine(Path.GetTempPath(), info.SetupName);
        await using (var input = await Http.GetStreamAsync(info.SetupUrl, ct).ConfigureAwait(false))
        await using (var output = File.Create(dest))
            await input.CopyToAsync(output, ct).ConfigureAwait(false);

        if (!AppUpdate.HashMatchesFile(dest, expected))
        {
            try { File.Delete(dest); } catch (Exception) { }
            throw new InvalidOperationException("Installer SHA-256 mismatch; aborting");
        }
        return dest;
    }

    public static void LaunchSilentAndExit(string setupPath)
    {
        var psi = new ProcessStartInfo(setupPath)
        {
            Arguments = "/S",
            UseShellExecute = true,
        };
        Process.Start(psi);
    }
}
