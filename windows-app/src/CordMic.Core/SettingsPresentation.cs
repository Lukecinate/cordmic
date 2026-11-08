namespace CordMic.Core;

/// <summary>
/// How Settings controls should present local choices versus a phone PONG echo.
/// The phone echo lags the user's last edit by a round-trip, so binding the
/// controls directly to it makes sliders and dropdowns snap back to the old
/// value and then jump forward when the PONG arrives.
/// </summary>
public static class SettingsPresentation
{
    /// <summary>Ignore a stale PONG for this long after the user (or this PC) changes a setting.</summary>
    public static readonly TimeSpan EchoIgnoreWindow = TimeSpan.FromMilliseconds(1500);

    public static int VisibleGain(int localGainDb) =>
        Math.Clamp(localGainDb, 0, VoiceChain.MaxGainDb);

    public static NoiseMode VisibleNoiseMode(NoiseMode local) => local;

    public static bool VisibleAutoVolume(bool local) => local;

    public static bool VisibleVoiceClarity(bool local) => local;

    /// <summary>
    /// Accept a phone-reported value into local settings only when the user is
    /// not editing and we did not recently push a change of our own.
    /// </summary>
    public static bool ShouldAcceptPhoneEcho(DateTime lastLocalChangeUtc, DateTime nowUtc, bool userEditing = false) =>
        !userEditing && (lastLocalChangeUtc == DateTime.MinValue || nowUtc - lastLocalChangeUtc >= EchoIgnoreWindow);

    /// <summary>True when a periodic UI refresh may write a control without clobbering an in-progress edit.</summary>
    public static bool ShouldApplyToControl(bool focused, bool captured, bool droppedDown) =>
        !focused && !captured && !droppedDown;

    public static NoiseMode NoiseModeFromPhone(bool noiseCancellation, int noiseBits, NoiseMode localFallback)
    {
        if (!noiseCancellation) return NoiseMode.Off;
        return noiseBits == 2 ? NoiseMode.Strong : noiseBits == 1 ? NoiseMode.Light : localFallback;
    }

    public static bool GainChanged(int currentDb, int nextDb) =>
        Math.Clamp(currentDb, 0, VoiceChain.MaxGainDb) != Math.Clamp(nextDb, 0, VoiceChain.MaxGainDb);
}
