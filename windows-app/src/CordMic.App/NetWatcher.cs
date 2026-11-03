using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CordMic.Core;

namespace CordMic.App;

internal sealed record TetherAdapter(string Id, string Name, string Description, int IfIndex,
                                     IPAddress? LocalIp, IPAddress? PhoneIp, bool CarriesInternet)
{
    public string Key => $"{Id}|{LocalIp}|{PhoneIp}|{CarriesInternet}";
}

/// <summary>
/// Finds the phone's USB-tethering adapter (RNDIS / NCM / Samsung). Polled from the UI timer and also
/// re-scanned on NetworkAddressChanged, so plug/unplug and "tethering toggled" are noticed within ~2 s.
/// </summary>
internal sealed class NetWatcher : IDisposable
{
    private volatile bool _dirty = true;
    private DateTime _lastScan = DateTime.MinValue;
    private string _lastKey = "";
    public TetherAdapter? Current { get; private set; }
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
        try { found = Scan(); }
        catch (Exception ex) { log("adapter scan failed: " + ex.Message); }
        var key = found?.Key ?? "";
        if (key == _lastKey) return;
        _lastKey = key;
        Current = found;
        log(found is null
            ? "tether adapter: none"
            : $"tether adapter: {found.Description} ({found.Name}) local={found.LocalIp} phone={found.PhoneIp} internetViaPhone={found.CarriesInternet}");
        Changed?.Invoke(found);
    }

    public static TetherAdapter? Scan()
    {
        uint best = 0;
        bool haveBest = Native.GetBestInterface(BitConverter.ToUInt32(new byte[] { 1, 1, 1, 1 }, 0), out best) == 0;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!DeviceRules.IsTetheringAdapter(ni.Description, ni.Name)) continue;
            var ip = ni.GetIPProperties();
            int idx = -1;
            try { idx = ip.GetIPv4Properties()?.Index ?? -1; } catch (NetworkInformationException) { }
            var local = ip.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
            if (local is null) continue; // no IPv4 yet (DHCP still running) -> try again next tick
            var phone = ip.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any))
                        ?? ip.DhcpServerAddresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            return new TetherAdapter(ni.Id, ni.Name, ni.Description, idx, local, phone,
                                     haveBest && idx >= 0 && best == (uint)idx);
        }
        return null;
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNet;
        NetworkChange.NetworkAvailabilityChanged -= OnNet;
    }
}
