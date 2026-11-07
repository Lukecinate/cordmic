using System.Diagnostics;
using CordMic.Core;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// 0.5.0: the 0.4.2 log's underrun/overflow oscillation, the render-period floor, and the allocation-free hot path.
/// </summary>
public class StabilityTests
{
    private readonly ITestOutputHelper _out;
    public StabilityTests(ITestOutputHelper o) => _out = o;

    [Theory]
    [InlineData(5, 0, 1)]     // period unknown: preset passes through
    [InlineData(10, 0, 2)]
    [InlineData(20, 0, 4)]
    [InlineData(5, 10, 4)]    // Low vs 10 ms period: floor is two pulls (4 packets / 20 ms)
    [InlineData(10, 10, 4)]   // Normal vs 10 ms period: same two-pull floor
    [InlineData(20, 10, 4)]   // Safe already equals the floor
    [InlineData(5, 5, 2)]     // a 5 ms period pulls one packet, so the floor is 2
    [InlineData(5, 2.7, 2)]   // a 2.7 ms period still pulls one 5 ms packet → floor 2 (10 ms)
    [InlineData(10, 2.7, 2)]  // Normal already equals that floor
    public void TargetFloorsAtTwoPulls(double requestMs, double periodMs, int packets) =>
        Assert.Equal(packets, JitterPolicy.EffectiveTargetPackets(requestMs, periodMs, 5));

    [Theory]
    [InlineData(5, 10, 6, 10)]
    [InlineData(5, 2.7, 3, 5)]
    [InlineData(10, 10, 6, 10)]
    public void HighWaterAndMaxDepthFollowPeriod(double requestMs, double periodMs, int high, int max)
    {
        Assert.Equal(high, JitterPolicy.HighWaterPackets(requestMs, periodMs, 5));
        Assert.Equal(max, JitterPolicy.MaxDepthPackets(requestMs, periodMs, 5));
    }

    [Fact]
    public void SteadyPullStopsTheOscillation()
    {
        // 3 minutes, deterministic. Legacy: 10 ms shared period, pull grows when the callback wakes late
        // (0.4.2 kept period+3 ms queued and wrote cap-padding). New: write one period, target floored.
        var legacyNormal = Simulate(targetPackets: 2, variablePull: true, seconds: 180, seed: 42);
        var legacyLow = Simulate(targetPackets: 1, variablePull: true, seconds: 180, seed: 42);
        int floored = JitterPolicy.EffectiveTargetPackets(10, 10, 5);
        var next = Simulate(targetPackets: floored, variablePull: false, seconds: 180, seed: 42);

        double legacyU = legacyNormal.Underruns / 3.0, nextU = next.Underruns / 3.0;
        double legacyO = legacyNormal.Overflows / 3.0, nextO = next.Overflows / 3.0;
        _out.WriteLine($"legacy normal  urate={legacyU:F2}/min orate={legacyO:F2}/min underruns={legacyNormal.Underruns} ovf={legacyNormal.Overflows}");
        _out.WriteLine($"legacy low     urate={legacyLow.Underruns / 3.0:F2}/min orate={legacyLow.Overflows / 3.0:F2}/min");
        _out.WriteLine($"0.5.0 period-aware urate={nextU:F2}/min orate={nextO:F2}/min underruns={next.Underruns} ovf={next.Overflows} catchup={next.CatchUp}");

        // Low uses the same pull as Normal but catch-up treats anything above 1 packet as backlog,
        // so it strips the packet the 10 ms callback still needs.
        var lowFixed = Simulate(targetPackets: 1, variablePull: false, seconds: 180, seed: 7, fastPhone: true);
        var held = Simulate(targetPackets: floored, variablePull: false, seconds: 180, seed: 7, fastPhone: true);
        _out.WriteLine($"low preset, fast phone  urate={lowFixed.Underruns / 3.0:F2}/min ovf={lowFixed.Overflows} catchup={lowFixed.CatchUp}");
        _out.WriteLine($"floored, fast phone     urate={held.Underruns / 3.0:F2}/min ovf={held.Overflows} catchup={held.CatchUp}");

        Assert.True(legacyNormal.Underruns >= 30, $"legacy sim did not reproduce underruns ({legacyNormal.Underruns})");
        Assert.True(next.Underruns * 10 <= legacyNormal.Underruns, $"underruns {next.Underruns} vs legacy {legacyNormal.Underruns}");
        Assert.True(next.Overflows * 10 <= Math.Max(1, legacyNormal.Overflows), $"overflows {next.Overflows} vs legacy {legacyNormal.Overflows}");
        Assert.True(lowFixed.Underruns > held.Underruns * 5, $"Low preset was not worse ({lowFixed.Underruns} vs {held.Underruns})");
    }

    [Fact]
    public void HotPathAllocatesNothing()
    {
        var jb = new JitterBuffer(240, targetPackets: 3, maxPackets: 12);
        var pkt = new byte[480];
        pkt[0] = 1;
        var dst = new byte[480];
        for (uint s = 0; s < 8; s++) jb.Push(s, pkt);
        jb.Pull(dst);

        var legacy = new Dictionary<uint, byte[]>();
        long legacyBefore = GC.GetAllocatedBytesForCurrentThread();
        for (uint s = 0; s < 4000; s++) legacy[s] = (byte[])pkt.Clone();
        long legacyBytes = GC.GetAllocatedBytesForCurrentThread() - legacyBefore;

        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (uint s = 8; s < 4008; s++)
        {
            jb.Push(s, pkt);
            jb.Pull(dst);
        }
        sw.Stop();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        _out.WriteLine($"push+pull x4000: {sw.Elapsed.TotalMilliseconds:F2} ms, allocated {bytes} B (legacy dictionary clone path {legacyBytes} B)");
        Assert.True(bytes < 1024, $"hot path allocated {bytes} bytes");
        Assert.True(legacyBytes > 4000 * 480, "legacy figure should count the per-packet arrays");
        Assert.Equal(0, jb.Stats.Underruns);
    }

    readonly record struct Sim(long Underruns, long Overflows, long CatchUp);

    static Sim Simulate(int targetPackets, bool variablePull, int seconds, int seed, bool fastPhone = false)
    {
        var jb = new JitterBuffer(240, targetPackets: targetPackets, maxPackets: Math.Max(targetPackets * 3, targetPackets + 4));
        var rng = new Random(seed);
        var pkt = new byte[480];
        double tEnd = seconds * 1000.0;
        double tPkt = 0.35;
        double tRen = 10;
        uint seq = 0;
        double padding = 3;
        const double period = 10, margin = 3;
        // 50 ppm is a mild clock error. The fast-phone case (~400 ppm) is what makes a 1-packet target
        // grow just enough for catch-up to strip it back under a 10 ms pull.
        double drift = fastPhone ? 400e-6 : 50e-6;
        while (tPkt < tEnd || tRen < tEnd)
        {
            if (tPkt <= tRen)
            {
                jb.Push(seq++, pkt);
                tPkt += 5.0 * (1.0 - drift) + (rng.NextDouble() * 2 - 1) * 0.40;
            }
            else
            {
                double pullMs = period;
                if (variablePull)
                {
                    double late = rng.NextDouble() < 0.30 ? rng.NextDouble() * 3.0 : 0;
                    padding -= period + late;
                    if (padding < 0) padding = 0;
                    pullMs = period + margin - padding;
                    if (pullMs < 1) pullMs = 1;
                    padding += pullMs;
                }
                int frames = (int)Math.Round(pullMs / 1000.0 * 48000);
                jb.Pull(frames * 2);
                tRen += period;
            }
        }
        return new Sim(jb.Stats.Underruns, jb.Stats.OverflowDrops, jb.Stats.CatchUpDrops);
    }
}
