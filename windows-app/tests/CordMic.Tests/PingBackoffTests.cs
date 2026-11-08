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

    [Fact]
    public void ExtendedPingCarriesModeAndStaysBackwardCompatible()
    {
        var ping = new byte[4];
        LinkControl.WritePing(ping, true, true, 6, 4, extended: true, voiceClarity: true, mode: NoiseMode.Strong);
        Assert.True(LinkControl.TryReadPing(ping, out bool nc, out bool agc, out int gain, out byte seq,
            out bool ext, out bool clarity, out NoiseMode mode));
        Assert.Equal((true, true, 6, (byte)4, true, true, NoiseMode.Strong), (nc, agc, gain, seq, ext, clarity, mode));
        // A 0.5.2 reader still sees bit0 NC and bit1 AGC.
        Assert.True(LinkControl.TryReadPing(ping, out bool nc2, out bool agc2, out _, out _));
        Assert.True(nc2 && agc2);

        var legacy = new byte[4];
        LinkControl.WritePing(legacy, noiseCancellation: true, agc: true, gainDb: 12, changeSeq: 1);
        Assert.True(LinkControl.TryReadPing(legacy, out _, out _, out _, out _, out bool ext2, out _, out NoiseMode mode2));
        Assert.False(ext2);
        Assert.Equal(NoiseMode.Light, mode2);
    }

    [Fact]
    public void ExtendedPingCarriesFrontFocusAndStaysBackwardCompatible()
    {
        var ping = new byte[4];
        LinkControl.WritePing(ping, true, true, 6, 5, extended: true, voiceClarity: true,
            mode: NoiseMode.Light, frontFocus: true);
        Assert.True(LinkControl.TryReadPing(ping, out _, out _, out _, out _, out bool ext, out _, out _, out bool front));
        Assert.True(ext && front);
        Assert.True((ping[1] & 64) != 0);
        Assert.True((ping[1] & 32) != 0);

        var off = new byte[4];
        LinkControl.WritePing(off, true, true, 6, 5, extended: true, voiceClarity: true,
            mode: NoiseMode.Light, frontFocus: false);
        Assert.True(LinkControl.TryReadPing(off, out _, out _, out _, out _, out _, out _, out _, out bool frontOff));
        Assert.False(frontOff);
    }
}
