namespace CordMic.Core.Usb;

/// <summary>
/// Decides whether Auto may leave the USB-tethering path. Hardware gate G-M1 has not been
/// decided, so the default is the existing tethering route. Testers can opt in locally.
/// There is no remote flag and no network check.
/// </summary>
public static class UsbEngineGate
{
    /// <summary>Always false until the user records a G-M1 decision in the hardware checklist.</summary>
    public const bool HardwareGateGm1Passed = false;

    public const string ExperimentalFileName = "enable-experimental-usb";
    public const string DisableFileName = "disable-usb-engine";

    public static bool KillSwitchOn(AppSettings? settings, bool cliNoUsbEngine, bool disableFilePresent)
    {
        if (cliNoUsbEngine) return true;
        if (disableFilePresent) return true;
        return settings?.UsbEngineOff == true;
    }

    /// <summary>
    /// USB / MIDI routes may be attempted only when the kill switch is off and either G-M1 has
    /// passed or the local experimental opt-in is set. Auto still prefers a ready tether adapter.
    /// </summary>
    public static bool UsbRoutesAllowed(AppSettings? settings, bool cliNoUsbEngine, bool disableFilePresent, bool experimentalFilePresent)
    {
        if (KillSwitchOn(settings, cliNoUsbEngine, disableFilePresent)) return false;
        // HardwareGateGm1Passed is a const false until the checklist is filled. The
        // comparison is kept so flipping the const is the only production change.
#pragma warning disable CS0162
        if (HardwareGateGm1Passed) return true;
#pragma warning restore CS0162
        if (settings?.EnableExperimentalUsbRoutes == true) return true;
        return experimentalFilePresent;
    }

    public static bool MidiRouteAllowed(AppSettings? settings, bool cliNoUsbEngine, bool disableFilePresent, bool experimentalFilePresent)
    {
        if (!UsbRoutesAllowed(settings, cliNoUsbEngine, disableFilePresent, experimentalFilePresent)) return false;
        // MIDI is a separate G-M1(b) decision; default off even when experimental USB is on,
        // unless the tester also sets the MIDI opt-in.
        return settings?.EnableExperimentalMidiRoute == true;
    }

    public static string WhyDisabled(AppSettings? settings, bool cliNoUsbEngine, bool disableFilePresent, bool experimentalFilePresent)
    {
        if (cliNoUsbEngine) return "cli";
        if (disableFilePresent) return "file";
        if (settings?.UsbEngineOff == true) return "settings";
        if (!UsbRoutesAllowed(settings, cliNoUsbEngine, disableFilePresent, experimentalFilePresent))
            return "gate-pending";
        return "";
    }
}
