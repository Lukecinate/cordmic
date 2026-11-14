using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using CordMic.Core;
using CordMic.Core.Dsp;
using CordMic.Core.Updates;

/// <summary>0.7.0: one-click updater (fake release server), portable self-update, settings migration, Settings layout.</summary>
public class Release070Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cordmic070-" + Guid.NewGuid().ToString("N"));
    public Release070Tests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (Exception) { } }

    // ---- fake GitHub release server ----------------------------------------------------------------------------

    private sealed class FakeServer : HttpMessageHandler
    {
        public readonly Dictionary<string, byte[]> Files = new();
        public string Tag = "v0.7.1";
        public int LatestHits, AssetHits;
        public TimeSpan PerChunkDelay = TimeSpan.Zero;
        public TaskCompletionSource? Gate;            // holds the asset download until released
        public bool OmitPortable;
        public string? SumsOverride;

        public byte[] Setup = RandomBytes(1_000_000, 1);
        public byte[] Portable = MakeZip(("CordMic.exe", "new exe"), ("rnnoise.dll", "new dll"), ("portable.txt", "portable"));

        public string SetupName => AppUpdate.SetupAssetName(Tag);
        public string PortableName => AppUpdate.PortableAssetName(Tag);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/latest"))
            {
                Interlocked.Increment(ref LatestHits);
                var assets = new List<string>
                {
                    Asset(SetupName, Setup.Length), Asset("SHA256SUMS", 10), Asset("CordMic-0.7.1-release.apk", 5),
                };
                if (!OmitPortable) assets.Add(Asset(PortableName, Portable.Length));
                var json = $"{{\"tag_name\":\"{Tag}\",\"html_url\":\"https://github.com/Lukecinate/cordmic/releases/tag/{Tag}\",\"assets\":[{string.Join(",", assets)}]}}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
            }
            var name = path[(path.LastIndexOf('/') + 1)..];
            if (name == "SHA256SUMS")
            {
                var sums = SumsOverride ?? $"{Hex(Setup)}  {SetupName}\n{Hex(Portable)}  {PortableName}\n";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sums) };
            }
            byte[]? body = name == SetupName ? Setup : name == PortableName ? Portable : null;
            if (body is null) return new HttpResponseMessage(HttpStatusCode.NotFound);
            Interlocked.Increment(ref AssetHits);
            if (Gate is { } g) await g.Task.WaitAsync(ct);
            var content = new StreamContent(new SlowStream(body, PerChunkDelay));
            content.Headers.ContentLength = body.Length;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        private static string Asset(string name, long size) =>
            $"{{\"name\":\"{name}\",\"size\":{size},\"browser_download_url\":\"https://example.test/dl/{name}\"}}";
    }

    private sealed class SlowStream : MemoryStream
    {
        private readonly TimeSpan _d;
        public SlowStream(byte[] b, TimeSpan d) : base(b) => _d = d;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_d > TimeSpan.Zero) await Task.Delay(_d, ct);
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 64 * 1024)], ct);
        }
    }

    private sealed class FakeInstaller : IUpdateInstaller
    {
        public int Calls;
        public UpdateError? Throw;
        public StagedUpdate? Last;
        public Task InstallAsync(StagedUpdate staged, CancellationToken ct)
        {
            Calls++;
            if (Throw is { } k) throw new UpdateException(k, "fake");
            Last = staged;
            Assert.True(File.Exists(staged.Path));
            staged.ReleaseForChild();
            return Task.CompletedTask;
        }
    }

    private static byte[] RandomBytes(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
    private static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static byte[] MakeZip(params (string name, string text)[] files)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (n, t) in files) { using var w = new StreamWriter(z.CreateEntry(n).Open()); w.Write(t); }
        return ms.ToArray();
    }

    private (UpdateCoordinator c, FakeServer srv, FakeInstaller inst) Make(InstallKind kind = InstallKind.Installed, Func<bool>? canApply = null, string current = "0.7.0")
    {
        var srv = new FakeServer();
        var engine = new UpdateEngine(UpdateEngine.CreateHttpClient(srv), current, Path.Combine(_root, "staging"), "https://api.example.test/repos/x/y/releases/latest");
        var inst = new FakeInstaller();
        return (new UpdateCoordinator(engine, kind, inst, canApply), srv, inst);
    }

    // ---- updater ----------------------------------------------------------------------------------------------

    [Fact]
    public void ReleaseJson_PicksVersionedAssetsAndPage()
    {
        var json = "{\"tag_name\":\"v0.7.0\",\"html_url\":\"https://github.com/Lukecinate/cordmic/releases/tag/v0.7.0\",\"assets\":[" +
            "{\"name\":\"CordMic-Setup-0.6.9.exe\",\"size\":1,\"browser_download_url\":\"u0\"}," +
            "{\"name\":\"CordMic-Setup-0.7.0.exe\",\"size\":2,\"browser_download_url\":\"u1\"}," +
            "{\"name\":\"CordMic-Portable-0.7.0.zip\",\"size\":3,\"browser_download_url\":\"u2\"}," +
            "{\"name\":\"SHA256SUMS\",\"size\":4,\"browser_download_url\":\"u3\"}]}";
        var r = AppUpdate.ParseLatestJson(json);
        Assert.Equal("CordMic-Setup-0.7.0.exe", r.SetupName);
        Assert.Equal("CordMic-Portable-0.7.0.zip", r.PortableName);
        Assert.Equal("u2", r.PortableUrl);
        Assert.Equal(3, r.AssetSizes!["CordMic-Portable-0.7.0.zip"]);
        Assert.StartsWith("https://github.com/Lukecinate/cordmic/releases/", r.HtmlUrl);
        Assert.Equal("CordMic-Portable-0.7.0.zip", AppUpdate.PortableAssetName("v0.7.0"));
        // a non-GitHub page link is replaced by the known release page
        var bad = AppUpdate.ParseLatestJson("{\"tag_name\":\"v1.0.0\",\"html_url\":\"https://evil.example/x\",\"assets\":[]}");
        Assert.Equal(AppUpdate.ReleasesPageUrl, bad.HtmlUrl);
    }

    [Fact]
    public async Task UpdateNow_DownloadsVerifiesInstallsWithProgress()
    {
        var (c, srv, inst) = Make();
        var phases = new List<UpdatePhase>();
        var progress = new List<DownloadProgress>();
        c.Changed += s => { lock (phases) { phases.Add(s.Phase); if (s.Phase == UpdatePhase.Downloading) progress.Add(s.Progress); } };
        await c.UpdateNowAsync();
        Assert.Equal(1, inst.Calls);
        Assert.Equal(InstallKind.Installed, inst.Last!.Kind);
        Assert.EndsWith("CordMic-Setup-0.7.1.exe", inst.Last.Path);
        Assert.Contains(UpdatePhase.Checking, phases);
        Assert.Contains(UpdatePhase.Ready, phases);
        Assert.Equal(UpdatePhase.Installing, phases[^1]);
        Assert.True(progress.Count >= 3, "progress reports: " + progress.Count);
        Assert.Equal(srv.Setup.Length, progress[^1].Received);
        Assert.Equal(srv.Setup.Length, progress[^1].Total);
        Assert.True(progress.Zip(progress.Skip(1)).All(p => p.Second.Received >= p.First.Received), "monotonic");
        Assert.Null(c.Staged); // handed to the installer
    }

    [Fact]
    public async Task BackgroundCheck_StopsAtReadyAndNeverInstalls()
    {
        var (c, _, inst) = Make();
        string? ready = null;
        c.ReadyInBackground += t => ready = t;
        await c.CheckAsync(download: true);
        Assert.Equal(UpdatePhase.Ready, c.State.Phase);
        Assert.True(c.State.ReadyToInstall);
        Assert.Equal("v0.7.1", ready);
        Assert.Equal(0, inst.Calls);
        Assert.NotNull(c.Staged);
        // the staged file is locked against writers and deleters (share modes are enforced on Windows only)
        if (OperatingSystem.IsWindows()) Assert.ThrowsAny<IOException>(() => new FileStream(c.Staged!.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
        await c.UpdateNowAsync(); // uses the staged file, no second download
        Assert.Equal(1, inst.Calls);
    }

    [Fact]
    public async Task ConcurrentChecks_ShareOneRequestAndOneDownload()
    {
        var (c, srv, _) = Make();
        srv.Gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 6).Select(_ => c.CheckAsync(download: true)).ToList();
        await Task.Delay(200);
        srv.Gate.SetResult();
        await Task.WhenAll(tasks);
        Assert.Equal(1, srv.LatestHits);
        Assert.Equal(1, srv.AssetHits);
        Assert.Single(tasks.Distinct());
        Assert.Equal(UpdatePhase.Ready, c.State.Phase);
        // a check after the update is staged reuses it
        await c.CheckAsync(download: true);
        Assert.Equal(1, srv.AssetHits);
    }

    [Fact]
    public async Task Cancel_StopsTheDownloadAndCleansStaging()
    {
        var (c, srv, inst) = Make();
        srv.PerChunkDelay = TimeSpan.FromMilliseconds(40);
        var started = new TaskCompletionSource();
        c.Changed += s => { if (s.Phase == UpdatePhase.Downloading && s.Progress.Received > 0) started.TrySetResult(); };
        var run = c.UpdateNowAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        c.Cancel();
        await run;
        Assert.True(c.State.Cancelled);
        Assert.Equal(UpdatePhase.Available, c.State.Phase);
        Assert.Equal(0, inst.Calls);
        Assert.Null(c.Staged);
        var staging = Path.Combine(_root, "staging");
        Assert.True(!Directory.Exists(staging) || Directory.GetDirectories(staging).Length == 0, "staging cleaned");
    }

    [Fact]
    public async Task HashMismatch_FailsDeletesAndOffersTheDownloadPage()
    {
        var (c, srv, inst) = Make();
        srv.SumsOverride = $"{new string('0', 64)}  {srv.SetupName}\n";
        await c.UpdateNowAsync();
        Assert.Equal(UpdatePhase.Failed, c.State.Phase);
        Assert.Equal(UpdateError.HashMismatch, c.State.Error);
        Assert.StartsWith("https://github.com/", c.State.ReleasePage);
        Assert.Equal(0, inst.Calls);
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "staging")));
    }

    [Fact]
    public async Task UpToDate_AndNoDowngrade()
    {
        var (c, srv, inst) = Make(current: "0.7.1");
        await c.UpdateNowAsync();
        Assert.Equal(UpdatePhase.UpToDate, c.State.Phase);
        srv.Tag = "v0.6.9";
        await c.CheckAsync(true);
        Assert.Equal(UpdatePhase.UpToDate, c.State.Phase);
        Assert.Equal(0, inst.Calls);
        Assert.Equal(0, srv.AssetHits);
    }

    [Fact]
    public async Task DeclinedPermission_KeepsTheUpdateReady()
    {
        var (c, _, inst) = Make();
        inst.Throw = UpdateError.Declined;
        await c.UpdateNowAsync();
        Assert.Equal(UpdatePhase.Ready, c.State.Phase);
        Assert.Equal(UpdateError.Declined, c.State.Error);
        Assert.NotNull(c.Staged);
        inst.Throw = null;
        await c.InstallStagedAsync();
        Assert.Equal(2, inst.Calls);
    }

    [Fact]
    public async Task Portable_DownloadsTheZip_OrFallsBackWhenFolderIsReadOnly()
    {
        var (c, srv, inst) = Make(InstallKind.Portable);
        await c.UpdateNowAsync();
        Assert.Equal(1, inst.Calls);
        Assert.EndsWith(srv.PortableName, inst.Last!.Path);

        var (ro, srv2, inst2) = Make(InstallKind.Portable, canApply: () => false);
        await ro.UpdateNowAsync();
        Assert.Equal(UpdatePhase.Failed, ro.State.Phase);
        Assert.Equal(UpdateError.NotWritable, ro.State.Error);
        Assert.Equal(0, srv2.AssetHits);
        Assert.Equal(0, inst2.Calls);

        var (na, srv3, _) = Make(InstallKind.Portable);
        srv3.OmitPortable = true;
        await na.UpdateNowAsync();
        Assert.Equal(UpdateError.NoAsset, na.State.Error);
    }

    // ---- portable detection and swap --------------------------------------------------------------------------

    [Fact]
    public void InstallMode_DetectsPortableAndInstalled()
    {
        var dir = Path.Combine(_root, "app");
        Directory.CreateDirectory(dir);
        bool Exists(string p) => File.Exists(p);
        Assert.Equal(InstallKind.Portable, InstallMode.Detect(dir, Exists, null));                 // 0.6.x zip: no uninstaller
        File.WriteAllText(Path.Combine(dir, InstallMode.Uninstaller), "");
        Assert.Equal(InstallKind.Installed, InstallMode.Detect(dir, Exists, null));
        Assert.Equal(InstallKind.Installed, InstallMode.Detect(_root, Exists, _root + Path.DirectorySeparatorChar)); // registry match
        File.WriteAllText(Path.Combine(dir, InstallMode.PortableMarker), "");
        Assert.Equal(InstallKind.Portable, InstallMode.Detect(dir, Exists, dir));                  // marker wins
        Assert.True(InstallMode.IsWritable(dir));
        Assert.False(InstallMode.IsWritable(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void PortableArgs_RoundTrip()
    {
        var target = Path.Combine(_root, "Cord Mic");
        var a = new PortableUpdateArgs(target, 4242);
        Assert.True(PortableUpdateArgs.TryParse(a.ToArgs(), out var b));
        Assert.Equal(a, b);
        Assert.False(PortableUpdateArgs.TryParse(new[] { PortableUpdateArgs.Verb, "relative\\dir" }, out _));
        Assert.False(PortableUpdateArgs.TryParse(new[] { PortableUpdateArgs.Verb }, out _));
        Assert.True(PortableUpdateArgs.Mentions(a.ToArgs()));
    }

    private string MakePortableDir(string name, params (string rel, string text)[] files)
    {
        var d = Path.Combine(_root, name);
        foreach (var (rel, text) in files)
        {
            var p = Path.Combine(d, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }
        return d;
    }

    private static Dictionary<string, string> Snapshot(string dir) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(dir, f), File.ReadAllText);

    [Fact]
    public void PortableSwap_ReplacesFilesAndKeepsOthers()
    {
        var target = MakePortableDir("target", ("CordMic.exe", "old exe"), ("rnnoise.dll", "old dll"), ("user-notes.txt", "mine"));
        var src = MakePortableDir("src", ("CordMic.exe", "new exe"), ("rnnoise.dll", "new dll"), ("tools/new.ps1", "new tool"));
        var r = PortableSwap.Apply(src, target, retryDelay: TimeSpan.Zero);
        Assert.Equal(SwapOutcome.Updated, r.Outcome);
        var snap = Snapshot(target);
        Assert.Equal("new exe", snap["CordMic.exe"]);
        Assert.Equal("new dll", snap["rnnoise.dll"]);
        Assert.Equal("new tool", snap[Path.Combine("tools", "new.ps1")]);
        Assert.Equal("mine", snap["user-notes.txt"]);
        Assert.DoesNotContain(Directory.GetDirectories(target), d => Path.GetFileName(d).StartsWith(PortableSwap.BackupPrefix));
    }

    [Fact]
    public void PortableSwap_RollsBackOnFailure()
    {
        var target = MakePortableDir("target", ("CordMic.exe", "old exe"), ("a.dll", "old a"), ("b.dll", "old b"));
        var before = Snapshot(target);
        var src = MakePortableDir("src", ("CordMic.exe", "new exe"), ("a.dll", "new a"), ("b.dll", "new b"), ("c.dll", "new c"));
        int n = 0;
        var log = new List<string>();
        var r = PortableSwap.Apply(src, target, log.Add, beforeFile: _ => { if (++n == 3) throw new IOException("disk full (test)"); }, retryDelay: TimeSpan.Zero);
        Assert.Equal(SwapOutcome.RolledBack, r.Outcome);
        Assert.Contains("disk full", r.Error);
        Assert.Equal(before, Snapshot(target));   // exactly the old files, no leftovers, no backup folder
        Assert.Contains(log, l => l.Contains("restoring"));
    }

    [Fact]
    public void PortableExtract_UsesTheVerifiedHandleAndRejectsUnsafePaths()
    {
        var zip = Path.Combine(_root, "u.zip");
        File.WriteAllBytes(zip, MakeZip(("CordMic.exe", "x"), ("tools/a.ps1", "y")));
        using (var h = SecureFiles.OpenLockedVerified(zip, Hex(File.ReadAllBytes(zip))))
        {
            Assert.Equal(2, PortableSwap.ExtractVerified(h, Path.Combine(_root, "out")));
        }
        Assert.Equal("y", File.ReadAllText(Path.Combine(_root, "out", "tools", "a.ps1")));

        var evil = Path.Combine(_root, "evil.zip");
        File.WriteAllBytes(evil, MakeZip(("CordMic.exe", "x"), ("../escape.txt", "boom")));
        using var h2 = SecureFiles.OpenLocked(evil);
        Assert.Throws<UpdateException>(() => PortableSwap.ExtractVerified(h2, Path.Combine(_root, "out2")));
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));

        var noExe = Path.Combine(_root, "noexe.zip");
        File.WriteAllBytes(noExe, MakeZip(("readme.txt", "x")));
        using var h3 = SecureFiles.OpenLocked(noExe);
        Assert.Throws<UpdateException>(() => PortableSwap.ExtractVerified(h3, Path.Combine(_root, "out3")));
    }

    // ---- settings migration -----------------------------------------------------------------------------------

    private string WriteSettings(string json)
    {
        var p = Path.Combine(_root, "settings-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(p, json);
        return p;
    }

    [Fact]
    public void Migration_KeepsEveryValueAndTurnsAutoUpdateOn()
    {
        var old = "{\"Version\":1,\"Port\":50005,\"Jitter\":\"Safe\",\"OutputDeviceId\":\"{dev}\",\"OutputDeviceName\":\"CABLE Input\",\"FirstRunDone\":true," +
                  "\"NoiseCancellation\":true,\"NoiseMode\":\"Strong\",\"VoiceClarity\":false,\"AutoVolume\":false,\"StudioPreset\":\"Whisper\"," +
                  "\"StudioAgcGainDb\":7.5,\"FrontFocus\":false,\"FrontFocusAudioRecord\":false,\"AutoUpdate\":false,\"LastUpdateCheckUtcTicks\":5," +
                  "\"MicGainDb\":12,\"OutputMode\":\"Exclusive\",\"Language\":\"id\",\"PairingCode\":\"123456\"}";
        var s = AppSettings.Load(WriteSettings(old));
        var note = s.MigrateSchema(() => false);
        Assert.Contains("turned on", note);
        Assert.True(s.AutoUpdate);
        Assert.False(s.InstallOnQuit);
        Assert.Equal(AppSettings.CurrentSchema, s.Version);
        Assert.Equal(JitterPreset.Safe, s.Jitter);
        Assert.Equal("{dev}", s.OutputDeviceId);
        Assert.Equal(NoiseMode.Strong, s.NoiseMode);
        Assert.False(s.VoiceClarity);
        Assert.False(s.AutoVolume);
        Assert.Equal(StudioPreset.Whisper, s.StudioPreset);
        Assert.Equal(7.5, s.StudioAgcGainDb);
        Assert.False(s.FrontFocus);
        Assert.Equal(12, s.MicGainDb);
        Assert.Equal(OutputMode.Exclusive, s.OutputMode);
        Assert.Equal("id", s.Language);
        Assert.Equal("123456", s.PairingCode);
        Assert.Null(s.MigrateSchema(() => false)); // once only
        // round trip keeps the new fields
        var path = Path.Combine(_root, "rt.json");
        s.InstallOnQuit = true;
        s.Save(path);
        var back = AppSettings.Load(path);
        Assert.True(back.AutoUpdate && back.InstallOnQuit);
        Assert.Equal(StudioPreset.Whisper, back.StudioPreset);
    }

    [Fact]
    public void Migration_RespectsAnOffChosenIn061OrLater()
    {
        var s = AppSettings.Load(WriteSettings("{\"Version\":1,\"AutoUpdate\":false}"));
        Assert.Contains("stay off", s.MigrateSchema(() => true));
        Assert.False(s.AutoUpdate);
        var fresh = new AppSettings();
        Assert.True(fresh.AutoUpdate);
        Assert.Null(fresh.MigrateSchema(() => true));
    }

    [Fact]
    public void Migration_ReadsTheLogForAnExplicitOff()
    {
        // The user's 0.5.5 toggles (before 0.6.1) do not count.
        var log055 = new[]
        {
            "2026-11-13 22:41:32.110 CordMic 0.5.5+e5d6381 start, exe=C:\\Program Files\\CordMic\\CordMic.exe",
            "2026-11-13 22:41:57.114 auto update -> on",
            "2026-11-13 22:42:00.678 auto update -> off",
            "2026-11-13 22:42:21.711 CordMic 0.6.1+e6b7c29 start, exe=C:\\Program Files\\CordMic\\CordMic.exe",
        };
        Assert.False(AutoUpdateMigration.ExplicitOffSince061(log055));
        var off062 = log055.Append("2026-11-14 10:00:00.000 CordMic 0.6.2+13f2288 start, exe=x").Append("2026-11-14 10:01:00.000 auto update -> off");
        Assert.True(AutoUpdateMigration.ExplicitOffSince061(off062));
        var onAgain = off062.Append("2026-11-14 10:02:00.000 auto update -> on");
        Assert.False(AutoUpdateMigration.ExplicitOffSince061(onAgain));
        var p1 = Path.Combine(_root, "cordmic.log.1"); var p2 = Path.Combine(_root, "cordmic.log");
        File.WriteAllLines(p1, off062); File.WriteAllLines(p2, new[] { "2026-11-14 11:00:00.000 CordMic 0.7.0+abc start, exe=x" });
        Assert.True(AutoUpdateMigration.ExplicitOffSince061(p1, p2, Path.Combine(_root, "missing.log")));
    }

    [Fact]
    public void Choices_MapOntoExistingSettingsWithoutLoss()
    {
        foreach (var phone in new[] { NoiseMode.Off, NoiseMode.Light, NoiseMode.Strong })
            foreach (var c in SettingsChoices.NoiseChoices)
            {
                var (p, st) = SettingsChoices.Apply(c, phone);
                Assert.Equal(c, SettingsChoices.Noise(p, st));
                if (SettingsChoices.IsStudio(c)) Assert.Equal(phone, p); // the phone fallback is kept
            }
        Assert.Equal(NoiseChoice.StudioMax, SettingsChoices.Noise(NoiseMode.Strong, StudioPreset.Max));
        foreach (var d in SettingsChoices.DelayChoices)
        {
            var (j, m) = SettingsChoices.Apply(d, JitterPreset.Normal, OutputMode.Exclusive);
            Assert.Equal(d, SettingsChoices.Delay(j, m));
        }
        Assert.Equal(DelayChoice.Custom, SettingsChoices.Delay(JitterPreset.Low, OutputMode.Exclusive));
        Assert.Equal((JitterPreset.Low, OutputMode.Exclusive), SettingsChoices.Apply(DelayChoice.Custom, JitterPreset.Low, OutputMode.Exclusive));
        // new installs land on named choices
        var fresh = new AppSettings();
        Assert.Equal(DelayChoice.Balanced, SettingsChoices.Delay(fresh.Jitter, fresh.OutputMode));
        Assert.Equal(NoiseChoice.PhoneLight, SettingsChoices.Noise(fresh.NoiseMode, fresh.StudioPreset));
    }

    // ---- Settings window catalog and layout -------------------------------------------------------------------

    [Fact]
    public void Catalog_HasFivePagesAndEveryStringInBothLanguages()
    {
        Assert.Equal(new[] { SettingsPage.Connection, SettingsPage.Sound, SettingsPage.NoiseVoice, SettingsPage.Updates, SettingsPage.Advanced }, SettingsCatalog.Pages);
        foreach (var k in SettingsCatalog.AllKeys().Distinct())
        {
            Assert.True(L.En.ContainsKey(k), "EN missing " + k);
            Assert.True(L.Id.ContainsKey(k), "ID missing " + k);
        }
        var ids = SettingsCatalog.Pages.SelectMany(SettingsCatalog.Items).Select(i => i.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        // every old setting has a home
        foreach (var id in new[] { "output", "autoVolume", "gain", "delay", "noise", "clarity", "frontFocus", "frontFocusAr", "jitter", "outMode",
                                   "pairing", "startup", "language", "autoUpdate", "installOnQuit", "optimizeCable", "resetAudio", "openLog", "fixRoute" })
            Assert.Contains(id, ids);
        // advanced items are not on the everyday pages
        foreach (var adv in new[] { "jitter", "outMode", "frontFocusAr", "optimizeCable" })
            Assert.DoesNotContain(adv, SettingsCatalog.Items(SettingsPage.Sound).Concat(SettingsCatalog.Items(SettingsPage.NoiseVoice)).Select(i => i.Id));
        foreach (var it in SettingsCatalog.Pages.Where(p => p != SettingsPage.Advanced).SelectMany(SettingsCatalog.Items).Where(i => i.Kind is SettingKind.Toggle or SettingKind.Choice or SettingKind.Radio or SettingKind.Segmented or SettingKind.Slider))
            Assert.False(string.IsNullOrEmpty(it.HelpKey), it.Id + " needs help text");
    }

    [Theory]
    [InlineData(96, 1920, 1040)]
    [InlineData(120, 1920, 1040)]
    [InlineData(144, 1920, 1040)]
    [InlineData(192, 1920, 1040)]
    [InlineData(96, 1366, 728)]
    [InlineData(144, 1366, 728)]
    [InlineData(192, 1366, 728)]
    [InlineData(192, 2560, 1400)]
    public void Layout_FitsTheWorkAreaAndScrollsLongPages(int dpi, int workW, int workH)
    {
        var (w, h) = SettingsLayout.InitialSize(dpi, workW, workH);
        var (mw, mh) = SettingsLayout.MinimumSize(dpi, workW, workH);
        Assert.InRange(w, mw, workW);
        Assert.InRange(h, mh, workH);
        Assert.True(w <= Math.Max(workW * 0.9, mw) + 1 && h <= Math.Max(workH * 0.9, mh) + 1, $"{w}x{h} in {workW}x{workH}");
        if (SettingsLayout.Px(SettingsLayout.DefaultWidth, dpi) <= workW * 0.9) Assert.Equal(SettingsLayout.Px(SettingsLayout.DefaultWidth, dpi), w);
        Assert.True(SettingsLayout.ContentWidth(mw, dpi) >= SettingsLayout.Px(SettingsLayout.MinContentWidth, dpi) - 2 || mw == workW);

        // the longest page (Noise & voice: six options with help) at this DPI: rows never overlap and scroll if needed
        double s = dpi / 96.0;
        int line = (int)Math.Round(20 * s), help = (int)Math.Round(17 * s);
        var rows = new[] { line + 6 * (line + help + (int)(6 * s)) + help, line + help, line + help, 3 * help };
        var (tops, total) = SettingsLayout.Stack(rows, dpi, (int)Math.Round(56 * s));
        for (int i = 1; i < rows.Length; i++) Assert.True(tops[i] >= tops[i - 1] + rows[i - 1] + SettingsLayout.Px(SettingsLayout.RowGap, dpi) - 1);
        int viewport = h - (int)Math.Round(40 * s);
        Assert.Equal(total > viewport, SettingsLayout.NeedsScroll(total, viewport));
        Assert.Equal(total, tops[^1] + rows[^1] + SettingsLayout.Px(SettingsLayout.ContentPadY, dpi));
    }
}
