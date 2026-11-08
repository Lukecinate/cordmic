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
        Assert.Equal(128, chain.DelayFrames);
        Assert.InRange(chain.NoiseLatencyMs, 2.6, 2.75);

        var input = new short[256 * 3];
        input[40] = 16000;
        chain.Process(input);
        Assert.Equal(0, Peak(input, 0, 128));
        Assert.True(Peak(input, 128, 256) > 8000, $"delayed peak {Peak(input, 128, 256)}");

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
        var input = Tone(48000 * 3, 1000, amp); // 3 s, so slow AGC can settle
        int inPeak = Peak(input, 0, input.Length);
        var chain = new VoiceChain(48000); // NC Light, AGC on, +6 dB, target −18
        chain.Process(input);
        int outPeak = Peak(input, 48000 * 2, 24000);
        double inDb = 20 * Math.Log10(inPeak / 32768.0);
        double outDb = 20 * Math.Log10(outPeak / 32768.0);
        _out.WriteLine($"level {inDb:F1} dBFS -> {outDb:F1} dBFS (peak {inPeak} -> {outPeak}), NC latency {chain.NoiseLatencyMs:F2} ms");
        // −40 + 6 user + up to 12 AGC = −22 if the cap binds; target −18 if AGC can reach it.
        Assert.InRange(outDb, -26.0, -15.0);
        Assert.True(outPeak < 30000, "limiter let a sample near full scale");
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

    [Fact]
    public void AgcBringsSpeechRmsNearTargetAndLeavesHissAlone()
    {
        // Peak −24 dBFS sine → RMS ≈ −27 dBFS. AGC should lift that near −18 without using the peak.
        double amp = 32768.0 * Math.Pow(10, -24.0 / 20.0);
        var speech = Tone(48000 * 2, 1000, amp);
        var chain = new VoiceChain(48000) { NoiseCancellation = false, VoiceClarity = false, HighPass = false, Agc = true, GainDb = 0 };
        chain.Process(speech);
        double outRms = Rms(speech, 48000 + 24000, 24000);
        double outDb = 20 * Math.Log10(Math.Max(outRms, 1) / 32768.0);
        _out.WriteLine($"speech RMS after AGC {outDb:F1} dBFS");
        Assert.InRange(outDb, -21.0, -15.0);

        var rng = new Random(2);
        double hissAmp = 32768.0 * Math.Pow(10, -55.0 / 20.0);
        var hiss = new short[48000];
        for (int i = 0; i < hiss.Length; i++)
            hiss[i] = (short)Math.Clamp(rng.NextDouble() * 2 * hissAmp - hissAmp, -32767, 32767);
        chain.Reset();
        chain.Process(hiss);
        double hissOut = Rms(hiss, 24000, 24000);
        double hissDb = 20 * Math.Log10(Math.Max(hissOut, 1) / 32768.0);
        _out.WriteLine($"hiss RMS after AGC {hissDb:F1} dBFS");
        Assert.True(hissDb < -48, $"hiss was boosted to {hissDb:F1} dBFS");
    }

}
