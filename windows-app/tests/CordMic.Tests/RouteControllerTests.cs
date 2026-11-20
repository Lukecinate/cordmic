using CordMic.Core;
using CordMic.Core.Usb;

namespace CordMic.Tests;

public class RouteControllerTests
{
    [Fact]
    public void DefaultAuto_withoutExperimental_staysOnTether()
    {
        var c = new RouteController(); // ExperimentalUsb false
        c.Post(new RouteEvent("Arm"));
        Assert.Equal(RouteState.TetherWaiting, c.State);
        c.Post(new RouteEvent("DeviceArrived", "dev-a"));
        Assert.Equal(RouteState.TetherWaiting, c.State);
        c.Post(new RouteEvent("TetherReady"));
        Assert.Equal(RouteState.TetherStreaming, c.State);
    }

    [Fact]
    public void TetherReady_wins_andDoesNotHandshake()
    {
        var c = Experimental();
        c.Post(new RouteEvent("Arm"));
        Assert.Equal(RouteState.Probing, c.State);
        c.Post(new RouteEvent("TetherReady"));
        Assert.Equal(RouteState.TetherStreaming, c.State);
        c.Post(new RouteEvent("DeviceArrived", "dev-a"));
        Assert.Equal(RouteState.TetherStreaming, c.State);
        Assert.DoesNotContain(c.Log, l => l.Contains("-> Handshaking"));
    }

    [Fact]
    public void OneHandshakePerPlugin_thenFallback()
    {
        var c = Experimental();
        c.Post(new RouteEvent("Arm"));
        c.Post(new RouteEvent("DeviceArrived", "dev-a"));
        Assert.Equal(RouteState.Handshaking, c.State);
        c.Post(new RouteEvent("HandshakeFail", "dev-a", "no-winusb"));
        Assert.Equal(RouteState.TetherWaiting, c.State);
        Assert.Equal(RouteHint.FallbackTether, c.Hint);
        c.Post(new RouteEvent("DeviceArrived", "dev-a"));
        Assert.Equal(RouteState.TetherWaiting, c.State);
        Assert.Equal(1, c.HandshakeAttemptsThisPlugin);
    }

    [Fact]
    public void FailureAfterStart_asksForReplug()
    {
        var c = Experimental();
        c.Post(new RouteEvent("Arm"));
        c.Post(new RouteEvent("DeviceArrived", "dev-a"));
        c.Post(new RouteEvent("HandshakeFail", "dev-a", "no-hello", AfterStart: true));
        Assert.Equal(RouteHint.ReplugThenTether, c.Hint);
        Assert.Equal(RouteState.TetherWaiting, c.State);
    }

    [Fact]
    public void ThirdFailedSession_blocklists()
    {
        var c = Experimental();
        for (int i = 0; i < 3; i++)
        {
            c.Post(new RouteEvent("Arm"));
            c.Post(new RouteEvent("DeviceArrived", "dev-a"));
            c.Post(new RouteEvent("HandshakeFail", "dev-a", "fail"));
            c.Post(new RouteEvent("Disarm"));
        }
        c.Post(new RouteEvent("Arm"));
        c.Post(new RouteEvent("DeviceArrived", "dev-a"));
        Assert.Equal(RouteState.TetherWaiting, c.State);
        Assert.True(c.Blocklist.IsBlocked("dev-a"));
        c.Post(new RouteEvent("TryUsbAgain", "dev-a"));
        Assert.False(c.Blocklist.IsBlocked("dev-a"));
        c.Post(new RouteEvent("Disarm"));
        c.Post(new RouteEvent("Arm"));
        c.Post(new RouteEvent("DeviceArrived", "dev-a"));
        Assert.Equal(RouteState.Handshaking, c.State);
    }

    [Fact]
    public void KillSwitch_andTetherOnly_skipUsb()
    {
        var c = Experimental();
        c.KillSwitch = true;
        c.Post(new RouteEvent("Arm"));
        Assert.Equal(RouteState.TetherWaiting, c.State);
        c.KillSwitch = false;
        c.Mode = UsbConnectionModeName.TetherOnly;
        c.Post(new RouteEvent("Disarm"));
        c.Post(new RouteEvent("Arm"));
        c.Post(new RouteEvent("DeviceArrived", "dev-a"));
        Assert.NotEqual(RouteState.Handshaking, c.State);
    }

    [Fact]
    public void LinkLost_reconnectsOnce_thenFallsBack()
    {
        var c = Experimental();
        c.Post(new RouteEvent("Arm"));
        c.Post(new RouteEvent("DeviceArrived", "dev-a"));
        c.Post(new RouteEvent("HandshakeOk"));
        c.Post(new RouteEvent("AccessoryOpened"));
        Assert.Equal(RouteState.UsbStreaming, c.State);
        c.Post(new RouteEvent("LinkLost", "dev-a", "unplug"));
        Assert.Equal(RouteState.Probing, c.State);
        c.Post(new RouteEvent("DeviceArrived", "dev-a"));
        c.Post(new RouteEvent("HandshakeOk"));
        c.Post(new RouteEvent("AccessoryOpened"));
        c.Post(new RouteEvent("LinkLost", "dev-a", "unplug"));
        Assert.Equal(RouteState.TetherWaiting, c.State);
    }

    [Fact]
    public void DisarmWins_andSuspendRemembersArm()
    {
        var c = Experimental();
        c.Post(new RouteEvent("Arm"));
        c.Post(new RouteEvent("DeviceArrived", "dev-a"));
        c.Post(new RouteEvent("Disarm"));
        Assert.Equal(RouteState.Idle, c.State);
        Assert.False(c.Armed);
        c.Post(new RouteEvent("Arm"));
        c.Post(new RouteEvent("PowerSuspend"));
        Assert.Equal(RouteState.Idle, c.State);
        c.Post(new RouteEvent("Resume"));
        Assert.True(c.Armed);
        Assert.Equal(RouteState.Probing, c.State);
    }

    [Fact]
    public void HealthUnhealthyForThreeTicks_losesLink()
    {
        var c = Streaming();
        c.Post(new RouteEvent("HealthUnhealthy", Reason: "crc"));
        Assert.Equal(RouteState.UsbSuspect, c.State);
        c.Post(new RouteEvent("HealthUnhealthy", Reason: "crc"));
        c.Post(new RouteEvent("HealthUnhealthy", Reason: "crc"));
        Assert.Equal(RouteState.Probing, c.State);
    }

    [Fact]
    public void Diagnostics_masksAddressesAndUsers_andOmitsRecordings()
    {
        string raw = "peer 192.168.42.5 path C:\\Users\\rozan\\log.txt also fe80::1";
        string d = SanitizedDiagnostics.Build("0.8.0-dev", "Windows", new[] { raw }, new[] { "route: Idle -> TetherWaiting (arm-tether)" });
        Assert.DoesNotContain("192.168.42.5", d);
        Assert.Contains("x.x.x.x", d);
        Assert.DoesNotContain("rozan", d, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[user]", d);
        Assert.True(SanitizedDiagnostics.LooksSafe(d));
        Assert.Contains("No recordings included", d);
    }

    [Fact]
    public void AudioGates_areProvisionalAndAligned()
    {
        var g = AudioQualityGatesFile.Default;
        Assert.Equal(AudioQualityGates.StatusProvisional, g.Status);
        Assert.Equal(2, g.MaxAddedLatencyMedianMs);
        Assert.Equal(10, g.MaxAddedLatencyP95Ms);
        Assert.Equal(30, g.ReplugWindowSessions);
        Assert.True(g.LatencyPass(1.5, 9));
        Assert.False(g.LatencyPass(3, 9));
        Assert.True(g.ReplugPass(0));
        Assert.False(g.CanLock(0));
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
