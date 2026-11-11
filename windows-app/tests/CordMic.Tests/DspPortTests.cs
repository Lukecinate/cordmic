using System.Diagnostics;
using System.Text.Json;
using CordMic.Core;
using CordMic.Core.Dsp;
using Xunit.Abstractions;

namespace CordMic.Tests;

/// <summary>0.6.0 spec §1–2: pitch-repeat PLC and cubic resampler, against the reference vectors (port/test_vectors.json).</summary>
[Collection("cpu-timing")]
public class DspPortTests
{
    private readonly ITestOutputHelper _out;
    public DspPortTests(ITestOutputHelper o) { _out = o; }

    private const int Fs = 48000, P = 240;
    private static JsonDocument Vectors() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "dsp_vectors.json")));

    private static double[] Signal(string name, int n)
    {
        var s = new double[n];
        if (name == "voiced_150Hz")
            for (int i = 0; i < n; i++) s[i] = 0.3 * Math.Sin(2 * Math.PI * 150 * i / Fs) + 0.1 * Math.Sin(2 * Math.PI * 450 * i / Fs + 0.5);
        else if (name == "lcg_noise")
        {
            ulong x = 12345;
            for (int i = 0; i < n; i++) { x = (1664525UL * x + 1013904223UL) % 4294967296UL; s[i] = (x / 4294967296.0 - 0.5) * 0.2; }
        }
        return s;
    }

    [Fact]
    public void Plc1_MatchesReferenceVectors()
    {
        using var doc = Vectors();
        double tol = doc.RootElement.GetProperty("tolerance_abs").GetDouble();
        foreach (var c in doc.RootElement.GetProperty("plc").EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            var lost = c.GetProperty("lost_packets").EnumerateArray().Select(e => e.GetInt32()).ToHashSet();
            var exp = c.GetProperty("output").EnumerateArray().Select(e => e.GetDouble()).ToArray();
            var sig = Signal(name, 40 * P);
            var plc = new PitchPlc(P);
            var pkt = new float[P];
            double maxErr = 0;
            for (int p = 0; p < 40; p++)
            {
                if (lost.Contains(p)) plc.Lost(pkt);
                else { for (int i = 0; i < P; i++) pkt[i] = (float)sig[p * P + i]; plc.Good(pkt); }
                for (int i = 0; i < P; i++) maxErr = Math.Max(maxErr, Math.Abs(pkt[i] - exp[p * P + i]));
            }
            _out.WriteLine($"PLC-1 {name}: max abs error {maxErr:E2}");
            Assert.True(maxErr <= tol, $"{name}: {maxErr}");
        }
    }

    [Fact]
    public void Plc2_NoLossIsBitIdentical()
    {
        var sig = Signal("voiced_150Hz", 100 * P);
        var plc = new PitchPlc(P);
        var pkt = new float[P];
        for (int p = 0; p < 100; p++)
        {
            for (int i = 0; i < P; i++) pkt[i] = (float)sig[p * P + i];
            plc.Good(pkt);
            for (int i = 0; i < P; i++) Assert.Equal((float)sig[p * P + i], pkt[i]);
        }
    }

    private static float Tone200(int n) => (float)(0.5 * Math.Sin(2 * Math.PI * 200 * n / Fs));

    [Fact]
    public void Plc3_200HzToneOneLostPacket()
    {
        var plc = new PitchPlc(P);
        var pkt = new float[P];
        double maxErr = 0;
        for (int p = 0; p < 30; p++)
        {
            if (p == 20) plc.Lost(pkt);
            else { for (int i = 0; i < P; i++) pkt[i] = Tone200(p * P + i); plc.Good(pkt); }
            if (p == 20 || p == 21)
                for (int i = 0; i < P; i++) maxErr = Math.Max(maxErr, Math.Abs(pkt[i] - Tone200(p * P + i)));
        }
        _out.WriteLine($"PLC-3 max error {maxErr:E2}");
        Assert.True(maxErr <= 1e-6, maxErr.ToString());
    }

    [Fact]
    public void Plc4_EightLostPacketsHaveNoClicks()
    {
        var plc = new PitchPlc(P);
        var pkt = new float[P];
        float prev = 0; double maxStep = 0, toneStep = 0;
        for (int n = 1; n < 40 * P; n++) toneStep = Math.Max(toneStep, Math.Abs(Tone200(n) - Tone200(n - 1)));
        for (int p = 0; p < 40; p++)
        {
            if (p >= 20 && p < 28) plc.Lost(pkt);
            else { for (int i = 0; i < P; i++) pkt[i] = Tone200(p * P + i); plc.Good(pkt); }
            for (int i = 0; i < P; i++) { if (p > 0 || i > 0) maxStep = Math.Max(maxStep, Math.Abs(pkt[i] - prev)); prev = pkt[i]; }
        }
        _out.WriteLine($"PLC-4 largest step {maxStep:F4} vs tone {toneStep:F4}");
        Assert.True(maxStep <= toneStep * 1.05);
    }

    [Fact]
    public void Plc5_LossAtStreamStartIsSilenceThenFadeIn()
    {
        var plc = new PitchPlc(P);
        var pkt = new float[P];
        plc.Lost(pkt);
        Assert.All(pkt, v => Assert.Equal(0f, v));
        for (int i = 0; i < P; i++) pkt[i] = 0.5f;
        plc.Good(pkt);
        Assert.True(pkt[0] < 0.01f && Math.Abs(pkt[P - 1] - 0.5f) < 1e-6);
        var fresh = new PitchPlc(P);
        for (int i = 0; i < 5; i++) { fresh.Lost(pkt); Assert.All(pkt, v => Assert.Equal(0f, v)); }
    }

    [Fact]
    public void Plc7_WorstFrameCpu()
    {
        var plc = new PitchPlc(P);
        var pkt = new float[P];
        var sig = Signal("voiced_150Hz", 2000 * P);
        var sw = new Stopwatch();
        double worst = 0;
        var times = new List<double>();
        for (int p = 0; p < 2000; p++)
        {
            sw.Restart();
            if (p % 10 == 9) plc.Lost(pkt); // each a new loss run: full pitch search
            else { for (int i = 0; i < P; i++) pkt[i] = (float)sig[p * P + i]; plc.Good(pkt); }
            double ms = sw.Elapsed.TotalMilliseconds;
            if (p > 200 && p % 10 == 9) times.Add(ms);
        }
        times.Sort();
        worst = times[(int)(times.Count * 0.99)];
        _out.WriteLine($"PLC-7 loss frame p99 {worst:F3} ms (max {times[^1]:F3}), searches {plc.PitchSearches}");
        Assert.True(worst <= 0.2, worst.ToString());
    }

    private sealed class ArraySource : IFloatSource
    {
        private readonly double[] _a; private int _i;
        public ArraySource(double[] a) { _a = a; }
        public float? Next() => _i < _a.Length ? (float)_a[_i++] : null;
    }

    [Fact]
    public void Rs1_Rs2_ResamplerMatchesVectorsAndRatioOneIsExactDelay()
    {
        using var doc = Vectors();
        var src = new double[4000];
        for (int n = 0; n < 4000; n++) src[n] = 0.5 * Math.Sin(2 * Math.PI * 1000 * n / Fs);
        foreach (var kv in doc.RootElement.GetProperty("asrc").EnumerateObject())
        {
            double ratio = double.Parse(kv.Name, System.Globalization.CultureInfo.InvariantCulture);
            var exp = kv.Value.GetProperty("output").EnumerateArray().Select(e => e.GetDouble()).ToArray();
            var rs = new CubicResampler();
            var y = new float[2000];
            rs.Process(y, ratio, new ArraySource(src));
            double err = 0;
            for (int k = 0; k < 2000; k++) err = Math.Max(err, Math.Abs(y[k] - exp[k]));
            _out.WriteLine($"RS-2 ratio {kv.Name}: max abs error {err:E2}");
            Assert.True(err <= 2e-5);
            if (ratio == 1.0) for (int k = 0; k < 2000; k++) Assert.Equal((float)src[k + 1], y[k]);
        }
        var c = new DriftController(1440);
        var ratios = doc.RootElement.GetProperty("controller").GetProperty("ratios").EnumerateArray().Select(e => e.GetDouble()).ToArray();
        double ce = 0;
        for (int k = 0; k < 1500; k++)
        {
            double fill = k < 500 ? 1540 : k < 1000 ? 1340 : 1440;
            ce = Math.Max(ce, Math.Abs(c.Update(fill) - ratios[k]));
        }
        _out.WriteLine($"RS-2 controller max abs error {ce:E2}");
        Assert.True(ce <= 1e-10, ce.ToString());
    }

    [Fact]
    public void Rs4_OpenLoop100ppm_8kHzTone()
    {
        // 100 ppm: the output is the tone at f / ratio.
        const double ratio = 1.0001;
        int nIn = 120000;
        var src = new double[nIn];
        for (int n = 0; n < nIn; n++) src[n] = 0.5 * Math.Sin(2 * Math.PI * 8000 * n / Fs);
        var rs = new CubicResampler();
        var y = new float[48000 + 65536];
        rs.Process(y, ratio, new ArraySource(src));
        double thd = TestDsp.ToneSpurDb(y, 1000);
        _out.WriteLine($"RS-4 8 kHz THD+N {thd:F1} dB");
        Assert.True(thd <= -60, thd.ToString());
    }

    [Fact]
    public void Rs5_CpuPer10msFrame()
    {
        var rs = new CubicResampler();
        var dc = new DriftController(1440);
        var src = new ArraySourceLoop();
        var y = new float[480];
        for (int i = 0; i < 500; i++) { rs.Process(y, dc.Ratio, src); dc.Update(1440 + (i % 7)); }
        var sw = Stopwatch.StartNew();
        const int N = 20000;
        for (int i = 0; i < N; i++) { rs.Process(y, dc.Ratio, src); dc.Update(1440 + (i % 7)); }
        double ms = sw.Elapsed.TotalMilliseconds / N;
        _out.WriteLine($"RS-5 {ms * 1000:F1} us per frame");
        Assert.True(ms <= 0.05);
    }

    private sealed class ArraySourceLoop : IFloatSource
    {
        private int _n;
        public float? Next() => (float)(0.3 * Math.Sin(2 * Math.PI * 1000 * (_n++ % 48000) / Fs));
    }
}
