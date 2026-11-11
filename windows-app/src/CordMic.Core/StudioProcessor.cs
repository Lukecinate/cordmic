using System.Diagnostics;
using CordMic.Core.Dsp;

namespace CordMic.Core;

/// <summary>
/// Runs <see cref="StudioChain"/> on its own worker thread, off the audio callback (0.6.0 spec §8, CPU-2).
/// The render thread pulls finished s16 audio from a ring; each pull wakes the worker, which pulls 10 ms frames from
/// the jitter buffer, processes them and keeps the ring one frame plus one render period ahead (one frame of added
/// latency). A deadline miss is a render pull that finds the ring short (the gap is faded, never left as a step) or a
/// frame that took longer than 10 ms to process. Three misses within one second raise <see cref="FellBack"/> once;
/// the receiver then switches Studio off for the session (CPU-4). No allocation after construction.
/// </summary>
public sealed class StudioProcessor : IDisposable
{
    public const int Frame = StudioChain.Frame;
    private const int RingCap = 48000 / 5; // 200 ms
    private const int WarmupFrames = 50;  // misses during the first 0.5 s (start-up, JIT) do not count

    private readonly StudioChain _chain;
    private readonly PullDelegate _pull;
    private readonly short[] _ring = new short[RingCap];
    private int _rRead, _rCount;
    private readonly object _ringLock = new();
    private readonly byte[] _pcm = new byte[Frame * 2];
    private readonly float[] _f = new float[Frame];
    private readonly short[] _s = new short[Frame];
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _stop;
    private volatile int _targetFill;
    private short _last;
    private long _framesDone;
    private readonly long[] _missAt = new long[3];
    private int _missIdx;
    private int _fellBack;
    private double _procSum, _procMax;
    private long _procN;
    private readonly object _statLock = new();

    public delegate void PullDelegate(Span<byte> dst);

    /// <summary>Raised once (from the worker or render thread) after three missed deadlines within one second.</summary>
    public event Action<string>? FellBack;
    public StudioPreset Preset => _chain.Preset;
    public long Misses { get; private set; }
    public double AgcGainDb => _chain.Agc.GainDb;
    public bool HasFallenBack => _fellBack != 0;
    /// <summary>Added latency: RNNoise look-ahead plus one frame of worker buffering, in ms.</summary>
    public static double AddedLatencyMs => StudioChain.LatencyMs + Frame / 48.0;

    public StudioProcessor(StudioChain chain, PullDelegate pullJitterBuffer, double renderPeriodMs, Action? threadInit = null)
    {
        _chain = chain;
        _pull = pullJitterBuffer;
        SetRenderPeriodMs(renderPeriodMs);
        _thread = new Thread(() => { try { threadInit?.Invoke(); } catch (Exception) { } Loop(); })
        { Name = "cordmic-studio", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void SetRenderPeriodMs(double ms)
    {
        int period = (int)Math.Ceiling(Math.Max(0, ms) * 48);
        _targetFill = Math.Min(RingCap - Frame, Frame + Math.Max(period, 48) + 96);
    }

    /// <summary>Mean / max processing time per 10 ms frame since the last call, in ms (for the log).</summary>
    public (double MeanMs, double MaxMs) TakeCpu()
    {
        lock (_statLock)
        {
            var r = _procN > 0 ? (_procSum / _procN, _procMax) : (double.NaN, double.NaN);
            _procSum = 0; _procMax = 0; _procN = 0;
            return r;
        }
    }

    /// <summary>Render thread: fills <paramref name="dst"/> (mono s16le) from the ring.</summary>
    public void Pull(Span<byte> dst)
    {
        int frames = dst.Length / 2, got = 0;
        lock (_ringLock)
        {
            int take = Math.Min(frames, _rCount);
            for (int i = 0; i < take; i++)
            {
                short v = _ring[_rRead];
                dst[2 * i] = (byte)v; dst[2 * i + 1] = (byte)(v >> 8);
                if (++_rRead == RingCap) _rRead = 0;
            }
            _rCount -= take;
            got = take;
            if (take > 0) _last = _ring[(_rRead - 1 + RingCap) % RingCap];
        }
        if (got < frames)
        {
            // Short ring: fade from the last sample (2 ms) instead of a step, then silence.
            for (int i = got; i < frames; i++)
            {
                int k = i - got;
                double g = k < 96 ? 0.5 + 0.5 * Math.Cos(Math.PI * (k + 1) / 96) : 0;
                short v = (short)Math.Round(_last * g);
                dst[2 * i] = (byte)v; dst[2 * i + 1] = (byte)(v >> 8);
            }
            _last = 0;
            if (Interlocked.Read(ref _framesDone) > WarmupFrames) NoteMiss("render pull found the Studio buffer empty");
        }
        _wake.Set();
    }

    private void NoteMiss(string why)
    {
        long now = Stopwatch.GetTimestamp();
        bool trip;
        lock (_statLock)
        {
            Misses++;
            _missAt[_missIdx] = now;
            _missIdx = (_missIdx + 1) % 3;
            long oldest = _missAt[_missIdx];
            trip = oldest != 0 && now - oldest <= Stopwatch.Frequency;
        }
        if (trip && Interlocked.Exchange(ref _fellBack, 1) == 0) FellBack?.Invoke(why);
    }

    private int Fill() { lock (_ringLock) return _rCount; }

    private void Loop()
    {
        var sw = new Stopwatch();
        while (!_stop)
        {
            _wake.WaitOne(5);
            while (!_stop && _fellBack == 0 && Fill() < _targetFill)
            {
                _pull(_pcm);
                sw.Restart();
                for (int i = 0; i < Frame; i++) _f[i] = (short)(_pcm[2 * i] | (_pcm[2 * i + 1] << 8)) / 32768f;
                _chain.Process(_f);
                for (int i = 0; i < Frame; i++) _s[i] = (short)Math.Clamp((int)Math.Round(_f[i] * 32768.0), short.MinValue, short.MaxValue);
                double ms = sw.Elapsed.TotalMilliseconds;
                lock (_ringLock)
                {
                    int w = (_rRead + _rCount) % RingCap;
                    for (int i = 0; i < Frame && _rCount < RingCap; i++)
                    {
                        _ring[w] = _s[i];
                        if (++w == RingCap) w = 0;
                        _rCount++;
                    }
                }
                lock (_statLock) { _procSum += ms; _procN++; if (ms > _procMax) _procMax = ms; }
                long done = Interlocked.Increment(ref _framesDone);
                if (ms > Frame / 48.0 && done > WarmupFrames) NoteMiss($"Studio frame took {ms:F1} ms");
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(1000);
        _chain.Dispose();
        _wake.Dispose();
    }
}
