using CordMic.Core.Usb;

namespace CordMic.Tests;

public class RouteControllerChaosTests
{
    [Fact]
    public void KillSwitch_midUsbStream_fallsBackToTether_withoutHandshake()
    {
        var c = Streaming();
        c.KillSwitch = true;
        c.Post(new RouteEvent("UserPrefChanged"));
        Assert.Equal(RouteState.TetherWaiting, c.State);
        c.Post(new RouteEvent("DeviceArrived", "dev-a"));
        Assert.NotEqual(RouteState.Handshaking, c.State);
        Assert.DoesNotContain(c.Log, l => l.Contains("-> Handshaking") && l.Contains("pref"));
    }

    [Fact]
    public void TryUsbAgain_doesNotInterruptTetherStreaming()
    {
        var c = Experimental();
        c.Post(new RouteEvent("Arm"));
        c.Post(new RouteEvent("TetherReady"));
        Assert.Equal(RouteState.TetherStreaming, c.State);
        c.Post(new RouteEvent("TryUsbAgain", "dev-a"));
        Assert.Equal(RouteState.TetherStreaming, c.State);
        c.Post(new RouteEvent("DeviceArrived", "dev-a"));
        Assert.Equal(RouteState.TetherStreaming, c.State);
        Assert.DoesNotContain(c.Log, l => l.Contains("-> Handshaking"));
    }

    [Fact]
    public void HealthOk_beforeThreeTicks_returnsToStreaming()
    {
        var c = Streaming();
        c.Post(new RouteEvent("HealthUnhealthy", Reason: "crc"));
        Assert.Equal(RouteState.UsbSuspect, c.State);
        c.Post(new RouteEvent("HealthOk"));
        Assert.Equal(RouteState.UsbStreaming, c.State);
        Assert.Equal(0, c.UsbHealthUnhealthyTicks);
    }

    [Fact]
    public void Disarm_duringHandshake_goesIdle()
    {
        var c = Experimental();
        c.Post(new RouteEvent("Arm"));
        c.Post(new RouteEvent("DeviceArrived", "dev-a"));
        Assert.Equal(RouteState.Handshaking, c.State);
        c.Post(new RouteEvent("Disarm"));
        Assert.Equal(RouteState.Idle, c.State);
        Assert.False(c.Armed);
        Assert.Equal(0, c.HandshakeAttemptsThisPlugin);
    }

    [Fact]
    public void Blocklist_trimsTo32Lru()
    {
        var b = new UsbBlocklist();
        for (int i = 0; i < 40; i++)
            b.RecordFailure("dev-" + i, i);
        Assert.Equal(UsbBlocklist.MaxEntries, b.Entries.Count);
        Assert.DoesNotContain(b.Entries, e => e.Key == "dev-0");
        Assert.Contains(b.Entries, e => e.Key == "dev-39");
    }

    [Fact]
    public void DeviceStorm_whileTetherStreaming_isIgnored()
    {
        var c = Experimental();
        c.Post(new RouteEvent("Arm"));
        c.Post(new RouteEvent("TetherReady"));
        for (int i = 0; i < 40; i++)
        {
            c.Post(new RouteEvent("DeviceArrived", "dev-" + i));
            c.Post(new RouteEvent("DeviceRemoved", "dev-" + i));
        }
        Assert.Equal(RouteState.TetherStreaming, c.State);
        Assert.DoesNotContain(c.Log, l => l.Contains("-> Handshaking"));
    }

    [Fact]
    public void EventFlood_staysInDefinedState()
    {
        var c = Experimental();
        c.Post(new RouteEvent("Arm"));
        var rng = new Random(20261121);
        string[] kinds =
        [
            "DeviceArrived", "DeviceRemoved", "TetherReady", "TetherLost",
            "HandshakeOk", "HandshakeFail", "AccessoryOpened", "LinkLost",
            "HealthUnhealthy", "HealthOk", "TimerFired", "FrameReceived",
            "UserPrefChanged", "TryUsbAgain", "PowerSuspend", "Resume", "Disarm", "Arm"
        ];
        for (int i = 0; i < 500; i++)
        {
            string kind = kinds[rng.Next(kinds.Length)];
            c.Post(new RouteEvent(kind, "dev-" + (i % 7), "flood", AfterStart: rng.Next(5) == 0));
        }
        Assert.True(Enum.IsDefined(c.State));
        Assert.True(c.Log.Count <= 256);
    }

    [Fact]
    public void DefaultAuto_deviceStorm_neverHandshakes()
    {
        var c = new RouteController();
        c.Post(new RouteEvent("Arm"));
        for (int i = 0; i < 20; i++)
            c.Post(new RouteEvent("DeviceArrived", "dev-" + i));
        Assert.Equal(RouteState.TetherWaiting, c.State);
        Assert.DoesNotContain(c.Log, l => l.Contains("-> Handshaking"));
    }

    static RouteController Experimental() => new() { ExperimentalUsb = true };

    static RouteController Streaming()
    {
        var c = Experimental();
        c.Post(new RouteEvent("Arm"));
        c.Post(new RouteEvent("DeviceArrived", "dev-a"));
        c.Post(new RouteEvent("HandshakeOk"));
        c.Post(new RouteEvent("AccessoryOpened"));
        return c;
    }
}
