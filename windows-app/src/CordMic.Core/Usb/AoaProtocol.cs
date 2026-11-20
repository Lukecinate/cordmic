using System.Text;

namespace CordMic.Core.Usb;

/// <summary>
/// Constructs Android Open Accessory 1.0 vendor requests 51 (GET_PROTOCOL), 52 (SEND_STRING)
/// and 53 (START). I/O is done by a platform pipe; this type is pure and has no device access.
/// String contents are fixed project constants; the accessory serial is a random per-install
/// token, not a hardware identifier.
/// </summary>
public static class AoaProtocol
{
    public const byte GetProtocol = 51;
    public const byte SendString = 52;
    public const byte Start = 53;
    public const int ControlTimeoutMs = 1000;
    public const int HandshakeBudgetMs = 5000;
    public const int ReenumerateWaitMs = 3000;
    public const ushort HostToDeviceVendor = 0x40;
    public const ushort DeviceToHostVendor = 0xC0;

    public const byte StringManufacturer = 0;
    public const byte StringModel = 1;
    public const byte StringDescription = 2;
    public const byte StringVersion = 3;
    public const byte StringUri = 4;
    public const byte StringSerial = 5;

    public readonly record struct ControlSetup(byte RequestType, byte Request, ushort Value, ushort Index, byte[] Data, int TimeoutMs);

    public static ControlSetup GetProtocolSetup() =>
        new((byte)DeviceToHostVendor, GetProtocol, 0, 0, new byte[2], ControlTimeoutMs);

    public static ControlSetup SendStringSetup(byte stringId, string value)
    {
        var payload = Encoding.UTF8.GetBytes(value + "\0");
        return new((byte)HostToDeviceVendor, SendString, 0, stringId, payload, ControlTimeoutMs);
    }

    public static ControlSetup StartSetup() =>
        new((byte)HostToDeviceVendor, Start, 0, 0, Array.Empty<byte>(), ControlTimeoutMs);

    public static IReadOnlyList<ControlSetup> HandshakeSetups(string appVersion, string installSerial)
    {
        return new[]
        {
            GetProtocolSetup(),
            SendStringSetup(StringManufacturer, UsbIds.Manufacturer),
            SendStringSetup(StringModel, UsbIds.Model),
            SendStringSetup(StringDescription, UsbIds.Description),
            SendStringSetup(StringVersion, appVersion),
            SendStringSetup(StringUri, UsbIds.Uri),
            SendStringSetup(StringSerial, installSerial),
            StartSetup(),
        };
    }

    public static bool TryReadProtocolVersion(ReadOnlySpan<byte> data, out int version)
    {
        version = 0;
        if (data.Length < 2) return false;
        version = data[0] | (data[1] << 8);
        return version >= 1;
    }

    /// <summary>Random 16-hex-character token used as the AOA serial string (not a device serial).</summary>
    public static string NewInstallSerial() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    public static string DescribeResult(bool protocolOk, int version, bool started) =>
        !protocolOk ? "get-protocol-failed"
        : version < 1 ? "protocol-unsupported"
        : started ? "start-sent"
        : "strings-sent";
}
