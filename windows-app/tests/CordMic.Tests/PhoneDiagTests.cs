using CordMic.Core;
using Xunit;

/// <summary>0.5.7: phone diagnostics in the PONG, requested by the PC, readable alongside older peers.</summary>
public class PhoneDiagTests
{
    static readonly byte[] Golden = { 0xC7, 0xC2, 0x11, 0x07, 0xF4, 0xBD }; // same bytes as the Kotlin ProtocolTest

    static FieldRev[] Revs()
    {
        var r = new FieldRev[SettingsSync.FieldCount];
        r[(int)SyncField.Gain] = new FieldRev(3, SyncOrigin.Phone);
        return r;
    }

    [Fact]
    public void DiagBlockMatchesTheWireLayout()
    {
        var d = new PhoneDiag(-62, Gated: true, Agc: true, AgcGainDb: 8.5, ExpanderDb: -6, FloorKnown: true);
        var buf = new byte[PhoneDiag.Size];
        d.Write(buf);
        Assert.Equal(Golden, buf);
        Assert.True(PhoneDiag.TryRead(buf, out var back));
        Assert.Equal(d, back);
        buf[2] ^= 1;
        Assert.False(PhoneDiag.TryRead(buf, out _)); // check byte catches corruption
    }

    [Fact]
    public void PongWithDiagKeepsNameSyncAndShowsStats()
    {
        var pong = new byte[96];
        var d = new PhoneDiag(-61, Gated: false, Agc: true, AgcGainDb: 7.5, ExpanderDb: 0, FloorKnown: true);
        int n = LinkControl.WritePong(pong, true, true, aaudio: true, exclusive: false, lowLatency: true,
            gainDb: 12, deviceName: "Pixel 8", mode: NoiseMode.Strong, sync: Revs(), diag: d);
        Assert.True(LinkControl.TryReadPong(pong.AsSpan(0, n), out var p));
        Assert.Equal("Pixel 8", p.Name);
        Assert.True(p.SyncCapable);
        Assert.Equal(3, p.SyncRevs![(int)SyncField.Gain].Seq);
        Assert.Equal(d, p.Diag);
        Assert.Equal("-61dBFS", p.FloorText);
        Assert.Equal("open", p.GateText);
        Assert.Equal("+7.5dB", p.AgcGainText);
        Assert.Equal("0.0dB", p.ExpanderText);

        var stats = new StatsSnapshot("10.0.0.2", 1, 1, 0, 0, 0, 0, 0, 0, 0, 20, 3, 2, 1, 10, -30, 0, Phone: p);
        Assert.Contains("floor=-61dBFS gate=open agcgain=+7.5dB exp=0.0dB", stats.Line());
    }

    [Fact]
    public void PongWithoutDiagFromAnOlderPhoneStillParses()
    {
        var pong = new byte[96];
        int n = LinkControl.WritePong(pong, true, false, aaudio: true, exclusive: false, lowLatency: false,
            gainDb: 0, deviceName: "SM-A546E", sync: Revs());
        Assert.True(LinkControl.TryReadPong(pong.AsSpan(0, n), out var p));
        Assert.Equal("SM-A546E", p.Name);
        Assert.Null(p.Diag);
        Assert.Equal("-", p.FloorText);
        Assert.Equal("-", p.GateText);
        Assert.Equal("-", p.AgcGainText);
    }

    [Fact]
    public void DiagWithAnEmptyNameAndUnmeasuredFloor()
    {
        var pong = new byte[64];
        var d = new PhoneDiag(-50, Gated: true, Agc: false, AgcGainDb: 0, ExpanderDb: -12, FloorKnown: false);
        int n = LinkControl.WritePong(pong, false, false, aaudio: false, exclusive: false, lowLatency: false,
            gainDb: 0, deviceName: "", sync: Revs(), diag: d);
        Assert.True(LinkControl.TryReadPong(pong.AsSpan(0, n), out var p));
        Assert.Equal("", p.Name);
        Assert.Equal("~-50dBFS", p.FloorText);
        Assert.Equal("closed", p.GateText);
        Assert.Equal("off", p.AgcGainText);
        Assert.Equal("-12.0dB", p.ExpanderText);
    }

    [Fact]
    public void WantDiagIsAnUnusedBitForOlderPhones()
    {
        // Older phones read only bits 0–3 of flags2; the request must not change any of them.
        Assert.Equal(0, SettingsSync.FlagWantDiag & (SettingsSync.FlagFrontFocusAr | SettingsSync.FlagSpeechLow
            | SettingsSync.FlagPeerSync | SettingsSync.FlagVoiceClarity));
        var buf = new byte[SettingsSync.Size];
        byte flags = (byte)(SettingsSync.FlagPeerSync | SettingsSync.FlagWantDiag);
        SettingsSync.Write(buf, flags, Revs());
        Assert.True(SettingsSync.TryRead(buf, out var f2, out _));
        Assert.Equal(flags, f2);
    }
}
