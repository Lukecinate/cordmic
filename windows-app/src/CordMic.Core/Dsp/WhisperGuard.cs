namespace CordMic.Core.Dsp;

/// <summary>Studio noise-reduction presets (PC side, 0.6.0).</summary>
public enum StudioPreset
{
    Off = 0,
    /// <summary>RNNoise with WhisperGuard Balanced (default when Studio is on).</summary>
    Balanced = 1,
    /// <summary>RNNoise with WhisperGuard Whisper priority.</summary>
    Whisper = 2,
    /// <summary>RNNoise with a −20 dB floor (0.9·RNNoise + 0.1·input), no guard.</summary>
    Max = 3,
}

/// <summary>
/// WhisperGuard (0.6.0 spec §3): an energy presence detector on the input decides when RNNoise is removing a
/// present signal (a whisper) and mixes back toward the 0.5.7 Strong output in proportion to how much RNNoise
/// removed. Frames are 480 samples; x, r and k must be time-aligned. Allocation-free.
/// </summary>
public sealed class WhisperGuard
{
    public const int Frame = 480;
    private Biquad2[] _bx, _br;
    private double _floor, _m, _minInit = double.MaxValue;
    private int _hang, _frames;
    public double Thr { get; }
    public double Lo { get; }
    public double Hi { get; }
    public double MMax { get; }
    /// <summary>Last frame: presence decision, RNNoise suppression in dB, and the smoothed mix weight.</summary>
    public bool Present { get; private set; }
    public double SuppressionDb { get; private set; }
    public double Mix => _m;

    public WhisperGuard(double thr, double lo, double hi, double mmax)
    {
        Thr = thr; Lo = lo; Hi = hi; MMax = mmax;
        _bx = DspFilters.FromSos(DspFilters.Band300To8000);
        _br = DspFilters.FromSos(DspFilters.Band300To8000);
    }

    public static WhisperGuard ForPreset(StudioPreset p) => p switch
    {
        StudioPreset.Whisper => new WhisperGuard(9, 6, 15, 1.0),
        _ => new WhisperGuard(12, 6, 15, 0.7),
    };

    public void Reset()
    {
        _bx = DspFilters.FromSos(DspFilters.Band300To8000);
        _br = DspFilters.FromSos(DspFilters.Band300To8000);
        _floor = 0; _m = 0; _minInit = double.MaxValue; _hang = 0; _frames = 0; Present = false; SuppressionDb = 0;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public void Process(ReadOnlySpan<float> x, ReadOnlySpan<float> r, ReadOnlySpan<float> k, Span<float> y)
    {
        int n = x.Length;
        double ex = 0, er = 0;
        for (int i = 0; i < n; i++)
        {
            double a = x[i], b = r[i];
            for (int s = 0; s < _bx.Length; s++) a = _bx[s].Process(a);
            for (int s = 0; s < _br.Length; s++) b = _br[s].Process(b);
            ex += a * a; er += b * b;
        }
        ex = ex / n + 1e-12; er = er / n + 1e-12;
        // Digital silence (the chain's own delay line at start, or a muted capture) must not seed the floor: a floor
        // at -120 dB would take minutes to creep up and would hold the guard open. Such frames are skipped for the
        // 30-frame initial minimum, and the floor never goes below -100 dB.
        const double MinFloor = 1e-10;
        if (_frames < 30)
        {
            if (ex >= MinFloor) { _frames++; _minInit = Math.Min(_minInit, ex); }
            _floor = _frames > 0 ? _minInit : MinFloor;
        }
        else _floor = Math.Max(MinFloor, ex < 4 * _floor ? Math.Min(_floor * 1.0069317, ex) : _floor * 1.0069317); // +3 dB/s
        if (ex > _floor * Math.Pow(10, Thr / 10)) _hang = 10;
        Present = _hang > 0;
        _hang--;
        SuppressionDb = 10 * Math.Log10(ex / er);
        double mt = Present ? Math.Clamp((SuppressionDb - Lo) / (Hi - Lo), 0, 1) * MMax : 0;
        double m0 = _m;
        _m = mt > _m ? _m + 0.5 * (mt - _m) : 0.9 * _m + 0.1 * mt;
        for (int i = 0; i < n; i++)
        {
            double w = m0 + (_m - m0) * (i + 1) / n;
            y[i] = (float)(r[i] + w * (k[i] - r[i]));
        }
    }
}
