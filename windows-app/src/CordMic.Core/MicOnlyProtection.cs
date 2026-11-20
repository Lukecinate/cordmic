using System.Text;

namespace CordMic.Core;

/// <summary>Why the mic-only protection is incomplete, for Settings (one pre-translated key, not concatenated).</summary>
public enum MicOnlyGap
{
    None,
    NoAdapter,
    DefaultRoutePresent,
    DnsNotLoopback,
    IgnoreDefaultOff,
    BestRouteIsPhone,
}

/// <summary>
/// 0.8.0 M0 (F3): a read-only picture of whether Windows is keeping PC internet off the phone.
/// Built from IP Helper / DNS facts supplied by the app; this type itself does no I/O.
/// </summary>
public sealed record MicOnlySnapshot(
    bool HasAdapter,
    bool? IgnoreDefaultRoutesV4,
    bool? IgnoreDefaultRoutesV6,
    int? MetricV4,
    int? MetricV6,
    int DefaultRoutesV4,
    int DefaultRoutesV6,
    bool? DnsIsLoopback,
    bool BestIpv4IsPhone,
    bool BestIpv6IsPhone,
    bool Ipv6Present)
{
    /// <summary>
    /// True when a tether adapter is up and every check that applies is clean. A family that is not present
    /// (no IPv6 on the interface) is not treated as a leak.
    /// </summary>
    public bool Blocked
    {
        get
        {
            if (!HasAdapter) return false;
            if (BestIpv4IsPhone || DefaultRoutesV4 > 0) return false;
            if (IgnoreDefaultRoutesV4 == false) return false;
            if (DnsIsLoopback == false) return false;
            if (Ipv6Present)
            {
                if (BestIpv6IsPhone || DefaultRoutesV6 > 0) return false;
                if (IgnoreDefaultRoutesV6 == false) return false;
            }
            // DNS unknown or IgnoreDefault unknown: do not claim "blocked".
            if (DnsIsLoopback is not true) return false;
            if (IgnoreDefaultRoutesV4 is not true) return false;
            return true;
        }
    }

    public MicOnlyGap Gap
    {
        get
        {
            if (!HasAdapter) return MicOnlyGap.NoAdapter;
            if (BestIpv4IsPhone || (Ipv6Present && BestIpv6IsPhone)) return MicOnlyGap.BestRouteIsPhone;
            if (DefaultRoutesV4 > 0 || (Ipv6Present && DefaultRoutesV6 > 0)) return MicOnlyGap.DefaultRoutePresent;
            if (IgnoreDefaultRoutesV4 == false || (Ipv6Present && IgnoreDefaultRoutesV6 == false))
                return MicOnlyGap.IgnoreDefaultOff;
            if (DnsIsLoopback == false) return MicOnlyGap.DnsNotLoopback;
            if (Blocked) return MicOnlyGap.None;
            // Incomplete read (nulls): treat as not blocked, no specific item.
            return DnsIsLoopback is null || IgnoreDefaultRoutesV4 is null
                ? MicOnlyGap.IgnoreDefaultOff
                : MicOnlyGap.None;
        }
    }

    /// <summary>Log line, no addresses.</summary>
    public string LogLine()
    {
        if (!HasAdapter) return "mic-only: no adapter";
        string Yn(bool? v) => v is true ? "yes" : v is false ? "no" : "?";
        var sb = new StringBuilder();
        sb.Append("mic-only: ignoreDefault=").Append(Yn(IgnoreDefaultRoutesV4));
        if (Ipv6Present) sb.Append('/').Append(Yn(IgnoreDefaultRoutesV6));
        sb.Append(", defaultRoutes=").Append(DefaultRoutesV4);
        if (Ipv6Present) sb.Append('/').Append(DefaultRoutesV6);
        sb.Append(", dns=").Append(DnsIsLoopback is true ? "loopback" : DnsIsLoopback is false ? "other" : "?");
        sb.Append(", metric=").Append(MetricV4?.ToString() ?? "-");
        if (Ipv6Present) sb.Append('/').Append(MetricV6?.ToString() ?? "-");
        if (BestIpv4IsPhone || BestIpv6IsPhone) sb.Append(", best=phone");
        return sb.ToString();
    }

    /// <summary>String table key for the failing item (empty when blocked or no adapter-specific text).</summary>
    public static string GapKey(MicOnlyGap g) => g switch
    {
        MicOnlyGap.NoAdapter => "status.dataFail.noAdapter",
        MicOnlyGap.DefaultRoutePresent => "status.dataFail.defaultRoute",
        MicOnlyGap.DnsNotLoopback => "status.dataFail.dns",
        MicOnlyGap.IgnoreDefaultOff => "status.dataFail.ignoreDefault",
        MicOnlyGap.BestRouteIsPhone => "status.dataFail.bestRoute",
        _ => "",
    };
}
