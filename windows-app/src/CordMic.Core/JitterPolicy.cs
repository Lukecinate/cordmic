namespace CordMic.Core;

/// <summary>
/// Jitter-buffer sizing against the WASAPI render period.
/// The floor is two pulls, or three when a callback consumes a single packet. Overflow trims to a high-water mark one period above the target, not all the way
/// back to the target. Max depth is the target plus five periods so a short 2.7 ms callback has burst room.
/// </summary>
public static class JitterPolicy
{
    public static int PullPackets(double renderPeriodMs, double packetMs)
    {
        if (renderPeriodMs <= 0 || packetMs <= 0) return 0;
        return Math.Max(1, (int)Math.Ceiling((renderPeriodMs - 1e-9) / packetMs));
    }

    public static int EffectiveTargetPackets(double requestedMs, double renderPeriodMs, double packetMs)
    {
        if (packetMs <= 0) packetMs = 5;
        int requested = Math.Max(1, (int)Math.Round(requestedMs / packetMs));
        int pull = PullPackets(renderPeriodMs, packetMs);
        if (pull <= 0) return requested;
        // A single-packet pull (2.7 ms exclusive period) needs three packets of target
        // so overflow/underrun have room instead of oscillating around a 10 ms floor.
        return Math.Max(requested, pull <= 1 ? 3 * pull : 2 * pull);
    }

    /// <summary>Trim-to mark: target plus one render period.</summary>
    public static int HighWaterPackets(double requestedMs, double renderPeriodMs, double packetMs)
    {
        int target = EffectiveTargetPackets(requestedMs, renderPeriodMs, packetMs);
        int pull = PullPackets(renderPeriodMs, packetMs);
        return target + Math.Max(1, pull);
    }

    /// <summary>
    /// Hard cap: target plus three render periods, or five when a callback
    /// consumes a single packet (a 2.7 ms exclusive period) so there is burst room.
    /// </summary>
    public static int MaxDepthPackets(double requestedMs, double renderPeriodMs, double packetMs)
    {
        int target = EffectiveTargetPackets(requestedMs, renderPeriodMs, packetMs);
        int pull = PullPackets(renderPeriodMs, packetMs);
        if (pull <= 0) return Math.Max(target * 3, target + 6);
        return target + (pull <= 1 ? 5 : 3) * pull;
    }

    /// <summary>Catch-up / sample-slip enable: residual stays more than one period above the target.</summary>
    public static double CatchUpEnableMs(double targetMs, double renderPeriodMs) =>
        targetMs + Math.Max(renderPeriodMs, 5);
}
