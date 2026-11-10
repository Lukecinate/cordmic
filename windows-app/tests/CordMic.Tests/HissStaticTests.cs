using CordMic.Core;
using Xunit;

/// <summary>0.5.6: hiss and static fixes (pre-gain gate, tracked floor, pause expander, gain ramp, underrun fades, adaptive target).</summary>
public class HissStaticTests
{
    static short[] Hiss(int n, double dbfs, int seed)
    {
        var rng = new Random(seed);
        double a = 32768.0 * Math.Pow(10, dbfs / 20.0) * Math.Sqrt(3); // uniform: RMS = a/√3
        var s = new short[n];
        for (int i = 0; i < n; i++) s[i] = (short)Math.Clamp((rng.NextDouble() * 2 - 1) * a, -32767, 32767);
        return s;
    }

    static double RmsDb(short[] s, int start, int len)
    {
        double sum = 0;
        for (int i = start; i < start + len; i++) sum += (double)s[i] * s[i];
        return 20 * Math.Log10(Math.Max(Math.Sqrt(sum / len), 1e-3) / 32768.0);
    }

    [Fact]
    public void RaisedUserGainDoesNotOpenTheGateOnHiss()
    {
        // −60 dBFS hiss with +12 dB user gain. 0.5.5 compared the gate with the post-gain level
        // (−48 dBFS, above the −44 dBFS default open), so AGC lifted the hiss by up to +12 dB.
        var s = Hiss(48000 * 4, -60, 3);
        var chain = new VoiceChain(48000) { NoiseCancellation = false, VoiceClarity = false, GainDb = 12 };
        chain.Process(s);
        double outDb = RmsDb(s, 48000 * 3, 48000);
        Assert.True(outDb < -50, $"hiss after chain {outDb:F1} dBFS");
        Assert.InRange(chain.NoiseFloorDbfs, -64, -56);
    }

    [Fact]
    public void PauseExpanderLowersHissBetweenWords()
    {
        double Run(NoiseMode exp)
        {
            var s = Hiss(48000 * 3, -58, 5);
            var chain = new VoiceChain(48000) { NoiseCancellation = false, VoiceClarity = false, Expander = exp, GainDb = 6 };
            chain.Process(s);
            return RmsDb(s, 48000 * 2, 48000);
        }
        double off = Run(NoiseMode.Off), light = Run(NoiseMode.Light), strong = Run(NoiseMode.Strong);
        Assert.InRange(off - light, 4.5, 7.5);
        Assert.InRange(off - strong, 10, 13.5);
    }

    [Fact]
    public void SpeechStillReachesTargetWithTrackedFloor()
    {
        // 1 s of −65 dBFS room noise, then a −36 dBFS tone with the same noise underneath.
        int n = 48000 * 4;
        var s = Hiss(n, -65, 7);
        double amp = 32768.0 * Math.Pow(10, -33.0 / 20.0);
        for (int i = 48000; i < n; i++) s[i] = (short)Math.Clamp(s[i] + amp * Math.Sin(2 * Math.PI * 1000 * i / 48000.0), -32767, 32767);
        var chain = new VoiceChain(48000) { NoiseCancellation = false, VoiceClarity = false, GainDb = 6 };
        chain.Process(s);
        double outDb = RmsDb(s, 48000 * 3, 48000);
        Assert.InRange(outDb, -26, -15);
    }

    [Fact]
    public void LiveGainChangeIsRamped()
    {
        var chain = new VoiceChain(48000) { NoiseCancellation = false, VoiceClarity = false, Agc = false, GainDb = 0 };
        double amp = 32768.0 * Math.Pow(10, -20.0 / 20.0);
        var a = new short[4800];
        for (int i = 0; i < a.Length; i++) a[i] = (short)(amp * Math.Sin(2 * Math.PI * 200 * i / 48000.0));
        var b = (short[])a.Clone();
        chain.Process(a);
        chain.GainDb = 18;
        chain.Process(b);
        int maxStep = 0;
        for (int i = 1; i < 480; i++) maxStep = Math.Max(maxStep, Math.Abs(b[i] - b[i - 1]));
        // A 200 Hz tone at −20 dBFS changes by at most ~86 per sample; an instant +18 dB step would jump far more.
        Assert.True(maxStep < 400, $"step {maxStep}");
    }

    const int FPP = 240, NB = FPP * 2;
    static byte[] Const(short v)
    {
        var b = new byte[NB];
        for (int i = 0; i < NB; i += 2) { b[i] = (byte)v; b[i + 1] = (byte)(v >> 8); }
        return b;
    }
    static short At(byte[] b, int frame) => (short)(b[frame * 2] | (b[frame * 2 + 1] << 8));

    [Fact]
    public void OverflowTrimSplicesFromThePreviousSample()
    {
        var jb = new JitterBuffer(FPP, targetPackets: 2, maxPackets: 4, highWaterPackets: 2);
        jb.Push(0, Const(10000)); jb.Push(1, Const(10000));
        jb.Pull(NB);
        for (uint q = 2; q < 8; q++) jb.Push(q, Const(-10000)); // burst trims to the high-water mark
        Assert.True(jb.Stats.OverflowDrops > 0);
        var o = jb.Pull(NB);
        // First sample continues from +10000 rather than restarting at 0 or jumping to −10000.
        Assert.True(At(o, 0) > 8000, $"first {At(o, 0)}");
        for (int f = 1; f < 40; f++) Assert.True(Math.Abs(At(o, f) - At(o, f - 1)) < 1000, $"step at {f}");
        Assert.Equal(-10000, At(o, 100));
    }

    [Fact]
    public void AdaptiveTargetRisesOnRepeatedUnderrunsAndRelaxes()
    {
        var jb = new JitterBuffer(FPP, targetPackets: 2, maxPackets: 6) { AdaptiveTarget = true };
        uint seq = 0;
        void Burst(int pkts) { for (int i = 0; i < pkts; i++) jb.Push(seq++, Const(1000)); }
        // Starve the buffer three times within a few seconds.
        for (int k = 0; k < 3; k++)
        {
            Burst(jb.Target);
            for (int i = 0; i < jb.Target + 3; i++) jb.Pull(NB);
            for (int i = 0; i < 200; i++) jb.Pull(NB); // 1 s of dry pulls (advance the clock only while _next is set)
        }
        Assert.True(jb.ExtraPackets >= 1, $"extra {jb.ExtraPackets}");
        Assert.Equal(jb.BaseTarget + jb.ExtraPackets, jb.Target);
        int raised = jb.ExtraPackets;
        // A calm minute: steady feed, one packet per pull.
        Burst(jb.Target);
        for (int i = 0; i < 12_400; i++) { jb.Push(seq++, Const(1000)); jb.Pull(NB); }
        Assert.True(jb.ExtraPackets < raised, $"extra stayed {jb.ExtraPackets}");
    }
}
