using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CordMic.Core;

namespace CordMic.App;

internal sealed record TetherAdapter(string Id, string Name, string Description, int IfIndex,
                                     IPAddress? LocalIp, IPAddress? PhoneIp, bool CarriesInternet, int PrefixLength = 24)
{
    public string Key => $"{Id}|{LocalIp}/{PrefixLength}|{PhoneIp}|{CarriesInternet}";

    /// <summary>The tether link's IPv4 subnet (0.6.1: the receiver accepts phones only from here and loopback).</summary>
    public Ipv4Subnet? Subnet => LocalIp is { } ip ? Ipv4Subnet.From(ip, PrefixLength is > 0 and <= 32 ? PrefixLength : 24) : null;
}

/// <summary>
/// Finds the phone's USB-tethering adapter (RNDIS / NCM / Samsung). Polled from the UI timer and also
/// re-scanned on NetworkAddressChanged, so plug/unplug and "tethering toggled" are noticed within ~2 s.
/// </summary>
internal sealed class NetWatcher : IDisposable
{
    private volatile bool _dirty = true;
    private DateTime _lastScan = DateTime.MinValue;
    private string? _lastKey; // null = nothing logged yet, so the first scan is always logged (0.7.2: also "none")
    public TetherAdapter? Current { get; private set; }
    /// <summary>0.7.2: every adapter that looks like a tethering adapter, up or not, with or without an address.</summary>
    public IReadOnlyList<TetherAdapterInfo> Candidates { get; private set; } = Array.Empty<TetherAdapterInfo>();
    public event Action<TetherAdapter?>? Changed;

    public NetWatcher()
    {
        NetworkChange.NetworkAddressChanged += OnNet;
        NetworkChange.NetworkAvailabilityChanged += OnNet;
    }

    private void OnNet(object? s, EventArgs e) => _dirty = true;

    /// <summary>Call from the UI thread (timer). Raises Changed on the UI thread.</summary>
    public void Tick(Action<string> log)
    {
        if (!_dirty && DateTime.UtcNow - _lastScan < TimeSpan.FromSeconds(2)) return;
        _dirty = false;
        _lastScan = DateTime.UtcNow;
        TetherAdapter? found = null;
        try { var cands = new List<TetherAdapterInfo>(); found = Scan(cands); Candidates = cands; }
        catch (Exception ex) { log("adapter scan failed: " + ex.Message); }
        var key = found?.Key ?? "";
        if (key == _lastKey) return;
        _lastKey = key;
        Current = found;
        log(found is null
            ? "tether adapter: none"
            : $"tether adapter: {found.Description} ({found.Name}) local={found.LocalIp}/{found.PrefixLength} phone={found.PhoneIp} internetViaPhone={found.CarriesInternet}");
        Changed?.Invoke(found);
    }

    public static TetherAdapter? Scan() => Scan(null);

    public static TetherAdapter? Scan(List<TetherAdapterInfo>? candidates)
    {
        TetherAdapter? result = null;
        uint best = 0;
        bool haveBest = Native.GetBestInterface(BitConverter.ToUInt32(new byte[] { 1, 1, 1, 1 }, 0), out best) == 0;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!DeviceRules.IsTetheringAdapter(ni.Description, ni.Name)) continue;
            bool up = ni.OperationalStatus == OperationalStatus.Up;
            if (!up) { candidates?.Add(new TetherAdapterInfo(ni.Description, ni.Name, false, false)); continue; }
            var ip = ni.GetIPProperties();
            int idx = -1;
            try { idx = ip.GetIPv4Properties()?.Index ?? -1; } catch (NetworkInformationException) { }
            var uni = ip.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            var local = uni?.Address;
            candidates?.Add(new TetherAdapterInfo(ni.Description, ni.Name, true, local is not null && !IsLinkLocal(local)));
            if (local is null || result is not null) continue; // no IPv4 yet (DHCP still running) -> try again next tick
            int prefix = 24;
            try { if (uni!.PrefixLength is > 0 and <= 32) prefix = uni.PrefixLength; } catch (Exception) { }
            var phone = ip.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any))
                        ?? ip.DhcpServerAddresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            result = new TetherAdapter(ni.Id, ni.Name, ni.Description, idx, local, phone,
                                       haveBest && idx >= 0 && best == (uint)idx, prefix);
            if (candidates is null) break;
        }
        return result;
    }

    private static bool IsLinkLocal(IPAddress a) { var b = a.GetAddressBytes(); return b.Length == 4 && b[0] == 169 && b[1] == 254; }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNet;
        NetworkChange.NetworkAvailabilityChanged -= OnNet;
    }
}
