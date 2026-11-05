namespace CordMic.Core;

/// <summary>
/// How many packets the jitter buffer must hold for a given render period.
/// A shared-mode callback consumes about one device period in a single pull. On the
/// 0.4.2 log that period is 10 ms (two 5 ms packets) while the Normal target is also
/// 10 ms and Low is 5 ms, so the pull empties the buffer. The next packet then only has
/// to be a fraction of a millisecond late to underrun, playback rebuffers, the phone's
/// packets pile up, and the hard cap dumps them back to the bare target. That is the
/// underrun/overflow oscillation in the log (and why Low, which is below one period, is worse).
/// The floor is one full pull plus one packet left over afterwards. Unknown period (0) keeps the preset.
/// </summary>
public static class JitterPolicy
{
    public static int EffectiveTargetPackets(double requestedMs, double renderPeriodMs, double packetMs)
    {
        if (packetMs <= 0) throw new ArgumentOutOfRangeException(nameof(packetMs));
        int requested = Math.Max(1, (int)Math.Round(requestedMs / packetMs, MidpointRounding.AwayFromZero));
        if (renderPeriodMs <= 0) return requested;
        int pull = Math.Max(1, (int)Math.Ceiling(renderPeriodMs / packetMs - 1e-9));
        return Math.Max(requested, pull + 1);
    }

    public static double EffectiveTargetMs(double requestedMs, double renderPeriodMs, double packetMs) =>
        EffectiveTargetPackets(requestedMs, renderPeriodMs, packetMs) * packetMs;
}
