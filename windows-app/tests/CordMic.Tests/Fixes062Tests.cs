using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CordMic.Core;
using CordMic.Core.Dsp;
using Xunit.Abstractions;

namespace CordMic.Tests;

/// <summary>0.6.2 regression tests: fixes from the 0.6.1 recording and log analysis.</summary>
[Collection("cpu-timing")]
public class Fixes062Tests
{
    private readonly ITestOutputHelper _out;
    public Fixes062Tests(ITestOutputHelper o) { _out = o; }
    private const int Fs = 48000, F = 480;

    private static double Db(ReadOnlySpan<float> x) { double e = 0; foreach (var v in x) e += v * (double)v; return 10 * Math.Log10(e / x.Length + 1e-30); }

    // ---- 1. Balanced presence breathing ---------------------------------------------------------------------------

    [Fact]
    public void Guard_ShortBumpsUnder300msDoNotOpen()
    {
        var g = WhisperGuard.ForPreset(StudioPreset.Balanced);
        var nz = StudioTests.Noise(Fs * 10, 0.001, 9);
        var x = new float[F]; var r = new float[F]; var k = new float[F]; var y = new float[F];
        double maxMix = 0;
        for (int f = 0; f < 1000; f++)
        {
            // every second a 200 ms bump 20 dB above the floor that "RNNoise" removes (a knock, a passing car)
            bool bump = f >= 200 && f % 100 < 20;
            for (int i = 0; i < F; i++) { x[i] = nz[f * F + i] * (bump ? 10f : 1f); r[i] = x[i] * 0.05f; k[i] = x[i] * 0.3f; }
            g.Process(x, r, k, y);
            if (f >= 200) maxMix = Math.Max(maxMix, g.Mix);
        }
        _out.WriteLine($"max mix over 200 ms bumps: {maxMix:F3}");
        Assert.True(maxMix < 0.02);
    }

    [Fact]
    public void Guard_SustainedPresenceOpensAfter300msAndVoiceOpensAtOnce()
    {
        var nz = StudioTests.Noise(Fs * 6, 0.001, 9);
        int OpenFrame(double vad)
        {
            var g = WhisperGuard.ForPreset(StudioPreset.Balanced);
            var x = new float[F]; var r = new float[F]; var k = new float[F]; var y = new float[F];
            for (int f = 0; f < 600; f++)
            {
                bool on = f >= 200;
                for (int i = 0; i < F; i++) { x[i] = nz[f * F + i] * (on ? 10f : 1f); r[i] = x[i] * 0.05f; k[i] = x[i] * 0.3f; }
                g.Process(x, r, k, y, on ? vad : 0);
                if (g.Mix > 0.3) return f - 200;
            }
            return -1;
        }
        int energyOnly = OpenFrame(0), withVoice = OpenFrame(0.9);
        _out.WriteLine($"guard opens after {energyOnly} frames (energy only), {withVoice} frames (RNNoise voice)");
        Assert.InRange(energyOnly, WhisperGuard.ArmFrames - 1, WhisperGuard.ArmFrames + 5);
        Assert.InRange(withVoice, 0, 3);
    }

    [Fact]
    public void ComfortNoise_IsAtMinus65Dbfs()
    {
        var cn = new ComfortNoise();
        var x = new float[Fs * 2];
        cn.AddTo(x);
        double lvl = Db(x);
        _out.WriteLine($"comfort noise {lvl:F2} dBFS");
        Assert.InRange(lvl, -66, -64);
    }

    [Theory]
    [InlineData(StudioPreset.Balanced)]
    [InlineData(StudioPreset.Max)]
    public void Chain_PausesNeverReachDigitalZeroAndStayNearTheFloor(StudioPreset preset)
    {
        if (!Rnnoise.IsAvailable) { _out.WriteLine("RNNoise library not present; skipped"); return; }
        using var chain = new StudioChain(preset, autoVolume: true, startGainDb: 15);
        var v = StudioTests.Voice(Fs * 3, 0.05);
        var fr = new float[F];
        var pauseDb = new List<double>();
        for (int f = 0; f < 600; f++) // 3 s speech, then 3 s of digital silence (a muted or gated capture)
        {
            for (int i = 0; i < F; i++) fr[i] = f < 300 ? v[f * F + i] : 0f;
            chain.Process(fr);
            if (f >= 330) pauseDb.Add(Db(fr));
        }
        double min = pauseDb.Min(), max = pauseDb.Max();
        double sp = chain.Agc.OutputLoudnessLufs;
        double expect = Math.Clamp(sp - StudioChain.ComfortBelowSpeechDb, -90, ComfortNoise.DefaultDbfs);
        _out.WriteLine($"{preset}: speech {sp:F1} dBFS, pause level {min:F1}..{max:F1} dBFS, expected comfort {expect:F1}");
        Assert.True(min > -92, "pauses must not fall to digital zero");
        Assert.True(max <= ComfortNoise.DefaultDbfs + 2, "the comfort floor is at most about -65 dBFS");
        Assert.InRange((min + max) / 2, expect - 4, expect + 4);
        Assert.True(max - min < 6, "pause level spread");
    }

    [Fact]
    public void Chain_ComfortReachesMinus65AtTheAutoVolumeTarget()
    {
        if (!Rnnoise.IsAvailable) return;
        using var chain = new StudioChain(StudioPreset.Balanced, autoVolume: false, startGainDb: 0);
        var v = StudioTests.Voice(Fs * 4, 0.25); // loud speech, about -20 dBFS on syllables
        var fr = new float[F]; var pauseDb = new List<double>();
        for (int f = 0; f < 700; f++)
        {
            for (int i = 0; i < F; i++) fr[i] = f < 400 ? v[f * F + i] : 0f;
            chain.Process(fr);
            if (f >= 430) pauseDb.Add(Db(fr));
        }
        _out.WriteLine($"comfort at loud speech: {pauseDb.Min():F1}..{pauseDb.Max():F1} dBFS");
        Assert.InRange(pauseDb.Average(), -68, -63);
    }

    // ---- 2. Studio gap concealment and buffering -------------------------------------------------------------------

    [Fact]
    public void Studio_AddsTwoFramesOfBufferingAnd40ms()
    {
        Assert.Equal(2, StudioProcessor.BufferFrames);
        Assert.Equal(40.0, StudioProcessor.AddedLatencyMs, 3);
    }

    private static short[] Sine(int n, double hz, double amp, int start = 0)
    {
        var s = new short[n];
        for (int i = 0; i < n; i++) s[i] = (short)Math.Round(amp * 32767 * Math.Sin(2 * Math.PI * hz * (start + i) / Fs));
        return s;
    }

    [Fact]
    public void GapConcealer_RepeatsThePitchThroughA9msHole()
    {
        var gc = new GapConcealer();
        var a = Sine(4800, 200, 0.3);
        gc.Real(a);
        var gap = new short[432]; // 9 ms, the hole seen in the 0.6.1 recording
        gc.Conceal(gap);
        var b = Sine(960, 200, 0.3, 4800 + 432);
        gc.Real(b);
        double eGap = 0; foreach (var s in gap) eGap += s * (double)s;
        double rmsGap = Math.Sqrt(eGap / gap.Length) / 32767, rmsRef = 0.3 / Math.Sqrt(2);
        // continuity: no step larger than the tone's own slope (plus slack for the crossfade) at either junction
        double slope = 0.3 * 32767 * 2 * Math.PI * 200 / Fs;
        double step1 = Math.Abs(gap[0] - a[^1]), maxStepB = 0;
        for (int i = 1; i < 96; i++) maxStepB = Math.Max(maxStepB, Math.Abs(b[i] - b[i - 1]));
        _out.WriteLine($"gap rms {20 * Math.Log10(rmsGap / rmsRef):F1} dB re tone, junction step {step1:F0}, resume max step {maxStepB:F0}, slope {slope:F0}");
        Assert.True(rmsGap > rmsRef * 0.9, "the hole is filled at full level, not faded");
        Assert.True(step1 <= slope * 1.5);
        Assert.True(maxStepB <= slope * 2.5);
        Assert.Equal(1, gc.Gaps);
        Assert.Equal(432, gc.ConcealedSamples);
    }

    [Fact]
    public void GapConcealer_PitchSearchIsCheapEnoughForTheRenderThread()
    {
        var gc = new GapConcealer();
        var a = Sine(4800, 180, 0.3); var gap = new short[48];
        for (int i = 0; i < 20; i++) { gc.Real(a); gc.Conceal(gap); } // warm up (JIT)
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 200; i++) { gc.Real(a.AsSpan(0, 480)); gc.Conceal(gap); }
        double ms = sw.Elapsed.TotalMilliseconds / 200;
        _out.WriteLine($"gap start incl. pitch search: {ms:F3} ms");
        Assert.True(ms < 1.0);
    }

    [Fact]
    public void GapConcealer_LongGapFadesToSilence()
    {
        var gc = new GapConcealer();
        gc.Real(Sine(4800, 150, 0.3));
        var gap = new short[4800];
        gc.Conceal(gap);
        Assert.True(gap.Skip(GapConcealer.FullLen + GapConcealer.FadeLen).All(s => s == 0));
        Assert.Contains(gap.Take(GapConcealer.FullLen), s => Math.Abs((int)s) > 3000);
    }

    [Fact]
    public void StudioProcessor_CountsAndConcealsUnderflows()
    {
        if (!Rnnoise.IsAvailable) return;
        var v = StudioTests.Voice(Fs * 4, 0.05);
        int pos = 0;
        void Pull(Span<byte> dst) { for (int i = 0; i < dst.Length / 2; i++) { short s = (short)(v[pos++ % v.Length] * 32767); dst[2 * i] = (byte)s; dst[2 * i + 1] = (byte)(s >> 8); } }
        using var sp = new StudioProcessor(new StudioChain(StudioPreset.Balanced, false), Pull, 10);
        var buf = new byte[F * 2];
        var sw = Stopwatch.StartNew();
        for (int k = 0; k < 120; k++) { while (sw.Elapsed.TotalMilliseconds < k * 10) Thread.Sleep(1); sp.Pull(buf); }
        Assert.Equal(0, sp.Underflows);
        var big = new byte[9600 * 2]; // a 200 ms pull cannot be covered by the ring: an underflow
        sp.Pull(big);
        Assert.True(sp.Underflows >= 1);
        Assert.True(sp.ConcealedSamples > 0);
        _out.WriteLine($"underflows {sp.Underflows}, concealed {sp.ConcealedSamples} samples, misses {sp.Misses}");
    }

    [Fact]
    public void StudioProcessor_PresetSwapIsSeamless()
    {
        if (!Rnnoise.IsAvailable) return;
        var v = StudioTests.Voice(Fs * 4, 0.05);
        int pos = 0;
        void Pull(Span<byte> dst) { for (int i = 0; i < dst.Length / 2; i++) { short s = (short)(v[pos++ % v.Length] * 32767); dst[2 * i] = (byte)s; dst[2 * i + 1] = (byte)(s >> 8); } }
        using var sp = new StudioProcessor(new StudioChain(StudioPreset.Balanced, true, 6), Pull, 10);
        var buf = new byte[F * 2];
        var sw = Stopwatch.StartNew();
        bool swapped = false;
        for (int k = 0; k < 250; k++)
        {
            while (sw.Elapsed.TotalMilliseconds < k * 10) Thread.Sleep(1);
            sp.Pull(buf);
            if (k == 100) { swapped = sp.ReplaceChain(new StudioChain(StudioPreset.Max, true, sp.AgcGainDb)); }
        }
        _out.WriteLine($"swaps {sp.Swaps}, preset {sp.Preset}, underflows {sp.Underflows}, gain {sp.AgcGainDb:F1}");
        Assert.True(swapped);
        Assert.Equal(1, sp.Swaps);
        Assert.Equal(StudioPreset.Max, sp.Preset);
        Assert.Equal(0, sp.Underflows);
    }

    // ---- 3. Drift control and in-place retarget --------------------------------------------------------------------

    [Fact]
    public void DriftController_DeadBandAndGain()
    {
        var dc = new DriftController(1000, DriftController.KpTuned) { DeadBand = 96 };
        for (int i = 0; i < 500; i++) dc.Update(1000 + 80);
        Assert.Equal(1.0, dc.Ratio, 9);
        dc.Gain = DriftController.KpTuned / 2;
        Assert.Equal(DriftController.KpTuned / 2, dc.Gain);
        var plain = new DriftController(1000, DriftController.KpTuned);
        for (int i = 0; i < 500; i++) plain.Update(1000 + 80);
        Assert.True(plain.Ratio > 1.0);
    }

    private static byte[] Pkt(int fpp, uint seq)
    {
        var b = new byte[fpp * 2];
        for (int i = 0; i < fpp; i++) { short s = (short)(3000 * Math.Sin(2 * Math.PI * 220 * (seq * fpp + i) / Fs)); b[2 * i] = (byte)s; b[2 * i + 1] = (byte)(s >> 8); }
        return b;
    }

    [Fact]
    public void JitterBuffer_RetargetKeepsAudioAndDoesNotRebuild()
    {
        var jb = new JitterBuffer(240, 1, 4, 12, Fs, 5) { Resample = true, PitchConcealment = true, AdaptiveTarget = true };
        var buf = new byte[480 * 2];
        uint seq = 1;
        for (int i = 0; i < 6; i++) jb.Push(seq++, Pkt(240, seq));
        for (int k = 0; k < 200; k++) { jb.Push(seq++, Pkt(240, seq)); jb.Push(seq++, Pkt(240, seq)); jb.Pull(buf); }
        double depth = jb.DepthMs();
        long under = jb.Stats.Underruns, ovf = jb.Stats.OverflowDrops;
        Assert.True(jb.Retarget(6, 14, 7, 10));
        jb.StudioMode = true;
        Assert.Equal(6, jb.BaseTarget);
        Assert.Equal(depth, jb.DepthMs(), 3); // nothing dropped by the change itself
        double maxPpmStep = 0, prev = jb.DriftPpm;
        for (int k = 0; k < 300; k++)
        {
            jb.Push(seq++, Pkt(240, seq)); jb.Push(seq++, Pkt(240, seq)); jb.Pull(buf);
            if (k > 0) maxPpmStep = Math.Max(maxPpmStep, Math.Abs(jb.DriftPpm - prev)); // k = 0 includes the gain change
            prev = jb.DriftPpm;
        }
        _out.WriteLine($"retargets {jb.Stats.Retargets}, underruns {jb.Stats.Underruns - under}, ovf {jb.Stats.OverflowDrops - ovf}, max ppm step {maxPpmStep:F2}");
        Assert.Equal(1, jb.Stats.Retargets);
        Assert.Equal(under, jb.Stats.Underruns);
        Assert.True(maxPpmStep < 5, "target slew keeps the ratio from jumping");
        Assert.False(jb.Retarget(6, 400, 7, 10)); // too large for the ring: the caller rebuilds
    }

    [Fact]
    public void JitterBuffer_IdleStreamStopsConcealingAndPauseClears()
    {
        var jb = new JitterBuffer(240, 1, 2) { PitchConcealment = true };
        var buf = new byte[480];
        uint seq = 1;
        for (int k = 0; k < 100; k++) { jb.Push(seq++, Pkt(240, seq)); jb.Pull(buf); }
        for (int k = 0; k < 2000; k++) jb.Pull(buf); // 10 s without packets
        _out.WriteLine($"uplc after 10 s idle: {jb.Stats.UnderrunConcealed}");
        Assert.True(jb.Stats.UnderrunConcealed <= jb.MaxUnderrunConcealPackets);
        jb.Pause();
        long u = jb.Stats.UnderrunConcealed;
        for (int k = 0; k < 100; k++) jb.Pull(buf);
        Assert.Equal(u, jb.Stats.UnderrunConcealed);
        jb.ResetStats();
        Assert.Equal(0, jb.Stats.Pushed + jb.Stats.UnderrunConcealed + jb.Stats.Underruns);
    }

    // ---- 4. Auto volume ---------------------------------------------------------------------------------------------

    [Fact]
    public void Agc_FloorIgnoresDigitalSilence()
    {
        var agc = new AgcB3(0);
        var v = StudioTests.Voice(Fs * 6, 0.02, 4); var nz = StudioTests.Noise(Fs * 6, 0.0003, 6); // floor ~ -70 dBFS
        var fr = new float[F];
        for (int f = 0; f + F <= v.Length; f += F)
        {
            bool zero = (f / Fs) % 2 == 1; // every other second is digital silence
            for (int i = 0; i < F; i++) fr[i] = zero ? 0 : v[f + i] + nz[f + i];
            agc.Process(fr, zero ? 0 : 0.9);
        }
        _out.WriteLine($"floor {agc.FloorDbfs:F1} dBFS");
        Assert.InRange(agc.FloorDbfs, -90, -55);
    }

    [Fact]
    public void Agc_BoostedFloorStaysBelowMinus60()
    {
        // Quiet speech (-45 dBFS) over a -72 dBFS floor: loudness wants ~+24 dB, the floor cap allows ~+12 dB.
        var agc = new AgcB3(0);
        var v = StudioTests.Voice(Fs * 30, 0.008, 4); var nz = StudioTests.Noise(Fs * 30, 0.00025, 6);
        var fr = new float[F];
        for (int f = 0; f + F <= v.Length; f += F)
        {
            double env = 0;
            for (int i = 0; i < F; i++) { fr[i] = v[f + i] + nz[f + i]; env = Math.Max(env, Math.Abs(v[f + i])); }
            agc.Process(fr, env > 0.003 ? 0.95 : 0.02);
        }
        _out.WriteLine($"gain {agc.GainDb:F1} dB, floor {agc.FloorDbfs:F1} dBFS, boosted floor {agc.FloorDbfs + agc.GainDb:F1} dBFS");
        Assert.True(agc.FloorDbfs + agc.GainDb <= AgcB3.CeilDbfs + 1.0);
        Assert.Equal(-60, AgcB3.CeilDbfs);
    }

    [Fact]
    public void Agc_EstimateIsNotPulledDownByDecayTails()
    {
        // 1 s of "speech" (frames at -30 dBFS, 40 % of them decay tails 20 dB lower, all with VAD above threshold), then
        // 0.5 s of a -90 dBFS pause. A plain power average reads ~2 dB low and over-boosts; the relative gate does not.
        var agc = new AgcB3(0);
        var fr = new float[F];
        var rnd = new Random(5);
        for (int f = 0; f < 4500; f++)
        {
            bool pause = f % 150 >= 100, tail = f % 10 >= 6;
            double a = pause ? 0 : (tail ? 0.1 : 1.0) * 0.0316 * Math.Sqrt(2);
            for (int i = 0; i < F; i++) fr[i] = (float)(a * Math.Sin(2 * Math.PI * 1000 * (f * F + i) / Fs) + 6e-5 * (rnd.NextDouble() - 0.5));
            agc.Process(fr, pause ? 0 : 0.9);
        }
        // Loud frames: -30 dBFS sine at 1 kHz ≈ -30.7 LUFS (K-weighting ~0 dB there) -> gain ≈ +9.7 dB to reach -21.
        _out.WriteLine($"gain {agc.GainDb:F1} dB, floor {agc.FloorDbfs:F1}");
        Assert.InRange(agc.GainDb, 8.7, 10.9);
    }

    [Fact]
    public void Agc_SavedGainIsCappedAt15()
    {
        Assert.Equal(15, AgcB3.ClampSaved(30));
        Assert.Equal(-12, AgcB3.ClampSaved(-40));
        Assert.Equal(AgcB3.DefaultStartGainDb, AgcB3.ClampSaved(double.NaN));
        var s = new AppSettings { StudioAgcGainDb = 27.9 };
        s.NormalizeAudio();
        Assert.Equal(15, s.StudioAgcGainDb);
    }

    // ---- 5. Stats ---------------------------------------------------------------------------------------------------

    [Fact]
    public void RateMeter_RebasesOnNewBufferAndNeverGoesNegative()
    {
        var m = new RateMeter();
        object a = new(), b = new();
        long f = 1000;
        m.Update(a, 10, 20, 1 * f, f);
        var r = m.Update(a, 13, 20, 11 * f, f);
        Assert.Equal(18.0 / 10 * 10, r.UnderrunPerMin, 3); // 3 in 10 s = 18/min
        r = m.Update(b, 0, 0, 12 * f, f);                    // new buffer: re-base, keep last rates
        Assert.True(r.UnderrunPerMin >= 0 && r.OverflowPerMin >= 0);
        r = m.Update(b, 1, 0, 23 * f, f);
        Assert.InRange(r.UnderrunPerMin, 5.4, 5.5);
        Assert.Equal(0, r.OverflowPerMin);
        r = m.Update(b, 0, 0, 40 * f, f);                    // counters reset: re-base, not negative
        Assert.True(r.UnderrunPerMin >= 0);
    }

    [Fact]
    public void Receiver_TimeoutPausesStudioStopsConcealmentAndNextSessionStartsClean()
    {
        using var rx = new Receiver(0, "127.0.0.1", 10, pingInterval: 0.1, peerTimeout: 0.4);
        rx.Start();
        bool studio = rx.ConfigureStudio(StudioPreset.Balanced, true, 15);
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var to = new IPEndPoint(IPAddress.Loopback, rx.Port);
        var stop = false;
        var render = new Thread(() => { var b = new byte[480 * 2]; while (!Volatile.Read(ref stop)) { rx.Pull(b); Thread.Sleep(10); } });
        render.Start();
        try
        {
            void Send(uint from, int n) { for (uint i = 0; i < n; i++) { s.SendTo(Protocol.Encode(Protocol.TAudio, from + i, Receiver.NowUs(), Pkt(240, from + i), 240, 0), to); Thread.Sleep(5); } }
            Send(1, 80);
            Thread.Sleep(1200); // timed out
            Assert.Null(rx.Peer);
            if (studio) { Assert.True(rx.StudioPaused); Assert.True(rx.StudioActive); Assert.Contains("studio=balanced-paused", rx.Snapshot().Line()); }
            long u1 = rx.Snapshot().UnderrunConcealed;
            Thread.Sleep(600);
            long u2 = rx.Snapshot().UnderrunConcealed;
            Assert.Equal(u1, u2); // nothing concealed (or counted) while idle
            Send(1000, 40);
            Thread.Sleep(100);
            var snap = rx.Snapshot();
            _out.WriteLine(snap.Line());
            Assert.NotNull(rx.Peer);
            Assert.InRange(snap.Received, 1, 40);  // counters restarted with the new session
            Assert.True(snap.Underruns <= 8); // only this short session (burst end + test pacing), not the previous one
            Assert.True(snap.UnderrunConcealed <= 40); // at most 200 ms of concealment after the burst ends
            Assert.Contains("studiomiss=", snap.Line());
            if (studio) Assert.False(rx.StudioPaused);
        }
        finally { Volatile.Write(ref stop, true); render.Join(); }
    }

    // ---- 6. Other ---------------------------------------------------------------------------------------------------

    [Fact]
    public void FileLog_FlushIsSafeAfterDisposeAndFromAnyThread()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cm062-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "log.txt");
        var log = new FileLog(path);
        Parallel.For(0, 50, i => { log.Write("line " + i); log.Flush(); });
        log.Dispose();
        log.Flush();
        log.Write("after dispose");
        log.Flush();
        var lines = File.ReadAllLines(path);
        Assert.Equal(51, lines.Length);
        Directory.Delete(dir, true);
    }

    private static string RepoFile(string rel)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "CordMic.sln"))) d = d.Parent;
        Assert.NotNull(d);
        return File.ReadAllText(Path.Combine(d!.FullName, rel));
    }

    [Fact]
    public void Build_StampsTheBuiltCommitAndInstallerAsksForACleanQuit()
    {
        var build = RepoFile("build.sh");
        Assert.Contains("-p:SourceRevisionId=$REV", build);
        Assert.Contains("git status --porcelain", build);
        var nsi = RepoFile("installer/cordmic.nsi");
        var prog = RepoFile("src/CordMic.App/Program.cs");
        Assert.Contains(@"Local\CordMic.Quit", nsi);
        Assert.Contains(@"Local\CordMic.Quit", prog);
        Assert.True(nsi.IndexOf("CordMic.Quit", StringComparison.Ordinal) < nsi.IndexOf("taskkill.exe\" /IM CordMic.exe", StringComparison.Ordinal));
        Assert.Contains("ProcessExit", prog);
        Assert.Contains("SessionEnding", prog);
    }

    [Fact]
    public void FrontFocusNarrowFieldFallbackIsReportedNeutrally()
    {
        var p = new PhoneLink(true, false, false, false, false, false, 0, DirCode: 3, FrontFocus: true);
        Assert.Equal("narrow", p.DirText);
        Assert.Contains("front focus (narrow field)", DeviceTune.FormatDetected("Phone", false, false, false, 3, 3, true));
        Assert.Contains("front focus unsupported", DeviceTune.FormatDetected("Phone", false, false, false, 3, 2, true));
    }
}
