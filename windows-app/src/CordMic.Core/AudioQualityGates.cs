namespace CordMic.Core;

/// <summary>
/// Provisional audio/underrun gates (IT review measure 5). Values are config-driven and
/// must not be locked until three 5+ minute recordings exist on the widened bench
/// (non-Samsung phone, Windows 10, Windows 11, Memory Integrity on).
/// </summary>
public sealed class AudioQualityGates
{
    public const string StatusProvisional = "provisional";
    public const string StatusLocked = "locked";

    /// <summary>Underruns per minute at or below this are treated as a pass. Provisional.</summary>
    public double MaxUnderrunsPerMinute { get; set; } = 2;

    /// <summary>Same-day USB minus tethering median latency, in milliseconds. Aligned with G3/M-3/M8.</summary>
    public double MaxAddedLatencyMedianMs { get; set; } = 2;

    public double MaxAddedLatencyP95Ms { get; set; } = 10;

    /// <summary>Allowed "replug needed" outcomes in <see cref="ReplugWindowSessions"/> sessions (target 0; more than 1 blocks beta).</summary>
    public int MaxReplugNeeded { get; set; } = 1;
    public int ReplugWindowSessions { get; set; } = 30;

    public int MinRecordings { get; set; } = 3;
    public int MinRecordingSeconds { get; set; } = 5 * 60;

    public string Status { get; set; } = StatusProvisional;

    public bool CanLock(int recordingsOfFiveMinutesOrMore) =>
        recordingsOfFiveMinutesOrMore >= MinRecordings && Status == StatusLocked;

    public bool LatencyPass(double medianDeltaMs, double p95DeltaMs) =>
        medianDeltaMs <= MaxAddedLatencyMedianMs && p95DeltaMs <= MaxAddedLatencyP95Ms;

    public bool ReplugPass(int replugNeededInWindow) =>
        replugNeededInWindow <= 0 || (replugNeededInWindow <= MaxReplugNeeded);
}

public static class AudioQualityGatesFile
{
    public static AudioQualityGates Default { get; } = new();
}
