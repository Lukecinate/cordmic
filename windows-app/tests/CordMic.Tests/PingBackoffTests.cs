using CordMic.Core;
using Xunit;

public class PingBackoffTests
{
    [Fact]
    public void NoBufferLogsOnceThenBacksOffAndRecoversOnce()
    {
        var b = new PingBackoff();
        Assert.Equal(TimeSpan.Zero, b.Delay);
        Assert.True(b.OnNoBuffer());
        Assert.Equal(TimeSpan.FromMilliseconds(250), b.Delay);
        Assert.False(b.OnNoBuffer());
        Assert.Equal(TimeSpan.FromMilliseconds(500), b.Delay);
        Assert.False(b.OnNoBuffer());
        Assert.Equal(TimeSpan.FromMilliseconds(1000), b.Delay);
        Assert.False(b.OnNoBuffer());
        Assert.Equal(TimeSpan.FromMilliseconds(2000), b.Delay);
        Assert.False(b.OnNoBuffer());
        Assert.Equal(TimeSpan.FromMilliseconds(4000), b.Delay);
        Assert.False(b.OnNoBuffer());
        Assert.Equal(TimeSpan.FromMilliseconds(4000), b.Delay); // capped
        Assert.True(b.OnSuccess());
        Assert.Equal(TimeSpan.Zero, b.Delay);
        Assert.False(b.OnSuccess());
        Assert.True(b.OnNoBuffer()); // a new episode logs again
    }

    [Fact]
    public void RecognisesWsaEnobufs()
    {
        Assert.True(PingBackoff.IsNoBuffer(10055));
        Assert.False(PingBackoff.IsNoBuffer(10054));
    }
}

public class LinkControlTests
{
    [Fact]
    public void PingAndPongPayloadsRoundTripAndOldPacketsDoNot()
    {
        var ping = new byte[4];
        LinkControl.WritePing(ping, noiseCancellation: true, agc: true, gainDb: 12, changeSeq: 3);
        Assert.True(LinkControl.TryReadPing(ping, out bool nc, out bool agc, out int gain, out byte seq));
        Assert.Equal((true, true, 12, (byte)3), (nc, agc, gain, seq));
        Assert.False(LinkControl.TryReadPing(ReadOnlySpan<byte>.Empty, out _, out _, out _, out _));

        var pong = new byte[4];
        LinkControl.WritePong(pong, true, true, aaudio: true, exclusive: false, lowLatency: true, gainDb: 18);
        Assert.True(LinkControl.TryReadPong(pong, out var phone));
        Assert.Equal("AAudio-ll", phone.Mode);
        Assert.Equal("+18dB", phone.GainText);
        Assert.Equal("on", phone.NoiseText);
        Assert.False(LinkControl.TryReadPong(new byte[4], out var unknown));
        Assert.Equal("?", unknown.Mode);

        ushort flags = Protocol.MakeFlags(noiseCancellation: true, agc: true, aaudio: true);
        Assert.Equal(0, flags & 0x0FFF); // 0.4.2 bits unchanged for mono 48 kHz
        Assert.True(Protocol.TryDecode(Protocol.Encode(Protocol.TAudio, 1, 2, new byte[480], 240, flags), out var h, out _));
        Assert.True(h.NoiseCancellation && h.Agc && h.AAudio);
        Assert.Equal(0x0210, Protocol.MakeFlags(channels: 2, sampleRate: 16000));
    }
}
