using CordMic.Core;
using Xunit;
using Xunit.Abstractions;

/// <summary>0.5.7: Strong noise reduction without musical noise, and with whispers and fricatives kept.</summary>
public class StrongNoiseTests
{
    readonly ITestOutputHelper _o;
    public StrongNoiseTests(ITestOutputHelper o) => _o = o;

    // 0.5.6 Strong on the same signals: hop jitter 4.3–4.5 dB, 5.8–6.0 % of hops jumping more than 8 dB,
    // per-bin gain spread 8.2–8.4 dB, speech harmonics cut by 13–15 dB.
    [Theory]
    [InlineData(-50, 0.02)]
    [InlineData(-45, 0.05)]
    public void StrongGainDoesNotFlutterOnSpeech(double noiseDb, double speechAmp)
    {
        var r = FlutterStats.Run(NoiseMode.Strong, noiseDb, speechAmp, 1);
        _o.WriteLine($"Strong: hop jitter {r.HopJitterDb:F2} dB, jumps>8dB {r.JumpRate:P2}, spread {r.FlutterDb:F2} dB, harmonics {r.HarmonicGainDb:F1} dB");
        Assert.True(r.HopJitterDb < 1.5, $"hop jitter {r.HopJitterDb:F2} dB");
        Assert.True(r.JumpRate < 0.005, $"jumps {r.JumpRate:P2}");
        Assert.True(r.FlutterDb < 6.0, $"spread {r.FlutterDb:F2} dB");
        Assert.True(r.HarmonicGainDb > -8.0, $"harmonics {r.HarmonicGainDb:F1} dB");
    }

    [Fact]
    public void LightGainIsSteadierStill()
    {
        var r = FlutterStats.Run(NoiseMode.Light, -50, 0.02, 2);
        Assert.True(r.HopJitterDb < 1.0, $"hop jitter {r.HopJitterDb:F2} dB");
        Assert.True(r.HarmonicGainDb > -5.0, $"harmonics {r.HarmonicGainDb:F1} dB");
    }

    [Fact]
    public void StrongLeavesALittleNaturalBackground()
    {
        var r = FlutterStats.Run(NoiseMode.Strong, -50, 0, 3);
        _o.WriteLine($"Strong noise only: mean gain {r.MeanGainDb:F1} dB, spread {r.FlutterDb:F2} dB");
        Assert.InRange(r.MeanGainDb, -16.5, -12.0); // floor about −15 dB, not near-silence
        Assert.True(r.FlutterDb < 1.0, $"spread {r.FlutterDb:F2} dB");
    }

    static double Rms(short[] s, int start, int len)
    {
        double sum = 0;
        for (int i = start; i < start + len; i++) sum += (double)s[i] * s[i];
        return Math.Sqrt(sum / len);
    }

    /// <summary>1 s of −55 dBFS hiss, then a 150 ms burst; returns burst output/input level in dB and pause level after.</summary>
    static (double BurstDb, double AfterDb) Burst(bool highPass, double burstDbfs)
    {
        var rng = new Random(9);
        int n = 48000 * 2, b0 = 48000, b1 = 48000 + 7200;
        double hiss = 32768 * Math.Pow(10, -55 / 20.0) * Math.Sqrt(3);
        var burst = new double[n];
        double prev = 0, prev2 = 0, sum = 0;
        for (int i = b0; i < b1; i++)
        {
            double w = rng.NextDouble() * 2 - 1;
            double v = highPass ? (w - 2 * prev + prev2) : w; // second difference: energy mostly above 6 kHz
            prev2 = prev; prev = w;
            burst[i] = v; sum += v * v;
        }
        double scale = 32768 * Math.Pow(10, burstDbfs / 20.0) / Math.Sqrt(sum / (b1 - b0));
        var x = new short[n];
        var dry = new short[n];
        for (int i = 0; i < n; i++)
        {
            double h = (rng.NextDouble() * 2 - 1) * hiss;
            x[i] = (short)Math.Clamp(h + burst[i] * scale, -32767, 32767);
            dry[i] = (short)Math.Clamp(burst[i] * scale, -32767, 32767);
        }
        var chain = new VoiceChain(48000) { Mode = NoiseMode.Strong, Agc = false, VoiceClarity = false, GainDb = 0, Expander = NoiseMode.Off };
        chain.Process(x);
        int d = VoiceChain.NoiseDelayFrames, skip = 960;
        double outDb = 20 * Math.Log10(Rms(x, b0 + d + skip, b1 - b0 - skip) / Rms(dry, b0 + skip, b1 - b0 - skip));
        double after = 20 * Math.Log10(Math.Max(Rms(x, b1 + 24000, 12000), 1e-3) / 32768.0);
        return (outDb, after);
    }

    [Fact]
    public void FricativeBurstIsKept()
    {
        var (db, after) = Burst(highPass: true, burstDbfs: -38);
        _o.WriteLine($"fricative: {db:F1} dB, pause after {after:F1} dBFS");
        Assert.True(db > -3.0, $"fricative attenuated by {-db:F1} dB");
        Assert.True(after < -66, $"hiss after the burst {after:F1} dBFS (noise estimate absorbed the burst)");
    }

    [Fact]
    public void WhisperIsKept()
    {
        var (db, after) = Burst(highPass: false, burstDbfs: -40);
        _o.WriteLine($"whisper: {db:F1} dB, pause after {after:F1} dBFS");
        Assert.True(db > -3.0, $"whisper attenuated by {-db:F1} dB");
        Assert.True(after < -66, $"hiss after the burst {after:F1} dBFS");
    }
}

public static class FlutterStats
{
    public readonly record struct Result(double FlutterDb, double JumpRate, double HarmonicGainDb, double MeanGainDb, double HopJitterDb);

    /// <summary>White noise plus an optional 200 Hz harmonic complex; per-bin gain statistics over 3 s; speech (3 Hz syllable envelope) starts after 1 s.</summary>
    public static Result Run(NoiseMode mode, double noiseDb, double speechAmp, int seed)
    {
        var rng = new Random(seed);
        double a = 32768 * Math.Pow(10, noiseDb / 20.0) * Math.Sqrt(3);
        var chain = new VoiceChain(48000) { Mode = mode, Agc = false, VoiceClarity = false, GainDb = 0, Expander = NoiseMode.Off };
        var buf = new short[128];
        var g = new double[129];
        var hist = new List<double[]>();
        for (int h = 0; h < 375 * 5; h++)
        {
            for (int i = 0; i < 128; i++)
            {
                int t = h * 128 + i;
                double v = (rng.NextDouble() * 2 - 1) * a;
                double env = t < 48000 ? 0 : 0.5 - 0.5 * Math.Cos(2 * Math.PI * 3.0 * t / 48000.0); // 3 Hz syllables after 1 s
                for (int m = 1; m <= 20 && speechAmp > 0; m++) v += env * 32768 * speechAmp / m * Math.Sin(2 * Math.PI * 200 * m * t / 48000.0);
                buf[i] = (short)Math.Clamp(v, -32767, 32767);
            }
            chain.Process(buf);
            if (h > 375 * 2) { chain.CopyBinGains(g); hist.Add(g.Select(x => 20 * Math.Log10(Math.Max(x, 1e-6))).ToArray()); }
        }
        double sumStd = 0, mean = 0; int nb = 0; long jumps = 0, pairs = 0; double dsq = 0;
        for (int k = 5; k < 120; k++)
        {
            var v = hist.Select(x => x[k]).ToArray();
            double m = v.Average(); mean += m;
            sumStd += Math.Sqrt(v.Select(x => (x - m) * (x - m)).Average()); nb++;
            for (int i = 1; i < v.Length; i++) { pairs++; double d = v[i] - v[i - 1]; dsq += d * d; if (Math.Abs(d) > 8) jumps++; }
        }
        double harm = 0; int nh = 0;
        if (speechAmp > 0)
            for (int m = 1; m <= 10; m++) { int k = (int)Math.Round(200.0 * m / 187.5); if (k < 5) continue; harm += hist.Average(x => x[k]); nh++; }
        return new Result(sumStd / nb, jumps / (double)pairs, nh > 0 ? harm / nh : double.NaN, mean / nb, Math.Sqrt(dsq / pairs));
    }
}
