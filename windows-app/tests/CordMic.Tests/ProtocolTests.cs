using CordMic.Core;
using Xunit;

public class ProtocolTests
{
    [Fact] // same vector as windows-receiver/tests/test_protocol.py and android ProtocolTest.kt
    public void HeaderLayoutMatchesPythonAndAndroid()
    {
        var b = Protocol.Encode(Protocol.TPing, 0x01020304, 0x1122334455667788UL);
        Assert.Equal("554d010404030201887766554433221100000000", Convert.ToHexString(b).ToLowerInvariant());
    }

    [Fact]
    public void RoundTripAudio()
    {
        var flags = Protocol.MakeFlags(channels: 1, sampleRate: 48000);
        Assert.Equal(0, flags);
        var data = Protocol.Encode(Protocol.TAudio, 123456, 987654321, new byte[480], 240, flags);
        Assert.Equal(Protocol.HeaderSize + 480, data.Length);
        Assert.True(Protocol.TryDecode(data, out var h, out _));
        Assert.Equal((Protocol.TAudio, 123456u, 987654321UL, (ushort)240), (h.Type, h.Seq, h.TsUs, h.Frames));
        Assert.Equal((1, 48000, 0), (h.Channels, h.SampleRate, h.Codec));
    }

    [Fact]
    public void FlagsMatchPythonMakeFlags()
    {
        // python: make_flags(channels=2, sample_rate=16000) == 0x0210 ; make_flags(channels=1, sample_rate=44100) == 0x0100
        Assert.Equal(0x0210, Protocol.MakeFlags(channels: 2, sampleRate: 16000));
        Assert.Equal(0x0100, Protocol.MakeFlags(channels: 1, sampleRate: 44100));
    }

    [Fact]
    public void BadPackets()
    {
        Assert.False(Protocol.TryDecode("xx"u8, out _, out var e1)); Assert.Equal("short packet", e1);
        var bad = new byte[20]; bad[0] = (byte)'X'; bad[1] = (byte)'X';
        Assert.False(Protocol.TryDecode(bad, out _, out var e2)); Assert.Equal("bad magic", e2);
        var mismatch = Protocol.Encode(Protocol.TAudio, 1, 1, new byte[10], 240, Protocol.MakeFlags());
        Assert.False(Protocol.TryDecode(mismatch, out _, out _));
        var v2 = Protocol.Encode(Protocol.TPing, 1, 1); v2[2] = 2;
        Assert.False(Protocol.TryDecode(v2, out _, out _));
    }

    [Fact]
    public void SeqWrap()
    {
        Assert.Equal(1, Protocol.SeqDiff(0, 0xFFFFFFFF));
        Assert.Equal(-1, Protocol.SeqDiff(0xFFFFFFFF, 0));
        Assert.Equal(5, Protocol.SeqDiff(10, 5));
    }
}
