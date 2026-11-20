namespace CordMic.Core;

/// <summary>Who asked to start or stop listening. Written to the log, never shown as concatenated UI text.</summary>
public enum ListenSource { Orb, Tray, AutoPhoneReady, Retry, Other }

/// <summary>What an orb click should do, after debounce.</summary>
public enum OrbAction { Listen, Stop, ShowGuidance, Ignore, Retry }

/// <summary>
/// 0.8.0 M0 (F1): the 0.7.2 orb treated every click as a toggle, including a double click and a click while
/// searching, which left the receiver not listening. This policy is pure so it can be tested without WinForms.
/// </summary>
public static class ListenPolicy
{
    public const int DebounceMs = 600;

    /// <summary>True when this click is too soon after the previous one (monotonic ticks).</summary>
    public static bool IsDebounced(long nowTicks, long lastClickTicks, long ticksPerSecond)
    {
        if (lastClickTicks <= 0 || ticksPerSecond <= 0) return false;
        long dt = nowTicks - lastClickTicks;
        if (dt < 0) return true; // clock went backwards: treat as too soon, do not act
        return dt * 1000 / ticksPerSecond < DebounceMs;
    }

    /// <summary>
    /// Orb click while armed and searching shows the tethering steps instead of stopping.
    /// A click while disconnected starts listening; while connected, stops; while in error, retries.
    /// <paramref name="streaming"/> is true only when a phone is already sending audio.
    /// </summary>
    public static OrbAction Click(bool armed, bool searching, bool error, bool streaming)
    {
        if (!armed) return OrbAction.Listen;
        if (error) return OrbAction.Retry;
        if (searching && !streaming) return OrbAction.ShowGuidance;
        return OrbAction.Stop;
    }

    /// <summary>
    /// Auto re-listen: if the user stopped listening while no phone was present (not streaming), listen again
    /// when a phone becomes ready. An explicit stop while streaming is respected until the user starts again.
    /// </summary>
    public static bool ShouldAutoRelisten(bool disarmedWhileIdle, bool nowReady, bool currentlyArmed) =>
        disarmedWhileIdle && nowReady && !currentlyArmed;

    public static string LogLine(bool on, ListenSource src) => src switch
    {
        ListenSource.Orb => on ? "listen on (orb)" : "listen off (orb)",
        ListenSource.Tray => on ? "listen on (tray)" : "listen off (tray)",
        ListenSource.AutoPhoneReady => "listen on (auto: phone ready)",
        ListenSource.Retry => "listen on (retry)",
        _ => on ? "listen on" : "listen off",
    };
}
