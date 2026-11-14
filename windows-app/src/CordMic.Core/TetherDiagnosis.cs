using System.Text.RegularExpressions;

namespace CordMic.Core;

/// <summary>0.7.2: what the PC can see of the phone's USB tethering link, from adapters and USB devices (read-only).</summary>
public enum TetherLink
{
    /// <summary>A tethering adapter is up with an IPv4 address.</summary>
    Ready,
    /// <summary>A tethering adapter exists but has no address yet or is not connected (Windows is still setting it up).</summary>
    Starting,
    /// <summary>The phone's USB network device is present but Windows reports a driver problem (code).</summary>
    DriverProblem,
    /// <summary>A phone is on USB but offers no network device: USB tethering is off ("Charging phone only").</summary>
    PhoneWithoutTethering,
    /// <summary>No phone and no tethering device on USB.</summary>
    NothingOnUsb,
}

/// <summary>One USB device node as reported by Windows Setup API (hardware and compatible IDs, names, class, problem code).</summary>
public sealed record UsbDeviceInfo(
    IReadOnlyList<string> HardwareIds,
    IReadOnlyList<string> CompatibleIds,
    string? Description,
    string? FriendlyName,
    bool IsNetClass,
    int ProblemCode);

/// <summary>A network adapter that looks like a tethering adapter (see <see cref="DeviceRules.IsTetheringAdapter"/>).</summary>
public sealed record TetherAdapterInfo(string Description, string Name, bool Up, bool HasIpv4);

public sealed record TetherDiagnosisResult(TetherLink Link, string? DeviceName = null, int ProblemCode = 0)
{
    /// <summary>Short, stable text for the log (no addresses).</summary>
    public string LogLine() => Link switch
    {
        TetherLink.Ready => "ready" + (DeviceName is null ? "" : $" ({DeviceName})"),
        TetherLink.Starting => "adapter present, no address yet" + (DeviceName is null ? "" : $" ({DeviceName})"),
        TetherLink.DriverProblem => $"phone network device has a driver problem, code {ProblemCode}" + (DeviceName is null ? "" : $" ({DeviceName})"),
        TetherLink.PhoneWithoutTethering => "phone on USB without a network device (USB tethering off)" + (DeviceName is null ? "" : $" ({DeviceName})"),
        _ => "nothing on USB that looks like a phone",
    };
}

public static class TetherDiagnosis
{
    /// <summary>USB vendor IDs of Android phone makers (Samsung first). PC and peripheral vendors are deliberately left out.</summary>
    public static readonly IReadOnlyList<string> PhoneVendorIds = new[]
    {
        "04E8", // Samsung
        "18D1", // Google (also generic Android)
        "2717", // Xiaomi
        "22D9", // OPPO / realme
        "2D95", // vivo
        "12D1", // Huawei / Honor
        "22B8", // Motorola
        "2A70", // OnePlus
        "0BB4", // HTC
        "1004", // LG
        "0FCE", // Sony
        "19D2", // ZTE
        "2A45", // Meizu
        "29A9", // Nothing
        "0E8D", // MediaTek-based phones
    };

    // RNDIS (Class E0/01/03 or EF/04/01), NCM (02/0D), Android "rndis" naming.
    static readonly Regex TetherClassRe = new(@"Class_E0&SubClass_01&Prot_03|Class_EF&SubClass_04&Prot_01|Class_02&SubClass_0D",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex VidRe = new(@"VID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex PhoneNameRe = new(@"\bandroid\b|samsung mobile|galaxy|\bpixel\b|\bmtp\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string? VendorId(UsbDeviceInfo d)
    {
        foreach (var id in d.HardwareIds)
        {
            var m = VidRe.Match(id);
            if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
        }
        return null;
    }

    public static bool IsPhone(UsbDeviceInfo d) =>
        (VendorId(d) is { } v && PhoneVendorIds.Contains(v)) || PhoneNameRe.IsMatch(Name(d) ?? "");

    /// <summary>The phone's network function: RNDIS/NCM class codes, a tethering adapter name, or a network-class node from a phone vendor.</summary>
    public static bool IsTetherDevice(UsbDeviceInfo d) =>
        d.CompatibleIds.Concat(d.HardwareIds).Any(id => TetherClassRe.IsMatch(id))
        // names only count for network-class devices: "SAMSUNG Mobile USB Composite Device" is present while tethering is off
        || (d.IsNetClass && DeviceRules.IsTetheringAdapter(d.Description, d.FriendlyName))
        || (d.IsNetClass && VendorId(d) is { } v && PhoneVendorIds.Contains(v));

    static string? Name(UsbDeviceInfo d) => string.IsNullOrWhiteSpace(d.FriendlyName) ? d.Description : d.FriendlyName;

    /// <summary>
    /// Combines adapters and USB devices into one answer. Order matters: a working adapter wins; then an adapter that is
    /// still starting; then a tethering device with a driver problem; then a phone without a network device; else nothing.
    /// </summary>
    public static TetherDiagnosisResult Diagnose(IReadOnlyList<TetherAdapterInfo> adapters, IReadOnlyList<UsbDeviceInfo>? usb)
    {
        var ready = adapters.FirstOrDefault(a => a.Up && a.HasIpv4);
        if (ready is not null) return new(TetherLink.Ready, ready.Description);
        var devices = usb ?? Array.Empty<UsbDeviceInfo>();
        var broken = devices.FirstOrDefault(d => IsTetherDevice(d) && d.ProblemCode != 0);
        if (broken is not null) return new(TetherLink.DriverProblem, Name(broken), broken.ProblemCode);
        var starting = adapters.FirstOrDefault();
        if (starting is not null) return new(TetherLink.Starting, starting.Description);
        var tether = devices.FirstOrDefault(d => IsTetherDevice(d));
        if (tether is not null) return new(TetherLink.Starting, Name(tether)); // device present, adapter not created yet
        var phone = devices.FirstOrDefault(IsPhone);
        if (phone is not null) return new(TetherLink.PhoneWithoutTethering, Name(phone));
        return new(TetherLink.NothingOnUsb);
    }

    /// <summary>String keys (title, detail, tooltip) for a state other than <see cref="TetherLink.Ready"/>.</summary>
    public static (string Title, string Detail, string Tip) Keys(TetherLink link) => link switch
    {
        TetherLink.Starting => ("status.tether.starting.title", "status.tether.starting.detail", "status.tether.starting.tip"),
        TetherLink.DriverProblem => ("status.tether.driver.title", "status.tether.driver.detail", "status.tether.driver.tip"),
        TetherLink.PhoneWithoutTethering => ("status.tether.off.title", "status.tether.off.detail", "status.tether.off.tip"),
        _ => ("status.tether.none.title", "status.tether.none.detail", "status.tether.none.tip"),
    };

    /// <summary>Long guidance for the Settings › Connection › Phone row, or null when the link works.</summary>
    public static string? HelpKey(TetherLink link) => link switch
    {
        TetherLink.Ready => null,
        TetherLink.DriverProblem => "status.tether.driver.help",
        TetherLink.Starting => "status.tether.starting.help",
        _ => "status.tether.steps",
    };
}
