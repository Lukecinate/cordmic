using CordMic.Core;
using Xunit;
using Xunit.Abstractions;

public class VoiceQualityTests
{
    private readonly ITestOutputHelper _out;
    public VoiceQualityTests(ITestOutputHelper o) => _out = o;

    static short[] Tone(int n, double freq, double amp)
    {
        var s = new short[n];
        for (int i = 0; i < n; i++)
            s[i] = (short)Math.Clamp(Math.Round(Math.Sin(2 * Math.PI * freq * i / 48000.0) * amp), -32767, 32767);
        return s;
    }

    static double Rms(short[] s, int off, int n)
    {
        double a = 0; int m = 0;
        for (int i = off; i < off + n && i < s.Length; i++) { a += (double)s[i] * s[i]; m++; }
        return Math.Sqrt(a / Math.Max(1, m));
    }

    static int Peak(short[] s, int off, int n)
    {
        int p = 0;
        for (int i = off; i < off + n && i < s.Length; i++) p = Math.Max(p, Math.Abs((int)s[i]));
        return p;
    }

    static int Clicks(short[] s, int off, int n, int thresh = 8000)
    {
        int c = 0;
        for (int i = off + 1; i < off + n && i < s.Length; i++)
            if (Math.Abs((int)s[i] - s[i - 1]) > thresh) c++;
        return c;
    }

    static double Thdn(short[] s, int off, int n, double freq)
    {
        double sr = 48000, re = 0, im = 0, e = 0;
        for (int i = 0; i < n && off + i < s.Length; i++)
        {
            double x = s[off + i] / 32768.0;
            double w = 2 * Math.PI * freq * i / sr;
            re += x * Math.Cos(w);
            im += x * Math.Sin(w);
            e += x * x;
        }
        double mag2 = (re * re + im * im) * 2.0 / (n * n);
        double tot = e / n;
        double resid = Math.Max(0, tot - mag2);
        return tot <= 1e-18 ? 0 : resid / tot;
    }

    static double BandEnergy(short[] s, int off, int n, double fLo, double fHi)
    {
        int N = 4096;
        if (off + N > s.Length) return 0;
        double e = 0;
        int k0 = (int)Math.Round(fLo * N / 48000.0);
        int k1 = (int)Math.Round(fHi * N / 48000.0);
        for (int k = Math.Max(1, k0); k <= k1 && k < N / 2; k++)
        {
            double re = 0, im = 0;
            for (int i = 0; i < N; i++)
            {
                double x = s[off + i] / 32768.0;
                double w = 2 * Math.PI * k * i / N;
                re += x * Math.Cos(w);
                im += x * Math.Sin(w);
            }
            e += re * re + im * im;
        }
        return e;
    }

    [Fact]
    public void DefaultChainTargetsMinus18AndDoesNotBoostHiss()
    {
        var lines = new System.Text.StringBuilder();
        void L(string s) { lines.AppendLine(s); _out.WriteLine(s); }

        {
            double amp = 32768.0 * Math.Pow(10, -40.0 / 20.0);
            var s = Tone(48000 * 3, 1000, amp);
            var chain = new VoiceChain(48000);
            chain.Process(s);
            int pk = Peak(s, 48000 * 2, 24000);
            double pdb = 20 * Math.Log10(pk / 32768.0);
            double thdn = Thdn(s, 48000 * 2, 24000, 1000);
            double thdnDb = 10 * Math.Log10(Math.Max(thdn, 1e-12));
            L($"default -40dBFS 1kHz: peak={pdb:F1} dBFS THD+N={thdnDb:F1} dB clicks={Clicks(s, 24000, 12000)} delay={chain.NoiseLatencyMs:F2} ms");
            Assert.InRange(pdb, -26.0, -15.0);
            Assert.True(thdnDb < -20, $"THD+N {thdnDb:F1} dB");
            Assert.InRange(chain.NoiseLatencyMs, 2.6, 2.75);
        }

        {
            double amp = 32768.0 * Math.Pow(10, -12.0 / 20.0);
            var s = Tone(48000 * 3, 1000, amp);
            new VoiceChain(48000).Process(s);
            int pk = Peak(s, 48000 * 2, 24000);
            double pdb = 20 * Math.Log10(pk / 32768.0);
            int clicks = Clicks(s, 48000 * 2, 24000);
            L($"default -12dBFS 1kHz: peak={pdb:F1} dBFS clicks={clicks}");
            Assert.True(pdb <= -0.8, $"peak {pdb:F1} above limiter ceiling");
            Assert.True(clicks < 5, $"clicks {clicks}");
        }

        {
            var rng = new Random(1);
            var s = new short[48000];
            for (int i = 0; i < s.Length; i++) s[i] = (short)rng.Next(-80, 81);
            double inRms = Rms(s, 0, s.Length);
            var chain = new VoiceChain(48000) { NoiseCancellation = false, HighPass = false, GainDb = 0 };
            chain.Process(s);
            double outRms = Rms(s, 24000, 12000);
            double delta = 20 * Math.Log10(outRms / inRms);
            L($"AGC on silence+hiss: rms {20 * Math.Log10(inRms / 32768):F1} -> {20 * Math.Log10(outRms / 32768):F1} dBFS (delta {delta:F1} dB)");
            Assert.True(delta < 3.0, $"AGC boosted hiss by {delta:F1} dB");
        }

        {
            var rng = new Random(2);
            var raw = new short[48000];
            for (int i = 0; i < raw.Length; i++) raw[i] = (short)rng.Next(-4000, 4001);
            var flat = (short[])raw.Clone();
            var eq = (short[])raw.Clone();
            new VoiceChain(48000) { NoiseCancellation = false, Agc = false, VoiceClarity = false, GainDb = 0 }.Process(flat);
            new VoiceChain(48000) { NoiseCancellation = false, Agc = false, VoiceClarity = true, GainDb = 0 }.Process(eq);
            double lowFlat = BandEnergy(flat, 8000, 4096, 20, 250);
            double lowEq = BandEnergy(eq, 8000, 4096, 20, 250);
            double midEq = BandEnergy(eq, 8000, 4096, 250, 4000);
            L($"voice-clarity tilt low(<250)/mid={lowEq / Math.Max(midEq, 1e-12):F3}  low vs no-EQ={lowEq / Math.Max(lowFlat, 1e-12):F3}");
            Assert.True(lowEq < lowFlat * 0.85, "voice clarity should cut rumble below 250 Hz");
        }

        // Rec1 (Default+AAudio+NC+gain0) had speech <250/1–4 kHz ≈ 7.6. Replay that file
        // through Voice Clarity alone so the notes can show the EQ's effect on the real voice.
        const string rec1 = "/tmp/cordmic-recs/rec1.s16";
        if (File.Exists(rec1))
        {
            var bytes = File.ReadAllBytes(rec1);
            var pcm = new short[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, pcm, 0, bytes.Length);
            var before = (short[])pcm.Clone();
            new VoiceChain(48000) { NoiseCancellation = false, Agc = false, VoiceClarity = true, GainDb = 0 }.Process(pcm);
            int mid = Math.Max(0, pcm.Length / 2 - 2048);
            double lowB = BandEnergy(before, mid, 4096, 20, 250);
            double midB = BandEnergy(before, mid, 4096, 1000, 4000);
            double lowA = BandEnergy(pcm, mid, 4096, 20, 250);
            double midA = BandEnergy(pcm, mid, 4096, 1000, 4000);
            L($"rec1 VoiceClarity-only mud <250/1-4k {lowB / Math.Max(midB, 1e-12):F3} -> {lowA / Math.Max(midA, 1e-12):F3}  low {lowA / Math.Max(lowB, 1e-12):F3}×");
        }

        File.WriteAllText("/tmp/cordmic-053-quality-after.txt", lines.ToString());
    }

    [Fact]
    public void ExpandS16ToF32PreservesScaleAndStereo()
    {
        var buf = new byte[8];
        // one stereo frame: +16384, -16384  →  0.5, -0.5
        buf[0] = 0x00; buf[1] = 0x40;
        buf[2] = 0x00; buf[3] = 0xC0;
        PcmConvert.ExpandS16ToF32(buf, 1, 2, 2);
        float l = BitConverter.ToSingle(buf, 0);
        float r = BitConverter.ToSingle(buf, 4);
        Assert.InRange(l, 0.499, 0.501);
        Assert.InRange(r, -0.501, -0.499);

        // mono upmix
        buf = new byte[8];
        buf[0] = 0x00; buf[1] = 0x40; // 16384
        PcmConvert.ExpandS16ToF32(buf, 1, 1, 2);
        l = BitConverter.ToSingle(buf, 0);
        r = BitConverter.ToSingle(buf, 4);
        Assert.InRange(l, 0.499, 0.501);
        Assert.Equal(l, r);
    }

    [Fact]
    public void DriftLockDoesNotHuntOrClickASine()
    {
        var jb = new JitterBuffer(240,
            targetPackets: JitterPolicy.EffectiveTargetPackets(5, 2.7, 5),
            maxPackets: JitterPolicy.MaxDepthPackets(5, 2.7, 5),
            highWaterPackets: JitterPolicy.HighWaterPackets(5, 2.7, 5))
        {
            CatchUp = false, DriftLock = true, RenderPeriodMs = 2.7, WindowPackets = 100,
        };
        uint seq = 1;
        var pkt = new byte[480];
        int phase = 0;
        void Fill()
        {
            for (int i = 0; i < 240; i++, phase++)
            {
                short v = (short)Math.Round(Math.Sin(2 * Math.PI * 1000 * phase / 48000.0) * 8000);
                pkt[i * 2] = (byte)v; pkt[i * 2 + 1] = (byte)(v >> 8);
            }
        }
        int pullFrames = 128;
        var outBuf = new byte[pullFrames * 2];
        var pcm = new short[48000];
        int po = 0;
        int seconds = 10;
        for (int i = 0; i < 8; i++, seq++) { Fill(); jb.Push(seq, pkt); }
        int ticks = (int)(seconds * 48000.0 / pullFrames);
        for (int t = 0; t < ticks; t++)
        {
            if (t % 2 == 0) { Fill(); jb.Push(seq++, pkt); }
            jb.Pull(outBuf);
            if (po + pullFrames <= pcm.Length)
            {
                for (int i = 0; i < pullFrames; i++)
                    pcm[po + i] = (short)(outBuf[i * 2] | (outBuf[i * 2 + 1] << 8));
                po += pullFrames;
            }
        }
        double min = seconds / 60.0;
        int clicks = Clicks(pcm, 1000, Math.Max(0, po - 2000));
        _out.WriteLine($"drift 2.7ms {seconds}s: slips={jb.Stats.SampleSlips} ({jb.Stats.SampleSlips / min:F0}/min, {jb.Stats.SampleSlips / (double)seconds:F1}/s) urate={jb.Stats.Underruns} orate={jb.Stats.OverflowDrops} clicks={clicks} ppm={jb.DriftPpm:F1} bufavg={jb.AvgDepthMs:F1} ms");
        File.AppendAllText("/tmp/cordmic-053-quality-after.txt",
            $"drift 2.7ms {seconds}s: slips={jb.Stats.SampleSlips} ({jb.Stats.SampleSlips / (double)seconds:F1}/s) urate={jb.Stats.Underruns} orate={jb.Stats.OverflowDrops} clicks={clicks} ppm={jb.DriftPpm:F1} bufavg={jb.AvgDepthMs:F1} ms\n");
        Assert.True(jb.Stats.SampleSlips < 80, $"slips {jb.Stats.SampleSlips} — still hunting");
        Assert.True(Math.Abs(jb.DriftPpm) <= 150.0001);
        Assert.True(clicks < 8, $"clicks {clicks}");
    }
}
