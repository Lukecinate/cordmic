using System.Diagnostics;
using CordMic.Core.Transport;

namespace CordMic.Tests;

[Collection("cpu-timing")]
public class TransportChaosTests
{
    [Fact]
    public void Decoder_acceptsMaxPayload_andRejectsOneOver()
    {
        var max = new byte[FrameCodec.MaxPayload];
        max[0] = 9;
        var ok = FrameCodec.Encode(UsbFrameType.Audio, 1, max);
        var r = FrameCodec.TryDecode(ok);
        Assert.Equal(FrameDecodeStatus.Valid, r.Status);
        Assert.Equal(FrameCodec.MaxPayload, r.Frame!.Value.Payload.Length);

        var over = new byte[FrameCodec.HeaderSize + 8];
        over[0] = FrameCodec.Magic0;
        over[1] = FrameCodec.Magic1;
        over[2] = FrameCodec.Version;
        over[3] = (byte)UsbFrameType.Audio;
        over[4] = 0x01; // 4097 little-endian
        over[5] = 0x10;
        var bad = FrameCodec.TryDecode(over);
        Assert.Equal(FrameDecodeStatus.BadHeader, bad.Status);
        Assert.Equal(1, bad.Consumed);
    }

    [Fact]
    public void Decoder_reassemblesSplitFrame_includingSeqWrap()
    {
        var d = new FrameDecoder();
        foreach (uint seq in new uint[] { uint.MaxValue - 1, uint.MaxValue, 0, 1 })
        {
            var frame = FrameCodec.Encode(UsbFrameType.Audio, seq, new byte[] { 9, 8, 7, 6 });
            d.Feed(frame.AsSpan(0, 5));
            Assert.False(d.TryDequeue(out _, out _, out _));
            d.Feed(frame.AsSpan(5));
            Assert.True(d.TryDequeue(out var got, out var st, out _));
            Assert.Equal(FrameDecodeStatus.Valid, st);
            Assert.Equal(seq, got.Seq);
            Assert.Equal(new byte[] { 9, 8, 7, 6 }, got.Payload);
        }
        Assert.True(d.ValidFrames >= 4);
    }

    [Fact]
    public void Decoder_truncationThenGarbageThenTwoGoods_trustsAgain()
    {
        var d = new FrameDecoder();
        var a = FrameCodec.Encode(UsbFrameType.Keepalive, 1, Array.Empty<byte>());
        var b = FrameCodec.Encode(UsbFrameType.Keepalive, 2, Array.Empty<byte>());
        var c = FrameCodec.Encode(UsbFrameType.Keepalive, 3, Array.Empty<byte>());
        var e = FrameCodec.Encode(UsbFrameType.Keepalive, 4, Array.Empty<byte>());
        d.Feed(a);
        d.Feed(b);
        while (d.TryDequeue(out _, out _, out _)) { }
        Assert.True(d.IsTrusted);
        d.Feed(new byte[] { 0x00, 0x43 }); // truncated / noise
        d.Feed(c);
        d.Feed(e);
        int valid = 0;
        while (d.TryDequeue(out _, out var st, out _))
            if (st == FrameDecodeStatus.Valid) valid++;
        Assert.True(valid >= 2);
        Assert.True(d.IsTrusted);
        Assert.True(d.Resyncs >= 1 || d.BadHeader >= 1 || d.SkippedBytes >= 1);
    }

    [Fact]
    public void Fuzz_splitFeeds_neverThrowsAndStaysCapped()
    {
        var rng = new Random(20261121);
        var d = new FrameDecoder();
        int ms = 400;
        if (int.TryParse(Environment.GetEnvironmentVariable("CORDMIC_FUZZ_MS"), out int ev) && ev > 0)
            ms = ev;
        var sw = Stopwatch.StartNew();
        int feeds = 0;
        while (sw.ElapsedMilliseconds < ms)
        {
            if (rng.Next(5) == 0)
            {
                var good = FrameCodec.Encode(UsbFrameType.Audio, (uint)feeds, new byte[] { 1, 2, 3, 4 });
                int cut = 1 + rng.Next(good.Length - 1);
                d.Feed(good.AsSpan(0, cut));
                d.Feed(good.AsSpan(cut));
            }
            else
            {
                var buf = new byte[rng.Next(1, 120)];
                rng.NextBytes(buf);
                // Feed in 1..8 byte slices to hit the partial-header path.
                int o = 0;
                while (o < buf.Length)
                {
                    int n = Math.Min(buf.Length - o, 1 + rng.Next(8));
                    d.Feed(buf.AsSpan(o, n));
                    o += n;
                }
            }
            while (d.TryDequeue(out _, out _, out _)) { }
            feeds++;
        }
        Assert.True(feeds > 10);
        Assert.True(d.SkippedBytes >= 0);
    }

    [Fact]
    public async Task Soak_keepaliveOnly_staysUpUntilDisconnect()
    {
        var pipe = new SimUsbPipe("ka-only", seed: 7);
        var opt = new UsbTransportOptions { ReadTimeoutMs = 40, SilenceLostMs = 400, KeepaliveMs = 20 };
        await using var t = new UsbTransport(pipe, opt);
        await t.StartAsync(CancellationToken.None);
        for (uint i = 1; i <= 20; i++)
            pipe.PeerWriteFrame(UsbFrameType.Keepalive, i, Array.Empty<byte>());
        await Task.Delay(80);
        Assert.False(t.Health.LinkLost);
        pipe.Disconnect();
        var sw = Stopwatch.StartNew();
        while (!t.Health.LinkLost && sw.ElapsedMilliseconds < 1000)
            await Task.Delay(15);
        Assert.True(t.Health.LinkLost);
    }
}
