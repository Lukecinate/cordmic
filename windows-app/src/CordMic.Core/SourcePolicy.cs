using System.Net;
using System.Net.Sockets;

namespace CordMic.Core;

/// <summary>An IPv4 network (address and prefix length).</summary>
public readonly record struct Ipv4Subnet(uint Network, int Prefix)
{
    public static Ipv4Subnet From(IPAddress address, int prefix)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("IPv4 only", nameof(address));
        prefix = Math.Clamp(prefix, 0, 32);
        return new Ipv4Subnet(ToUInt(address) & Mask(prefix), prefix);
    }

    public bool Contains(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        return (ToUInt(address) & Mask(Prefix)) == Network;
    }

    private static uint Mask(int prefix) => prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);

    private static uint ToUInt(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    public override string ToString()
    {
        uint n = Network;
        return $"{n >> 24}.{(n >> 16) & 255}.{(n >> 8) & 255}.{n & 255}/{Prefix}";
    }
}

/// <summary>
/// Who may talk to the receiver (0.6.1, audit H1). Immutable; the app swaps in a new instance when the USB tethering
/// adapter changes or the pairing code is switched. Loopback is always trusted (local tools and tests).
/// </summary>
public sealed class SourcePolicy
{
    private readonly Ipv4Subnet[] _subnets;

    public SourcePolicy(IEnumerable<Ipv4Subnet>? tetherSubnets = null, byte[]? pairingKey = null, bool anySource = false,
                        bool trustLoopback = true)
    {
        TrustLoopback = trustLoopback;
        _subnets = tetherSubnets?.ToArray() ?? Array.Empty<Ipv4Subnet>();
        if (pairingKey is not null && pairingKey.Length != Pairing.KeySize) throw new ArgumentException("bad pairing key", nameof(pairingKey));
        PairingKey = pairingKey;
        AnySource = anySource;
    }

    /// <summary>Loopback only, no pairing: the default until the app reports the tether adapter.</summary>
    public static SourcePolicy LoopbackOnly { get; } = new();

    public IReadOnlyList<Ipv4Subnet> Subnets => _subnets;
    /// <summary>Set: a peer must answer a pairing challenge first.</summary>
    public byte[]? PairingKey { get; }
    public bool PairingRequired => PairingKey is not null;
    /// <summary>Developer harness only: accept every source address (still subject to pairing and the peer lock).</summary>
    public bool AnySource { get; }
    /// <summary>Loopback is trusted (always in the app; tests turn it off to emulate a second host on 127.0.0.x).</summary>
    public bool TrustLoopback { get; }

    public bool IsTrustedNetwork(IPAddress address)
    {
        if (AnySource || (TrustLoopback && IPAddress.IsLoopback(address))) return true;
        foreach (var s in _subnets) if (s.Contains(address)) return true;
        return false;
    }

    public override string ToString() =>
        (AnySource ? "any source" : "loopback" + string.Concat(_subnets.Select(s => ", " + s))) + (PairingRequired ? ", pairing code required" : "");
}
