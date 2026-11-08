using System.Text.Json;
using CordMic.Core;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// 0.5.2: sample-level drift lock, overflow hysteresis, and a before/after sim of the field-log
/// periods (10 ms IAudioClient fallback and 2.7 ms IAudioClient3).
/// </summary>
public class PolicySimTests
{
    private readonly ITestOutputHelper _out;
    public PolicySimTests(ITestOutputHelper o) => _out = o;

    public readonly record struct Rates(double Urate, double Orate, double CatchUpPerMin, double SlipPerMin, double AvgDepthMs, long Underruns, long Overflows, long CatchUp, long Slips);

    static JitterBuffer BufferFor(double requestMs, double periodMs, bool v052)
    {
        int target = v052
            ? JitterPolicy.EffectiveTargetPackets(requestMs, periodMs, 5)
            : LegacyTarget(requestMs, periodMs, 5);
        int max = v052
            ? JitterPolicy.MaxDepthPackets(requestMs, periodMs, 5)
            : Math.Max(target * 3, target + 4);
        int high = v052 ? JitterPolicy.HighWaterPackets(requestMs, periodMs, 5) : target;
        return new JitterBuffer(240, 1, target, max, 48000, high)
        {
            CatchUp = !v052,
            DriftLock = v052,
            RenderPeriodMs = periodMs,
        };
    }

    static int LegacyTarget(double requestedMs, double renderPeriodMs, double packetMs)
    {
        int requested = Math.Max(1, (int)Math.Round(requestedMs / packetMs));
        if (renderPeriodMs <= 0) return requested;
        int pull = Math.Max(1, (int)Math.Ceiling((renderPeriodMs - 1e-9) / packetMs));
        return Math.Max(requested, pull + 1);
    }

    static Rates Run(double periodMs, double requestMs, bool v052, int seconds = 180, int seed = 42,
        double ppm = -12, double latePct = 0.02, double? lateMs = null)
    {
        var jb = BufferFor(requestMs, periodMs, v052);
        var rng = new Random(seed);
        var pkt = new byte[480];
        double tEnd = seconds * 1000.0;
        double tPkt = 0.35;
        double tRen = periodMs;
        uint seq = 0;
        double drift = ppm * 1e-6;
        double padding = periodMs;
        double lastLate = 0;
        while (tPkt < tEnd || tRen < tEnd)
        {
            if (tPkt <= tRen)
            {
                jb.Push(seq++, pkt);
                // Field logs showed about 0.2–0.3 ms arrival jitter and about −12 ppm drift.
                tPkt += 5.0 * (1.0 - drift) + (rng.NextDouble() * 2 - 1) * 0.25;
            }
            else
            {
                // Emulate the 0.5.x writer: keep about (period + 2 ms) queued in the endpoint,
                // write at most one period, and skip a write when padding is already high.
                // Late wakes change elapsed time; (late - previous late) averages to zero so
                // production and consumption stay matched. A few skipped writes reproduce the
                // standing-depth climb seen when the endpoint was already full.
                double lateCap = lateMs ?? Math.Min(8, periodMs * 1.5);
                double late = rng.NextDouble() < latePct ? lateCap * rng.NextDouble() : 0;
                double elapsed = Math.Max(0.5, periodMs + late - lastLate);
                lastLate = late;
                padding = Math.Max(0, padding - elapsed);
                double targetPad = periodMs + 2;
                double want = padding >= targetPad ? 0 : Math.Min(periodMs, targetPad - padding);
                int frames = (int)Math.Round(want / 1000.0 * 48000);
                if (frames > 0) jb.Pull(frames * 2);
                padding += want;
                tRen += elapsed;
            }
        }
        double minutes = seconds / 60.0;
        double avg = double.IsNaN(jb.AvgDepthMs) ? jb.DepthMs() : jb.AvgDepthMs;
        return new Rates(
            jb.Stats.Underruns / minutes,
            jb.Stats.OverflowDrops / minutes,
            jb.Stats.CatchUpDrops / minutes,
            jb.Stats.SampleSlips / minutes,
            avg,
            jb.Stats.Underruns, jb.Stats.OverflowDrops, jb.Stats.CatchUpDrops, jb.Stats.SampleSlips);
    }

    [Fact]
    public void FieldLogPeriodsBeforeAndAfter()
    {
        var rows = new (string name, double period, double request)[]
        {
            ("10ms / Low 5ms (0.5.0 field pattern)", 10.0, 5),
            ("2.7ms / Low 5ms (IAudioClient3 min)", 2.7, 5),
        };
        var report = new System.Text.StringBuilder();
        report.AppendLine("period_ms request_ms version urate/min orate/min catchup/min slip/min bufavg_ms");
        foreach (var row in rows)
        {
            var before = Run(row.period, row.request, v052: false);
            var after = Run(row.period, row.request, v052: true);
            _out.WriteLine($"{row.name}");
            _out.WriteLine($"  before  urate={before.Urate:F2}/min orate={before.Orate:F2}/min catchup={before.CatchUpPerMin:F2}/min slip={before.SlipPerMin:F2}/min bufavg={before.AvgDepthMs:F1} ms");
            _out.WriteLine($"  after   urate={after.Urate:F2}/min orate={after.Orate:F2}/min catchup={after.CatchUpPerMin:F2}/min slip={after.SlipPerMin:F2}/min bufavg={after.AvgDepthMs:F1} ms");
            report.AppendLine($"{row.period:F1} {row.request:F0} before {before.Urate:F2} {before.Orate:F2} {before.CatchUpPerMin:F2} {before.SlipPerMin:F2} {before.AvgDepthMs:F1}");
            report.AppendLine($"{row.period:F1} {row.request:F0} after  {after.Urate:F2} {after.Orate:F2} {after.CatchUpPerMin:F2} {after.SlipPerMin:F2} {after.AvgDepthMs:F1}");

            Assert.Equal(0, after.CatchUp); // packet-drop catch-up is off
            Assert.True(after.Urate < 8, $"{row.name} after urate {after.Urate} too high");
            // Overflow, not sample-hunting, is how a standing backlog drains. ~150 ppm → < 450 slips/min.
            Assert.True(after.SlipPerMin < 450, $"{row.name} slip {after.SlipPerMin} still hunting");
            Assert.True(after.Orate < 80, $"{row.name} after orate {after.Orate} too high");
        }
        File.WriteAllText("/tmp/cordmic-052-sim.txt", report.ToString());
    }

    [Fact]
    public void DriftLockDrainsStandingBacklogWithoutPacketDrops()
    {
        var jb = new JitterBuffer(240, targetPackets: 4, maxPackets: 40, highWaterPackets: 6)
        {
            CatchUp = false,
            DriftLock = true,
            RenderPeriodMs = 10,
            WindowPackets = 40,
        };
        uint seq = 1;
        byte[] Pkt(int n) => Enumerable.Repeat((byte)(n % 256), 480).ToArray();
        for (int i = 0; i < 10; i++, seq++) jb.Push(seq, Pkt((int)seq));
        for (int i = 0; i < 4000; i++, seq++)
        {
            jb.Push(seq, Pkt((int)seq));
            jb.Pull(480);
        }
        Assert.Equal(0, jb.Stats.CatchUpDrops);
        // A standing offset may rise toward ±800 ppm (about 768 slips in 20 s). The old hunt produced thousands.
        Assert.True(jb.Stats.SampleSlips < 1000, $"slips {jb.Stats.SampleSlips} (controller hunting)");
        Assert.Equal(0, jb.Stats.Underruns);
        _out.WriteLine($"standing backlog slips={jb.Stats.SampleSlips} depth={jb.DepthPackets} ppm={jb.DriftPpm:F1}");
    }

    [Fact]
    public void AdaptiveLockTracksTwoHundredFiftyPpmAtShortPeriod()
    {
        var fast = Run(2.7, 5, v052: true, seconds: 30, ppm: 250);
        var slow = Run(2.7, 5, v052: true, seconds: 30, ppm: -250);
        _out.WriteLine($"250 ppm  urate={fast.Urate:F2}/min orate={fast.Orate:F2}/min slip={fast.SlipPerMin:F1}/min buf={fast.AvgDepthMs:F1} ms");
        _out.WriteLine($"-250 ppm urate={slow.Urate:F2}/min orate={slow.Orate:F2}/min slip={slow.SlipPerMin:F1}/min buf={slow.AvgDepthMs:F1} ms");
        Assert.Equal(0, fast.CatchUp);
        Assert.Equal(0, slow.CatchUp);
        Assert.True(fast.Urate < 8, $"fast urate {fast.Urate}");
        Assert.True(slow.Urate < 8, $"slow urate {slow.Urate}");
        Assert.True(fast.Orate < 30, $"fast orate {fast.Orate}");
        Assert.True(slow.Orate < 30, $"slow orate {slow.Orate}");
    }

    [Fact]
    public void OverflowTrimsToHighWaterNotBareTarget()
    {
        var jb = new JitterBuffer(240, targetPackets: 4, maxPackets: 10, highWaterPackets: 6);
        uint seq = 1;
        var pkt = new byte[480];
        for (int i = 0; i < 16; i++, seq++) jb.Push(seq, pkt);
        Assert.True(jb.DepthPackets <= 10, $"depth {jb.DepthPackets}");
        Assert.True(jb.DepthPackets >= 6, $"trimmed below high-water: {jb.DepthPackets}");
        Assert.True(jb.Stats.OverflowDrops >= 1);
    }
}

public class CableHelperLogicTests
{
    [Fact]
    public void PrefersStereoCableInputOverSixteenChannel()
    {
        var names = new[]
        {
            "CABLE In 16ch (VB-Audio Virtual Cable)",
            "Speakers (Realtek(R) Audio)",
            "CABLE Input (VB-Audio Virtual Cable)",
            "CABLE In 16 Ch (VB-Audio Virtual Cable)",
        };
        Assert.Equal("CABLE Input (VB-Audio Virtual Cable)", DeviceRules.PickVirtualCable(names));
        Assert.True(DeviceRules.VirtualCableRank(names[0]) > DeviceRules.VirtualCableRank(names[2]));
        Assert.True(CableIdentity.IsSixteenChannel(names[0]));
        Assert.True(CableIdentity.IsStereoCableInput(names[2]));
        Assert.False(CableIdentity.IsStereoCableInput(names[0]));
    }

    [Fact]
    public void HelperOnlyAcceptsFixedVerbs()
    {
        Assert.True(CableHelperArgs.TryParse(new[] { "--configure-cable" }, out var v, out var err));
        Assert.Equal(CableHelperArgs.Verb.Configure, v);
        Assert.Null(err);

        Assert.True(CableHelperArgs.TryParse(new[] { "--restore-cable" }, out v, out err));
        Assert.Equal(CableHelperArgs.Verb.Restore, v);

        Assert.False(CableHelperArgs.TryParse(new[] { "--configure-cable", "C:\\evil.exe" }, out v, out err));
        Assert.Equal(CableHelperArgs.Verb.None, v);
        Assert.NotNull(err);

        Assert.False(CableHelperArgs.TryParse(new[] { "--configure-cable", "{0.0.0.00000000}.{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}" }, out v, out err));
        Assert.NotNull(err);

        Assert.False(CableHelperArgs.TryParse(new[] { "--tray" }, out v, out err));
        Assert.Equal(CableHelperArgs.Verb.None, v);
        Assert.Null(err);

        Assert.False(CableHelperArgs.TryParse(Array.Empty<string>(), out v, out err));
        Assert.True(CableHelperArgs.MentionsHelperVerb(new[] { "foo", "--restore-cable" }));
    }

    [Fact]
    public void EndpointIdAndTargetChecksFailClosed()
    {
        const string render = "{0.0.0.00000000}.{8f12c4a1-2b3c-4d5e-a678-9abcdeff0011}";
        const string capture = "{0.0.1.00000000}.{8f12c4a1-2b3c-4d5e-a678-9abcdeff0011}";
        const string stereoIn = "CABLE Input (VB-Audio Virtual Cable)";
        const string stereoOut = "CABLE Output (VB-Audio Virtual Cable)";
        const string ch16 = "CABLE In 16ch (VB-Audio Virtual Cable)";

        Assert.True(CableIdentity.TryParseEndpointId(render, out bool isRender, out var guid));
        Assert.True(isRender);
        Assert.Equal("8f12c4a1-2b3c-4d5e-a678-9abcdeff0011", guid);
        Assert.True(CableIdentity.TryParseEndpointId(capture, out isRender, out _));
        Assert.False(isRender);

        Assert.False(CableIdentity.TryParseEndpointId(@"C:\Windows\system32\cmd.exe", out _, out _));
        Assert.False(CableIdentity.TryParseEndpointId("{0.0.0.00000000}.{not-a-guid}", out _, out _));
        Assert.False(CableIdentity.TryParseEndpointId("{0.0.2.00000000}.{8f12c4a1-2b3c-4d5e-a678-9abcdeff0011}", out _, out _));

        Assert.True(CableIdentity.IsHelperTarget(render, stereoIn, "VB-Audio", wantRender: true));
        Assert.False(CableIdentity.IsHelperTarget(render, stereoIn, "VB-Audio", wantRender: false));
        Assert.False(CableIdentity.IsHelperTarget(render, ch16, "VB-Audio", wantRender: true));
        Assert.True(CableIdentity.IsHelperTarget(capture, stereoOut, "", wantRender: false));
        Assert.False(CableIdentity.IsHelperTarget(capture, "Microphone (Realtek(R) Audio)", "Realtek", wantRender: false));
        Assert.False(CableIdentity.IsHelperTarget(render, "Speakers (Realtek(R) Audio)", "Realtek", wantRender: true));
        Assert.Equal(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\{8f12c4a1-2b3c-4d5e-a678-9abcdeff0011}",
            CableIdentity.RegistryPropertiesPath(true, guid));
    }

    [Fact]
    public void FormatComparisonPrefers16or24Bit48kStereo()
    {
        var pcm16 = WaveFormatBlob.PcmStereo48k(16);
        var pcm24 = WaveFormatBlob.PcmStereo48k(24);
        Assert.True(WaveFormatBlob.IsOptimalStereo48k(pcm16));
        Assert.True(WaveFormatBlob.IsOptimalStereo48k(pcm24));
        Assert.Contains("16-bit PCM 48000 Hz 2ch", WaveFormatBlob.Describe(pcm16));

        // IEEE float 32-bit 2ch 48 kHz (typical shared mix format) is not the stored default we want.
        var mix = (byte[])pcm16.Clone();
        mix[0] = 0xFE; mix[1] = 0xFF;
        WaveFormatBlob.SubtypeIeeeFloat.ToByteArray().CopyTo(mix, 24);
        // valid bits 32, bits 32
        mix[14] = 32; mix[15] = 0; mix[18] = 32; mix[19] = 0;
        Assert.True(WaveFormatBlob.TryParse(mix, out _, out _, out _, out bool ieeeFloat));
        Assert.True(ieeeFloat);
        Assert.False(WaveFormatBlob.IsOptimalStereo48k(mix));

        Assert.Throws<ArgumentOutOfRangeException>(() => WaveFormatBlob.PcmStereo48k(32));
        Assert.False(WaveFormatBlob.IsOptimalStereo48k(Array.Empty<byte>()));
    }

    [Fact]
    public void BackupRoundTripAndRejectsUnsafePayloads()
    {
        const string id = "{0.0.0.00000000}.{8f12c4a1-2b3c-4d5e-a678-9abcdeff0011}";
        var file = new CableBackupFile
        {
            Version = 1,
            CreatedUtc = "2026-11-07T00:00:00Z",
            AppVersion = "0.5.2",
            Endpoints =
            {
                new CableEndpointBackup
                {
                    DeviceId = id,
                    FriendlyName = "CABLE Input (VB-Audio Virtual Cable)",
                    IsRender = true,
                    Properties =
                    {
                        new CablePropertyBackup
                        {
                            Key = CableBackup.DeviceFormatKey,
                            Type = "blob",
                            Hex = CableBackup.ToHex(WaveFormatBlob.PcmStereo48k(16)),
                        },
                        new CablePropertyBackup
                        {
                            Key = CableBackup.DisableSysFxKey,
                            Type = "ui4",
                            Dword = 0,
                        },
                    },
                },
            },
        };
        string json = CableBackup.Serialize(file);
        Assert.True(CableBackup.TryDeserialize(json, out var back, out var err), err);
        Assert.NotNull(back);
        Assert.Single(back!.Endpoints);
        Assert.Equal(id, back.Endpoints[0].DeviceId);
        Assert.Equal(2, back.Endpoints[0].Properties.Count);
        Assert.True(CableBackup.TryParseHex(back.Endpoints[0].Properties[0].Hex!, out var blob));
        Assert.True(WaveFormatBlob.IsOptimalStereo48k(blob));

        Assert.False(CableBackup.TryDeserialize("{not json", out _, out err));
        Assert.NotNull(err);

        Assert.True(CableBackup.TryDeserialize(json, out var evil, out _));
        evil!.Endpoints[0].Properties[0].Key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run,0";
        Assert.False(CableBackup.TryDeserialize(CableBackup.Serialize(evil), out _, out err));
        Assert.Contains("not allowed", err);

        Assert.True(CableBackup.TryDeserialize(json, out evil, out _));
        evil!.Endpoints[0].DeviceId = @"C:\Windows\system32\cmd.exe";
        Assert.False(CableBackup.TryDeserialize(CableBackup.Serialize(evil), out _, out err));

        Assert.True(CableBackup.TryDeserialize(json, out evil, out _));
        evil!.Version = 99;
        Assert.False(CableBackup.TryDeserialize(CableBackup.Serialize(evil), out _, out err));
    }

    [Fact]
    public void SavedLowLatencyIsNotSilentlyMovedToAuto()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cordmic-om-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, """{"Jitter":"Low","OutputMode":"LowLatency","FirstRunDone":true}""");
            var loaded = AppSettings.Load(path);
            Assert.Equal(OutputMode.LowLatency, loaded.OutputMode);
            Assert.Equal(OutputMode.Auto, new AppSettings().OutputMode);
        }
        finally { Directory.Delete(dir, true); }
    }
}
