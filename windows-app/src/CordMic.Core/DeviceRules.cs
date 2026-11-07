using System.Text.RegularExpressions;

namespace CordMic.Core;

/// <summary>Names Windows and phones use for USB tethering adapters (RNDIS, NCM, "Android USB").</summary>
public static class DeviceRules
{
    static readonly Regex TetherRe = new(
        @"remote\s*ndis|rndis|usbncm|usb\s*ncm|\bncm\b|samsung mobile usb|android usb ethernet|usb ethernet/rndis",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    static readonly Regex CableRe = new(@"^CABLE Input\b|^CABLE-[A-D] Input\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex VacRe = new(@"\bLine 1\b.*\bVirtual Audio Cable\b|\bVirtual Audio Cable\b.*\bLine 1\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex VmRe = new(@"VoiceMeeter\s+(Input|Output|Aux|VAIO)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex CableOutRe = new(@"^CABLE(?:-[A-D])? Input\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsTetheringAdapter(string? description, string? name = null) =>
        TetherRe.IsMatch(description ?? "") || (name != null && TetherRe.IsMatch(name));

    /// <summary>
    /// Preference rank for a playback device. 0 is best (stereo VB-CABLE Input).
    /// Multi-channel VB-CABLE endpoints such as "CABLE In 16ch" rank last among virtual cables
    /// so the 2-channel input is chosen automatically.
    /// </summary>
    public static int VirtualCableRank(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1;
        if (CableIdentity.IsStereoCableInput(name)) return 0;
        if (CableRe.IsMatch(name) && !CableIdentity.IsSixteenChannel(name)) return 1;
        if (VacRe.IsMatch(name)) return 2;
        if (VmRe.IsMatch(name)) return 3;
        if (name.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase))
            return CableIdentity.IsSixteenChannel(name) ? 8 : 4;
        return -1;
    }

    public static string? PickVirtualCable(IEnumerable<string> names) =>
        names.OrderBy(n => VirtualCableRank(n) is var r && r >= 0 ? r : 99).ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
             .FirstOrDefault(n => VirtualCableRank(n) >= 0);

    public static string? MicNameFor(string? playbackName)
    {
        if (string.IsNullOrWhiteSpace(playbackName)) return null;
        if (CableIdentity.IsStereoCableInput(playbackName))
            return Regex.Replace(playbackName, @"\bInput\b", "Output", RegexOptions.IgnoreCase);
        var m = CableOutRe.Match(playbackName);
        if (m.Success) return CableOutRe.Replace(playbackName, m.Value.Replace("Input", "Output", StringComparison.OrdinalIgnoreCase), 1);
        if (VacRe.IsMatch(playbackName)) return Regex.Replace(playbackName, @"\bLine 1\b", "Line 1", RegexOptions.IgnoreCase);
        if (VmRe.IsMatch(playbackName)) return Regex.Replace(playbackName, @"\bInput\b", "Output", RegexOptions.IgnoreCase);
        return null;
    }
}

public enum JitterPreset { Low, Normal, Safe }

public static class Presets
{
    public static double Ms(JitterPreset p) => p switch { JitterPreset.Low => 5, JitterPreset.Safe => 20, _ => 10 };
    public static string Label(JitterPreset p) => p switch
    {
        JitterPreset.Low => L.T("jitter.low"),
        JitterPreset.Safe => L.T("jitter.safe"),
        _ => L.T("jitter.normal"),
    };
}

/// <summary>How the Windows app opens the output device. New installs default to Auto.</summary>
public enum OutputMode { LowLatency, Stable, Exclusive, Auto }

public static class OutputModes
{
    public static readonly OutputMode[] All = { OutputMode.Auto, OutputMode.LowLatency, OutputMode.Stable, OutputMode.Exclusive };
    public static string Label(OutputMode m) => m switch
    {
        OutputMode.Auto => L.T("outmode.auto"),
        OutputMode.Stable => L.T("outmode.stable"),
        OutputMode.Exclusive => L.T("outmode.exclusive"),
        _ => L.T("outmode.low"),
    };
}
