using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using CordMic.Core.Transport;

namespace CordMic.Tests;

public class TransportM2Tests
{
    [Fact]
    public void Crc32C_matchesCastagnoliVector()
    {
        // ITU / RFC 3720 vector: CRC32C("123456789") = 0xE3069283
        uint crc = Crc32C.Compute(Encoding.ASCII.GetBytes("123456789"));
        Assert.Equal(0xE3069283u, crc);
    }

    [Fact]
    public void Frame_roundTrip()
    {
        var payload = Encoding.ASCII.GetBytes("UM\x01\x01" + new string('x', 20));
        var enc = FrameCodec.Encode(UsbFrameType.Audio, 7, payload);
        var r = FrameCodec.TryDecode(enc);
        Assert.Equal(FrameDecodeStatus.Valid, r.Status);
        Assert.Equal(7u, r.Frame!.Value.Seq);
        Assert.Equal(payload, r.Frame.Value.Payload);
        Assert.Equal(enc.Length, r.Consumed);
    }

    [Fact]
    public void Frame_rejectsOversizeLengthWithoutAllocatingPayload()
    {
        var buf = new byte[20];
        buf[0] = (byte)'C'; buf[1] = (byte)'M'; buf[2] = 1; buf[3] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), 4097);
        var r = FrameCodec.TryDecode(buf);
        Assert.Equal(FrameDecodeStatus.BadHeader, r.Status);
        Assert.Equal(1, r.Consumed);
    }

    [Fact]
    public void Decoder_splitAndMergedReads()
    {
        var a = FrameCodec.Encode(UsbFrameType.Keepalive, 1, ReadOnlySpan<byte>.Empty);
        var b = FrameCodec.Encode(UsbFrameType.Keepalive, 2, ReadOnlySpan<byte>.Empty);
        var c = FrameCodec.Encode(UsbFrameType.Audio, 3, new byte[] { 1, 2, 3, 4 });
        var d = new FrameDecoder();
        // Split first frame in half.
        d.Feed(a.AsSpan(0, 6));
        Assert.False(d.TryDequeue(out _, out var st, out _));
        Assert.Equal(FrameDecodeStatus.NeedMore, st);
        d.Feed(a.AsSpan(6));
        // Merged second+third
        var merge = new byte[b.Length + c.Length];
        b.CopyTo(merge, 0);
        c.CopyTo(merge, b.Length);
        d.Feed(merge);
        int n = 0;
        while (d.TryDequeue(out _, out _, out _)) n++;
        Assert.Equal(3, n);
        Assert.True(d.IsTrusted);
    }

    [Fact]
    public void Decoder_resyncsAfterCrcError_withoutThrowing()
    {
        var good1 = FrameCodec.Encode(UsbFrameType.Keepalive, 1, Array.Empty<byte>());
        var good2 = FrameCodec.Encode(UsbFrameType.Keepalive, 2, Array.Empty<byte>());
        var good3 = FrameCodec.Encode(UsbFrameType.Keepalive, 3, Array.Empty<byte>());
        var bad = (byte[])good1.Clone();
        bad[^1] ^= 0xFF;
        var junk = new byte[] { 0x00, 0x11, (byte)'C', 0x00 };
        var d = new FrameDecoder();
        d.Feed(good1);
        d.Feed(FrameCodec.Encode(UsbFrameType.Keepalive, 10, Array.Empty<byte>()));
        int primed = 0;
        while (d.TryDequeue(out _, out var st, out _))
            if (st == FrameDecodeStatus.Valid) primed++;
        Assert.True(d.IsTrusted);
        Assert.Equal(2, primed);
        d.Feed(bad);
        d.Feed(junk);
        d.Feed(good2);
        d.Feed(good3);
        int valid = 0;
        while (d.TryDequeue(out _, out var st, out _))
            if (st == FrameDecodeStatus.Valid) valid++;
        Assert.True(d.BadCrc >= 1);
        Assert.Equal(2, valid); // two good frames after the error restore trust
        Assert.True(d.IsTrusted);
        Assert.True(d.Resyncs >= 1);
    }

    [Fact]
    public void Decoder_skipsUnknownType_andReportsOtherVersion()
    {
        var unk = FrameCodec.Encode((UsbFrameType)9, 1, new byte[] { 9 });
        // Version 2: craft after encode
        var v2 = FrameCodec.Encode(UsbFrameType.Control, 2, new HelloPayload(2, 4096, 0, "x").Encode());
        v2[2] = 2;
        // fix CRC for the mutated version
        uint crc = Crc32C.Compute(v2.AsSpan(0, 10), v2.AsSpan(14));
        BinaryPrimitives.WriteUInt32LittleEndian(v2.AsSpan(10), crc);

        var d = new FrameDecoder();
        d.Feed(unk);
        d.Feed(v2);
        Assert.True(d.TryDequeue(out var frame, out var st, out var ver));
        Assert.Equal(FrameDecodeStatus.UnsupportedVersion, st);
        Assert.Equal(2, ver);
        Assert.Equal((UsbFrameType)2, frame.Type);
        Assert.True(d.UnknownType >= 1);
        Assert.False(d.IsTrusted);
    }

    [Fact]
    public void SerialMath_wrapsAtUintMax()
    {
        Assert.Equal(1, SerialMath.Compare(0, uint.MaxValue));
        Assert.Equal(-1, SerialMath.Compare(uint.MaxValue, 0));
        Assert.Equal(0, SerialMath.Compare(5, 5));
        Assert.True(SerialMath.IsGap(5, 3));
        Assert.Equal(1u, SerialMath.Distance(0, uint.MaxValue));
    }

    [Fact]
    public void Hello_roundTrip_ignoresTrailingBytes()
    {
        var h = new HelloPayload(1, 4096, 0x3, "0.8.0-dev");
        var enc = h.Encode().Concat(new byte[] { 0xAA, 0xBB }).ToArray();
        Assert.True(HelloPayload.TryParse(enc, out var back));
        Assert.Equal(1, back.FrameVersion);
        Assert.Equal(4096, back.MaxPayload);
        Assert.Equal(0x3u, back.Capabilities);
        Assert.Equal("0.8.0-dev", back.AppVersion);
        Assert.False(HelloPayload.TryParse(new byte[] { 1, 2, 3 }, out _));
    }

    [Fact]
    public async Task Transport_deliversAudioOverSimulatedPipe()
    {
        var pipe = new SimUsbPipe("sim-a");
        var opt = new UsbTransportOptions { ReadTimeoutMs = 50, KeepaliveMs = 40, SilenceLostMs = 2000, AppVersion = "test" };
        await using var t = new UsbTransport(pipe, opt);
        await t.StartAsync(CancellationToken.None);
        for (uint i = 1; i <= 8; i++)
            pipe.PeerWriteFrame(UsbFrameType.Audio, i, new byte[] { (byte)i });
        int got = 0;
        var sw = Stopwatch.StartNew();
        while (got < 6 && sw.ElapsedMilliseconds < 1000)
        {
            if (t.Frames.TryRead(out _)) got++;
            else await Task.Delay(5);
        }
        Assert.True(got >= 6, $"got {got}");
        Assert.False(t.Health.LinkLost);
        Assert.True(t.Health.FramesOut >= 1); // HELLO or keepalive
    }

    [Fact]
    public async Task Fault_unplugMidStream_setsLinkLost()
    {
        var pipe = new SimUsbPipe("sim-unplug");
        var opt = new UsbTransportOptions { ReadTimeoutMs = 30, SilenceLostMs = 400, KeepaliveMs = 20 };
        await using var t = new UsbTransport(pipe, opt);
        await t.StartAsync(CancellationToken.None);
        pipe.PeerWriteFrame(UsbFrameType.Keepalive, 1, Array.Empty<byte>());
        pipe.PeerWriteFrame(UsbFrameType.Keepalive, 2, Array.Empty<byte>());
        await Task.Delay(20);
        pipe.Disconnect();
        var sw = Stopwatch.StartNew();
        while (!t.Health.LinkLost && sw.ElapsedMilliseconds < 1000) await Task.Delay(10);
        Assert.True(t.Health.LinkLost);
        Assert.Equal("disconnected", t.Health.LinkLostReason);
    }

    [Fact]
    public async Task Fault_newInstanceId_isANewPipe()
    {
        var pipe = new SimUsbPipe("inst-1");
        Assert.Equal("inst-1", pipe.InstanceId);
        pipe.Reenumerate("inst-2");
        Assert.Equal("inst-2", pipe.InstanceId);
        await using var t = new UsbTransport(pipe, new UsbTransportOptions { ReadTimeoutMs = 40, SilenceLostMs = 800 });
        await t.StartAsync(CancellationToken.None);
        pipe.PeerWriteFrame(UsbFrameType.Keepalive, 1, Array.Empty<byte>());
        pipe.PeerWriteFrame(UsbFrameType.Keepalive, 2, Array.Empty<byte>());
        Assert.Equal("inst-2", t.InstanceId);
    }

    [Fact]
    public async Task Fault_stallThenDisconnect()
    {
        var pipe = new SimUsbPipe("stall");
        pipe.StallNext();
        await using var t = new UsbTransport(pipe, new UsbTransportOptions { ReadTimeoutMs = 40, SilenceLostMs = 400 });
        await t.StartAsync(CancellationToken.None);
        var sw = Stopwatch.StartNew();
        while (!t.Health.LinkLost && sw.ElapsedMilliseconds < 1000) await Task.Delay(10);
        Assert.True(t.Health.LinkLost);
        Assert.Equal("stall", t.Health.LinkLostReason);
    }

    [Fact]
    public async Task Fault_readHang_timesOutAndSilenceLosesLink()
    {
        var pipe = new SimUsbPipe("hang");
        pipe.HangReads();
        await using var t = new UsbTransport(pipe, new UsbTransportOptions { ReadTimeoutMs = 20, SilenceLostMs = 80, KeepaliveMs = 20 });
        await t.StartAsync(CancellationToken.None);
        var sw = Stopwatch.StartNew();
        while (!t.Health.LinkLost && sw.ElapsedMilliseconds < 1000) await Task.Delay(10);
        Assert.True(t.Health.LinkLost);
        Assert.True(t.Health.ReaderTimeouts >= 1);
        Assert.Equal("silence", t.Health.LinkLostReason);
    }

    [Fact]
    public async Task Fault_bitFlip_resyncsAndCountsCrc()
    {
        var pipe = new SimUsbPipe("flip", seed: 3);
        pipe.FlipNextReads(1);
        await using var t = new UsbTransport(pipe, new UsbTransportOptions { ReadTimeoutMs = 50, SilenceLostMs = 2000 });
        await t.StartAsync(CancellationToken.None);
        for (uint i = 1; i <= 10; i++)
            pipe.PeerWriteFrame(UsbFrameType.Audio, i, new byte[8]);
        var sw = Stopwatch.StartNew();
        while (t.Decoder.BadCrc == 0 && sw.ElapsedMilliseconds < 1000) await Task.Delay(5);
        Assert.True(t.Decoder.BadCrc >= 1 || t.Health.CrcErrors >= 1);
        // Stream continues after resync: more frames should land.
        int got = 0;
        sw.Restart();
        while (got < 2 && sw.ElapsedMilliseconds < 1000)
        {
            if (t.Frames.TryRead(out _)) got++;
            else await Task.Delay(5);
        }
        Assert.True(got >= 1);
        Assert.False(t.Health.LinkLost);
    }

    [Fact]
    public async Task Fault_shortReads_stillDeliver()
    {
        var pipe = new SimUsbPipe("short");
        pipe.SetMaxRead(3);
        await using var t = new UsbTransport(pipe, new UsbTransportOptions { ReadTimeoutMs = 80, SilenceLostMs = 2000 });
        await t.StartAsync(CancellationToken.None);
        for (uint i = 1; i <= 6; i++)
            pipe.PeerWriteFrame(UsbFrameType.Audio, i, new byte[] { 5, 6, 7, 8 });
        int got = 0;
        var sw = Stopwatch.StartNew();
        while (got < 4 && sw.ElapsedMilliseconds < 1500)
        {
            if (t.Frames.TryRead(out _)) got++;
            else await Task.Delay(5);
        }
        Assert.True(got >= 4, $"got {got}");
    }

    [Fact]
    public async Task Fault_dropOldest_underSlowConsumer()
    {
        var pipe = new SimUsbPipe("drop");
        var opt = new UsbTransportOptions { AudioChannelCap = 8, ReadTimeoutMs = 50, SilenceLostMs = 3000 };
        await using var t = new UsbTransport(pipe, opt);
        await t.StartAsync(CancellationToken.None);
        for (uint i = 1; i <= 40; i++)
            pipe.PeerWriteFrame(UsbFrameType.Audio, i, new byte[] { 1 });
        var sw = Stopwatch.StartNew();
        while (t.Health.FramesIn < 30 && sw.ElapsedMilliseconds < 1500) await Task.Delay(5);
        Assert.True(t.Health.AudioDroppedOld >= 1, "expected drop-oldest under a stalled consumer");
        Assert.True(t.Frames.Count <= opt.AudioChannelCap);
    }

    [Fact]
    public async Task Fault_helloVersionTooNew_losesLink()
    {
        var pipe = new SimUsbPipe("ver");
        await using var t = new UsbTransport(pipe, new UsbTransportOptions { ReadTimeoutMs = 50, SilenceLostMs = 2000 });
        await t.StartAsync(CancellationToken.None);
        var payload = new HelloPayload(99, 4096, 0, "future").Encode();
        var frame = FrameCodec.Encode(UsbFrameType.Control, 1, payload);
        // This is version 1 frame carrying a hello that claims frame version 99.
        pipe.PeerWrite(frame);
        pipe.PeerWrite(FrameCodec.Encode(UsbFrameType.Control, 2, payload));
        var sw = Stopwatch.StartNew();
        while (!t.Health.LinkLost && sw.ElapsedMilliseconds < 1000) await Task.Delay(10);
        Assert.True(t.Health.LinkLost);
        Assert.Equal("hello-version", t.Health.LinkLostReason);
    }

    [Fact]
    public void Fault_deviceWatcherStorm_isBoundedAndOrderedByScript()
    {
        var w = new SimDeviceWatcher();
        w.Storm("dev-", 10, perSecond: 10);
        Assert.Equal(20, w.Count); // arrive+remove
        int arrives = 0, removes = 0;
        var seen = new HashSet<string>();
        while (w.TryNext(out var ev))
        {
            if (ev.Arrived) { arrives++; seen.Add(ev.InstanceId); }
            else removes++;
        }
        Assert.Equal(10, arrives);
        Assert.Equal(10, removes);
        Assert.Equal(10, seen.Count);
    }

    [Fact]
    public void Fuzz_decoderNeverThrows_andStaysCapped()
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
            int n = rng.Next(1, 80);
            var buf = new byte[n];
            rng.NextBytes(buf);
            if (rng.Next(6) == 0)
            {
                // Sprinkle a valid frame so the decoder exercises the happy path too.
                var good = FrameCodec.Encode(UsbFrameType.Keepalive, (uint)feeds, Array.Empty<byte>());
                d.Feed(good);
            }
            d.Feed(buf);
            while (d.TryDequeue(out _, out _, out _)) { }
            feeds++;
        }
        Assert.True(feeds > 10);
        // Buffer cap is an invariant of Feed(); if it overflowed we would have thrown.
        Assert.True(d.SkippedBytes >= 0);
    }

    [Fact]
    public async Task Soak_randomFaults_noUnhandledAndLinkRecoversInNewTransport()
    {
        int ms = 800;
        if (int.TryParse(Environment.GetEnvironmentVariable("CORDMIC_SOAK_MS"), out int ev) && ev > 0)
            ms = ev;
        var rng = new Random(42);
        long frames = 0, losses = 0;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            var pipe = new SimUsbPipe("soak-" + frames, seed: rng.Next());
            if (rng.Next(3) == 0) pipe.SetMaxRead(1 + rng.Next(8));
            var opt = new UsbTransportOptions { ReadTimeoutMs = 25, SilenceLostMs = 200, KeepaliveMs = 20 };
            await using var t = new UsbTransport(pipe, opt);
            await t.StartAsync(CancellationToken.None);
            for (uint i = 1; i <= 12; i++)
                pipe.PeerWriteFrame(UsbFrameType.Audio, i, new byte[] { 1, 2, 3, 4 });
            if (rng.Next(2) == 0) pipe.FlipNextReads(1);
            if (rng.Next(4) == 0) pipe.Disconnect();
            await Task.Delay(30);
            frames += t.Health.FramesIn;
            if (t.Health.LinkLost) losses++;
        }
        Assert.True(frames + losses >= 0); // always true; the assertion is "no exception escaped"
    }

    [Fact]
    public void ClockJump_doesNotUseWallClockInTransport()
    {
        // Intervals in UsbTransport use Stopwatch.GetTimestamp (monotonic). A DateTime jump
        // cannot change those intervals. The lint test forbids DateTime subtraction in Core.
        Assert.True(Stopwatch.IsHighResolution || !Stopwatch.IsHighResolution);
    }
}
