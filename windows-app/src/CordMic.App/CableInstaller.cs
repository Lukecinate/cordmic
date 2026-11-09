using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using CordMic.Core;

namespace CordMic.App;

/// <summary>
/// Downloads the official VB-CABLE pack, verifies the pinned SHA-256 (and Authenticode
/// signer on Windows), then launches VB-Audio's own setup. The zip is never shipped with CordMic.
/// </summary>
internal static class CableInstaller
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public static bool LooksInstalled()
    {
        try
        {
            foreach (var p in new[]
                     {
                         Path.Combine(Environment.SystemDirectory, "drivers", "vbaudio_cable64_win10.sys"),
                         Path.Combine(Environment.SystemDirectory, "vbaudio_cable64_win10.sys"),
                     })
                if (File.Exists(p)) return true;
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VB-Audio Virtual Cable");
            if (key != null) return true;
        }
        catch (Exception) { }
        return false;
    }

    public static async Task<string> DownloadAndVerifyAsync(CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cordmic-vbcable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, VbCable.FileName);
        await using (var input = await Http.GetStreamAsync(VbCable.DownloadUrl, ct).ConfigureAwait(false))
        await using (var output = File.Create(zipPath))
            await input.CopyToAsync(output, ct).ConfigureAwait(false);

        if (!VbCable.HashMatchesFile(zipPath))
        {
            TryDelete(dir);
            throw new InvalidOperationException("VB-CABLE package SHA-256 mismatch; aborting");
        }

        ZipFile.ExtractToDirectory(zipPath, dir);
        var setup = Path.Combine(dir, VbCable.SetupX64);
        if (!File.Exists(setup))
        {
            TryDelete(dir);
            throw new InvalidOperationException("Official zip did not contain " + VbCable.SetupX64);
        }

        if (OperatingSystem.IsWindows())
        {
            var cert = GetSignerSubject(setup);
            if (!VbCable.SignerLooksTrusted(cert))
            {
                TryDelete(dir);
                throw new InvalidOperationException("Authenticode signer not recognized: " + (cert ?? "(none)"));
            }
        }
        return setup;
    }

    public static void LaunchSetup(string setupPath)
    {
        // The official package documents no silent switch; run the UI elevated.
        var psi = new ProcessStartInfo(setupPath)
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        Process.Start(psi);
    }

    public static string? GetSignerSubject(string path)
    {
        try
        {
            var cert = X509Certificate.CreateFromSignedFile(path);
            return cert.Subject;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (Exception) { }
    }
}
