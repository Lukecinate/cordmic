namespace CordMic.Core.Usb;

/// <summary>Persisted values of <see cref="AppSettings.UsbConnectionMode"/>.</summary>
public static class UsbConnectionModeName
{
    public const string Auto = "auto";
    public const string UsbOnly = "usb";
    public const string TetherOnly = "tether";

    public static string Normalize(string? raw)
    {
        if (string.Equals(raw, UsbOnly, StringComparison.OrdinalIgnoreCase)) return UsbOnly;
        if (string.Equals(raw, TetherOnly, StringComparison.OrdinalIgnoreCase)) return TetherOnly;
        return Auto;
    }

    public static bool IsTetherOnly(string? raw) => Normalize(raw) == TetherOnly;
    public static bool IsUsbOnly(string? raw) => Normalize(raw) == UsbOnly;
}
