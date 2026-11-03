using CordMic.Core;
using Xunit;

/// <summary>Ports of windows-receiver/tests/test_jitter.py (reference behaviour).</summary>
public class JitterTests
{
    const int FPP = 240, NB = FPP * 2;
    static byte[] Pkt(int n) => Enumerable.Repeat((byte)(n % 256), NB).ToArray();
    static byte[] Sil => new byte[NB];

    [Fact]
    public void PrebufferThenInOrder()
    {
        var jb = new JitterBuffer(FPP, targetPackets: 2);
        jb.Push(10, Pkt(10));
        Assert.Equal(Sil, jb.Pull(NB));
        jb.Push(11, Pkt(11));
        Assert.Equal(Pkt(10), jb.Pull(NB));
        Assert.Equal(Pkt(11), jb.Pull(NB));
    }

    [Fact]
    public void ReorderIsFixed()
    {
        var jb = new JitterBuffer(FPP, targetPackets: 3);
        foreach (var s in new uint[] { 1, 3, 2 }) jb.Push(s, Pkt((int)s));
        Assert.Equal(Pkt(1), jb.Pull(NB)); Assert.Equal(Pkt(2), jb.Pull(NB)); Assert.Equal(Pkt(3), jb.Pull(NB));
    }

    [Fact]
    public void LossConcealedWithSilence()
    {
        var jb = new JitterBuffer(FPP, targetPackets: 2);
        foreach (var s in new uint[] { 1, 3, 4 }) jb.Push(s, Pkt((int)s));
        Assert.Equal(Pkt(1), jb.Pull(NB)); Assert.Equal(Sil, jb.Pull(NB));
        Assert.Equal(Pkt(3), jb.Pull(NB)); Assert.Equal(Pkt(4), jb.Pull(NB));
        Assert.Equal(1, jb.Stats.Concealed);
    }

    [Fact]
    public void LatePacketDropped()
    {
        var jb = new JitterBuffer(FPP, targetPackets: 1);
        jb.Push(1, Pkt(1)); jb.Push(3, Pkt(3));
        jb.Pull(NB); jb.Pull(NB);
        jb.Push(2, Pkt(2));
        Assert.Equal(1, jb.Stats.Late);
        Assert.Equal(Pkt(3), jb.Pull(NB));
    }

    [Fact]
    public void UnderrunRebuffers()
    {
        var jb = new JitterBuffer(FPP, targetPackets: 2);
        jb.Push(1, Pkt(1)); jb.Push(2, Pkt(2));
        jb.Pull(NB); jb.Pull(NB);
        Assert.Equal(Sil, jb.Pull(NB));
        Assert.Equal(1, jb.Stats.Underruns);
        jb.Push(3, Pkt(3));
        Assert.Equal(Sil, jb.Pull(NB));
        jb.Push(4, Pkt(4));
        Assert.Equal(Pkt(3), jb.Pull(NB));
    }

    [Fact]
    public void OverflowBoundsLatency()
    {
        var jb = new JitterBuffer(FPP, targetPackets: 2, maxPackets: 6);
        for (uint s = 0; s < 100; s++) jb.Push(s, Pkt((int)s));
        Assert.True(jb.DepthPackets <= 6);
        Assert.True(jb.Stats.OverflowDrops > 0);
        Assert.True(jb.DepthMs() <= 30.0);
    }

    [Fact]
    public void OddPullSizes()
    {
        var jb = new JitterBuffer(FPP, targetPackets: 1);
        for (uint s = 0; s < 4; s++) jb.Push(s, Pkt((int)s));
        var data = new List<byte>();
        for (int i = 0; i < NB * 4 / 100; i++) data.AddRange(jb.Pull(100));
        data.AddRange(jb.Pull(NB * 4 % 100));
        Assert.Equal(Pkt(0).Concat(Pkt(1)).Concat(Pkt(2)).Concat(Pkt(3)).ToArray(), data.ToArray());
    }

    [Fact]
    public void SenderRestart()
    {
        var jb = new JitterBuffer(FPP, targetPackets: 1);
        jb.Push(50000, Pkt(1)); jb.Pull(NB);
        jb.Push(0, Pkt(2));
        Assert.Equal(Pkt(2), jb.Pull(NB));
    }

    [Fact]
    public void SeqWraparound()
    {
        var jb = new JitterBuffer(FPP, targetPackets: 2);
        jb.Push(0xFFFFFFFF, Pkt(1)); jb.Push(0, Pkt(2)); jb.Push(1, Pkt(3));
        Assert.Equal(Pkt(1), jb.Pull(NB)); Assert.Equal(Pkt(2), jb.Pull(NB)); Assert.Equal(Pkt(3), jb.Pull(NB));
    }

    [Fact]
    public void DuplicateIgnored()
    {
        var jb = new JitterBuffer(FPP, targetPackets: 1);
        jb.Push(5, Pkt(5)); jb.Push(5, Pkt(9));
        Assert.Equal(1, jb.Stats.Duplicates);
        Assert.Equal(Pkt(5), jb.Pull(NB));
    }

    [Fact]
    public void PacketSizeChangeReconfigures()
    {
        var jb = new JitterBuffer(FPP, targetPackets: 1);
        jb.Push(1, Pkt(1));
        var small = Enumerable.Repeat((byte)7, 240).ToArray(); // 120 frames
        jb.Push(2, small);
        Assert.Equal(120, jb.FramesPerPacket);
        Assert.Equal(small, jb.Pull(240));
    }
}
