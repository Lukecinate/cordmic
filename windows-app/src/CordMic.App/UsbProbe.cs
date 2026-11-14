using System.Runtime.InteropServices;
using System.Text;
using CordMic.Core;

namespace CordMic.App;

/// <summary>
/// 0.7.2: read-only list of the USB device nodes Windows has right now (Setup API, no admin, nothing is changed):
/// hardware and compatible IDs, names, whether it is a network-class device, and the Device Manager problem code.
/// <see cref="TetherDiagnosis"/> turns the list into "phone without tethering", "driver problem" and so on.
/// </summary>
internal static class UsbProbe
{
    private const uint DIGCF_PRESENT = 0x2, DIGCF_ALLCLASSES = 0x4;
    private const uint SPDRP_DEVICEDESC = 0x0, SPDRP_HARDWAREID = 0x1, SPDRP_COMPATIBLEIDS = 0x2, SPDRP_FRIENDLYNAME = 0xC;
    private const uint DN_HAS_PROBLEM = 0x400;
    private const int CR_SUCCESS = 0;
    private static readonly Guid NetClass = new("4d36e972-e325-11ce-bfc1-08002be10318");
    private const int MaxDevices = 512; // a PC has far fewer USB nodes; bounds the loop if the API misbehaves

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data, uint property,
        out uint regType, byte[]? buffer, uint size, out uint required);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    /// <summary>USB device nodes present now; empty when Setup API is unavailable. Never throws.</summary>
    public static IReadOnlyList<UsbDeviceInfo> Scan()
    {
        var list = new List<UsbDeviceInfo>();
        if (!OperatingSystem.IsWindows()) return list;
        IntPtr set = IntPtr.Zero;
        try
        {
            set = SetupDiGetClassDevsW(IntPtr.Zero, "USB", IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return list;
            for (uint i = 0; i < MaxDevices; i++)
            {
                var d = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(set, i, ref d)) break; // ERROR_NO_MORE_ITEMS
                int problem = 0;
                if (CM_Get_DevNode_Status(out var status, out var prob, d.DevInst, 0) == CR_SUCCESS && (status & DN_HAS_PROBLEM) != 0)
                    problem = (int)prob;
                list.Add(new UsbDeviceInfo(
                    MultiSz(set, ref d, SPDRP_HARDWAREID),
                    MultiSz(set, ref d, SPDRP_COMPATIBLEIDS),
                    Text(set, ref d, SPDRP_DEVICEDESC),
                    Text(set, ref d, SPDRP_FRIENDLYNAME),
                    d.ClassGuid == NetClass,
                    problem));
            }
        }
        catch (Exception) { /* diagnosis only: an empty list just means "unknown" */ }
        finally { if (set != IntPtr.Zero && set != new IntPtr(-1)) SetupDiDestroyDeviceInfoList(set); }
        return list;
    }

    private static byte[]? Raw(IntPtr set, ref SP_DEVINFO_DATA d, uint prop)
    {
        SetupDiGetDeviceRegistryPropertyW(set, ref d, prop, out _, null, 0, out uint need);
        if (need == 0 || need > 64 * 1024) return null;
        var buf = new byte[need];
        return SetupDiGetDeviceRegistryPropertyW(set, ref d, prop, out _, buf, need, out _) ? buf : null;
    }

    private static string? Text(IntPtr set, ref SP_DEVINFO_DATA d, uint prop)
    {
        var b = Raw(set, ref d, prop);
        if (b is null) return null;
        var s = Encoding.Unicode.GetString(b).TrimEnd('\0');
        return s.Length == 0 ? null : s;
    }

    private static IReadOnlyList<string> MultiSz(IntPtr set, ref SP_DEVINFO_DATA d, uint prop)
    {
        var b = Raw(set, ref d, prop);
        if (b is null) return Array.Empty<string>();
        return Encoding.Unicode.GetString(b).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
}
