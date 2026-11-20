using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using CordMic.Core;

namespace CordMic.App;

/// <summary>
/// 0.8.0 M0 (F3): read-only picture of mic-only routing (no admin rights, no writes).
/// IP Helper offsets match the documented Windows 10+ x64 layout of MIB_IPINTERFACE_ROW (168 bytes)
/// and MIB_IPFORWARD_ROW2 (104 bytes). On a non-Windows host this returns null.
/// </summary>
internal static class RouteProtection
{
    const ushort AF_INET = 2, AF_INET6 = 23;
    const int IpInterfaceRowSize = 168;
    const int IpInterfaceIndexOffset = 16;
    const int IpInterfaceMetricOffset = 148;
    const int IpInterfaceDisableDefaultOffset = 166;
    const int IpForwardRowSize = 104;
    const int IpForwardTableHeader = 8;
    const int IpForwardIndexOffset = 8;
    const int IpForwardDestOffset = 12; // IP_ADDRESS_PREFIX: SOCKADDR_INET (family at 0) + prefix length at 28

    [DllImport("iphlpapi.dll")]
    static extern int GetIpInterfaceEntry(IntPtr row);

    [DllImport("iphlpapi.dll")]
    static extern int GetIpForwardTable2(ushort family, out IntPtr table);

    [DllImport("iphlpapi.dll")]
    static extern void FreeMibTable(IntPtr table);

    [DllImport("iphlpapi.dll")]
    static extern int GetBestInterfaceEx(IntPtr destAddr, out uint bestIfIndex);

    /// <summary>Build a snapshot for the tether adapter, or a "no adapter" snapshot when index is invalid.</summary>
    public static MicOnlySnapshot Read(int ifIndex, bool bestIpv4IsPhone)
    {
        if (ifIndex < 0 || !OperatingSystem.IsWindows())
            return new MicOnlySnapshot(false, null, null, null, null, 0, 0, null, false, false, false);
        try { return ReadCore(ifIndex, bestIpv4IsPhone); }
        catch (Exception) { return new MicOnlySnapshot(true, null, null, null, null, 0, 0, null, bestIpv4IsPhone, false, false); }
    }

    static MicOnlySnapshot ReadCore(int ifIndex, bool bestIpv4IsPhone)
    {
        var v4 = TryInterface((uint)ifIndex, AF_INET);
        var v6 = TryInterface((uint)ifIndex, AF_INET6);
        bool ipv6 = v6.ok;
        int def4 = CountDefaultRoutes(AF_INET, (uint)ifIndex);
        int def6 = ipv6 ? CountDefaultRoutes(AF_INET6, (uint)ifIndex) : 0;
        bool? dns = DnsIsLoopback(ifIndex);
        bool best6 = ipv6 && BestInterfaceIs(AF_INET6, (uint)ifIndex);
        return new MicOnlySnapshot(
            HasAdapter: true,
            IgnoreDefaultRoutesV4: v4.ok ? v4.ignoreDefault : null,
            IgnoreDefaultRoutesV6: v6.ok ? v6.ignoreDefault : null,
            MetricV4: v4.ok ? v4.metric : null,
            MetricV6: v6.ok ? v6.metric : null,
            DefaultRoutesV4: Math.Max(0, def4),
            DefaultRoutesV6: Math.Max(0, def6),
            DnsIsLoopback: dns,
            BestIpv4IsPhone: bestIpv4IsPhone,
            BestIpv6IsPhone: best6,
            Ipv6Present: ipv6);
    }

    static (bool ok, bool ignoreDefault, int metric) TryInterface(uint ifIndex, ushort family)
    {
        IntPtr buf = Marshal.AllocHGlobal(IpInterfaceRowSize);
        try
        {
            for (int i = 0; i < IpInterfaceRowSize; i++) Marshal.WriteByte(buf, i, 0);
            Marshal.WriteInt16(buf, 0, (short)family);
            Marshal.WriteInt32(buf, IpInterfaceIndexOffset, (int)ifIndex);
            int err = GetIpInterfaceEntry(buf);
            if (err != 0) return (false, false, 0);
            int metric = Marshal.ReadInt32(buf, IpInterfaceMetricOffset);
            byte ignore = Marshal.ReadByte(buf, IpInterfaceDisableDefaultOffset);
            return (true, ignore != 0, metric);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static int CountDefaultRoutes(ushort family, uint ifIndex)
    {
        int err = GetIpForwardTable2(family, out IntPtr table);
        if (err != 0 || table == IntPtr.Zero) return 0;
        try
        {
            int n = Marshal.ReadInt32(table, 0);
            if (n < 0 || n > 65536) return 0;
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                IntPtr row = table + IpForwardTableHeader + i * IpForwardRowSize;
                uint idx = (uint)Marshal.ReadInt32(row, IpForwardIndexOffset);
                if (idx != ifIndex) continue;
                short destFamily = Marshal.ReadInt16(row, IpForwardDestOffset);
                byte prefixLen = Marshal.ReadByte(row, IpForwardDestOffset + 28);
                if (prefixLen != 0) continue;
                if (destFamily != family) continue;
                count++;
            }
            return count;
        }
        finally { FreeMibTable(table); }
    }

    static bool BestInterfaceIs(ushort family, uint ifIndex)
    {
        // Local route lookup only: no packet is sent.
        byte[] addr = family == AF_INET6 ? V6Probe() : V4Probe();
        IntPtr p = Marshal.AllocHGlobal(addr.Length);
        try
        {
            Marshal.Copy(addr, 0, p, addr.Length);
            if (GetBestInterfaceEx(p, out uint best) != 0) return false;
            return best == ifIndex;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    static byte[] V4Probe()
    {
        // sockaddr_in for 1.1.1.1 (same destination NetWatcher already uses for IPv4).
        var b = new byte[16];
        b[0] = (byte)AF_INET; // little-endian family
        b[4] = 1; b[5] = 1; b[6] = 1; b[7] = 1;
        return b;
    }

    static byte[] V6Probe()
    {
        // sockaddr_in6 for 2001:4860:4860::8888 (route lookup only).
        var b = new byte[28];
        b[0] = (byte)AF_INET6;
        // 2001:4860:4860:0000:0000:0000:0000:8888
        b[8] = 0x20; b[9] = 0x01; b[10] = 0x48; b[11] = 0x60;
        b[12] = 0x48; b[13] = 0x60;
        b[22] = 0x88; b[23] = 0x88;
        return b;
    }

    static bool? DnsIsLoopback(int ifIndex)
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                int idx = -1;
                try { idx = ni.GetIPProperties().GetIPv4Properties()?.Index ?? -1; } catch (NetworkInformationException) { }
                if (idx != ifIndex) continue;
                var dns = ni.GetIPProperties().DnsAddresses;
                if (dns.Count == 0) return false;
                foreach (var a in dns)
                    if (!IPAddress.IsLoopback(a)) return false;
                return true;
            }
        }
        catch (Exception) { }
        return null;
    }
}
