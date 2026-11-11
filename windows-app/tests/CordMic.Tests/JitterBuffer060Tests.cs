using CordMic.Core;
using Xunit.Abstractions;

namespace CordMic.Tests;

/// <summary>0.6.0: pitch-repeat concealment and the cubic drift resampler inside the jitter buffer.</summary>
public class JitterBuffer060Tests
{
    private readonly ITestOutputHelper _out;
    public JitterBuffer060Tests(ITestOutputHelper o) { _out = o; }
    private const int Fpp = 240, Fs = 48000;

    private static byte[] TonePacket(long seqIndex, double freq, double amp = 0.5)
    {
        var b = new byte[Fpp * 2];
        for (int i = 0; i < Fpp; i++)
        {
            short v = (short)Math.Round(amp * 32767 * Math.Sin(2 * Math.PI * freq * (seqIndex * Fpp + i) / Fs));
            b[2 * i] = (byte)v; b[2 * i + 1] = (byte)(v >> 8);
        }
        return b;
    }

    private static float[] ToF(byte[] b) { var f = new float[b.Length / 2]; for (int i = 0; i < f.Length; i++) f[i] = (short)(b[2 * i] | (b[2 * i + 1] << 8)) / 32768f; return f; }

    [Fact]
    public void DefaultsKeep057Behaviour()
    {
        var jb = new JitterBuffer(Fpp);
        Assert.False(jb.PitchConcealment);
        Assert.False(jb.Resample);
    }

    [Fact]
    public void LostPacketIsConcealedByPitchRepeat()
    {
        var jb = new JitterBuffer(Fpp, targetPackets: 2) { PitchConcealment = true };
        var outp = new List<float>();
        var buf = new byte[Fpp * 2];
        for (uint s = 1; s <= 60; s++)
        {
            if (s != 30) jb.Push(s, TonePacket(s, 200));
            if (s > 2) { jb.Pull(buf); outp.AddRange(ToF(buf)); }
        }
        Assert.Equal(1, jb.Stats.Concealed);
        // Output stays a clean 200 Hz tone through the loss: compare with the tone at the best lag.
        var y = outp.ToArray();
        double maxStep = 0;
        for (int i = 1; i < y.Length; i++) maxStep = Math.Max(maxStep, Math.Abs(y[i] - y[i - 1]));
        double toneStep = 0.5 * 2 * Math.PI * 200 / Fs * 1.02;
        _out.WriteLine($"max step {maxStep:F4} vs tone {toneStep:F4}");
        Assert.True(maxStep <= toneStep);
    }

    [Fact]
    public void UnderrunIsConcealedThenFadesToSilence()
    {
        var jb = new JitterBuffer(Fpp, targetPackets: 2) { PitchConcealment = true };
        var buf = new byte[Fpp * 2];
        uint s = 1;
        for (; s <= 20; s++) { jb.Push(s, TonePacket(s, 200)); if (s > 2) jb.Pull(buf); }
        // sender stalls for 12 packets (60 ms)
        var tail = new List<float>();
        for (int i = 0; i < 12; i++) { jb.Pull(buf); tail.AddRange(ToF(buf)); }
        Assert.True(jb.Stats.UnderrunConcealed >= 6, jb.Stats.UnderrunConcealed.ToString());
        Assert.Equal(1, jb.Stats.Underruns);
        // first packet of the gap still carries the tone; after 6+ packets it is silent
        Assert.True(tail.Take(Fpp).Max() > 0.3f);
        Assert.True(tail.Skip(9 * Fpp).Select(Math.Abs).Max() < 1e-4f);
        double maxStep = 0; for (int i = 1; i < tail.Count; i++) maxStep = Math.Max(maxStep, Math.Abs(tail[i] - tail[i - 1]));
        Assert.True(maxStep <= 0.5 * 2 * Math.PI * 200 / Fs * 1.05);
        // stream resumes cleanly
        for (int i = 0; i < 10; i++, s++) { jb.Push(s, TonePacket(s, 200)); jb.Pull(buf); }
        Assert.True(jb.Stats.Played > 20);
    }

    private static double Gauss(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());

    /// <summary>RS-3: 120 s closed loop, sender clock off by <paramref name="ppm"/>, |N(0, 3 ms)| in-order arrival jitter, 10 ms render pulls.</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(100, 1)]
    [InlineData(300, 1)]
    [InlineData(-300, 1)]
    [InlineData(100, 2)]
    [InlineData(300, 3)]
    public void Rs3_ClosedLoopDrift(int ppm, int seed)
    {
        const int targetPk = 6; // 30 ms = 1440 frames, the reference simulation's target
        var jb = new JitterBuffer(Fpp, targetPackets: targetPk, maxPackets: 40) { Resample = true, DriftLock = false, PitchConcealment = true, AdaptiveTarget = false };
        var rnd = new Random(seed);
        const double seconds = 120;
        double pktPeriod = Fpp / (double)Fs / (1 + ppm / 1e6);
        int nOut = (int)(seconds * Fs);
        var y = new float[nOut];
        var buf = new byte[480 * 2];
        uint seq = 1; double lastArrival = 0; long sent = 0;
        double nextSend = 0; double pendingArrival = double.NaN; byte[]? pending = null;
        var fills = new List<double>(); var ratios = new List<double>(); var trace = new System.Text.StringBuilder();
        int pulls = nOut / 480;
        // pre-fill like a real start: packets begin arriving 0.1 s before the first pull
        double t0 = 0.1;
        long underrunsAtStart = -1;
        for (int k = 0; k < pulls; k++)
        {
            double now = t0 + k * 0.01;
            while (true)
            {
                if (pending is null)
                {
                    double arr = Math.Max(lastArrival, nextSend + Math.Abs(Gauss(rnd)) * 0.003);
                    pending = TonePacket(sent, 1000); pendingArrival = arr; nextSend += pktPeriod; sent++;
                }
                if (pendingArrival > now) break;
                jb.Push(seq++, pending); lastArrival = pendingArrival; pending = null;
            }
            double fb = jb.FillFrames;
            jb.Pull(buf);
            double fa = jb.FillFrames;
            if (k == 100) underrunsAtStart = jb.Stats.Underruns;
            for (int i = 0; i < 480; i++) y[k * 480 + i] = (short)(buf[2 * i] | (buf[2 * i + 1] << 8)) / 32768f;
            if (k >= 6000) { fills.Add((fb + fa) / 2); ratios.Add(jb.DriftPpm); }
            if (k % 500 == 0) trace.Append($" [{k / 100}s {jb.FillFrames:F0} {jb.DriftPpm:F0}]");
        }
        double thd = TestDsp.ToneSpurDb(y, 90 * Fs);
        double mean = fills.Average();
        double std1 = Std(fills.Take(fills.Count / 2)), std2 = Std(fills.Skip(fills.Count / 2));
        double rMean = ratios.Average(), rJit = Math.Sqrt(ratios.Select(r => (r - rMean) * (r - rMean)).Average());
        long under = jb.Stats.Underruns - underrunsAtStart;
        _out.WriteLine($"RS-3 {ppm} ppm seed {seed}: THD+N {thd:F1} dB, underruns {under}, fill {mean:F0} (std {std1:F0} -> {std2:F0}), ratio {rMean:F0} ppm, jitter {rJit:F1} ppm, resyncs {jb.ResampleResyncs}, concealed {jb.Stats.Concealed}, overflow {jb.Stats.OverflowDrops}, late {jb.Stats.Late}, resets {jb.Stats.Resets}, played {jb.Stats.Played}, pushed {jb.Stats.Pushed}, target {jb.Target} hw {jb.HighWater} max {jb.MaxDepth}, trace {trace}");
        Assert.True(thd <= -80, $"THD+N {thd:F1}");
        Assert.Equal(0, under);
        Assert.InRange(mean, 1440 - 96, 1440 + 96);
        Assert.True(rJit <= 100, $"jitter {rJit:F1}");
        Assert.True(std2 <= std1 * 1.25 + 5, "fill std growing");
    }

    private sealed class Fifo : CordMic.Core.Dsp.IFloatSource
    {
        public readonly Queue<float> Q = new();
        public float? Next() => Q.Count > 0 ? Q.Dequeue() : null;
    }

    /// <summary>RS-3 as specified (float domain, as the reference simulation): resampler + controller on a FIFO.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(300)]
    [InlineData(-300)]
    public void Rs3_FloatClosedLoop(int ppm)
    {
        var rnd = new Random(7);
        var fifo = new Fifo();
        var rs = new CordMic.Core.Dsp.CubicResampler();
        var dc = new CordMic.Core.Dsp.DriftController(1440, CordMic.Core.Dsp.DriftController.KpTuned);
        double pktPeriod = Fpp / (double)Fs / (1 + ppm / 1e6);
        int pulls = 12000; var y = new float[pulls * 480]; var block = new float[480];
        long sent = 0; double nextSend = 0, lastArr = 0; double pendArr = double.NaN; bool pend = false;
        int underruns = 0; var fills = new List<double>(); var ratios = new List<double>();
        // start at target, as the jitter buffer does when it leaves buffering
        for (int i = 0; i < 6; i++) { for (int j = 0; j < Fpp; j++) fifo.Q.Enqueue((float)(0.5 * Math.Sin(2 * Math.PI * 1000 * (sent * Fpp + j) / Fs))); sent++; }
        double t0 = 6 * pktPeriod; nextSend = t0;
        for (int k = 0; k < pulls; k++)
        {
            double now = t0 + k * 0.01;
            while (true)
            {
                if (!pend) { pendArr = Math.Max(lastArr, nextSend + Math.Abs(Gauss(rnd)) * 0.003); nextSend += pktPeriod; pend = true; }
                if (pendArr > now) break;
                for (int j = 0; j < Fpp; j++) fifo.Q.Enqueue((float)(0.5 * Math.Sin(2 * Math.PI * 1000 * (sent * Fpp + j) / Fs)));
                sent++; lastArr = pendArr; pend = false;
            }
            double before = fifo.Q.Count + rs.HeldSamples;
            if (fifo.Q.Count < 480 * dc.Ratio + 4) underruns++;
            rs.Process(block, dc.Ratio, fifo);
            block.CopyTo(y, k * 480);
            double after = fifo.Q.Count + rs.HeldSamples;
            dc.Update((before + after) / 2);
            if (k >= 6000) { fills.Add((before + after) / 2); ratios.Add((dc.Ratio - 1) * 1e6); }
        }
        double thd = TestDsp.ToneSpurDb(y, 90 * Fs);
        double mean = fills.Average(), rMean = ratios.Average(), rJit = Math.Sqrt(ratios.Select(r => (r - rMean) * (r - rMean)).Average());
        double std1 = Std(fills.Take(fills.Count / 2)), std2 = Std(fills.Skip(fills.Count / 2));
        _out.WriteLine($"RS-3 float {ppm} ppm: THD+N {thd:F1} dB, underruns {underruns}, fill {mean:F0} (std {std1:F0} -> {std2:F0}), ratio jitter {rJit:F1} ppm");
        Assert.True(thd <= -80);
        Assert.Equal(0, underruns);
        Assert.InRange(mean, 1440 - 96, 1440 + 96);
        Assert.True(rJit <= 100);
        Assert.True(std2 <= std1 * 1.25 + 5);
    }

    private static double Std(IEnumerable<double> xs) { var a = xs.ToArray(); double m = a.Average(); return Math.Sqrt(a.Select(v => (v - m) * (v - m)).Average()); }
}
