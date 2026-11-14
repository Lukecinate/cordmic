using System.ComponentModel;
using System.Diagnostics;
using CordMic.Core;
using CordMic.Core.Updates;
using Microsoft.Win32;

namespace CordMic.App;

/// <summary>Windows side of the 0.7.0 updater: install-kind detection and the two ways an update is started.</summary>
internal static class AppUpdater
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CordMic";

    /// <summary>InstallLocation written by the installer (64-bit view), or null.</summary>
    public static string? RegisteredInstallDir()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = hklm.OpenSubKey(UninstallKey);
            return k?.GetValue("InstallLocation") as string;
        }
        catch (Exception) { return null; }
    }

    public static InstallKind DetectKind() => InstallMode.Detect(Paths.ExeDir, File.Exists, RegisteredInstallDir());

    public static UpdateCoordinator Create(string currentVersion, InstallKind kind, Action afterStart, Action<string> log)
    {
        var engine = new UpdateEngine(UpdateEngine.CreateHttpClient(), currentVersion, Paths.Staging);
        IUpdateInstaller installer = kind == InstallKind.Portable ? new PortableInstaller(afterStart, log) : new SetupInstaller(afterStart);
        Func<bool> canApply = kind == InstallKind.Portable ? () => InstallMode.IsWritable(Paths.ExeDir) : () => true;
        return new UpdateCoordinator(engine, kind, installer, canApply) { Log = log };
    }

    public static void OpenReleasePage(string? url)
    {
        var u = url != null && url.StartsWith("https://github.com/", StringComparison.Ordinal) ? url : AppUpdate.ReleasesPageUrl;
        try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); } catch (Exception) { }
    }

    /// <summary>
    /// Installed copy: starts the verified installer silently through ShellExecute (one UAC prompt) and keeps the
    /// locked handle until the installer process exists. The installer closes CordMic and starts it again.
    /// </summary>
    private sealed class SetupInstaller : IUpdateInstaller
    {
        private readonly Action _afterStart;
        public SetupInstaller(Action afterStart) => _afterStart = afterStart;

        public async Task InstallAsync(StagedUpdate staged, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(staged.Path) { Arguments = "/S", UseShellExecute = true, ErrorDialog = false };
            Process? p;
            try { p = await Task.Run(() => Process.Start(psi), ct).ConfigureAwait(false); }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223) { throw new UpdateException(UpdateError.Declined, "UAC declined", e); }
            catch (Exception e) { throw new UpdateException(UpdateError.InstallFailed, e.Message, e); }
            if (p is null) throw new UpdateException(UpdateError.InstallFailed, "the installer did not start");
            using (p)
            {
                try { await Task.Run(() => p.WaitForExit(2000), ct).ConfigureAwait(false); } catch (Exception) { }
            }
            staged.ReleaseForChild();
            _afterStart();
        }
    }

    /// <summary>
    /// Portable copy: unpacks the verified zip (read through the locked handle) next to it in the private staging
    /// folder and starts the new CordMic.exe as a helper that waits for this process to exit, swaps the files of the
    /// portable folder with rollback, and starts CordMic again. No elevation.
    /// </summary>
    private sealed class PortableInstaller : IUpdateInstaller
    {
        private readonly Action _afterStart;
        private readonly Action<string> _log;
        public PortableInstaller(Action afterStart, Action<string> log) { _afterStart = afterStart; _log = log; }

        public async Task InstallAsync(StagedUpdate staged, CancellationToken ct)
        {
            var newDir = Path.Combine(staged.Dir, "new");
            try { await Task.Run(() => PortableSwap.ExtractVerified(staged.Handle, newDir), ct).ConfigureAwait(false); }
            catch (UpdateException) { throw; }
            catch (Exception e) { throw new UpdateException(UpdateError.InstallFailed, "could not unpack the update: " + e.Message, e); }
            var helper = Path.Combine(newDir, PortableSwap.ExeName);
            var psi = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = newDir };
            foreach (var a in new PortableUpdateArgs(Paths.ExeDir, Environment.ProcessId).ToArgs()) psi.ArgumentList.Add(a);
            try { using var p = Process.Start(psi) ?? throw new UpdateException(UpdateError.InstallFailed, "the update helper did not start"); }
            catch (UpdateException) { throw; }
            catch (Exception e) { throw new UpdateException(UpdateError.InstallFailed, e.Message, e); }
            _log($"portable update {staged.Tag}: helper started");
            staged.ReleaseForChild();
            _afterStart();
        }
    }
}

/// <summary>
/// <c>CordMic.exe --apply-portable-update &lt;folder&gt; --pid &lt;n&gt;</c>, run from the unpacked new version: waits for
/// the old CordMic to exit, swaps the portable folder's files (rolled back on any error) and starts CordMic again.
/// </summary>
internal static class PortableUpdateHelper
{
    public static int Run(string[] args)
    {
        if (!PortableUpdateArgs.TryParse(args, out var a) || a is null) return 64;
        using var log = new FileLog(Paths.Log, backgroundWriter: false);
        void Log(string m) { try { log.Write(m); log.Flush(); } catch (Exception) { } }
        try
        {
            if (a.WaitPid > 0)
            {
                try { using var old = Process.GetProcessById(a.WaitPid); if (!old.WaitForExit(60_000)) { Log("portable update: CordMic did not exit; update skipped"); return 3; } }
                catch (ArgumentException) { } // already gone
            }
            var source = AppContext.BaseDirectory;
            if (InstallMode.SamePath(source, a.TargetDir)) { Log("portable update: refusing to update a folder onto itself"); return 64; }
            if (!File.Exists(Path.Combine(a.TargetDir, PortableSwap.ExeName))) { Log("portable update: target has no CordMic.exe; skipped"); return 4; }
            var r = PortableSwap.Apply(source, a.TargetDir, Log);
            Log(r.Outcome == SwapOutcome.Updated
                ? $"portable update: done ({r.FilesReplaced} files)"
                : $"portable update: rolled back ({r.Error})");
            return r.Outcome == SwapOutcome.Updated ? 0 : 2;
        }
        catch (Exception e) { Log("portable update helper: " + e.Message); return 1; }
        finally
        {
            try
            {
                var exe = Path.Combine(a.TargetDir, PortableSwap.ExeName);
                if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = a.TargetDir });
            }
            catch (Exception e) { Log("portable update: could not start CordMic: " + e.Message); }
        }
    }
}
