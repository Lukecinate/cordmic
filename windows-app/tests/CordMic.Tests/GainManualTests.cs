using System.Globalization;
using CordMic.Core;

/// <summary>1.1.1 manual gain: default 0 dB, signed range, no auto-boost, meter stays below yellow.</summary>
public class GainManualTests
{
    const int Sr = 48000;

    static short[] Tone(int n, double hz, double amp)
    {
        var s = new short[n];
        for (int i = 0; i < n; i++)
            s[i] = (short)Math.Clamp(Math.Round(amp * Math.Sin(2 * Math.PI * hz * i / Sr)), -32767, 32767);
        return s;
    }

    static int Peak(short[] s, int off, int n)
    {
        int p = 0;
        for (int i = off; i < off + n && i < s.Length; i++)
            p = Math.Max(p, Math.Abs((int)s[i]));
        return p;
    }

    static double PeakDb(short[] s, int off, int n)
    {
        int p = Peak(s, off, n);
        return 20 * Math.Log10(Math.Max(p, 1) / 32768.0);
    }

    static VoiceChain Live(double gain = 0, bool podcast = true, bool agc = true) =>
        new(Sr)
        {
            GainDb = gain,
            Podcast = podcast,
            Agc = agc,
            NoiseCancellation = false,
            VoiceClarity = false,
            HighPass = false,
            Expander = NoiseMode.Off,
        };

    [Fact]
    public void DefaultsAndRange()
    {
        Assert.Equal(0, VoiceChain.DefaultGainDb);
        Assert.Equal(-24, VoiceChain.MinGainDb);
        Assert.Equal(24, VoiceChain.MaxGainDb);
        Assert.Equal(-9, VoiceChain.MeterYellowDbfs);
        Assert.Equal(-1, VoiceChain.MeterRedDbfs);
        Assert.Equal(-12, VoiceChain.MeterPeakCeilingDbfs);
        Assert.Equal(-10, VoiceChain.CeilingDbfs);
        Assert.Equal(4, AppSettings.CurrentSchema);
        Assert.Equal(0, new AppSettings().MicGainDb);
        Assert.Equal(0, new VoiceChain(Sr).GainDb);
        Assert.Equal(-24, VoiceChain.ClampGain(-99));
        Assert.Equal(24, VoiceChain.ClampGain(99));
        Assert.Equal(-12, VoiceChain.ClampGain(-12));
    }

    [Fact]
    public void NudgeSnapAndFormat_AreTheSharedKnobSliderModel()
    {
        Assert.Equal(0.5, VoiceChain.NudgeGain(0, 1, fine: false));
        Assert.Equal(-0.5, VoiceChain.NudgeGain(0, -1, fine: false));
        Assert.Equal(0.1, VoiceChain.NudgeGain(0, 1, fine: true));
        Assert.Equal(-0.1, VoiceChain.NudgeGain(0, -1, fine: true));
        Assert.Equal(-24, VoiceChain.NudgeGain(-24, -1, fine: false));
        Assert.Equal(24, VoiceChain.NudgeGain(24, 1, fine: false));
        Assert.Equal(-6.0, VoiceChain.SnapGain(-6.04));
        Assert.Equal("0.0 dB", VoiceChain.FormatGain(0));
        Assert.Equal("0.0 dB", VoiceChain.FormatGain(0.04));
        Assert.Equal("+3.5 dB", VoiceChain.FormatGain(3.5));
        Assert.Equal("−12.0 dB", VoiceChain.FormatGain(-12));
        Assert.Equal("−0.5 dB", VoiceChain.FormatGain(-0.5));
        Assert.Equal(0, VoiceChain.SnapGain(VoiceChain.DefaultGainDb));
    }

    [Fact]
    public void NegativeGainAttenuates_AndLegacyByteNeverWraps()
    {
        Assert.Equal(0, VoiceChain.LegacyGainByte(-6));
        Assert.Equal(0, VoiceChain.LegacyGainByte(-0.1));
        Assert.Equal(0, VoiceChain.LegacyGainByte(0));
        Assert.Equal(6, VoiceChain.LegacyGainByte(6.2));
        double amp = 32768.0 * Math.Pow(10, -18.0 / 20.0);
        var a = Tone(Sr / 2, 1000, amp);
        var b = (short[])a.Clone();
        var zero = Live(0, podcast: false, agc: false);
        var cut = Live(-12, podcast: false, agc: false);
        zero.Process(a);
        cut.Process(b);
        double z = PeakDb(a, 2000, 8000);
        double c = PeakDb(b, 2000, 8000);
        Assert.InRange(z - c, 10.0, 14.0);
    }

    [Fact]
    public void AgcNeverAutoRaises_OnQuietOrTypicalSpeech()
    {
        foreach (bool podcast in new[] { true, false })
        {
            double amp = 32768.0 * Math.Pow(10, -30.0 / 20.0);
            var s = Tone(Sr * 3, 180, amp);
            var chain = Live(0, podcast, agc: true);
            chain.Process(s);
            double outDb = PeakDb(s, Sr * 2, Sr / 2);
            Assert.True(outDb < -26, $"boosted to {outDb:F1} dBFS podcast={podcast}");
            Assert.True(outDb < VoiceChain.MeterYellowDbfs);
            if (podcast)
                Assert.InRange(chain.PodcastAgcGainDb, -0.3, 0.05);
        }
    }

    [Fact]
    public void LoudAndFullScaleStayBelowYellow_AtZeroAndPositiveGain()
    {
        void Check(short[] src, double gain, bool podcast, string label)
        {
            var chain = Live(gain, podcast, agc: true);
            chain.Process(src);
            double pk = PeakDb(src, (int)(Sr * 0.08), src.Length - (int)(Sr * 0.08));
            Assert.True(pk < VoiceChain.MeterYellowDbfs, $"{label} peak {pk:F2} dBFS reached yellow");
        }

        double shoutAmp = 32768.0 * Math.Pow(10, -3.0 / 20.0);
        var shout = Tone(Sr, 1000, shoutAmp);
        var square = new short[Sr];
        for (int i = 0; i < square.Length; i++) square[i] = (short)((i / 80) % 2 == 0 ? 32767 : -32767);

        foreach (bool podcast in new[] { true, false })
        {
            Check((short[])shout.Clone(), 0, podcast, $"shout gain0 podcast={podcast}");
            Check((short[])shout.Clone(), 12, podcast, $"shout gain+12 podcast={podcast}");
            Check((short[])square.Clone(), 0, podcast, $"full-scale gain0 podcast={podcast}");
            Check((short[])square.Clone(), 24, podcast, $"full-scale gain+24 podcast={podcast}");
        }
    }

    [Fact]
    public void TypicalSpeechStaysInHealthyGreen_AtDefaultGain()
    {
        double amp = 32768.0 * Math.Pow(10, -18.0 / 20.0);
        var s = Tone(Sr * 2, 180, amp);
        var chain = Live(0, podcast: true, agc: true);
        chain.Process(s);
        double pk = PeakDb(s, Sr, Sr / 2);
        Assert.True(pk < VoiceChain.MeterYellowDbfs);
        Assert.InRange(pk, -24, -14);
    }

    [Fact]
    public void GainExtRoundTripsSignedTenths_AndLegacyPingStaysCompatible()
    {
        var buf = new byte[GainExt.Size];
        foreach (double db in new[] { 0, 0.1, -0.5, -24, 24, 3.5, -12.3 })
        {
            Assert.Equal(4, GainExt.Write(buf, db));
            Assert.True(GainExt.TryRead(buf, out double got));
            Assert.Equal(VoiceChain.SnapGain(db), got, 3);
        }

        var ping = new byte[LinkControl.Size];
        LinkControl.WritePing(ping, true, true, -6.5, 1);
        Assert.True(LinkControl.TryReadPing(ping, out _, out _, out double legacy, out _));
        Assert.Equal(0, legacy);

        var full = new byte[LinkControl.Size + SettingsSync.Size + GainExt.Size];
        LinkControl.WritePing(full, true, true, -6.5, 2, extended: true, voiceClarity: true, mode: NoiseMode.Light);
        SettingsSync.Write(full.AsSpan(LinkControl.Size), SettingsSync.FlagPeerSync, Array.Empty<FieldRev>());
        GainExt.Write(full.AsSpan(LinkControl.Size + SettingsSync.Size), -6.5);
        Assert.True(LinkControl.TryReadPing(full, out _, out _, out double fine, out _));
        Assert.Equal(-6.5, fine);

        var pong = new byte[256];
        var revs = new FieldRev[SettingsSync.FieldCount];
        int n = LinkControl.WritePong(pong, true, true, true, false, true, -8.0, "SM-TEST", sync: revs, writeGainExt: false);
        Assert.True(n < 256);
        Assert.True(LinkControl.TryReadPong(pong.AsSpan(0, n), out var phone));
        Assert.Equal(0, phone.GainDb);
        int n2 = LinkControl.WritePong(pong, true, true, true, false, true, -8.0, "SM-TEST", sync: revs, writeGainExt: true);
        Assert.True(LinkControl.TryReadPong(pong.AsSpan(0, n2), out var phone2));
        Assert.Equal(-8.0, phone2.GainDb);
    }

    [Fact]
    public void EnAndIdGainStringsExistAndMentionTheRange()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "CHANGELOG.md"))) d = d.Parent;
        Assert.NotNull(d);
        var root = d!.FullName;
        foreach (var rel in new[]
        {
            "android/app/src/main/res/values/strings.xml",
            "android/app/src/main/res/values-in/strings.xml",
        })
        {
            var xml = File.ReadAllText(Path.Combine(root, rel));
            Assert.Contains("gain_help", xml);
            Assert.Contains("gain_access", xml);
            Assert.Contains("24", xml);
        }
    }

    [Fact]
    public void DeviceTuneNeverSuggestsABoost()
    {
        Assert.Equal(0, DeviceTune.SuggestedGain(-36, -60));
        Assert.Equal(0, DeviceTune.SuggestedGain(-12, -40));
        Assert.Equal(0, DeviceTune.SuggestedGain(null, null));
    }
}
