namespace CordMic.Core;

/// <summary>
/// 0.7.1 (audit M5): when automatic update checks are due. Uses the saved last-check time, but a time more than one
/// hour in the future (saved while the clock was wrong) counts as due instead of blocking checks until that date.
/// </summary>
public static class UpdateSchedule
{
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromHours(1);

    public static bool IsDue(long lastCheckUtcTicks, DateTime nowUtc, TimeSpan interval)
    {
        if (lastCheckUtcTicks <= 0 || lastCheckUtcTicks > DateTime.MaxValue.Ticks) return true;
        var last = new DateTime(lastCheckUtcTicks, DateTimeKind.Utc);
        if (last - nowUtc > FutureTolerance) return true;
        return nowUtc - last >= interval;
    }
}

/// <summary>
/// 0.7.1 (audit M5): a repeating deadline on a monotonic millisecond clock (<see cref="Environment.TickCount64"/> in
/// the app), so moving the wall clock back or forth does not stop or burst the periodic re-evaluation.
/// </summary>
public sealed class PeriodicGate
{
    private readonly Func<long> _nowMs;
    private readonly long _periodMs;
    private long _nextMs = long.MinValue;

    public PeriodicGate(TimeSpan period, Func<long>? nowMs = null)
    {
        _periodMs = (long)period.TotalMilliseconds;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    /// <summary>True at most once per period (the first call is always true).</summary>
    public bool TryEnter()
    {
        long now = _nowMs();
        if (_nextMs != long.MinValue && now < _nextMs) return false;
        _nextMs = now + _periodMs;
        return true;
    }
}

/// <summary>What the tray balloon currently on screen is about (audit M1).</summary>
public enum BalloonKind { None, UpdateReady, Other }

public static class BalloonPolicy
{
    /// <summary>Only a click on the "update ready" balloon may start an install, and only while it is still ready.</summary>
    public static bool InstallOnClick(BalloonKind shown, bool updateReady) => shown == BalloonKind.UpdateReady && updateReady;
}

/// <summary>
/// 0.7.1 (audit L2): turns many requests from any thread into at most one pending call of <c>work</c> on the target
/// context. A request that arrives while one is pending is merged; one arriving while <c>work</c> runs schedules
/// one more call so the last state is never missed.
/// </summary>
public sealed class PostCoalescer
{
    private readonly Action<Action> _post;
    private readonly Action _work;
    private int _pending;

    public PostCoalescer(Action<Action> post, Action work) { _post = post; _work = work; }

    public void Request()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        _post(() =>
        {
            Volatile.Write(ref _pending, 0);
            _work();
        });
    }
}

/// <summary>
/// 0.7.1 (audit M7): font scale for a window on a monitor with its own DPI. GDI+ converts point sizes with the system
/// DPI, so a window at <paramref name="windowDpi"/> needs points × windowDpi / systemDpi to look the same size.
/// </summary>
public static class FontScale
{
    public static float Points(float basePoints, int windowDpi, int systemDpi)
    {
        if (windowDpi <= 0 || systemDpi <= 0) return basePoints;
        return (float)(basePoints * windowDpi / (double)systemDpi);
    }
}

/// <summary>
/// 0.7.1 (audit M7): holds the current set of disposable resources (fonts) built for one DPI. <see cref="Switch"/>
/// builds the new set, lets the caller move every control over, then disposes the old set, so no control ever points
/// at a disposed font and no font is leaked.
/// </summary>
public sealed class DpiResources<T> : IDisposable where T : class, IDisposable
{
    private readonly Func<int, T> _factory;
    public T Current { get; private set; }
    public int Dpi { get; private set; }

    public DpiResources(int dpi, Func<int, T> factory) { _factory = factory; Dpi = dpi; Current = factory(dpi); }

    /// <summary>Returns false (nothing changes) when <paramref name="dpi"/> equals the current DPI.</summary>
    public bool Switch(int dpi, Action<T> apply)
    {
        if (dpi == Dpi) return false;
        var old = Current;
        Current = _factory(dpi);
        Dpi = dpi;
        try { apply(Current); }
        finally { old.Dispose(); }
        return true;
    }

    public void Dispose() => Current.Dispose();
}
