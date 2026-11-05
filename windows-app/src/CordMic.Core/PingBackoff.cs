namespace CordMic.Core;

/// <summary>
/// Backs off UDP sends that fail with WSAENOBUFS (10055, "lacked sufficient buffer space")
/// and logs once per episode instead of on every ping.
/// </summary>
public sealed class PingBackoff
{
    public const int NoBufferSpace = 10055;
    private int _shift;
    private bool _inEpisode;

    /// <summary>How long to wait before the next send after the latest no-buffer failure. Zero while healthy.</summary>
    public TimeSpan Delay { get; private set; } = TimeSpan.Zero;

    public static bool IsNoBuffer(int socketErrorCode) => socketErrorCode == NoBufferSpace;

    /// <summary>Record a no-buffer failure. Returns true only for the first failure of an episode (log that one).</summary>
    public bool OnNoBuffer()
    {
        bool log = !_inEpisode;
        _inEpisode = true;
        // 250 ms, 500 ms, 1 s, 2 s, then 4 s.
        int ms = 250 << Math.Min(_shift, 4);
        _shift = Math.Min(_shift + 1, 4);
        Delay = TimeSpan.FromMilliseconds(ms);
        return log;
    }

    /// <summary>Record a successful send. Returns true once, when an episode just ended (log the recovery).</summary>
    public bool OnSuccess()
    {
        bool log = _inEpisode;
        _inEpisode = false;
        _shift = 0;
        Delay = TimeSpan.Zero;
        return log;
    }
}
