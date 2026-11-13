using System.Diagnostics;

namespace CordMic.Core;

/// <summary>
/// Rate limit for repeated log lines (0.6.1, audit L4): the first message for a key is logged, then at most one per
/// window with the number of suppressed repeats. Thread-safe; bounded to <see cref="MaxKeys"/> keys.
/// </summary>
public sealed class LogLimiter
{
    public const int MaxKeys = 256;
    private readonly object _l = new();
    private readonly Dictionary<string, (long Last, int Suppressed)> _seen = new();
    private readonly long _window;

    public LogLimiter(TimeSpan window) => _window = (long)(window.TotalSeconds * Stopwatch.Frequency);

    /// <summary>True when the line should be written; <paramref name="suppressed"/> is the count skipped since the last one.</summary>
    public bool ShouldLog(string key, long nowTicks, out int suppressed)
    {
        lock (_l)
        {
            if (_seen.TryGetValue(key, out var e))
            {
                if (nowTicks - e.Last < _window)
                {
                    _seen[key] = (e.Last, e.Suppressed + 1);
                    suppressed = 0;
                    return false;
                }
                suppressed = e.Suppressed;
                _seen[key] = (nowTicks, 0);
                return true;
            }
            if (_seen.Count >= MaxKeys) _seen.Clear();
            _seen[key] = (nowTicks, 0);
            suppressed = 0;
            return true;
        }
    }

    public bool ShouldLog(string key, out int suppressed) => ShouldLog(key, Stopwatch.GetTimestamp(), out suppressed);

    /// <summary>Formats "msg (n similar suppressed)" when repeats were skipped.</summary>
    public static string WithCount(string msg, int suppressed) => suppressed > 0 ? $"{msg} ({suppressed} similar suppressed)" : msg;
}
