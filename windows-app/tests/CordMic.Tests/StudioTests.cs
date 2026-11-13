using System.Diagnostics;
using CordMic.Core;
using CordMic.Core.Dsp;
using Xunit.Abstractions;

namespace CordMic.Tests;

/// <summary>0.6.0 Studio NR: RNNoise wrapper, alignment, WhisperGuard, AGC B3, worker and fallback.</summary>
[Collection("cpu-timing")]
public class StudioTests
{
    private readonly ITestOutputHelper _out;
    public StudioTests(ITestOutputHelper o) { _out = o; }
    private const int Fs = 48000, F = 480;

    /// <summary>Voice-like test signal: glottal-pulse harmonics with a gliding pitch, syllable envelope and formant-ish tilt.</summary>
    internal static float[] Voice(int n, double level = 0.1, int seed = 3)
    {
        var y = new float[n];
        var rnd = new Random(seed);
        double ph = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Fs;
            double f0 = 130 + 30 * Math.Sin(2 * Math.PI * 0.7 * t);
            ph += 2 * Math.PI * f0 / Fs;
            double v = 0;
            for (int h = 1; h <= 60 && h * f0 < 20000; h++) v += Math.Sin(h * ph) / (h * (1 + Math.Pow(h * f0 / 3000, 2)));
            double env = Math.Max(0, Math.Sin(2 * Math.PI * 2.5 * t)); // syllables with pauses
            y[i] = (float)(level * env * (v + 1.0 * (rnd.NextDouble() - 0.5)) + 1e-4 * (rnd.NextDouble() - 0.5));
        }
        return y;
    }

    private static bool Rn() => Rnnoise.IsAvailable;

    [Fact]
    public void Nr9_RnnoiseDelayMatchesCompensation()
    {
        if (!Rn()) { _out.WriteLine("RNNoise library not present; skipped"); return; }
        var x = Voice(Fs * 4);
        var y = new float[x.Length];
        using var rn = new Rnnoise();
        for (int f = 0; f + F <= x.Length; f += F) rn.Process(x.AsSpan(f, F), y.AsSpan(f, F));
        int best = -1; double bc = double.MinValue;
        for (int lag = 0; lag <= 2000; lag++)
        {
            double c = 0, e = 0;
            for (int i = Fs; i < x.Length - 2000; i++) { c += x[i] * y[i + lag]; e += y[i + lag] * y[i + lag]; }
            c /= Math.Sqrt(e + 1e-20);
            if (c > bc) { bc = c; best = lag; }
        }
        _out.WriteLine($"NR-9 measured RNNoise delay {best} samples (compensation {StudioChain.RnnoiseDelay})");
        // RNNoise is time-varying, so a cross-correlation peak on a synthetic signal can sit a sample off the true
        // delay; the regression harness confirms 960 exactly on LibriSpeech (tools/harness, NR-9).
        Assert.InRange(best, StudioChain.RnnoiseDelay - 1, StudioChain.RnnoiseDelay);
    }

    private static float[] Noise(int n, double rms, int seed)
    {
        var r = new Random(seed); var y = new float[n];
        double lp = 0;
        for (int i = 0; i < n; i++) { double w = (r.NextDouble() - 0.5) * Math.Sqrt(12); lp = 0.7 * lp + 0.3 * w; y[i] = (float)(rms * 1.8 * lp); }
        return y;
    }

    [Theory]
    [InlineData(StudioPreset.Balanced)]
    [InlineData(StudioPreset.Whisper)]
    [InlineData(StudioPreset.Max)]
    public void StudioChainReducesNoiseAndKeepsVoice(StudioPreset preset)
    {
        if (!Rn()) return;
        int n = Fs * 6;
        var v = Voice(n, 0.1); var nz = Noise(n, 0.01, 5);
        var x = new float[n]; for (int i = 0; i < n; i++) x[i] = v[i] + nz[i];
        var noiseOnly = (float[])nz.Clone();
        using var c1 = new StudioChain(preset, autoVolume: false);
        using var c2 = new StudioChain(preset, autoVolume: false);
        for (int f = 0; f + F <= n; f += F) { c1.Process(x.AsSpan(f, F)); c2.Process(noiseOnly.AsSpan(f, F)); }
        double Rms(float[] a, int from) { double e = 0; for (int i = from; i < a.Length; i++) e += a[i] * a[i]; return Math.Sqrt(e / (a.Length - from)); }
        double noiseIn = Rms(nz, Fs * 2), noiseOut = Rms(noiseOnly, Fs * 2), speechOut = Rms(x, Fs * 2), speechIn = Rms(v, Fs * 2);
        double nr = 20 * Math.Log10(noiseIn / noiseOut), keep = 20 * Math.Log10(speechOut / speechIn);
        _out.WriteLine($"{preset}: steady noise reduced {nr:F1} dB, voice level change {keep:F1} dB");
        Assert.True(nr >= 10, nr.ToString());
        // RNNoise does not recognise this synthetic voice as speech, so Max (no guard) attenuates it; the voice
        // preservation gates for Max use real speech in the regression harness (NR-7).
        if (preset != StudioPreset.Max) Assert.InRange(keep, -6, 1);
        Assert.All(x, s => Assert.True(float.IsFinite(s)));
    }

    /// <summary>
    /// NR-10 / CPU-3, deterministic since 0.6.1 (audit L11). The wall-clock p99.9 limit failed on loaded shared VMs, so
    /// the test now checks what makes the cost bounded: zero allocations on the processing thread over 70 s of audio, and exactly one
    /// RNNoise frame and one Strong-suppressor frame per 10 ms frame (no hidden reprocessing). Timing is printed for
    /// information; the real-time margin is enforced at runtime by the StudioProcessor fallback (3 missed deadlines).
    /// </summary>
    [Fact]
    public void Nr10_Cpu3_StudioChainCostAndNoAllocations()
    {
        if (!Rn()) return;
        int n = Fs * 70;
        var v = Voice(Fs * 10, 0.05); var nz = Noise(Fs * 10, 0.005, 9);
        var frame = new float[F];
        using var c = new StudioChain(StudioPreset.Balanced, autoVolume: true);
        var times = new double[n / F];
        var sw = new Stopwatch();
        for (int i = 0; i < 300; i++) { for (int j = 0; j < F; j++) frame[j] = v[(i * F + j) % v.Length] + nz[(i * F + j) % nz.Length]; c.Process(frame); }
        long rn0 = c.RnnoiseFrames, st0 = c.StrongFrames;
        long gc0 = GC.CollectionCount(0);
        long alloc0 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < times.Length; i++)
        {
            int o = (i * F) % v.Length;
            for (int j = 0; j < F; j++) frame[j] = v[(o + j) % v.Length] + nz[(o + j) % nz.Length];
            sw.Restart();
            c.Process(frame);
            times[i] = sw.Elapsed.TotalMilliseconds;
        }
        long alloc = GC.GetAllocatedBytesForCurrentThread() - alloc0;
        long gcs = GC.CollectionCount(0) - gc0;
        Array.Sort(times);
        double mean = times.Average(), p999 = times[(int)(times.Length * 0.999)];
        _out.WriteLine($"NR-10 Studio chain: {times.Length} frames, rnnoise {c.RnnoiseFrames - rn0}, strong {c.StrongFrames - st0}; allocated {alloc} B, gen0 GCs {gcs}; " +
                       $"info: mean {mean:F3} ms, p99.9 {p999:F3} ms, max {times[^1]:F3} ms per 10 ms frame");
        Assert.Equal(0, alloc); // per-thread counter: other tests running in parallel do not affect it
        Assert.Equal(times.Length, c.RnnoiseFrames - rn0);
        Assert.Equal(times.Length, c.StrongFrames - st0);
    }

    [Fact]
    public void LightModeCostUnderBudget()
    {
        var vc = new VoiceChain(48000) { Mode = NoiseMode.Strong };
        var v = Voice(Fs * 2, 0.05);
        var s = new short[F];
        var sw = new Stopwatch();
        for (int i = 0; i < 200; i++) { for (int j = 0; j < F; j++) s[j] = (short)(v[(i * F + j) % v.Length] * 32767); vc.Process(s); }
        int N = 3000; double tot = 0;
        for (int i = 0; i < N; i++)
        {
            for (int j = 0; j < F; j++) s[j] = (short)(v[(i * F + j) % v.Length] * 32767);
            sw.Restart(); vc.Process(s); tot += sw.Elapsed.TotalMilliseconds;
        }
        _out.WriteLine($"CPU Light (0.5.7 Strong): {tot / N:F3} ms per frame");
        Assert.True(tot / N <= 0.2);
    }

    [Fact]
    public void WhisperGuardMaxBlendAndPresetTable()
    {
        var b = WhisperGuard.ForPreset(StudioPreset.Balanced);
        Assert.Equal((12.0, 6.0, 15.0, 0.7), (b.Thr, b.Lo, b.Hi, b.MMax));
        var w = WhisperGuard.ForPreset(StudioPreset.Whisper);
        Assert.Equal((9.0, 6.0, 15.0, 1.0), (w.Thr, w.Lo, w.Hi, w.MMax));
    }

    [Fact]
    public void WhisperGuardMixesBackOnlyWhenSpeechIsPresentAndSuppressed()
    {
        var g = WhisperGuard.ForPreset(StudioPreset.Whisper);
        var nz = Noise(Fs * 4, 0.001, 2); var v = Voice(Fs * 4, 0.02);
        var x = new float[F]; var r = new float[F]; var k = new float[F]; var y = new float[F];
        double mixNoise = 0, mixVoice = 0;
        for (int f = 0; f < 200; f++) // 2 s noise only: RNNoise removes it, but nothing is present
        {
            for (int i = 0; i < F; i++) { x[i] = nz[f * F + i]; r[i] = x[i] * 0.05f; k[i] = x[i] * 0.3f; }
            g.Process(x, r, k, y); mixNoise = Math.Max(mixNoise, g.Mix);
        }
        for (int f = 200; f < 400; f++) // whisper over the noise that "RNNoise" removed (20 dB down): mix back
        {
            for (int i = 0; i < F; i++) { x[i] = nz[f * F + i] + v[f * F + i]; r[i] = x[i] * 0.1f; k[i] = x[i] * 0.8f; }
            g.Process(x, r, k, y); mixVoice = Math.Max(mixVoice, g.Mix);
        }
        _out.WriteLine($"guard mix: noise-only max {mixNoise:F2}, whisper max {mixVoice:F2}");
        Assert.True(mixNoise < 0.05);
        Assert.True(mixVoice > 0.9);
    }

    [Fact]
    public void Agc_ReachesTargetHoldsInPausesAndLimitsPeaks()
    {
        // Far speech at about -40 dBFS with RNNoise VAD 1 during syllables, a quiet floor at -75 dBFS.
        var agc = new AgcB3(0);
        int n = Fs * 20;
        var v = Voice(n, 0.012, 4); var nz = Noise(n, 0.0002, 6);
        var frame = new float[F]; var outp = new float[n];
        double gainBeforePause = 0, maxChangeInPause = 0;
        for (int f = 0; f + F <= n; f += F)
        {
            bool pause = f >= Fs * 12 && f < Fs * 18;
            double env = 0;
            for (int i = 0; i < F; i++) { frame[i] = (pause ? 0 : v[f + i]) + nz[f + i]; env = Math.Max(env, Math.Abs(v[f + i])); }
            double vad = !pause && env > 0.004 ? 0.95 : 0.02;
            if (f == Fs * 12) gainBeforePause = agc.GainDb;
            agc.Process(frame, vad);
            if (pause) maxChangeInPause = Math.Max(maxChangeInPause, Math.Abs(agc.GainDb - gainBeforePause));
            frame.CopyTo(outp, f);
        }
        // loudness of speech frames in the last 4 s before the pause (K-weighting ignored: speech-band signal)
        double e = 0; int cnt = 0;
        for (int f = Fs * 8; f < Fs * 12; f += F)
        {
            double fe = 0; for (int i = 0; i < F; i++) fe += outp[f + i] * outp[f + i];
            if (Math.Abs(v[f + F / 2]) > 0 && fe / F > 1e-5) { e += fe / F; cnt++; }
        }
        double lufsApprox = -0.691 + 10 * Math.Log10(e / cnt);
        double peak = outp.Max(Math.Abs);
        _out.WriteLine($"AGC: gain {gainBeforePause:F1} dB, speech level {lufsApprox:F1} (target -21), pause gain change {maxChangeInPause:F2} dB, peak {20 * Math.Log10(peak):F2} dBFS, floor {agc.FloorDbfs:F1}");
        Assert.InRange(lufsApprox, -24, -18);
        Assert.True(maxChangeInPause <= 1.0);
        Assert.True(peak <= Math.Pow(10, -1.0 / 20) + 1e-3);
    }

    [Fact]
    public void Agc_DisabledKeepsGain()
    {
        var agc = new AgcB3(0) { Enabled = false };
        var v = Voice(Fs * 3, 0.01);
        var frame = new float[F];
        for (int f = 0; f + F <= v.Length; f += F) { v.AsSpan(f, F).CopyTo(frame); agc.Process(frame, 1); }
        Assert.Equal(0, agc.GainDb);
    }

    [Fact]
    public void Hint1_SyntheticTraces()
    {
        HintAfter(12, false).Should(true, "12 dB -> shown");
        HintAfter(16, false).Should(false, "16 dB from hidden -> unchanged");
        HintAfter(16, true).Should(true, "16 dB from shown -> unchanged");
        HintAfter(20, true).Should(false, "20 dB -> hidden");
    }

    private static bool HintAfter(double snrDb, bool startVisible)
    {
        var h = new SnrHint();
        double noiseP = Math.Pow(10, -60 / 10.0);
        if (startVisible) Feed(h, 8, noiseP, 2500);
        Assert.Equal(startVisible, h.Visible);
        Feed(h, snrDb, noiseP, 2500);
        return h.Visible;
    }

    private static void Feed(SnrHint h, double snrDb, double noiseP, int frames)
    {
        // alternating 0.5 s of speech (noise + speech power) and 0.3 s of pause
        double speechP = noiseP * Math.Pow(10, snrDb / 10);
        for (int i = 0; i < frames; i++) h.AddFramePower(i % 80 < 50 ? noiseP + speechP : noiseP);
    }

    [Fact]
    public void HintNeedsTenSecondsBeforeShowing()
    {
        var h = new SnrHint();
        Feed(h, 20, 1e-6, 1000);
        Feed(h, 10, 1e-6, 600);
        Assert.False(h.Visible);
    }

    [Fact]
    public void StudioProcessorDeliversAudioWithoutMisses()
    {
        if (!Rn()) return;
        var v = Voice(Fs * 4, 0.05);
        int pos = 0;
        void Pull(Span<byte> dst)
        {
            for (int i = 0; i < dst.Length / 2; i++) { short s = (short)(v[pos++ % v.Length] * 32767); dst[2 * i] = (byte)s; dst[2 * i + 1] = (byte)(s >> 8); }
        }
        using var sp = new StudioProcessor(new StudioChain(StudioPreset.Balanced, false), Pull, 10);
        bool fell = false; sp.FellBack += _ => fell = true;
        var buf = new byte[480 * 2];
        Thread.Sleep(50);
        double energy = 0;
        var sw = Stopwatch.StartNew();
        for (int k = 0; k < 300; k++)
        {
            while (sw.Elapsed.TotalMilliseconds < k * 10) Thread.Sleep(1);
            sp.Pull(buf);
            for (int i = 0; i < 480; i++) { short s = (short)(buf[2 * i] | (buf[2 * i + 1] << 8)); energy += s * (double)s; }
        }
        _out.WriteLine($"processor misses {sp.Misses}, cpu {sp.TakeCpu()}");
        Assert.False(fell);
        Assert.True(energy > 0);
        Assert.True(sp.Misses <= 1);
    }

    [Fact]
    public void Cpu4_ThreeMissesInOneSecondFallBack()
    {
        if (!Rn()) return;
        int calls = 0;
        void SlowPull(Span<byte> dst) { dst.Clear(); if (++calls > 60) Thread.Sleep(25); }
        using var sp = new StudioProcessor(new StudioChain(StudioPreset.Balanced, false), SlowPull, 10);
        string? why = null; var fired = new ManualResetEventSlim();
        sp.FellBack += w => { why = w; fired.Set(); };
        var buf = new byte[480 * 2];
        var sw = Stopwatch.StartNew();
        for (int k = 0; k < 400 && !fired.IsSet; k++)
        {
            while (sw.Elapsed.TotalMilliseconds < k * 10) Thread.Sleep(1);
            sp.Pull(buf);
        }
        Assert.True(fired.Wait(2000));
        _out.WriteLine($"fallback after {sw.Elapsed.TotalMilliseconds:F0} ms: {why}");
        Assert.True(sp.HasFallenBack);
    }
}

internal static class BoolAssert
{
    public static void Should(this bool actual, bool expected, string what) => Assert.True(actual == expected, what);
}

public class StrongDelayTests
{
    private readonly ITestOutputHelper _o; public StrongDelayTests(ITestOutputHelper o) { _o = o; }
    /// <summary>The Studio chain delays the PC Strong branch to line up with RNNoise; this pins the Strong delay.</summary>
    [Fact]
    public void VoiceChainStrongDelayMatchesStudioCompensation()
    {
        var x = StudioTests.Voice(48000 * 4, 0.1);
        var vc = new VoiceChain(48000) { Mode = NoiseMode.Strong, Agc = false, Expander = NoiseMode.Off, VoiceClarity = false, GainDb = 0 };
        var s = new short[x.Length]; for (int i = 0; i < x.Length; i++) s[i] = (short)(x[i] * 32767);
        for (int f = 0; f < s.Length; f += 480) vc.Process(s.AsSpan(f, 480));
        int best = 0; double bc = double.MinValue;
        for (int lag = 0; lag < 600; lag++) { double c = 0; for (int i = 48000; i < x.Length - 600; i++) c += x[i] * s[i + lag]; if (c > bc) { bc = c; best = lag; } }
        _o.WriteLine($"Strong delay {best}");
        Assert.Equal(CordMic.Core.Dsp.StudioChain.StrongDelay, best);
    }
}
