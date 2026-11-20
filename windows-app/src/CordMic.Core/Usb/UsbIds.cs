namespace CordMic.Core.Usb;

/// <summary>
/// Public Android Open Accessory and CordMic accessory identifiers.
/// Values come from the published AOA 1.0 specification, not from other projects' source.
/// </summary>
public static class UsbIds
{
    public const int GoogleVid = 0x18D1;
    public const int AccessoryPid = 0x2D00;
    public const int AccessoryAdbPid = 0x2D01;

    public const string HardwareIdAccessory = @"USB\VID_18D1&PID_2D00";
    public const string HardwareIdAccessoryIf0 = @"USB\VID_18D1&PID_2D01&MI_00";

    /// <summary>WinUSB device-interface GUID used in the accessory INF. Fixed for the life of the package.</summary>
    public const string AccessoryInterfaceGuid = "{A3C9C3B7-8D41-4E6E-9B2F-0C0D8C0C0D80}";

    public const string Manufacturer = "CordMic";
    public const string Model = "CordMic Mic";
    public const string Description = "CordMic USB microphone";
    public const string Uri = "https://github.com/Lukecinate/cordmic";

    public static bool IsAccessory(int vid, int pid) =>
        vid == GoogleVid && (pid == AccessoryPid || pid == AccessoryAdbPid);
}
