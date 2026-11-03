using System.Text.RegularExpressions;

namespace CordMic.Core;

/// <summary>Pure name-matching rules (platform-independent and unit-tested; used by the Windows app).</summary>
public static class DeviceRules
{
    // Windows adapter descriptions seen for Android USB tethering:
    // "Remote NDIS based Internet Sharing Device", "Remote NDIS Compatible Device",
    // "SAMSUNG Mobile USB Remote NDIS Network Device", "UsbNcm Host Device", "USB Ethernet/RNDIS Gadget".
    private static readonly Regex Tether = new(
        @"remote\s*ndis|rndis|usbncm|usb\s*ncm|\bncm\b|samsung mobile usb|android usb ethernet|usb ethernet/rndis",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsTetheringAdapter(string description, string? name = null) =>
        Tether.IsMatch(description ?? "") || (name != null && Tether.IsMatch(name));

    // Render endpoints of known virtual cables. The app plays INTO these; apps record from the paired input.
    private static readonly (Regex Render, string RecordName, int Rank)[] Cables =
    {
        (new Regex(@"^CABLE Input\b|VB-Audio Virtual Cable", RegexOptions.IgnoreCase), "CABLE Output (VB-Audio Virtual Cable)", 0),
        (new Regex(@"CABLE-[A-D] Input|VB-Audio Cable [A-D]", RegexOptions.IgnoreCase), "CABLE-x Output (VB-Audio Cable x)", 1),
        (new Regex(@"Line \d+ \(Virtual Audio Cable\)", RegexOptions.IgnoreCase), "Line x (Virtual Audio Cable)", 2),
        (new Regex(@"VoiceMeeter (Aux )?Input", RegexOptions.IgnoreCase), "VoiceMeeter Output", 3),
    };

    /// <summary>Rank of a render endpoint as virtual cable (0 = best), or -1 if it is not one.</summary>
    public static int VirtualCableRank(string renderName)
    {
        foreach (var c in Cables) if (c.Render.IsMatch(renderName ?? "")) return c.Rank;
        return -1;
    }

    public static string? MicNameFor(string renderName)
    {
        foreach (var c in Cables) if (c.Render.IsMatch(renderName ?? "")) return c.RecordName;
        return null;
    }

    /// <summary>Choose the best virtual cable from render endpoint names; null if none.</summary>
    public static string? PickVirtualCable(IEnumerable<string> renderNames) =>
        renderNames.Select(n => (n, r: VirtualCableRank(n))).Where(x => x.r >= 0)
                   .OrderBy(x => x.r).Select(x => x.n).FirstOrDefault();
}

public enum JitterPreset { Low, Normal, Safe }

/// <summary>Windows output path. LowLatency: shared mode, about one device period queued. Stable: shared mode,
/// whole buffer kept full (pre-0.4.2 behaviour). Exclusive: WASAPI exclusive mode at a short period.</summary>
public enum OutputMode { LowLatency, Stable, Exclusive }

public static class OutputModes
{
    public static readonly OutputMode[] All = { OutputMode.LowLatency, OutputMode.Stable, OutputMode.Exclusive };
    public static string Label(OutputMode m) => m switch
    {
        OutputMode.Stable => L.T("outmode.stable"),
        OutputMode.Exclusive => L.T("outmode.exclusive"),
        _ => L.T("outmode.low"),
    };
}

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
