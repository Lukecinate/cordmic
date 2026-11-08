using CordMic.Core;

public class SettingsPresentationTests
{
    [Fact]
    public void VisibleValuesFollowLocalSettingsNotAStalePhoneEcho()
    {
        Assert.Equal(8, SettingsPresentation.VisibleGain(8));
        Assert.Equal(0, SettingsPresentation.VisibleGain(-4));
        Assert.Equal(VoiceChain.MaxGainDb, SettingsPresentation.VisibleGain(99));
        Assert.Equal(NoiseMode.Strong, SettingsPresentation.VisibleNoiseMode(NoiseMode.Strong));
        Assert.False(SettingsPresentation.VisibleAutoVolume(false));
        Assert.True(SettingsPresentation.VisibleVoiceClarity(true));
    }

    [Fact]
    public void StalePhoneEchoIsIgnoredUntilTheGraceWindowPasses()
    {
        var t0 = new DateTime(2026, 11, 8, 7, 30, 0, DateTimeKind.Utc);
        Assert.False(SettingsPresentation.ShouldAcceptPhoneEcho(t0, t0.AddMilliseconds(200)));
        Assert.False(SettingsPresentation.ShouldAcceptPhoneEcho(t0, t0.AddMilliseconds(1499)));
        Assert.True(SettingsPresentation.ShouldAcceptPhoneEcho(t0, t0.AddMilliseconds(1500)));
        Assert.True(SettingsPresentation.ShouldAcceptPhoneEcho(DateTime.MinValue, t0));
        Assert.False(SettingsPresentation.ShouldAcceptPhoneEcho(t0, t0.AddSeconds(10), userEditing: true));
    }

    [Fact]
    public void PhoneNoiseModeIsIdempotentWithUnknownBits()
    {
        Assert.Equal(NoiseMode.Off, SettingsPresentation.NoiseModeFromPhone(false, 2, NoiseMode.Strong));
        Assert.Equal(NoiseMode.Light, SettingsPresentation.NoiseModeFromPhone(true, 1, NoiseMode.Off));
        Assert.Equal(NoiseMode.Strong, SettingsPresentation.NoiseModeFromPhone(true, 2, NoiseMode.Off));
        Assert.Equal(NoiseMode.Light, SettingsPresentation.NoiseModeFromPhone(true, 0, NoiseMode.Light));
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, true, false)]
    public void PeriodicRefreshDoesNotOverwriteABusyControl(bool focused, bool captured, bool droppedDown, bool apply) =>
        Assert.Equal(apply, SettingsPresentation.ShouldApplyToControl(focused, captured, droppedDown));

    [Fact]
    public void IdenticalGainIsNotAChange()
    {
        Assert.False(SettingsPresentation.GainChanged(6, 6));
        Assert.False(SettingsPresentation.GainChanged(-1, 0));
        Assert.True(SettingsPresentation.GainChanged(3, 8));
    }
}
