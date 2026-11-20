using System.Security.Cryptography;
using CordMic.Core;
using CordMic.Core.Dsp;

namespace CordMic.Tests;

/// <summary>0.8.0 M0: 0.7.2 field fixes (orb policy, jitter seed, mic-only read-back, names, protocol extras).</summary>
public class Release080M0Tests
{
    const int Fs = 48000, Fpp = 240;
    const double PktMs = 5;

    // ---- F1 orb / listen policy ----------------------------------------------------------------

    [Fact]
    public void Orb_DebounceIgnoresASecondClickWithin600Ms()
    {
        const long tps = 10_000_000; // 100 ns ticks, like Stopwatch on Windows
        Assert.False(ListenPolicy.IsDebounced(tps, 0, tps));
        Assert.True(ListenPolicy.IsDebounced(tps + tps * 599 / 1000, tps, tps));
        Assert.False(ListenPolicy.IsDebounced(tps + tps * 600 / 1000, tps, tps));
        Assert.False(ListenPolicy.IsDebounced(tps + tps, tps, tps));
        // A backwards clock is treated as too soon, never as "act now".
        Assert.True(ListenPolicy.IsDebounced(tps - 1, tps, tps));
        Assert.False(ListenPolicy.IsDebounced(1, 0, 0)); // bad period: do not debounce forever
    }

    [Fact]
    public void Orb_ClickWhileSearchingShowsGuidanceInsteadOfStopping()
    {
        Assert.Equal(OrbAction.Listen, ListenPolicy.Click(armed: false, searching: false, error: false, streaming: false));
        Assert.Equal(OrbAction.ShowGuidance, ListenPolicy.Click(armed: true, searching: true, error: false, streaming: false));
        Assert.Equal(OrbAction.Stop, ListenPolicy.Click(armed: true, searching: false, error: false, streaming: true));
        Assert.Equal(OrbAction.Retry, ListenPolicy.Click(armed: true, searching: false, error: true, streaming: false));
        // Searching but already streaming (shouldn't happen) still stops, not guidance.
        Assert.Equal(OrbAction.Stop, ListenPolicy.Click(armed: true, searching: true, error: false, streaming: true));
    }

    [Fact]
    public void Orb_AutoRelistenOnlyWhenDisarmedWhileIdleAndPhoneBecomesReady()
    {
        Assert.True(ListenPolicy.ShouldAutoRelisten(disarmedWhileIdle: true, nowReady: true, currentlyArmed: false));
        Assert.False(ListenPolicy.ShouldAutoRelisten(true, nowReady: false, currentlyArmed: false));
        Assert.False(ListenPolicy.ShouldAutoRelisten(false, nowReady: true, currentlyArmed: false));
        Assert.False(ListenPolicy.ShouldAutoRelisten(true, nowReady: true, currentlyArmed: true));
    }

    [Fact]
    public void Orb_LogLinesNameTheSource()
    {
        Assert.Equal("listen on (orb)", ListenPolicy.LogLine(true, ListenSource.Orb));
        Assert.Equal("listen off (tray)", ListenPolicy.LogLine(false, ListenSource.Tray));
        Assert.Equal("listen on (auto: phone ready)", ListenPolicy.LogLine(true, ListenSource.AutoPhoneReady));
        Assert.Equal("listen on (retry)", ListenPolicy.LogLine(true, ListenSource.Retry));
    }

    [Fact]
    public void Orb_RapidClicksAreASingleAction()
    {
        const long tps = 1_000_000;
        long last = 0;
        int acted = 0;
        for (int i = 0; i < 10; i++)
        {
            long now = tps + tps * i / 20; // 50 ms apart, starting at 1 s (last=0 means "no previous click")
            if (ListenPolicy.IsDebounced(now, last, tps)) continue;
            last = now;
            acted++;
        }
        Assert.Equal(1, acted);
    }

    // ---- F2 jitter / drift seed ----------------------------------------------------------------

    [Fact]
    public void History_KeyIsSaltedHashAndNeverStoresTheRawId()
    {
        byte[] id = Enumerable.Range(0, 16).Select(i => (byte)(i + 1)).ToArray();
        string salt = PhoneLinkHistory.NewSalt();
        string key = PhoneLinkHistory.Key("SM-S721B", id, salt);
        Assert.StartsWith("sm-s721b:", key);
        Assert.DoesNotContain(Convert.ToHexString(id), key, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PhoneLinkHistory.HashHexChars, key.Split(':')[1].Length);
        // Different salt, different key.
        Assert.NotEqual(key, PhoneLinkHistory.Key("SM-S721B", id, PhoneLinkHistory.NewSalt()));
        // Missing id falls back to model-only.
        Assert.Equal("model-only:sm-s721b", PhoneLinkHistory.Key("SM-S721B", null, salt));
        Assert.Equal("", PhoneLinkHistory.Key("  ", null, salt));
        // Bad / empty salt falls back rather than throwing.
        Assert.Equal("model-only:pixel 8", PhoneLinkHistory.Key("Pixel 8", id, "zz"));
        Assert.Equal("model-only:pixel 8", PhoneLinkHistory.Key("Pixel 8", id, ""));
    }

    [Fact]
    public void History_ClampsTargetDriftAndEvictsLruPast32()
    {
        Assert.Equal(10, PhoneLinkHistory.ClampTargetMs(-5));
        Assert.Equal(40, PhoneLinkHistory.ClampTargetMs(99));
        Assert.Equal(10, PhoneLinkHistory.ClampTargetMs(double.NaN));
        Assert.Equal(1000, PhoneLinkHistory.ClampPpm(5000));
        Assert.Equal(-1000, PhoneLinkHistory.ClampPpm(-9000));
        Assert.Equal(0, PhoneLinkHistory.ConfidenceFromDuration(10));
        Assert.InRange(PhoneLinkHistory.ConfidenceFromDuration(60), 0.2, 1);

        var list = new List<PhoneLinkSeed>();
        for (int i = 0; i < 40; i++)
            list = PhoneLinkHistory.Upsert(list, new PhoneLinkSeed { Key = "p" + i, TargetMs = 25, DriftPpm = 100 }, i);
        Assert.Equal(PhoneLinkHistory.MaxPhones, list.Count);
        Assert.DoesNotContain(list, s => s.Key == "p0");
        Assert.Contains(list, s => s.Key == "p39");
        // Update of an existing key does not grow the list and touches LRU.
        list = PhoneLinkHistory.Upsert(list, new PhoneLinkSeed { Key = "p20", TargetMs = 30, DriftPpm = 200 }, 1000);
        Assert.Equal(PhoneLinkHistory.MaxPhones, list.Count);
        Assert.Equal(30, PhoneLinkHistory.Find(list, "p20")!.TargetMs);
        Assert.Equal(1000, PhoneLinkHistory.Find(list, "p20")!.LastUsedUtcTicks);
    }

    [Fact]
    public void History_SeedLogLineUsesInvariantNumbers()
    {
        Assert.Equal("seed: target=25.0ms, drift=533ppm (from history, confidence=0.60)",
            PhoneLinkHistory.SeedLogLine(25, 533, true, 0.6));
        Assert.Equal("seed: target=20.0ms, drift=0ppm (from default)",
            PhoneLinkHistory.SeedLogLine(20, 0, false, 0));
        Assert.DoesNotContain("install", PhoneLinkHistory.SeedLogLine(20, 0, false, 0), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Settings_NormalizePhoneSeedsDropsBadKeysAndClamps()
    {
        var s = new AppSettings
        {
            PhoneSeedSalt = "not-hex",
            PhoneSeeds =
            {
                new PhoneLinkSeed { Key = "", TargetMs = 25 },
                new PhoneLinkSeed { Key = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", TargetMs = 25 },
                new PhoneLinkSeed { Key = "sm-s721b:0123456789abcdef", TargetMs = 99, DriftPpm = 5000, Confidence = 2 },
            },
        };
        s.NormalizePhoneSeeds();
        Assert.Null(s.PhoneSeedSalt);
        Assert.Single(s.PhoneSeeds);
        Assert.Equal(40, s.PhoneSeeds[0].TargetMs);
        Assert.Equal(1000, s.PhoneSeeds[0].DriftPpm);
        Assert.Equal(1, s.PhoneSeeds[0].Confidence);
    }

    [Fact]
    public void DriftController_SeedHoldsTheRatioWhenFillIsOnTarget()
    {
        var dc = new DriftController(960, DriftController.KpTuned);
        dc.Seed(533);
        Assert.InRange(dc.Ratio, 1 + 530e-6, 1 + 536e-6);
        for (int i = 0; i < 200; i++) dc.Update(960, 0.01);
        Assert.InRange((dc.Ratio - 1) * 1e6, 500, 560);
        dc.Seed(5000);
        Assert.InRange((dc.Ratio - 1) * 1e6, 990, 1000);
        dc.Seed(double.NaN);
        Assert.Equal(1.0, dc.Ratio, 6);
    }

    [Fact]
    public void JitterBuffer_FirstMinuteRaisesAfterTwoUnderrunsIn30Seconds()
    {
        var jb = new JitterBuffer(Fpp, targetPackets: 4, maxPackets: 12, sampleRate: Fs) { AdaptiveTarget = true };
        uint seq = 0;
        void Burst(int n) { for (int i = 0; i < n; i++) jb.Push(seq++, new byte[Fpp * 2]); }
        // First underrun at ~1 s, second ~20 s later (outside the old 10 s window, inside the new 30 s one).
        Burst(jb.Target);
        for (int i = 0; i < Fs / Fpp + 4; i++) jb.Pull(new byte[Fpp * 2]); // ~1 s of pulls, some dry
        int afterFirst = jb.ExtraPackets;
        // Advance ~20 s of playout with a tiny trickle so the clock moves but we stay dry-ish.
        for (int s = 0; s < 20; s++)
        {
            jb.Push(seq++, new byte[Fpp * 2]);
            for (int i = 0; i < Fs / Fpp; i++) jb.Pull(new byte[Fpp * 2]);
        }
        Assert.True(jb.ExtraPackets > afterFirst, $"extra stayed {jb.ExtraPackets} after two underruns 20 s apart in the first minute");
    }

    [Fact]
    public void JitterBuffer_ReplayWithSeededDriftAnd3MsPeriodStaysUnderOneUnderrunPerMinute()
    {
        // Exclusive 3 ms period, 5 ms packets, ±600 ppm phone clock, start from a learned 25 ms / 600 ppm seed.
        const double periodMs = 3, seconds = 60, ppm = 600;
        int floor = JitterPolicy.EffectiveTargetPackets(25, periodMs, PktMs);
        int max = JitterPolicy.MaxDepthPackets(25, periodMs, PktMs);
        int high = JitterPolicy.HighWaterPackets(25, periodMs, PktMs);
        var jb = new JitterBuffer(Fpp, 1, floor, max, Fs, high)
        {
            Resample = true, DriftLock = false, PitchConcealment = true, AdaptiveTarget = true,
            RenderPeriodMs = periodMs,
        };
        Assert.True(jb.Seed(floor, ppm));
        var rnd = new Random(7);
        double pktPeriod = Fpp / (double)Fs / (1 + ppm / 1e6);
        int pullFrames = (int)Math.Round(periodMs * Fs / 1000.0);
        var buf = new byte[pullFrames * 2];
        uint seq = 1; double lastArrival = 0, nextSend = 0, pendingArrival = double.NaN;
        byte[]? pending = null; long sent = 0;
        int pulls = (int)(seconds * 1000 / periodMs);
        // Pre-roll packets so the first pulls are not an underrun.
        double t0 = 0.15;
        long underStart = -1;
        for (int k = 0; k < pulls; k++)
        {
            double now = t0 + k * (periodMs / 1000.0);
            while (true)
            {
                if (pending is null)
                {
                    double arr = Math.Max(lastArrival, nextSend + Math.Abs(Gauss(rnd)) * 0.0013);
                    pending = Tone(sent, 1000); pendingArrival = arr; nextSend += pktPeriod; sent++;
                }
                if (pendingArrival > now) break;
                jb.Push(seq++, pending); lastArrival = pendingArrival; pending = null;
            }
            jb.Pull(buf);
            if (k == 200) underStart = jb.Stats.Underruns; // ignore start-up
        }
        long under = jb.Stats.Underruns - (underStart < 0 ? 0 : underStart);
        double measuredMin = (seconds - 200 * periodMs / 1000.0) / 60.0;
        double perMin = measuredMin > 0 ? under / measuredMin : under;
        Assert.True(perMin <= 1.0, $"underruns {under} = {perMin:F2}/min (period 3 ms, {ppm} ppm, seeded)");
    }

    [Fact]
    public void JitterBuffer_ReplaySurvivesLossDisconnectAndJunk()
    {
        int floor = JitterPolicy.EffectiveTargetPackets(20, 3, PktMs);
        var jb = new JitterBuffer(Fpp, 1, floor, JitterPolicy.MaxDepthPackets(20, 3, PktMs), Fs,
            JitterPolicy.HighWaterPackets(20, 3, PktMs))
        { Resample = true, PitchConcealment = true, AdaptiveTarget = true, RenderPeriodMs = 3 };
        jb.Seed(floor, -600);
        var rnd = new Random(3);
        var buf = new byte[(int)(0.003 * Fs) * 2];
        uint seq = 1;
        // Burst, then 15 % loss, then a 200 ms disconnect, then garbage sequence jumps.
        for (int i = 0; i < 80; i++) jb.Push(seq++, Tone(i, 440));
        for (int i = 0; i < 400; i++)
        {
            if (rnd.NextDouble() > 0.15) jb.Push(seq, Tone(seq, 440));
            seq++;
            jb.Pull(buf);
        }
        for (int i = 0; i < 70; i++) jb.Pull(buf); // disconnect
        seq += 200;
        for (int i = 0; i < 80; i++) jb.Push(seq++, Tone(seq, 440));
        for (int i = 0; i < 200; i++) jb.Pull(buf);
        jb.Push(uint.MaxValue - 2, Tone(1, 100));
        jb.Push(0, new byte[0]); // empty
        jb.Push(1, new byte[3]); // truncated
        jb.Pull(buf); // still produces a frame; no throw
    }

    // ---- F3 mic-only snapshot -----------------------------------------------------------------

    [Fact]
    public void MicOnly_BlockedWhenEveryCheckIsClean()
    {
        var ok = new MicOnlySnapshot(true, true, true, 9999, 9999, 0, 0, true, false, false, true);
        Assert.True(ok.Blocked);
        Assert.Equal(MicOnlyGap.None, ok.Gap);
        Assert.Contains("ignoreDefault=yes/yes", ok.LogLine());
        Assert.Contains("defaultRoutes=0/0", ok.LogLine());
        Assert.Contains("dns=loopback", ok.LogLine());
        Assert.Contains("metric=9999/9999", ok.LogLine());
        Assert.DoesNotContain("192.", ok.LogLine());
    }

    [Theory]
    [InlineData(false, true, 0, 0, true, false, false, true, MicOnlyGap.IgnoreDefaultOff)]
    [InlineData(true, true, 1, 0, true, false, false, true, MicOnlyGap.DefaultRoutePresent)]
    [InlineData(true, true, 0, 0, false, false, false, true, MicOnlyGap.DnsNotLoopback)]
    [InlineData(true, true, 0, 0, true, true, false, true, MicOnlyGap.BestRouteIsPhone)]
    [InlineData(true, true, 0, 1, true, false, true, true, MicOnlyGap.BestRouteIsPhone)]
    public void MicOnly_ReportsTheFailingItem(bool ign4, bool ign6, int def4, int def6, bool dns, bool best4, bool best6, bool v6, MicOnlyGap gap)
    {
        var s = new MicOnlySnapshot(true, ign4, ign6, 9999, 9999, def4, def6, dns, best4, best6, v6);
        Assert.False(s.Blocked);
        Assert.Equal(gap, s.Gap);
        Assert.False(string.IsNullOrEmpty(MicOnlySnapshot.GapKey(gap)));
    }

    [Fact]
    public void MicOnly_Ipv6AbsentIsNotALeakAndNoAdapterIsNotBlocked()
    {
        var no6 = new MicOnlySnapshot(true, true, null, 9999, null, 0, 0, true, false, false, false);
        Assert.True(no6.Blocked);
        Assert.DoesNotContain("/", no6.LogLine().Split("defaultRoutes=")[1].Split(',')[0]); // only one family
        var none = new MicOnlySnapshot(false, null, null, null, null, 0, 0, null, false, false, false);
        Assert.False(none.Blocked);
        Assert.Equal(MicOnlyGap.NoAdapter, none.Gap);
        Assert.Equal("mic-only: no adapter", none.LogLine());
    }

    [Fact]
    public void MicOnly_UnknownReadsDoNotClaimBlocked()
    {
        var unknown = new MicOnlySnapshot(true, null, null, null, null, 0, 0, null, false, false, false);
        Assert.False(unknown.Blocked);
    }

    [Fact]
    public void Catalog_ConnectionShowsPhoneDataAndFixRoute()
    {
        var ids = SettingsCatalog.Items(SettingsPage.Connection).Select(i => i.Id).ToList();
        Assert.Contains("phoneData", ids);
        Assert.Contains("fixRoute", ids);
        Assert.DoesNotContain("fixRoute", SettingsCatalog.Items(SettingsPage.Advanced).Select(i => i.Id));
        foreach (var k in new[] { "set.phoneData", "status.dataBlocked", "status.dataNotBlocked", "menu.fixRoute.off",
                     "status.dataFail.noAdapter", "status.dataFail.dns", "status.dataFail.defaultRoute",
                     "status.dataFail.ignoreDefault", "status.dataFail.bestRoute" })
        {
            Assert.True(L.En.ContainsKey(k), "EN missing " + k);
            Assert.True(L.Id.ContainsKey(k), "ID missing " + k);
            Assert.False(string.IsNullOrWhiteSpace(L.En[k]));
            Assert.False(string.IsNullOrWhiteSpace(L.Id[k]));
        }
    }

    [Fact]
    public void Catalog_EveryLanguageKeyStillPaired()
    {
        foreach (var k in SettingsCatalog.AllKeys().Distinct())
        {
            Assert.True(L.En.ContainsKey(k), "EN missing " + k);
            Assert.True(L.Id.ContainsKey(k), "ID missing " + k);
        }
    }

    // ---- F7 names + protocol extras -----------------------------------------------------------

    [Theory]
    [InlineData("SAMSUNG Mobile USB Composite Device ", "SAMSUNG Mobile USB Composite Device")]
    [InlineData("  USB  NCM\tDevice  ", "USB NCM Device")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("Pixel 8", "Pixel 8")]
    public void DeviceNames_TrimAndCollapse(string raw, string want) => Assert.Equal(want, DeviceNames.Clean(raw));

    [Fact]
    public void TetherDiagnosis_StripsTrailingSpaceOnDeviceName()
    {
        var usb = new[] { new UsbDeviceInfo(new[] { "USB\\VID_04E8&PID_6860" }, Array.Empty<string>(),
            "SAMSUNG Mobile USB Composite Device ", "SAMSUNG Mobile USB Composite Device ", false, 0) };
        var r = TetherDiagnosis.Diagnose(Array.Empty<TetherAdapterInfo>(), usb);
        Assert.Equal(TetherLink.PhoneWithoutTethering, r.Link);
        Assert.Equal("SAMSUNG Mobile USB Composite Device", r.DeviceName);
        Assert.DoesNotContain("Device )", r.LogLine());
    }

    [Fact]
    public void Pong_InstallIdIsIgnoredByAReaderThatDoesNotAskAndParsedWhenPresent()
    {
        var revs = new FieldRev[SettingsSync.FieldCount];
        byte[] id = RandomNumberGenerator.GetBytes(16);
        var buf = new byte[96];
        int n = LinkControl.WritePong(buf, true, true, true, false, true, 6, "SM-S721B ",
            NoiseMode.Light, true, 1, 2, false, false, revs, true, null, id);
        Assert.True(n > SettingsSync.Size);
        Assert.True(LinkControl.TryReadPong(buf.AsSpan(0, n), out var phone));
        Assert.Equal("SM-S721B", phone.Name);
        Assert.NotNull(phone.InstallId);
        Assert.Equal(id, phone.InstallId);
        // A 0.7.x-style payload (no block) still parses, and leftover unknown bytes after sync are not required.
        var old = new byte[64];
        int o = LinkControl.WritePong(old, true, true, true, false, true, 6, "Pixel 8",
            NoiseMode.Light, true, 1, 2, false, false, revs);
        Assert.True(LinkControl.TryReadPong(old.AsSpan(0, o), out var p2));
        Assert.Null(p2.InstallId);
        Assert.Equal("Pixel 8", p2.Name);
    }

    [Fact]
    public void Pong_TruncatedOrZeroInstallIdIsIgnoredAndDoesNotThrow()
    {
        var revs = new FieldRev[SettingsSync.FieldCount];
        var buf = new byte[80];
        int n = LinkControl.WritePong(buf, true, true, true, false, true, 6, "X",
            NoiseMode.Light, true, 0, 1, false, false, revs, true, null, new byte[16]); // all-zero written? Write skips invalid? all-zero has length 16 so it is written
        // Force a truncated copy.
        Assert.True(LinkControl.TryReadPong(buf.AsSpan(0, Math.Max(4, n - 3)), out _));
        Assert.True(PhoneIdBlock.TryRead(Enumerable.Repeat((byte)0xC8, 2).Concat(new byte[16]).ToArray(), out _) == false);
        Assert.Equal(0, PhoneIdBlock.Write(new byte[4], new byte[16]));
        Assert.False(PhoneIdBlock.TryRead(stackalloc byte[] { 0x00 }, out _));
    }

    [Fact]
    public void SettingsSync_FlagWantPhoneKeyIsBit6AndUnknownFlagsDoNotBreakRead()
    {
        Assert.Equal(1 << 6, SettingsSync.FlagWantPhoneKey);
        var buf = new byte[SettingsSync.Size];
        SettingsSync.Write(buf, (byte)(SettingsSync.FlagPeerSync | SettingsSync.FlagWantPhoneKey | 0x80), Array.Empty<FieldRev>());
        Assert.True(SettingsSync.TryRead(buf, out var flags, out _));
        Assert.Equal((byte)(SettingsSync.FlagPeerSync | SettingsSync.FlagWantPhoneKey | 0x80), flags);
    }

    [Fact]
    public void AppSources_UseReadOnlyIpHelperAndLogArmSource()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "CordMic.sln"))) d = d.Parent;
        Assert.NotNull(d);
        string app = Path.Combine(d!.FullName, "src", "CordMic.App");
        string route = File.ReadAllText(Path.Combine(app, "RouteProtection.cs"));
        Assert.Contains("GetIpInterfaceEntry", route);
        Assert.Contains("GetIpForwardTable2", route);
        Assert.Contains("GetBestInterfaceEx", route);
        Assert.DoesNotContain("SetIpInterfaceEntry", route);
        string main = File.ReadAllText(Path.Combine(app, "MainForm.cs"));
        Assert.Contains("ListenPolicy.IsDebounced", main);
        Assert.Contains("OrbAction.ShowGuidance", main);
        Assert.Contains("DoubleClick", main);
        string ctl = File.ReadAllText(Path.Combine(app, "AppController.cs"));
        Assert.Contains("ListenSource.AutoPhoneReady", ctl);
        Assert.Contains("route fix skipped: no tether adapter", ctl);
        Assert.Contains("FlagWantPhoneKey", File.ReadAllText(Path.Combine(d.FullName, "src", "CordMic.Core", "Receiver.cs")));
        string ps = File.ReadAllText(Path.Combine(d.FullName, "installer", "tools", "set-tether-metric.ps1"));
        Assert.Contains("no tether adapter", ps);
    }

    static byte[] Tone(long n, double hz)
    {
        var b = new byte[Fpp * 2];
        for (int i = 0; i < Fpp; i++)
        {
            double t = (n * Fpp + i) / (double)Fs;
            short v = (short)(Math.Sin(2 * Math.PI * hz * t) * 8000);
            b[2 * i] = (byte)v; b[2 * i + 1] = (byte)(v >> 8);
        }
        return b;
    }

    static double Gauss(Random r)
    {
        double u, v, s;
        do { u = 2 * r.NextDouble() - 1; v = 2 * r.NextDouble() - 1; s = u * u + v * v; }
        while (s >= 1 || s == 0);
        return u * Math.Sqrt(-2 * Math.Log(s) / s);
    }
}
