using CordMic.Core;
using Xunit;

public class SettingsSyncTests
{
    [Fact]
    public void HigherSequenceWinsAndEchoIsIgnored()
    {
        var local = new FieldRev(1, SyncOrigin.Pc);
        Assert.True(SettingsSync.ShouldApply(local, new FieldRev(2, SyncOrigin.Phone)));
        Assert.False(SettingsSync.ShouldApply(local, new FieldRev(1, SyncOrigin.Pc))); // echo
        Assert.False(SettingsSync.ShouldApply(local, new FieldRev(1, SyncOrigin.Phone))); // same seq, keep local
        Assert.False(SettingsSync.ShouldApply(local, new FieldRev(0, SyncOrigin.Phone)));
        Assert.False(SettingsSync.ShouldApply(new FieldRev(3, SyncOrigin.Phone), new FieldRev(2, SyncOrigin.Pc)));
    }

    [Fact]
    public void InProgressEditIsNeverOverwritten()
    {
        var local = new FieldRev(1, SyncOrigin.Pc);
        Assert.False(SettingsSync.ShouldApply(local, new FieldRev(4, SyncOrigin.Phone), localBusy: true));
        Assert.True(SettingsSync.ShouldApply(local, new FieldRev(4, SyncOrigin.Phone), localBusy: false));
    }

    [Fact]
    public void NextSeqSkipsZeroAndWraps()
    {
        Assert.Equal(1, SettingsSync.NextSeq(0, 0));
        Assert.Equal(6, SettingsSync.NextSeq(5, 3));
        Assert.Equal(1, SettingsSync.NextSeq(255, 255));
    }

    [Fact]
    public void SequenceWrapAroundStillAdvances()
    {
        Assert.True(SettingsSync.SeqAhead(1, 255));
        Assert.False(SettingsSync.SeqAhead(255, 1));
        Assert.False(SettingsSync.SeqAhead(10, 10));
    }

    [Fact]
    public void SyncPayloadRoundTripAndLegacyPongNameUntouched()
    {
        var revs = new FieldRev[SettingsSync.FieldCount];
        revs[(int)SyncField.Gain] = new FieldRev(4, SyncOrigin.Phone);
        revs[(int)SyncField.FrontFocus] = new FieldRev(2, SyncOrigin.Pc);
        var buf = new byte[SettingsSync.Size];
        byte flags = (byte)(SettingsSync.FlagPeerSync | SettingsSync.FlagFrontFocusAr | SettingsSync.FlagSpeechLow);
        SettingsSync.Write(buf, flags, revs);
        Assert.True(SettingsSync.TryRead(buf, out var f2, out var got));
        Assert.Equal(flags, f2);
        Assert.Equal(4, got[(int)SyncField.Gain].Seq);
        Assert.Equal(SyncOrigin.Phone, got[(int)SyncField.Gain].Origin);
        Assert.Equal(SyncOrigin.Pc, got[(int)SyncField.FrontFocus].Origin);

        // Classic PONG name, no trailing sync: 0.5.4 peers stay readable.
        var pong = new byte[32];
        int n = LinkControl.WritePong(pong, true, true, aaudio: true, exclusive: true, lowLatency: true,
            gainDb: 6, deviceName: "SM-S721B", mode: NoiseMode.Light, frontFocus: true, dirCode: 2, micCount: 3);
        Assert.True(LinkControl.TryReadPong(pong.AsSpan(0, n), out var phone));
        Assert.Equal("SM-S721B", phone.Name);
        Assert.False(phone.SyncCapable);

        // Extended PONG: name + NUL + sync. Name must still parse.
        var ext = new byte[64];
        int used = LinkControl.WritePong(ext, true, true, aaudio: false, exclusive: false, lowLatency: false,
            gainDb: 9, deviceName: "SM-S721B", mode: NoiseMode.Strong, frontFocus: true, dirCode: 1, micCount: 3,
            frontFocusAudioRecord: true, speechLow: true, sync: revs);
        Assert.True(LinkControl.TryReadPong(ext.AsSpan(0, used), out var p2));
        Assert.Equal("SM-S721B", p2.Name);
        Assert.True(p2.SyncCapable);
        Assert.True(p2.FrontFocusAudioRecord);
        Assert.True(p2.SpeechLow);
        Assert.Equal(4, p2.SyncRevs![(int)SyncField.Gain].Seq);
        Assert.Equal(SyncOrigin.Phone, p2.SyncRevs[(int)SyncField.Gain].Origin);
    }

    [Fact]
    public void ExtendedPingCarriesFrontFocusAudioRecordWithoutBreakingOldReaders()
    {
        var ping = new byte[4];
        LinkControl.WritePing(ping, true, true, 6, 1, extended: true, voiceClarity: true,
            mode: NoiseMode.Light, frontFocus: true, frontFocusAudioRecord: true);
        Assert.True((ping[1] & 128) != 0);
        Assert.True(LinkControl.TryReadPing(ping, out _, out _, out _, out _, out _, out _, out _, out bool front, out bool ar));
        Assert.True(front && ar);
    }
}
