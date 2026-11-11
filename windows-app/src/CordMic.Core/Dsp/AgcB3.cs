namespace CordMic.Core.Dsp;

/// <summary>
/// "B3" speech loudness AGC (0.6.0 spec §4), 10 ms frames after Studio NR. The loudness estimate (K-weighted,
/// EMA 0.025 per speech frame) and the gain only move on frames that are 8 dB above a 2 s minimum-statistics floor
/// AND have RNNoise voice probability ≥ 0.5 (smoothed, ~50 ms release); otherwise the gain is held, so pauses with
/// keyboard or traffic noise do not pump. Gain = target − loudness, capped so the measured floor stays at or below
/// −50 dBFS, limited to −12…+30 dB, slewed +6 / −12 dB/s, per-sample ramp, then a −1 dBFS limiter.
/// The starting gain is the last session's value (persisted by the app; default +15 dB). Allocation-free.
/// </summary>
public sealed class AgcB3
{
    public const double TargetLufs = -21, CeilDbfs = -50, GMax = 30, GMin = -12, UpDbPerSec = 6, DownDbPerSec = 12;
    public const double OpenDb = 8, VadThreshold = 0.5, DefaultStartGainDb = 15;
    private const int Frame = 480, HistFrames = 200, Block = 5;

    private Biquad2 _k1 = DspFilters.KShelf(), _k2 = DspFilters.KHighPass();
    private readonly PeakLimiter _lim = new();
    private readonly double[] _hist = new double[HistFrames];
    private int _hn, _hp, _fi;
    private double _fl = double.NaN, _e = -1, _g, _vs;

    /// <summary>When false the gain stays at <see cref="GainDb"/> (Auto volume off); the limiter still runs.</summary>
    public bool Enabled { get; set; } = true;
    public double GainDb => _g;
    public double FloorDbfs => _fl;
    public bool LastFrameSpeech { get; private set; }

    public AgcB3(double startGainDb = DefaultStartGainDb) { _g = Math.Clamp(startGainDb, GMin, GMax); }

    public void Reset(double startGainDb)
    {
        _k1 = DspFilters.KShelf(); _k2 = DspFilters.KHighPass(); _lim.Reset();
        Array.Clear(_hist); _hn = _hp = _fi = 0; _fl = double.NaN; _e = -1; _vs = 0;
        _g = Math.Clamp(startGainDb, GMin, GMax);
    }

    /// <summary>Processes one 480-sample frame in place. <paramref name="vad"/> is RNNoise's voice probability for this frame.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public void Process(Span<float> x, double vad)
    {
        int n = x.Length;
        double ek = 0, p = 0;
        for (int i = 0; i < n; i++)
        {
            double s = x[i];
            p += s * s;
            double kk = _k2.Process(_k1.Process(s));
            ek += kk * kk;
        }
        double lvl = 10 * Math.Log10(p / n + 1e-20);
        _hist[_hp] = lvl; _hp = (_hp + 1) % HistFrames; if (_hn < HistFrames) _hn++;
        if (++_fi % Block == 0)
        {
            double mn = double.MaxValue;
            int start = (_hp - _hn + HistFrames) % HistFrames, blocks = _hn / Block;
            for (int b = 0; b < blocks; b++)
            {
                double a = 0;
                for (int j = 0; j < Block; j++) a += _hist[(start + b * Block + j) % HistFrames];
                a /= Block;
                if (a < mn) mn = a;
            }
            if (blocks > 0) _fl = double.IsNaN(_fl) || mn < _fl ? mn : _fl + 0.1 * (mn - _fl);
        }
        _vs = vad > _vs ? vad : 0.8 * _vs + 0.2 * vad;
        bool speech = Enabled && !double.IsNaN(_fl) && lvl > _fl + OpenDb && _vs >= VadThreshold;
        LastFrameSpeech = speech;
        double want = _g;
        if (speech)
        {
            double ef = ek / n + 1e-20;
            _e = _e < 0 ? ef : _e + 0.025 * (ef - _e);
            want = TargetLufs - (-0.691 + 10 * Math.Log10(_e));
            want = Math.Min(want, CeilDbfs - Math.Max(_fl, -100));
            want = Math.Clamp(want, GMin, GMax);
        }
        double st = (want > _g ? UpDbPerSec : DownDbPerSec) * n / 48000.0;
        double gn = _g + Math.Clamp(want - _g, -st, st);
        double l0 = Math.Pow(10, _g / 20), l1 = Math.Pow(10, gn / 20);
        _g = gn;
        for (int i = 0; i < n; i++)
            x[i] = (float)_lim.Step(x[i] * (l0 + (l1 - l0) * (i + 1) / n));
    }
}
