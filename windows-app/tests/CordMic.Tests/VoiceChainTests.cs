using System.Diagnostics;
using CordMic.Core;
using Xunit;
using Xunit.Abstractions;

public class VoiceChainTests
{
    private readonly ITestOutputHelper _out;
    public VoiceChainTests(ITestOutputHelper o) => _out = o;

    static short[] Tone(int n, double freq, double amp)
    {
        var s = new short[n];
        for (int i = 0; i < n; i++)
            s[i] = (short)Math.Clamp(Math.Round(Math.Sin(2 * Math.PI * freq * i / 48000.0) * amp), -32767, 32767);
        return s;
    }

    static int Peak(short[] s, int off, int n)
    {
        int p = 0;
        for (int i = off; i < off + n && i < s.Length; i++) p = Math.Max(p, Math.Abs((int)s[i]));
        return p;
    }

    static double Rms(short[] s, int off, int n)
    {
        double a = 0; int m = 0;
        for (int i = off; i < off + n && i < s.Length; i++) { double v = s[i]; a += v * v; m++; }
        return Math.Sqrt(a / Math.Max(1, m));
    }

    [Fact]
    public void FftRoundTrip()
    {
        var re = new double[256];
        var im = new double[256];
        for (int i = 0; i < 256; i++) re[i] = Math.Sin(2 * Math.PI * 8 * i / 256.0);
        var orig = (double[])re.Clone();
        VoiceChain.Fft(re, im, false);
        VoiceChain.Fft(re, im, true);
        for (int i = 0; i < 256; i++) Assert.InRange(re[i], orig[i] - 1e-9, orig[i] + 1e-9);
    }

    [Fact]
    public void NoiseSuppressionDelaysByOneBlockAndNothingElseDoes()
    {
        var chain = new VoiceChain(48000) { NoiseCancellation = true, Agc = false, HighPass = false, GainDb = 0 };
        Assert.Equal(256, chain.DelayFrames);
        Assert.InRange(chain.NoiseLatencyMs, 5.3, 5.4);

        var input = new short[256 * 3];
        // Impulse past the 16-sample block fade, so the delayed copy is not attenuated.
        input[40] = 16000;
        chain.Process(input);
        Assert.Equal(0, Peak(input, 0, 256));
        Assert.True(Peak(input, 256, 256) > 8000, $"delayed peak {Peak(input, 256, 256)}");

        var plain = new VoiceChain(48000) { NoiseCancellation = false, Agc = false, HighPass = false, GainDb = 0 };
        var tone = Tone(480, 1500, 8000);
        var copy = (short[])tone.Clone();
        plain.Process(copy);
        Assert.Equal(0, plain.DelayFrames);
        Assert.InRange(Peak(copy, 0, 480), 7000, 9000);
    }

    [Fact]
    public void DefaultChainIsClearlyLouderAndLimited()
    {
        // -40 dBFS sine, the middle of the range in the 0.4.2 log (-36..-70).
        double amp = 32768.0 * Math.Pow(10, -40.0 / 20.0);
        var input = Tone(48000, 1000, amp); // 1 s
        int inPeak = Peak(input, 0, input.Length);
        var chain = new VoiceChain(48000); // NC on, AGC on, +12 dB
        chain.Process(input);
        int outPeak = Peak(input, 24000, 12000);
        double inDb = 20 * Math.Log10(inPeak / 32768.0);
        double outDb = 20 * Math.Log10(outPeak / 32768.0);
        _out.WriteLine($"level {inDb:F1} dBFS -> {outDb:F1} dBFS (peak {inPeak} -> {outPeak}), NC latency {chain.NoiseLatencyMs:F2} ms");
        Assert.True(outDb > inDb + 18, $"only gained {outDb - inDb:F1} dB");
        Assert.True(outPeak < 32000, "limiter let a sample reach full scale");
    }

    [Fact]
    public void LimiterClipsAHotSignal()
    {
        var chain = new VoiceChain(48000) { NoiseCancellation = false, Agc = false, HighPass = false, GainDb = 24 };
        var s = Tone(2400, 1000, 20000);
        chain.Process(s);
        int peak = Peak(s, 0, s.Length);
        Assert.InRange(peak, 20000, 31000);
        Assert.True(peak < 32000);
    }

    [Fact]
    public void NoiseFloorDropsWhileATonePasses()
    {
        var chain = new VoiceChain(48000) { NoiseCancellation = true, Agc = false, HighPass = false, GainDb = 0 };
        var rng = new Random(1);
        var noise = new short[48000];
        for (int i = 0; i < noise.Length; i++) noise[i] = (short)(rng.Next(-400, 401));
        double inRms = Rms(noise, 0, noise.Length);
        chain.Process(noise);
        double outRms = Rms(noise, 24000, 12000);
        _out.WriteLine($"noise rms {inRms:F0} -> {outRms:F0} ({20 * Math.Log10(outRms / inRms):F1} dB)");
        Assert.True(outRms < inRms * 0.5, "expected at least 6 dB of noise reduction");

        chain.Reset();
        var tone = Tone(24000, 1500, 8000);
        chain.Process(tone);
        int peak = Peak(tone, 8000, 8000);
        _out.WriteLine($"tone peak after NC {peak}");
        Assert.True(peak > 5000, "noise suppressor ate the tone");
    }

    [Fact]
    public void ProcessDoesNotAllocate()
    {
        var chain = new VoiceChain(48000);
        var block = Tone(480, 1000, 1000);
        chain.Process(block);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 2000; i++) chain.Process(block); // 2000 * 10 ms = 20 s
        sw.Stop();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        _out.WriteLine($"20 s of voice chain: {sw.Elapsed.TotalMilliseconds:F1} ms CPU, allocated {bytes} B ({sw.Elapsed.TotalMilliseconds / 20.0:F3} ms CPU per 1 s audio)");
        Assert.True(bytes < 1024, $"allocated {bytes}");
    }
}
