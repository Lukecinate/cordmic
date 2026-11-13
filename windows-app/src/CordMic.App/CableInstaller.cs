using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using CordMic.Core;

namespace CordMic.App;

/// <summary>Extracted, verified VB-CABLE setup. The files stay locked against writes until it is disposed.</summary>
internal sealed class StagedCable : IDisposable
{
    public StagedCable(string setupPath, string dir, LockedFiles locks) { SetupPath = setupPath; Dir = dir; Locks = locks; }
    public string SetupPath { get; }
    public string Dir { get; }
    public LockedFiles Locks { get; }
    public void Dispose()
    {
        Locks.Dispose();
        SecureFiles.TryDelete(Dir);
    }
}

/// <summary>
/// Downloads the official VB-CABLE pack, verifies the pinned SHA-256, extracts it into a private directory with every
/// file locked against writes, checks the setup's Authenticode signature with WinVerifyTrust on that handle plus the
/// signer name, then launches VB-Audio's own setup. The zip is never shipped with CordMic.
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

    public static async Task<StagedCable> DownloadAndVerifyAsync(CancellationToken ct = default)
    {
        byte[] zip = await Http.GetByteArrayAsync(VbCable.DownloadUrl, ct).ConfigureAwait(false);
        // The pin is checked on the in-memory bytes, so nothing on disk can change what was verified.
        if (!VbCable.HashMatches(zip))
            throw new InvalidOperationException("VB-CABLE package SHA-256 mismatch; aborting");

        var dir = SecureFiles.CreatePrivateDirectory(Paths.Staging, "vbcable-");
        var locks = new LockedFiles();
        try
        {
            var setup = VbCable.ExtractLocked(zip, dir, locks);
            if (OperatingSystem.IsWindows())
            {
                var handle = locks.Handles.First(h => string.Equals(h.Name, setup, StringComparison.OrdinalIgnoreCase));
                int hr = Authenticode.Verify(handle, setup);
                if (hr != 0) throw new InvalidOperationException($"Authenticode signature not trusted (0x{hr:X8})");
                var subject = GetSignerSubject(setup);
                if (!VbCable.SignerLooksTrusted(subject))
                    throw new InvalidOperationException("Authenticode signer not recognized: " + (subject ?? "(none)"));
            }
            return new StagedCable(setup, dir, locks);
        }
        catch
        {
            locks.Dispose();
            SecureFiles.TryDelete(dir);
            throw;
        }
    }

    /// <summary>
    /// Runs the official setup elevated (no documented silent switch) and keeps every extracted file locked until it
    /// exits, then removes the staging directory.
    /// </summary>
    public static async Task RunSetupAsync(StagedCable staged)
    {
        var psi = new ProcessStartInfo(staged.SetupPath)
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        try
        {
            using var p = await Task.Run(() => Process.Start(psi)).ConfigureAwait(true);
            if (p is not null)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(true); }
                catch (OperationCanceledException) { }
            }
        }
        finally { staged.Dispose(); }
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
}
