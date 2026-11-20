namespace CordMic.Core.Usb;

public enum UsbFunctionKind
{
    Unknown = 0,
    Accessory = 1,
    AdbWinUsb = 2,
    TetherRndis = 3,
    Mtp = 4,
    Midi = 5,
    ChargingOrComposite = 6,
}

public sealed class UsbInterfaceRecord
{
    public int Index { get; set; }
    public int InterfaceClass { get; set; }
    public int InterfaceSubClass { get; set; }
    public int InterfaceProtocol { get; set; }
    public string BoundService { get; set; } = "";
    public bool MsOsCompatibleWinUsb { get; set; }
}

/// <summary>One USB device as seen by a read-only catalog scan. No raw serial, IPs or user names.</summary>
public sealed class UsbDeviceRecord
{
    public int VendorId { get; set; }
    public int ProductId { get; set; }
    public string HashedSerial { get; set; } = "";
    public string Name { get; set; } = "";
    public string BoundService { get; set; } = "";
    public int? ProblemCode { get; set; }
    public bool MsOsDescriptorPresent { get; set; }
    public bool MsOsDescriptorUnknown { get; set; } = true;
    public UsbFunctionKind Kind { get; set; }
    public List<UsbInterfaceRecord> Interfaces { get; set; } = new();

    public string HardwareId => $"USB\\VID_{VendorId:X4}&PID_{ProductId:X4}";

    public static UsbFunctionKind Classify(int vid, int pid, string? boundService, IReadOnlyList<UsbInterfaceRecord>? ifaces, string? name)
    {
        if (UsbIds.IsAccessory(vid, pid)) return UsbFunctionKind.Accessory;
        string svc = (boundService ?? "").Trim();
        string n = DeviceNames.Clean(name).ToLowerInvariant();
        if (ifaces is not null)
        {
            foreach (var i in ifaces)
            {
                if (i.InterfaceClass == 0x01 && i.InterfaceSubClass == 0x03) return UsbFunctionKind.Midi; // Audio, MIDI streaming
                if (i.MsOsCompatibleWinUsb || string.Equals(i.BoundService, "WinUSB", StringComparison.OrdinalIgnoreCase))
                {
                    if (i.InterfaceClass == 0xFF) return UsbFunctionKind.AdbWinUsb;
                }
                // CDC ECM/NCM or RNDIS-like
                if (i.InterfaceClass == 0x02 || i.InterfaceClass == 0x0A) return UsbFunctionKind.TetherRndis;
                if (i.InterfaceClass == 0x06) return UsbFunctionKind.Mtp; // Still Image / PTP often used for MTP
                if (i.InterfaceClass == 0x08) return UsbFunctionKind.Mtp; // Mass storage
            }
        }
        if (svc.Equals("WINUSB", StringComparison.OrdinalIgnoreCase)) return UsbFunctionKind.AdbWinUsb;
        if (svc.Equals("RNDIS", StringComparison.OrdinalIgnoreCase) || svc.Equals("NCM", StringComparison.OrdinalIgnoreCase)
            || n.Contains("remote ndis") || n.Contains("rndis") || n.Contains("ncm"))
            return UsbFunctionKind.TetherRndis;
        if (n.Contains("mtp") || n.Contains("media transfer") || svc.Contains("WUDFWpdMtp", StringComparison.OrdinalIgnoreCase))
            return UsbFunctionKind.Mtp;
        if (n.Contains("midi")) return UsbFunctionKind.Midi;
        if (n.Contains("composite") || n.Contains("charging")) return UsbFunctionKind.ChargingOrComposite;
        return UsbFunctionKind.Unknown;
    }
}
