using CordMic.Core;
using Xunit;

/// <summary>0.4.2 latency work: soft catch-up of a standing backlog, resume sizing, averaged depth.</summary>
public class CatchUpTests
{
    const int FPP = 240, NB = FPP * 2;
    static byte[] Pkt(int n) => Enumerable.Repeat((byte)(n % 256), NB).ToArray();
    static byte[] Sil => new byte[NB];

    static JitterBuffer Backlogged(bool catchUp, int backlog = 8)
    {
        var jb = new JitterBuffer(FPP, targetPackets: 2, maxPackets: 40) { CatchUp = catchUp };
        uint seq = 1;
        for (int i = 0; i < backlog; i++, seq++) jb.Push(seq, Pkt((int)seq));
        // steady state: one packet in, one packet out, so without catch-up the backlog never shrinks
        for (int i = 0; i < 400; i++, seq++) { jb.Push(seq, Pkt((int)seq)); jb.Pull(NB); }
        return jb;
    }

    [Fact]
    public void StandingBacklogIsDrainedToTarget()
    {
        var jb = Backlogged(catchUp: true);
        Assert.True(jb.DepthPackets <= jb.Target + 1, $"depth {jb.DepthPackets}");
        Assert.True(jb.Stats.CatchUpDrops >= 5, $"drops {jb.Stats.CatchUpDrops}");
        Assert.Equal(0, jb.Stats.Underruns);
        Assert.Equal(0, jb.Stats.Concealed);
        Assert.Equal(0, jb.Stats.OverflowDrops);
    }

    [Fact]
    public void WithoutCatchUpBacklogStays()
    {
        var jb = Backlogged(catchUp: false);
        Assert.True(jb.DepthPackets >= 8, $"depth {jb.DepthPackets}");
        Assert.Equal(0, jb.Stats.CatchUpDrops);
    }

    [Fact]
    public void SmallJitterAroundTargetIsNotTrimmed()
    {
        var jb = new JitterBuffer(FPP, targetPackets: 2, maxPackets: 40);
        uint seq = 1;
        jb.Push(seq++, Pkt(1)); jb.Push(seq++, Pkt(2));
        // bursts of 2 every other pull: depth oscillates around target, never a standing excess
        for (int i = 0; i < 400; i++)
        {
            if (i % 2 == 0) { jb.Push(seq, Pkt((int)seq)); seq++; jb.Push(seq, Pkt((int)seq)); seq++; }
            jb.Pull(NB);
        }
        Assert.Equal(0, jb.Stats.CatchUpDrops);
        Assert.Equal(0, jb.Stats.Underruns);
    }

    [Fact]
    public void AverageDepthReportedAfterWindow()
    {
        var jb = new JitterBuffer(FPP, targetPackets: 2, maxPackets: 40) { WindowPackets = 50 };
        Assert.True(double.IsNaN(jb.AvgDepthMs));
        uint seq = 1;
        for (int i = 0; i < 3; i++, seq++) jb.Push(seq, Pkt((int)seq));
        for (int i = 0; i < 60; i++, seq++) { jb.Push(seq, Pkt((int)seq)); jb.Pull(NB); }
        Assert.False(double.IsNaN(jb.AvgDepthMs));
        // 3-4 packets queued (15-20 ms) around each pull: average wait must be inside that band
        Assert.InRange(jb.AvgDepthMs, 10, 20);
        Assert.False(double.IsNaN(jb.MinResidualMs));
    }

    [Fact]
    public void CrossfadeGoesFromFirstToSecondPacket()
    {
        static byte[] Const(short v) { var b = new byte[NB]; for (int i = 0; i < FPP; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 2), v); return b; }
        var dst = new byte[NB];
        JitterBuffer.CrossfadeInto(Const(10000), Const(-10000), dst, 1);
        short S(int i) => BitConverter.ToInt16(dst, i * 2);
        Assert.InRange(S(0), 9800, 10000);
        Assert.InRange(S(FPP - 1), -10000, -9800);
        Assert.InRange(S(FPP / 2), -200, 200);
    }

    [Fact]
    public void ResumeWaitsUntilAWholePullIsAvailable()
    {
        var jb = new JitterBuffer(FPP, targetPackets: 1, maxPackets: 40);
        jb.Push(1, Pkt(1));
        Assert.Equal(new byte[2 * NB], jb.Pull(2 * NB)); // one packet cannot satisfy a 10 ms pull: keep waiting
        jb.Push(2, Pkt(2));
        Assert.Equal(Pkt(1).Concat(Pkt(2)).ToArray(), jb.Pull(2 * NB));
    }
}
