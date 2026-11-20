using CordMic.Core.Transport;
using CordMic.Core.Usb;

namespace CordMic.Tests;

public class UsbM4Tests
{
    [Fact]
    public async Task Handshake_okOnProtocol1()
    {
        var ch = new SimAoaChannel { ProtocolVersion = 1 };
        var h = new AoaHandshaker(ch, "0.8.0-dev", "aabbccdd");
        var r = await h.RunAsync(CancellationToken.None);
        Assert.Equal(AoaHandshakeStatus.Ok, r.Status);
        Assert.Equal(1, r.ProtocolVersion);
        Assert.Equal(AoaProtocol.GetProtocol, ch.Requests[0]);
        Assert.Equal(AoaProtocol.Start, ch.Requests[^1]);
        Assert.Equal(8, ch.Transfers);
    }

    [Fact]
    public async Task Handshake_failsWhenProtocolMissing()
    {
        var ch = new SimAoaChannel { FailGetProtocol = true };
        var r = await new AoaHandshaker(ch, "x", "y").RunAsync(CancellationToken.None);
        Assert.Equal(AoaHandshakeStatus.ProtocolFailed, r.Status);
    }

    [Fact]
    public async Task Handshake_timesOutWithinBudget()
    {
        var ch = new SimAoaChannel { DelayMs = 2000 };
        var r = await new AoaHandshaker(ch, "x", "y").RunAsync(CancellationToken.None);
        Assert.Equal(AoaHandshakeStatus.Timeout, r.Status);
    }

    [Fact]
    public async Task Handshake_cancelIsDistinctFromTimeout()
    {
        var ch = new SimAoaChannel { DelayMs = 2000 };
        using var cts = new CancellationTokenSource();
        var task = new AoaHandshaker(ch, "x", "y").RunAsync(cts.Token);
        cts.CancelAfter(20);
        var r = await task;
        Assert.Equal(AoaHandshakeStatus.Cancelled, r.Status);
    }

    [Fact]
    public void PipePolicies_disableRawIo_andClearStall()
    {
        var p = WinUsbPipePolicy.DefaultPolicies();
        Assert.Contains(p, x => x.Policy == WinUsbPipePolicy.RawIo && x.Value == 0);
        Assert.Contains(p, x => x.Policy == WinUsbPipePolicy.AutoClearStall && x.Value == 1);
        Assert.Equal(500, WinUsbPipePolicy.BulkReadTimeoutMs);
        Assert.InRange(WinUsbPipePolicy.OutstandingReads, 2, 4);
    }

    [Fact]
    public void DeviceWatch_pumpsArrivalsAndDoesNotReuseInstanceIds()
    {
        using var w = new SimDeviceWatch();
        string? last = null;
        int arrives = 0;
        w.Changed += ev =>
        {
            if (ev.Arrived)
            {
                arrives++;
                Assert.NotEqual(last, ev.InstanceId);
                last = ev.InstanceId;
            }
        };
        w.Start();
        w.Enqueue(new DeviceWatchEvent(true, "a-1", WatchedDeviceKind.Accessory, UsbIds.GoogleVid, UsbIds.AccessoryPid));
        w.Enqueue(new DeviceWatchEvent(false, "a-1", WatchedDeviceKind.Accessory, UsbIds.GoogleVid, UsbIds.AccessoryPid));
        w.Enqueue(new DeviceWatchEvent(true, "a-2", WatchedDeviceKind.Accessory, UsbIds.GoogleVid, UsbIds.AccessoryPid));
        Assert.Equal(3, w.Pump());
        Assert.Equal(2, arrives);
        Assert.Equal("a-2", last);
    }

    [Fact]
    public async Task AfterHandshake_transportCanUseAFreshPipe()
    {
        var ch = new SimAoaChannel();
        var hs = await new AoaHandshaker(ch, "0.8.0-dev", "s").RunAsync(CancellationToken.None);
        Assert.Equal(AoaHandshakeStatus.Ok, hs.Status);
        var pipe = new SimUsbPipe("acc-after-start");
        await using var t = new UsbTransport(pipe, new UsbTransportOptions { ReadTimeoutMs = 40, SilenceLostMs = 1500 });
        await t.StartAsync(CancellationToken.None);
        pipe.PeerWriteFrame(UsbFrameType.Keepalive, 1, Array.Empty<byte>());
        pipe.PeerWriteFrame(UsbFrameType.Keepalive, 2, Array.Empty<byte>());
        Assert.Equal("acc-after-start", t.InstanceId);
    }
}
