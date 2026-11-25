using System.Diagnostics;
using CordMic.Core;
using CordMic.Core.Dsp;
using Xunit.Abstractions;

namespace CordMic.Tests;

/// <summary>
/// 1.1.0 podcast chain: every stage under a chaotic environment (silence, full-scale square, DC, NaN/Inf, denormals,
/// random bursts and dropouts), at 8 / 16 / 44.1 / 48 kHz, mono and stereo, with parameters changed mid-stream.
/// Mirrors android/app/src/test/java/dev/cordmic/PodcastChainTest.kt.
/// </summary>
public class PodcastChainTests
{
    internal static readonly int[] Rates = { 8000, 16000, 44100, 48000 };
    internal static readonly double Ceil = PodcastChain.Db(-1);

    internal sealed class Rng
    {
        private ulong _s;
        public Rng(ulong s) { _s = s; }
        public double Next() { _s ^= _s << 13; _s ^= _s >> 7; _s ^= _s << 17; return (_s >> 11) / (double)(1UL << 53) * 2 - 1; }
    }

    internal static double[] Chaos(int sr, int ch, double seconds, ulong seed)
    {
        int n = (int)(sr * seconds);
        var x = new double[n * ch];
        var r = new Rng(seed);
        int i = 0, seg = 0;
        while (i < n)
        {
            int len = Math.Min(n - i, Math.Max(1, (int)(sr * (0.02 + 0.2 * Math.Abs(r.Next())))));
            int kind = (seg++ * 7 + (int)(Math.Abs(r.Next()) * 100)) % 9;
            double amp = Math.Pow(10, (-60 + 66 * Math.Abs(r.Next())) / 20), f0 = 90 + 250 * Math.Abs(r.Next());
            for (int j = 0; j < len; j++)
            {
                double t = (double)(i + j) / sr;
                double v = kind switch
                {
                    0 => 0,
                    1 => Math.Sin(2 * Math.PI * 220 * t) >= 0 ? 1 : -1,
                    2 => 1,
                    3 => j % 17 == 0 ? double.NaN : j % 23 == 0 ? double.PositiveInfinity : j % 29 == 0 ? double.NegativeInfinity : 0.3 * r.Next(),
                    4 => 1e-310 * (j % 2 == 0 ? 1 : -1),
                    5 => amp * r.Next(),
                    6 => Harm(f0, t, amp),
                    7 => amp * Math.Sin(2 * Math.PI * Math.Min(6500, sr * 0.45) * t) + amp * 0.3 * r.Next(),
                    _ => 4 * r.Next(),
                };
                for (int c = 0; c < ch; c++) x[(i + j) * ch + c] = c == 1 ? v * 0.7 : v;
            }
            i += len;
        }
        return x;
    }

    private static double Harm(double f0, double t, double amp)
    {
        double s = 0;
        for (int h = 1; h <= 8; h++) s += Math.Sin(2 * Math.PI * f0 * h * t) / h;
        return amp * s * 0.5;
    }

    internal static void Configure(PodcastChain pc, int stage)
    {
        pc.AgcEnabled = stage is 0 or 4;
        pc.DynamicEqEnabled = stage is 1 or 4;
        pc.CompressorEnabled = stage is 2 or 4;
        pc.LimiterEnabled = stage is 3 or 4;
    }

    private static double RmsDb(double[] x, int from, int to)
    {
        double s = 0;
        for (int i = from; i < to; i++) s += x[i] * x[i];
        return 10 * Math.Log10(s / (to - from) + 1e-20);
    }

    [Fact]
    public void EveryStage_StaysFiniteAndBounded_InChaos_AllRates_MonoStereo_ParamChanges()
    {
        var presets = new[] { PodcastPresets.Off, PodcastPresets.Light, PodcastPresets.Strong, PodcastPresets.StudioWhisper };
        foreach (int sr in Rates)
            for (int ch = 1; ch <= 2; ch++)
                for (int stage = 0; stage <= 4; stage++)
                {
                    var pc = new PodcastChain(sr, ch, PodcastPresets.Strong);
                    Configure(pc, stage);
                    var x = Chaos(sr, ch, 2.0, (ulong)(1234 + sr + ch * 7 + stage));
                    int off = 0, b = 0;
                    while (off < x.Length)
                    {
                        int frames = Math.Min(240 * ch, x.Length - off) / ch;
                        if (b++ % 5 == 0) pc.Params = presets[b % presets.Length];
                        pc.Process(x, frames, off);
                        off += frames * ch;
                    }
                    double lim = pc.LimiterEnabled ? Ceil + 1e-12 : 65;
                    foreach (var v in x)
                    {
                        Assert.True(double.IsFinite(v), $"finite sr={sr} ch={ch} stage={stage}");
                        Assert.True(Math.Abs(v) <= lim, $"bounded sr={sr} ch={ch} stage={stage} v={v}");
                    }
                }
    }

    [Fact]
    public void Silence_StaysSilent_DenormalsFlush_AgcNotRaised()
    {
        foreach (int sr in Rates)
            for (int ch = 1; ch <= 2; ch++)
            {
                var pc = new PodcastChain(sr, ch, PodcastPresets.Light);
                var x = new double[sr * ch];
                for (int i = 0; i < x.Length; i += 2) x[i] = 1e-310;
                pc.Process(x, sr);
                Assert.All(x, v => Assert.True(Math.Abs(v) < 1e-12));
                Assert.Equal(0, pc.AgcGainDb, 2);
            }
    }

    [Fact]
    public void Dc_IsRemovedByHighPass()
    {
        foreach (int sr in Rates)
        {
            var pc = new PodcastChain(sr, 1, PodcastPresets.Light) { AgcEnabled = false, CompressorEnabled = false };
            var x = Enumerable.Repeat(0.5, sr).ToArray();
            pc.Process(x, sr);
            Assert.True(Math.Abs(x[sr - 1]) < 1e-3, $"sr={sr}");
        }
    }

    [Fact]
    public void FullScaleSquare_IsLimitedAtCeiling()
    {
        foreach (int sr in Rates)
            for (int ch = 1; ch <= 2; ch++)
            {
                var pc = new PodcastChain(sr, ch, PodcastPresets.Strong);
                var x = new double[sr * ch];
                for (int k = 0; k < x.Length; k++) x[k] = ((k / ch) / (sr / 200)) % 2 == 0 ? 1 : -1;
                pc.Process(x, sr);
                double pk = x.Max(Math.Abs);
                Assert.True(pk <= Ceil + 1e-12, $"sr={sr} peak={pk}");
                Assert.True(pk > Ceil * 0.5);
            }
    }

    [Fact]
    public void Agc_BringsQuietSpeechTowardTarget_WithoutLiftingNoise()
    {
        const int sr = 48000;
        var pc = new PodcastChain(sr, 1, PodcastPresets.Light) { DynamicEqEnabled = false, CompressorEnabled = false };
        var r = new Rng(9);
        double nAmp = PodcastChain.Db(-72) * Math.Sqrt(3);
        int n = (int)(sr * 5.5);
        var x = new double[n];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / sr, nz = nAmp * r.Next();
            x[i] = t < 1.5 ? nz : nz + PodcastChain.Db(-35) * Math.Sqrt(2) * Math.Sin(2 * Math.PI * 180 * t) * (0.8 + 0.2 * Math.Sin(2 * Math.PI * 3 * t));
        }
        pc.Process(x, n);
        Assert.True(RmsDb(x, sr / 2, (int)(1.5 * sr)) < -51.5);
        double sp = RmsDb(x, (int)(4.5 * sr), n);
        Assert.InRange(sp, -21, -13);
    }

    [Fact]
    public void Agc_NeverLiftsRoomFloorAboveNoiseCeiling()
    {
        const int sr = 48000;
        var pc = new PodcastChain(sr, 1, PodcastPresets.Strong);
        var r = new Rng(31);
        var x = new double[sr * 6];
        for (int i = 0; i < x.Length; i++)
        {
            double t = (double)i / sr, nz = PodcastChain.Db(-58) * Math.Sqrt(3) * r.Next();
            x[i] = ((int)t % 2) == 1 ? nz + 0.02 * Math.Sin(2 * Math.PI * 150 * t) : nz;
        }
        pc.Process(x, x.Length);
        Assert.True(RmsDb(x, 4 * sr + sr / 4, 5 * sr) < -51.5);
    }

    [Fact]
    public void Agc_HoldsGainThroughPauses_NoPumping()
    {
        const int sr = 48000;
        var pc = new PodcastChain(sr, 1, PodcastPresets.Light);
        var tone = new double[sr * 3];
        for (int i = 0; i < tone.Length; i++) tone[i] = PodcastChain.Db(-30) * Math.Sqrt(2) * Math.Sin(2 * Math.PI * 200.0 * i / sr);
        pc.Process(tone, tone.Length);
        double before = pc.AgcGainDb;
        var pause = new double[sr / 2];
        pc.Process(pause, pause.Length);
        Assert.True(pc.Gated);
        Assert.InRange(pc.AgcGainDb, before - 1, before + 1);
    }

    [Fact]
    public void DynamicEq_CutsMudAndSibilance_LeavesMidrange()
    {
        const int sr = 48000;
        double OutDb(double f)
        {
            var pc = new PodcastChain(sr, 1, PodcastPresets.Light) { AgcEnabled = false, CompressorEnabled = false, LimiterEnabled = false };
            var x = new double[sr];
            for (int i = 0; i < sr; i++) x[i] = 0.1 * Math.Sin(2 * Math.PI * f * i / sr);
            pc.Process(x, sr);
            return RmsDb(x, sr / 2, sr) - 20 * Math.Log10(0.1 / Math.Sqrt(2));
        }
        Assert.InRange(OutDb(1000), -0.5, 0.5);
        Assert.True(OutDb(30) < -10);
        double mud = OutDb(250), ess = OutDb(6500);
        Assert.InRange(mud, -PodcastPresets.Light.MudMaxCutDb - 0.5, -1.0);
        Assert.InRange(ess, -PodcastPresets.Light.EssMaxCutDb - 0.5, -2.0);
    }

    [Fact]
    public void GainComputer_IsSoftKnee_Continuous_Monotonic()
    {
        double t = -22, r = 3, w = 10;
        Assert.Equal(0, PodcastChain.GainComputer(-40, t, r, w), 12);
        Assert.Equal((1 / r - 1) * 20, PodcastChain.GainComputer(t + 20, t, r, w), 12);
        foreach (var e in new[] { t - w / 2, t + w / 2 })
            Assert.Equal(PodcastChain.GainComputer(e - 1e-9, t, r, w), PodcastChain.GainComputer(e + 1e-9, t, r, w), 6);
        double prev = 0;
        for (double l = -40; l < 0; l += 0.25) { double g = PodcastChain.GainComputer(l, t, r, w); Assert.True(g <= prev + 1e-12); prev = g; }
    }

    [Fact]
    public void Compressor_ReleaseIsProgramDependent()
    {
        const int sr = 48000;
        double Recovery(double sec)
        {
            var pc = new PodcastChain(sr, 1, PodcastPresets.Light) { AgcEnabled = false, DynamicEqEnabled = false, LimiterEnabled = false };
            var loud = new double[(int)(sec * sr)];
            for (int i = 0; i < loud.Length; i++) loud[i] = 0.5 * Math.Sin(2 * Math.PI * 300.0 * i / sr);
            pc.Process(loud, loud.Length);
            var q = new double[PodcastChain.Ctrl];
            int t = 0;
            while (pc.CompGainReductionDb > 1.0 && t < sr * 3) { pc.Process(q, q.Length); t += q.Length; }
            return t * 1000.0 / sr;
        }
        double s = Recovery(0.03), l = Recovery(2.0);
        Assert.True(l > s * 2, $"short={s} long={l}");
    }

    [Fact]
    public void Limiter_LatencyMatchesLookahead_AndImpulseAligns()
    {
        foreach (int sr in Rates)
            foreach (var p in new[] { PodcastPresets.Off, PodcastPresets.Light, PodcastPresets.Strong, PodcastPresets.StudioBalanced })
            {
                var pc = new PodcastChain(sr, 1, p) { AgcEnabled = false, DynamicEqEnabled = false, CompressorEnabled = false };
                int expect = (int)Math.Round(p.LookaheadMs * sr / 1000.0, MidpointRounding.ToEven);
                Assert.Equal(expect, pc.LatencyFrames);
                Assert.True(pc.LatencyMs <= 3.0);
                var x = new double[sr / 10]; x[10] = 0.5;
                pc.Process(x, x.Length);
                Assert.Equal(10 + expect, Array.FindIndex(x, v => Math.Abs(v) > 0.1));
            }
    }

    [Fact]
    public void TruePeakGuard_HoldsCeiling_TwoSampleDelay()
    {
        foreach (int sr in Rates)
            for (int ch = 1; ch <= 2; ch++)
            {
                var g = new TruePeakGuard(sr, ch, -1);
                var r = new Rng((ulong)(77 + sr));
                int n = sr / 2;
                var x = new double[n * ch];
                for (int i = 0; i < x.Length; i++) x[i] = i % 101 == 0 ? double.NaN : 3 * r.Next();
                for (int f = 0; f < n; f++) g.ProcessFrame(x, f * ch);
                Assert.All(x, v => Assert.True(double.IsFinite(v) && Math.Abs(v) <= Ceil + 1e-12));
            }
        var gg = new TruePeakGuard(48000);
        var outs = Enumerable.Range(0, 6).Select(i => gg.Process(i == 1 ? 0.25 : 0)).ToArray();
        Assert.Equal(0.25, outs[3], 12);
    }

    [Fact]
    public void SpectralMasks_StayWithinFloors_InChaos_MonoStereo()
    {
        double minG = PodcastChain.Db(-12) * PodcastChain.Db(-10);
        foreach (int sr in Rates)
            for (int ch = 1; ch <= 2; ch++)
            {
                var sv = Enumerable.Range(0, ch).Select(_ => new SpectralVoice(sr, 256, 128)).ToArray();
                const int bins = 129;
                var r = new Rng((ulong)(5 + sr));
                double[] psd = new double[bins], mag = new double[bins], noise = Enumerable.Repeat(1e-6, bins).ToArray(), o = new double[bins];
                for (int frame = 0; frame < 400; frame++)
                    for (int c = 0; c < ch; c++)
                    {
                        bool loud = (frame / 40) % 2 == 0;
                        for (int k = 0; k < bins; k++) { double p = frame % 97 == 0 ? 0 : (loud ? 1e-2 : 1e-6) * (1 + Math.Abs(r.Next())); psd[k] = p; mag[k] = Math.Sqrt(p); }
                        sv[c].Compute(psd, mag, noise, 1, 1, o);
                        Assert.All(o, g => Assert.True(double.IsFinite(g) && g >= minG - 1e-12 && g <= 1 + 1e-12));
                    }
            }
    }

    [Fact]
    public void Dereverb_AttenuatesTail_Dialog_KeepsVoice_CutsNoise()
    {
        const int bins = 129;
        var sv = new SpectralVoice(48000, 256, 128);
        double[] psd = new double[bins], mag = new double[bins], noise = Enumerable.Repeat(1e-9, bins).ToArray(), o = new double[bins];
        double perFrame = Math.Pow(10, -60.0 / 10 / (0.5 * 48000 / 128)), p = 1e-2, tail = 1;
        for (int frame = 0; frame < 120; frame++)
        {
            p = frame < 20 ? 1e-2 : p * perFrame;
            for (int k = 0; k < bins; k++) { psd[k] = p; mag[k] = Math.Sqrt(p); }
            sv.Compute(psd, mag, noise, 0.5, 0, o);
            if (frame == 60) tail = o[20];
        }
        Assert.True(tail < 0.8, $"tail {tail}");
        var sv2 = new SpectralVoice(48000, 256, 128);
        Array.Fill(noise, 1e-6);
        for (int frame = 0; frame < 50; frame++)
        {
            for (int k = 0; k < bins; k++) { double v = k >= 2 && k <= 20 && k % 2 == 0 ? 1e-3 : 2e-6; psd[k] = v; mag[k] = Math.Sqrt(v); }
            sv2.Compute(psd, mag, noise, 0, 0.45, o);
        }
        Assert.True(o[4] > 0.97, $"voice {o[4]}");
        for (int frame = 0; frame < 200; frame++)
        {
            Array.Fill(psd, 1e-6); Array.Fill(mag, 1e-3);
            sv2.Compute(psd, mag, noise, 0, 0.45, o);
        }
        Assert.InRange(o[40], PodcastChain.Db(-4.5) - 1e-9, 0.8);
    }

    [Fact]
    public void Integration_VoiceChain_NeverExceedsCeiling_InChaos_AllPresets()
    {
        int limit = (int)Math.Round(32768 * Ceil) + 1;
        foreach (int sr in Rates)
            foreach (NoiseMode mode in Enum.GetValues<NoiseMode>())
                foreach (bool agc in new[] { true, false })
                {
                    var vc = new VoiceChain(sr) { Podcast = true, Mode = mode, Expander = mode, Agc = agc, GainDb = 24, PodcastParams = PodcastPresets.ForNoise(mode) };
                    var x = Chaos(sr, 1, 1.5, (ulong)(99 + sr + (int)mode));
                    var s = x.Select(v => double.IsFinite(v) ? (short)Math.Clamp((int)(v * 32767), -32768, 32767) : (short)0).ToArray();
                    var block = new short[sr / 100];
                    for (int off = 0; off < s.Length; off += block.Length)
                    {
                        int n = Math.Min(block.Length, s.Length - off);
                        var span = block.AsSpan(0, n);
                        s.AsSpan(off, n).CopyTo(span);
                        vc.Process(span);
                        foreach (var v in span) Assert.True(Math.Abs((int)v) <= limit, $"sr={sr} mode={mode} v={v}");
                    }
                }
    }

    [Fact]
    public void VoiceChain_LatencyPerPreset()
    {
        var vc = new VoiceChain(48000) { Podcast = true, Mode = NoiseMode.Off, PodcastParams = PodcastPresets.Off };
        Assert.Equal(48 + 2, vc.ChainDelayFrames);
        vc.PodcastParams = PodcastPresets.Light;
        Assert.Equal(256 + 72 + 2, vc.ChainDelayFrames);
        vc.Mode = NoiseMode.Strong; vc.PodcastParams = PodcastPresets.Strong;
        Assert.Equal(256 + 96 + 2, vc.ChainDelayFrames);
        vc.Podcast = false;
        Assert.Equal(256, vc.ChainDelayFrames);
    }

    [Fact]
    public void CpuGuard_DropsSpectralStages_WhenTooSlow()
    {
        long t = 0;
        var vc = new VoiceChain(48000) { Podcast = true, Mode = NoiseMode.Off, PodcastParams = PodcastPresets.Light, Clock = () => t += 4_000_000 };
        Assert.True(vc.SpectralActive);
        var b = new short[240];
        for (int i = 0; i < 1000; i++) vc.Process(b);
        Assert.True(vc.Cpu.Tripped);
        Assert.False(vc.SpectralActive);
        Assert.Equal(72 + 2, vc.ChainDelayFrames);
    }

    [Fact]
    public void LegacyChain_IsUnchanged_WhenPodcastOff()
    {
        // The class default keeps the 0.5.x chain bit-exact for its existing tests; only the app turns Podcast on.
        Assert.False(new VoiceChain().Podcast);
        var a = new VoiceChain(48000) { Mode = NoiseMode.Strong };
        var x = Chaos(48000, 1, 1, 7).Select(v => double.IsFinite(v) ? (short)Math.Clamp((int)(v * 32767), -32768, 32767) : (short)0).ToArray();
        var y = (short[])x.Clone();
        a.Process(x);
        var bChain = new VoiceChain(48000) { Mode = NoiseMode.Strong, PodcastParams = PodcastPresets.Strong };
        bChain.Process(y);
        Assert.Equal(x, y);
    }

    [Fact]
    public void FlagChainOff_IsBit7_AndKotlinPresetsMatch()
    {
        Assert.Equal(128, SettingsSync.FlagChainOff);
        var kt = File.ReadAllText(Path.Combine(RepoRoot(), "android/app/src/main/java/dev/cordmic/PodcastChain.kt"));
        Assert.Contains("val OFF = PodcastParams(mudMaxCutDb = 3.0, essMaxCutDb = 5.0, lookaheadMs = 1.0, limReleaseMs = 50.0)", kt);
        Assert.Contains("val LIGHT = PodcastParams(lookaheadMs = 1.5, dereverb = 0.35, dialog = 0.30)", kt);
        Assert.Contains("val STRONG = PodcastParams(lookaheadMs = 2.0, limReleaseMs = 80.0, dereverb = 0.50, dialog = 0.45)", kt);
        Assert.Contains("const val FLAG_CHAIN_OFF = 1 shl 7", File.ReadAllText(Path.Combine(RepoRoot(), "android/app/src/main/java/dev/cordmic/SettingsSync.kt")));
        var d = new PodcastParams();
        Assert.Equal((-17.0, 24.0, 80.0, 250.0, 6500.0, -22.0, 3.0, 10.0, -1.0), (d.AgcTargetDbfs, d.AgcMaxDb, d.HpHz, d.MudHz, d.EssHz, d.CompThreshDbfs, d.CompRatio, d.CompKneeDb, d.LimCeilingDbfs));
        Assert.Contains("val agcTargetDbfs: Double = -17.0", kt);
        Assert.Contains("val compRatio: Double = 3.0", kt);
    }

    internal static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "CHANGELOG.md"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }
}

/// <summary>1.1.0 Studio integration, settings migration, release pins.</summary>
public class Release110Tests
{
    [Fact]
    public void Studio_WithVoiceChain_NeverExceedsCeiling_AndReportsDelay()
    {
        if (!Rnnoise.IsAvailable) return;
        foreach (var preset in new[] { StudioPreset.Balanced, StudioPreset.Whisper, StudioPreset.Max })
        {
            using var c = new StudioChain(preset, autoVolume: true, voiceChain: true);
            Assert.True(c.VoiceChainOn);
            Assert.Equal(96 + 256 + 2 + 2, c.VoiceChainDelay);
            Assert.True(c.ChainLatencyMs < 28);
            var x = PodcastChainTests.Chaos(48000, 1, 3, (ulong)preset);
            var f = new float[StudioChain.Frame];
            for (int o = 0; o + f.Length <= x.Length; o += f.Length)
            {
                for (int i = 0; i < f.Length; i++) f[i] = (float)(double.IsFinite(x[o + i]) ? Math.Clamp(x[o + i], -1, 1) : 0);
                c.Process(f);
                foreach (var v in f) Assert.True(float.IsFinite(v) && Math.Abs(v) <= PodcastChainTests.Ceil + 1e-6, $"{preset} {v}");
            }
        }
    }

    [Fact]
    public void Studio_WithoutVoiceChain_KeepsB3AndZeroExtraDelay()
    {
        if (!Rnnoise.IsAvailable) return;
        using var c = new StudioChain(StudioPreset.Balanced, autoVolume: true);
        Assert.False(c.VoiceChainOn);
        Assert.Equal(0, c.VoiceChainDelay);
        Assert.True(c.Agc.Enabled);
    }

    [Fact]
    public void Settings_FromSchema2_MigrateToVoiceChainOn()
    {
        var path = Path.Combine(Path.GetTempPath(), "cordmic-110-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{"Version":2,"AutoUpdate":false,"NoiseMode":"Strong","VoiceClarity":false}""");
            var s = AppSettings.Load(path);
            Assert.True(s.PodcastVoiceChain);
            var note = s.MigrateSchema(() => false);
            Assert.NotNull(note);
            Assert.Contains("podcast voice chain on", note);
            Assert.DoesNotContain("automatic update", note); // schema 2 already decided auto-update; keep the user's off
            Assert.False(s.AutoUpdate);
            Assert.Equal(3, s.Version);
            Assert.Equal(NoiseMode.Strong, s.NoiseMode);
            Assert.False(s.VoiceClarity);
            Assert.Null(s.MigrateSchema(() => false));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Settings_FromSchema1_StillRunsAutoUpdateMigration_AndChainOn()
    {
        var s = new AppSettings { Version = 1, AutoUpdate = false, PodcastVoiceChain = false };
        var note = s.MigrateSchema(() => false)!;
        Assert.Contains("automatic update downloads turned on", note);
        Assert.Contains("podcast voice chain on", note);
        Assert.True(s.AutoUpdate);
        Assert.True(s.PodcastVoiceChain);
    }

    [Fact]
    public void Settings_ExplicitOffInSchema3_StaysOff()
    {
        var path = Path.Combine(Path.GetTempPath(), "cordmic-110-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{"Version":3,"PodcastVoiceChain":false}""");
            var s = AppSettings.Load(path);
            Assert.Null(s.MigrateSchema(() => false));
            Assert.False(s.PodcastVoiceChain);
            s.ResetAudioDefaults();
            Assert.True(s.PodcastVoiceChain);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SettingsCatalog_HasPodcastToggle_Localized()
    {
        var items = SettingsCatalog.Items(SettingsPage.NoiseVoice);
        Assert.Contains(items, i => i.Id == "podcast" && i.Kind == SettingKind.Toggle);
    }

    [Fact]
    public void AppVersion_Is110_VersionCode26_WireVersionUnchanged()
    {
        var root = PodcastChainTests.RepoRoot();
        var csproj = File.ReadAllText(Path.Combine(root, "windows-app/src/CordMic.App/CordMic.App.csproj"));
        Assert.Contains("<Version>1.1.0</Version>", csproj);
        var gradle = File.ReadAllText(Path.Combine(root, "android/app/build.gradle.kts"));
        Assert.Contains("versionCode = 26", gradle);
        Assert.Contains("versionName = \"1.1.0\"", gradle);
        Assert.Equal(1, Protocol.Version);
        Assert.False(CordMic.Core.Usb.UsbEngineGate.HardwareGateGm1Passed);
    }
}

/// <summary>CPU cost per stage (printed) with a loose real-time gate; runs alone in the cpu-timing collection.</summary>
[Collection("cpu-timing")]
public class PodcastChainBenchmarkTests
{
    private readonly ITestOutputHelper _out;
    public PodcastChainBenchmarkTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void CpuBenchmark_PerStage()
    {
        const int sr = 48000, n = sr * 5;
        var x0 = PodcastChainTests.Chaos(sr, 1, 5, 4242).Select(v => double.IsFinite(v) ? Math.Clamp(v, -1, 1) : 0).ToArray();
        double Bench(string name, Action<double[]> run)
        {
            run((double[])x0.Clone());
            var y = (double[])x0.Clone();
            var sw = Stopwatch.StartNew(); run(y); sw.Stop();
            double ns = sw.Elapsed.TotalMilliseconds * 1e6 / n, rt = sw.Elapsed.TotalSeconds / (n / (double)sr) * 100;
            _out.WriteLine($"BENCH dotnet {name}: {ns:F1} ns/sample, {rt:F3} % of real time");
            return rt;
        }
        string[] names = { "agc", "dynamic-eq", "compressor", "limiter", "podcast-all" };
        for (int stage = 0; stage <= 4; stage++)
        {
            int st = stage;
            Bench(names[st], x => { var pc = new PodcastChain(sr, 1, PodcastPresets.Strong); PodcastChainTests.Configure(pc, st); for (int o = 0; o < n; o += 240) pc.Process(x, Math.Min(240, n - o), o); });
        }
        Bench("true-peak-guard", x => { var g = new TruePeakGuard(sr); for (int i = 0; i < n; i++) x[i] = g.Process(x[i]); });
        var s0 = x0.Select(v => (short)(v * 32767)).ToArray();
        double Vc(string name, Action<VoiceChain> setup)
        {
            long Run()
            {
                var vc = new VoiceChain(sr); setup(vc);
                var s = (short[])s0.Clone();
                var sw = Stopwatch.StartNew();
                for (int o = 0; o < n; o += 240) vc.Process(s.AsSpan(o, Math.Min(240, n - o)));
                return sw.ElapsedTicks;
            }
            Run(); long ticks = Run();
            double sec = ticks / (double)Stopwatch.Frequency, rt = sec / (n / (double)sr) * 100;
            _out.WriteLine($"BENCH dotnet {name}: {sec * 1e9 / n:F1} ns/sample, {rt:F3} % of real time");
            return rt;
        }
        Vc("voicechain-legacy-strong", v => { v.Mode = NoiseMode.Strong; v.Expander = NoiseMode.Strong; });
        Vc("voicechain-podcast-strong-ns-only", v => { v.Mode = NoiseMode.Strong; v.Podcast = true; v.PodcastParams = PodcastPresets.Strong with { Dereverb = 0, Dialog = 0 }; });
        Vc("voicechain-podcast-off-preset", v => { v.Mode = NoiseMode.Off; v.Podcast = true; v.PodcastParams = PodcastPresets.Off; });
        Vc("voicechain-podcast-light-spectral-only", v => { v.Mode = NoiseMode.Off; v.Podcast = true; v.PodcastParams = PodcastPresets.Light; });
        double full = Vc("voicechain-podcast-strong-full", v => { v.Mode = NoiseMode.Strong; v.Expander = NoiseMode.Strong; v.Podcast = true; v.PodcastParams = PodcastPresets.Strong; });
        Assert.True(full < 25, $"full chain {full} % of real time");
    }
}
