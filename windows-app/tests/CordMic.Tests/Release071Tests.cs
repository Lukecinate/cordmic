using System.IO.Compression;
using System.Text;
using CordMic.Core;
using CordMic.Core.Updates;
using FakeServer = Release070Tests.FakeServer;
using FakeInstaller = Release070Tests.FakeInstaller;

/// <summary>0.7.1: regression tests for the 0.7.0 audit (docs: cordmic-audit-0.7.0.md) and the two known limitations.</summary>
public class Release071Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cordmic071-" + Guid.NewGuid().ToString("N"));
    public Release071Tests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (Exception) { } }

    private string Staging => Path.Combine(_root, "staging");

    private UpdateCoordinator Make(FakeServer srv, IUpdateInstaller inst, InstallKind kind = InstallKind.Installed, string current = "0.7.0", TimeSpan? watchdog = null)
    {
        var engine = new UpdateEngine(UpdateEngine.CreateHttpClient(srv), current, Staging, "https://api.example.test/repos/x/y/releases/latest");
        return new UpdateCoordinator(engine, kind, inst) { InstallWatchdog = watchdog ?? TimeSpan.FromMinutes(3) };
    }

    /// <summary>Installer that blocks until released (a UAC prompt left open).</summary>
    private sealed class BlockingInstaller : IUpdateInstaller
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public StagedUpdate? Last;
        public async Task InstallAsync(StagedUpdate staged, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Last = staged;
            Entered.TrySetResult();
            await Release.Task;
            Assert.False(staged.IsDisposed, "the staged update must not be dropped while installing");
            Assert.True(File.Exists(staged.Path));
            staged.ReleaseForChild();
        }
    }

    // ---- H1: install vs. check, double install --------------------------------------------------------------------

    [Fact]
    public async Task H1_SecondInstallWhileInstallingIsIgnored()
    {
        var srv = new FakeServer();
        var inst = new BlockingInstaller();
        var c = Make(srv, inst);
        await c.CheckAsync(download: true);
        Assert.Equal(UpdatePhase.Ready, c.State.Phase);
        var first = c.InstallStagedAsync();
        await inst.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = c.InstallStagedAsync();
        var third = c.UpdateNowAsync();
        await Task.WhenAll(second, third).WaitAsync(TimeSpan.FromSeconds(10)); // return at once
        inst.Release.SetResult();
        await first;
        Assert.Equal(1, inst.Calls);
        Assert.Equal(UpdatePhase.Installing, c.State.Phase);
    }

    [Fact]
    public async Task H1_CheckDuringInstallDoesNotDropOrFlipState()
    {
        var srv = new FakeServer();
        var inst = new BlockingInstaller();
        var c = Make(srv, inst);
        await c.CheckAsync(download: true);
        int hits = srv.LatestHits;
        var install = c.InstallStagedAsync();
        await inst.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        srv.Tag = "v0.7.2"; // a newer release appears while the UAC prompt is open
        await c.CheckAsync(download: true).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(hits, srv.LatestHits);                 // no check ran
        Assert.Equal(UpdatePhase.Installing, c.State.Phase); // not flipped back to Ready
        inst.Release.SetResult();
        await install;                                      // BlockingInstaller asserts the update was not dropped
        Assert.Equal("v0.7.1", inst.Last!.Tag);
    }

    [Fact]
    public async Task H1_InstallWaitsForRunningCheck()
    {
        var srv = new FakeServer();
        var inst = new FakeInstaller();
        var c = Make(srv, inst);
        await c.CheckAsync(download: true);                 // v0.7.1 ready
        srv.Tag = "v0.7.2";
        srv.Gate = new TaskCompletionSource();
        var check = c.CheckAsync(download: true);           // finds v0.7.2, held in the download
        await Task.Delay(100);
        var install = c.InstallStagedAsync();               // must wait instead of racing the check
        await Task.Delay(100);
        Assert.Equal(0, inst.Calls);
        srv.Gate.SetResult();
        await Task.WhenAll(check, install).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, inst.Calls);
        Assert.Equal("v0.7.2", inst.Last!.Tag);
    }

    // ---- M1, M2 ----------------------------------------------------------------------------------------------------

    [Fact]
    public void M1_BalloonPolicyOnlyUpdateBalloonInstalls()
    {
        Assert.True(BalloonPolicy.InstallOnClick(BalloonKind.UpdateReady, updateReady: true));
        Assert.False(BalloonPolicy.InstallOnClick(BalloonKind.UpdateReady, updateReady: false));
        Assert.False(BalloonPolicy.InstallOnClick(BalloonKind.Other, updateReady: true));
        Assert.False(BalloonPolicy.InstallOnClick(BalloonKind.None, updateReady: true));
    }

    [Fact]
    public async Task M2_NoEventsAfterDispose()
    {
        var srv = new FakeServer { PerChunkDelay = TimeSpan.FromMilliseconds(30) };
        var c = Make(srv, new FakeInstaller());
        int after = 0, logsAfter = 0;
        bool disposed = false;
        var downloading = new TaskCompletionSource();
        c.Changed += s => { if (Volatile.Read(ref disposed)) Interlocked.Increment(ref after); if (s.Phase == UpdatePhase.Downloading && s.Progress.Received > 0) downloading.TrySetResult(); };
        c.ReadyInBackground += _ => { if (Volatile.Read(ref disposed)) Interlocked.Increment(ref after); };
        c.Log = _ => { if (Volatile.Read(ref disposed)) Interlocked.Increment(ref logsAfter); };
        var run = c.CheckAsync(download: true);
        await downloading.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Volatile.Write(ref disposed, true);
        c.Dispose();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, after);
        Assert.Equal(0, logsAfter);
        Assert.Null(c.Staged);
        await c.CheckAsync(true); // no-op after dispose
        Assert.Equal(1, srv.LatestHits);
    }

    // ---- M4, M5 ----------------------------------------------------------------------------------------------------

    [Fact]
    public void M4_CleanOldStagingKeepsReadyAndRemovesOld()
    {
        var keep = SecureFiles.CreatePrivateDirectory(Staging, "update-");
        var old1 = SecureFiles.CreatePrivateDirectory(Staging, "update-");
        var old2 = SecureFiles.CreatePrivateDirectory(Staging, "run-");
        File.WriteAllText(Path.Combine(old1, "CordMic-Setup.exe"), "x");
        int removed = UpdateEngine.CleanOldStaging(Staging, keep, DateTime.UtcNow.AddHours(2));
        Assert.Equal(2, removed);
        Assert.True(Directory.Exists(keep));
        Assert.False(Directory.Exists(old1));
        Assert.False(Directory.Exists(old2));
        // fresh folders (a child may still be using them) stay
        var fresh = SecureFiles.CreatePrivateDirectory(Staging, "update-");
        Assert.Equal(0, UpdateEngine.CleanOldStaging(Staging, keep));
        Assert.True(Directory.Exists(fresh));
    }

    [Fact]
    public void M4_PortableBackupsAreCleaned()
    {
        var dir = Path.Combine(_root, "portable");
        var bak = Path.Combine(dir, PortableSwap.BackupPrefix + "abc123");
        Directory.CreateDirectory(bak);
        File.WriteAllText(Path.Combine(bak, "CordMic.exe"), "old");
        File.WriteAllText(Path.Combine(dir, "CordMic.exe"), "current");
        Assert.Equal(0, PortableSwap.CleanBackups(dir));                         // too new
        Assert.Equal(1, PortableSwap.CleanBackups(dir, DateTime.UtcNow.AddHours(2)));
        Assert.False(Directory.Exists(bak));
        Assert.True(File.Exists(Path.Combine(dir, "CordMic.exe")));
    }

    [Fact]
    public void M5_FutureLastCheckIsDue()
    {
        var now = new DateTime(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc);
        var six = TimeSpan.FromHours(6);
        Assert.True(UpdateSchedule.IsDue(0, now, six));
        Assert.False(UpdateSchedule.IsDue(now.AddHours(-1).Ticks, now, six));
        Assert.True(UpdateSchedule.IsDue(now.AddHours(-7).Ticks, now, six));
        Assert.False(UpdateSchedule.IsDue(now.AddMinutes(30).Ticks, now, six)); // small skew: not due
        Assert.True(UpdateSchedule.IsDue(now.AddYears(70).Ticks, now, six));    // saved while the clock was far ahead
        Assert.True(UpdateSchedule.IsDue(long.MaxValue, now, six));
    }

    [Fact]
    public void M5_ScheduleUsesMonotonicTicks()
    {
        long ms = 1_000;
        var g = new PeriodicGate(TimeSpan.FromMinutes(30), () => ms);
        Assert.True(g.TryEnter());
        ms += 29 * 60_000;
        Assert.False(g.TryEnter());
        ms += 60_000;
        Assert.True(g.TryEnter());
        Assert.False(g.TryEnter());
        // the wall clock is not involved: a long run of ticks keeps working
        ms = long.MaxValue / 2;
        Assert.True(g.TryEnter());
    }

    [Fact]
    public void M5_FutureCreationTimeCountsAsOld()
    {
        var now = DateTime.UtcNow;
        Assert.False(UpdateEngine.IsStale(now.AddMinutes(-30), now));
        Assert.True(UpdateEngine.IsStale(now.AddHours(-2), now));
        Assert.False(UpdateEngine.IsStale(now.AddHours(5), now));
        Assert.True(UpdateEngine.IsStale(now.AddDays(3), now));
    }

    // ---- M6 ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void M6_HelperArgsRoundTripWithStartTime()
    {
        var a = new PortableUpdateArgs(Path.Combine(_root, "Cord Mic"), 4242, new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc).Ticks);
        Assert.True(PortableUpdateArgs.TryParse(a.ToArgs(), out var b));
        Assert.Equal(a, b);
        Assert.False(PortableUpdateArgs.TryParse(new[] { PortableUpdateArgs.Verb, a.TargetDir, "--started", "-5" }, out _));
        Assert.False(PortableUpdateArgs.TryParse(new[] { PortableUpdateArgs.Verb, a.TargetDir, "--pid", "12x" }, out _));
    }

    [Fact]
    public void M6_ArgsFrom070AreAccepted()
    {
        var target = Path.Combine(_root, "app");
        Assert.True(PortableUpdateArgs.TryParse(new[] { PortableUpdateArgs.Verb, target, "--pid", "77" }, out var a));
        Assert.Equal(77, a!.WaitPid);
        Assert.Equal(0, a.StartedUtcTicks);
        Assert.True(PortableUpdateArgs.IsSameProcess(0, DateTime.UtcNow)); // unknown start time: wait as 0.7.0 did
    }

    [Fact]
    public void M6_SameProcessCheck()
    {
        var t = new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);
        Assert.True(PortableUpdateArgs.IsSameProcess(t.Ticks, t.AddMilliseconds(400)));
        Assert.False(PortableUpdateArgs.IsSameProcess(t.Ticks, t.AddSeconds(30)));   // reused PID, other process
        Assert.True(PortableUpdateArgs.IsSameProcess(t.Ticks, t.ToLocalTime()));      // local vs UTC
    }

    // ---- M7 ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(96, 96, 10.5f)]
    [InlineData(120, 96, 13.125f)]
    [InlineData(144, 96, 15.75f)]
    [InlineData(168, 96, 18.375f)]
    [InlineData(192, 96, 21f)]
    [InlineData(96, 144, 7f)]
    [InlineData(144, 144, 10.5f)]
    [InlineData(192, 144, 14f)]
    public void M7_FontScaleForDpi(int windowDpi, int systemDpi, float expected)
    {
        Assert.Equal(expected, FontScale.Points(10.5f, windowDpi, systemDpi), 0.001f);
        Assert.Equal(10.5f, FontScale.Points(10.5f, 0, systemDpi));
    }

    private sealed class FakeFonts : IDisposable
    {
        public readonly int Dpi;
        public bool Disposed;
        public FakeFonts(int dpi) => Dpi = dpi;
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void M7_FontSetDisposesOldFonts()
    {
        var made = new List<FakeFonts>();
        var r = new DpiResources<FakeFonts>(96, d => { var f = new FakeFonts(d); made.Add(f); return f; });
        Assert.False(r.Switch(96, _ => throw new InvalidOperationException("no switch at the same DPI")));
        FakeFonts? seenOld = null;
        Assert.True(r.Switch(144, now =>
        {
            seenOld = made[0];
            Assert.False(seenOld.Disposed, "old fonts stay alive while controls switch");
            Assert.Equal(144, now.Dpi);
        }));
        Assert.True(seenOld!.Disposed);
        Assert.Equal(144, r.Current.Dpi);
        r.Switch(192, _ => { });
        r.Dispose();
        Assert.All(made, f => Assert.True(f.Disposed));
        Assert.Equal(3, made.Count);
    }

    // ---- M8: ready update survives a restart -----------------------------------------------------------------------

    private async Task<UpdateCoordinator> ReadyThenRestart(FakeServer srv, FakeInstaller inst, Action? between = null, string current = "0.7.0", InstallKind kind = InstallKind.Installed)
    {
        var c1 = Make(srv, new FakeInstaller(), kind);
        await c1.CheckAsync(download: true);
        Assert.Equal(UpdatePhase.Ready, c1.State.Phase);
        c1.Dispose(); // app quits with the update ready
        between?.Invoke();
        return Make(srv, inst, kind, current);
    }

    [Fact]
    public async Task M8_ReadyUpdateIsRestoredAfterRestart()
    {
        var srv = new FakeServer();
        var inst = new FakeInstaller();
        var c2 = await ReadyThenRestart(srv, inst);
        var phases = new List<UpdatePhase>();
        c2.Changed += s => { lock (phases) phases.Add(s.Phase); };
        await c2.StartupAsync(autoDownload: true, checkDue: false);
        Assert.Equal(UpdatePhase.Ready, c2.State.Phase);
        Assert.Equal("v0.7.1", c2.State.Tag);
        Assert.Equal(1, srv.AssetHits);   // not downloaded again
        Assert.Equal(1, srv.LatestHits);  // no network needed to offer it
        if (OperatingSystem.IsWindows()) Assert.ThrowsAny<IOException>(() => new FileStream(c2.Staged!.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
        await c2.UpdateNowAsync();
        Assert.Equal(1, inst.Calls);
        Assert.Equal("v0.7.1", inst.Last!.Tag);
        Assert.Equal(1, srv.AssetHits);
    }

    [Fact]
    public async Task M8_ReadyUpdateIsRestoredForPortableToo()
    {
        var srv = new FakeServer();
        var inst = new FakeInstaller();
        var c2 = await ReadyThenRestart(srv, inst, kind: InstallKind.Portable);
        await c2.StartupAsync(autoDownload: true, checkDue: true); // due: checks, finds the same tag, stays ready
        Assert.Equal(UpdatePhase.Ready, c2.State.Phase);
        Assert.EndsWith(srv.PortableName, c2.Staged!.Path);
        Assert.Equal(1, srv.AssetHits);
        Assert.Equal(2, srv.LatestHits);
    }

    [Fact]
    public async Task M8_TamperedReadyUpdateIsDiscardedAndRedownloaded()
    {
        var srv = new FakeServer();
        var inst = new FakeInstaller();
        var c2 = await ReadyThenRestart(srv, inst, between: () =>
        {
            var file = Directory.GetFiles(Staging, "CordMic-Setup-*.exe", SearchOption.AllDirectories).Single();
            var b = File.ReadAllBytes(file); b[100] ^= 0xFF; File.WriteAllBytes(file, b);
        });
        await c2.StartupAsync(autoDownload: true, checkDue: false); // not due, but unusable -> download now
        Assert.Equal(UpdatePhase.Ready, c2.State.Phase);
        Assert.Equal(2, srv.AssetHits);
        Assert.Single(Directory.GetDirectories(Staging));            // the tampered folder is gone
        Assert.Equal(Release070Tests.Hex(srv.Setup), c2.Staged!.Sha256);
    }

    [Fact]
    public async Task M8_UnusableWithAutoDownloadOffDoesNotDownload()
    {
        var srv = new FakeServer();
        var c2 = await ReadyThenRestart(srv, new FakeInstaller(), between: () =>
            File.Delete(Directory.GetFiles(Staging, "CordMic-Setup-*.exe", SearchOption.AllDirectories).Single()));
        await c2.StartupAsync(autoDownload: false, checkDue: false);
        Assert.Equal(UpdatePhase.Idle, c2.State.Phase);
        Assert.Equal(1, srv.AssetHits);
        Assert.Empty(Directory.GetDirectories(Staging));
    }

    [Fact]
    public async Task M8_OlderReadyUpdateIsDiscarded()
    {
        var srv = new FakeServer();
        var c2 = await ReadyThenRestart(srv, new FakeInstaller(), current: "0.7.1"); // that update is now installed
        await c2.StartupAsync(autoDownload: true, checkDue: false);
        Assert.Equal(UpdatePhase.Idle, c2.State.Phase);
        Assert.Null(c2.Staged);
        Assert.Equal(1, srv.AssetHits);
        Assert.Empty(Directory.GetDirectories(Staging));
    }

    [Fact]
    public async Task M8_NonPrivateFolderIsRefused()
    {
        if (OperatingSystem.IsWindows()) return; // the Windows ACL variant needs a second account; covered by review
        var srv = new FakeServer();
        var c2 = await ReadyThenRestart(srv, new FakeInstaller(), between: () =>
        {
            var d = Directory.GetDirectories(Staging).Single();
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute | UnixFileMode.OtherWrite);
        });
        await c2.StartupAsync(autoDownload: true, checkDue: false);
        Assert.Equal(2, srv.AssetHits); // refused and downloaded again into a new private folder
        Assert.True(SecureFiles.IsPrivateDirectory(c2.Staged!.Dir));
    }

    [Fact]
    public void M8_ReadyManifestRoundTrip()
    {
        var d = SecureFiles.CreatePrivateDirectory(Staging, "update-");
        var sha = new string('a', 64);
        new ReadyManifest("v0.7.1", InstallKind.Portable, "CordMic-Portable-0.7.1.zip", sha).Write(d);
        var m = ReadyManifest.TryRead(Path.Combine(d, ReadyManifest.FileName));
        Assert.Equal(new ReadyManifest("v0.7.1", InstallKind.Portable, "CordMic-Portable-0.7.1.zip", sha), m);
        foreach (var bad in new[]
        {
            "{\"Tag\":\"v0.7.1\",\"Kind\":0,\"File\":\"..\\/x.exe\",\"Sha256\":\"" + sha + "\"}",
            "{\"Tag\":\"v0.7.1\",\"Kind\":0,\"File\":\"x.exe\",\"Sha256\":\"zz\"}",
            "{\"Tag\":\"nope\",\"Kind\":0,\"File\":\"x.exe\",\"Sha256\":\"" + sha + "\"}",
            "{\"Tag\":\"v0.7.1\",\"Kind\":7,\"File\":\"x.exe\",\"Sha256\":\"" + sha + "\"}",
            "not json",
        })
        {
            var p = Path.Combine(_root, "m.json");
            File.WriteAllText(p, bad);
            Assert.Null(ReadyManifest.TryRead(p));
        }
    }

    // ---- M9, M10 -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task M9_InstallWatchdogFails()
    {
        var srv = new FakeServer();
        var c = Make(srv, new FakeInstaller(), watchdog: TimeSpan.FromMilliseconds(200));
        var failed = new TaskCompletionSource();
        c.Changed += s => { if (s.Phase == UpdatePhase.Failed) failed.TrySetResult(); };
        await c.UpdateNowAsync();
        Assert.Equal(UpdatePhase.Installing, c.State.Phase);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(UpdateError.InstallFailed, c.State.Error);
        Assert.StartsWith("https://github.com/", c.State.ReleasePage);
        int hits = srv.LatestHits;
        await c.CheckAsync(download: false);
        Assert.Equal(hits + 1, srv.LatestHits); // checks work again
    }

    [Fact]
    public void M10_EachUnpackAttemptGetsAFreshFolder()
    {
        var d = SecureFiles.CreatePrivateDirectory(Staging, "update-");
        var a = PortableSwap.NewUnpackDir(d);
        Directory.CreateDirectory(a);
        File.WriteAllText(Path.Combine(a, "CordMic.exe"), "partial");
        var b = PortableSwap.NewUnpackDir(d);
        Assert.NotEqual(a, b);
        Assert.False(Directory.Exists(b));
        Assert.StartsWith(d, b);
    }

    // ---- Low -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task L1_LastCheckIsSetUnderLockAndReadable()
    {
        var srv = new FakeServer();
        var c = Make(srv, new FakeInstaller());
        Assert.Null(c.LastCheckUtc);
        var reads = Task.Run(() => { for (int i = 0; i < 20000; i++) _ = c.LastCheckUtc; });
        await c.CheckAsync(download: false);
        await reads;
        Assert.NotNull(c.LastCheckUtc);
        Assert.True(DateTime.UtcNow - c.LastCheckUtc!.Value < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void L2_CoalescerPostsOnce()
    {
        var queue = new List<Action>();
        int work = 0;
        var c = new PostCoalescer(a => queue.Add(a), () => work++);
        for (int i = 0; i < 300; i++) c.Request();
        Assert.Single(queue);
        queue[0]();
        Assert.Equal(1, work);
        c.Request(); // a request after the work ran schedules one more
        Assert.Equal(2, queue.Count);
        queue[1]();
        Assert.Equal(2, work);
    }

    [Fact]
    public void L4_ExtractStopsAtRealByteLimit()
    {
        byte[] zip;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                using (var w = z.CreateEntry(PortableSwap.ExeName).Open()) w.Write(new byte[3000]);
                using (var w = z.CreateEntry("big.dll").Open()) w.Write(new byte[10_000]);
            }
            zip = ms.ToArray();
        }
        var p = Path.Combine(_root, "u.zip");
        File.WriteAllBytes(p, zip);
        using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read);
        var e = Assert.Throws<UpdateException>(() => PortableSwap.ExtractVerified(fs, Path.Combine(_root, "out"), maxTotalBytes: 8000));
        Assert.Equal(UpdateError.TooLarge, e.Kind);
        using var fs2 = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Equal(2, PortableSwap.ExtractVerified(fs2, Path.Combine(_root, "out2"), maxTotalBytes: 20_000));
    }

    [Fact]
    public void L6_MigrationReadsLogOpenForWriting()
    {
        var log = Path.Combine(_root, "cordmic.log");
        using var writer = new FileStream(log, FileMode.Create, FileAccess.Write, FileShare.Read);
        var text = "2026-11-13 10:00:00.000 CordMic 0.6.2+abc start, exe=x\n2026-11-13 10:01:00.000 auto update -> off\n";
        writer.Write(Encoding.UTF8.GetBytes(text));
        writer.Flush();
        Assert.True(AutoUpdateMigration.ExplicitOffSince061(log)); // the writer still has the file open
    }

    [Fact]
    public void L7_MissingVersionMeansSchema1()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, "{ \"AutoUpdate\": false, \"Language\": \"id\" }");
        var s = AppSettings.Load(path);
        Assert.Equal(1, s.Version);
        Assert.NotNull(s.MigrateSchema(() => false));
        Assert.True(s.AutoUpdate);
        Assert.Equal("id", s.Language);
        // a current file keeps its schema
        s.Save(path);
        Assert.Equal(AppSettings.CurrentSchema, AppSettings.Load(path).Version);
    }
}
