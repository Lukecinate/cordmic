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
/// Since 0.6.2 the guard only opens after the presence condition has held for <see cref="ArmFrames"/> (300 ms) or
/// RNNoise itself reports voice (probability ≥ <see cref="ArmVad"/>), so short bumps and noise bursts no longer
/// switch the mix on and off; the mix releases more slowly (~200 ms) so pauses do not "breathe".
/// </summary>
public sealed class WhisperGuard
{
    public const int Frame = 480;
    /// <summary>Frames the energy presence must hold (leaky count) before the guard may open: 300 ms.</summary>
    public const int ArmFrames = 30;
    /// <summary>RNNoise voice probability that opens the guard without waiting for <see cref="ArmFrames"/>.</summary>
    public const double ArmVad = 0.2;
    /// <summary>Per-frame decay of the mix weight when it falls (0.95 ≈ 200 ms time constant; 0.6.1 used 0.9).</summary>
    public const double Release = 0.95;
    private Biquad2[] _bx, _br;
    private double _floor, _m, _minInit = double.MaxValue;
    private int _hang, _frames, _run, _cHang;
    private bool _armed;
    /// <summary>Arming time in frames (default <see cref="ArmFrames"/>; settable for evaluation).</summary>
    public int ArmFramesUsed { get; set; } = ArmFrames;
    /// <summary>Mix release factor (default <see cref="Release"/>; settable for evaluation).</summary>
    public double ReleaseUsed { get; set; } = Release;
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
        _floor = 0; _m = 0; _minInit = double.MaxValue; _hang = 0; _frames = 0; _run = 0; _cHang = 0; _armed = false; Present = false; SuppressionDb = 0;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public void Process(ReadOnlySpan<float> x, ReadOnlySpan<float> r, ReadOnlySpan<float> k, Span<float> y, double vad = 0)
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
        bool raw = ex > _floor * Math.Pow(10, Thr / 10);
        // Presence must hold for 300 ms of above-threshold frames inside one region (gaps up to 300 ms, i.e. syllable
        // pauses, are bridged) before the guard may open; a bump or a short noise burst never gets there.
        if (raw) { _cHang = ArmFramesUsed; _run = Math.Min(_run + 1, 1000); }
        else if (--_cHang <= 0) { _cHang = 0; _run = 0; }
        if (raw && (_run >= ArmFramesUsed || vad >= ArmVad)) _armed = true;
        if (raw && _armed) _hang = 10;
        Present = _hang > 0;
        if (!Present) _armed = false;
        _hang--;
        SuppressionDb = 10 * Math.Log10(ex / er);
        double mt = Present ? Math.Clamp((SuppressionDb - Lo) / (Hi - Lo), 0, 1) * MMax : 0;
        double m0 = _m;
        _m = mt > _m ? _m + 0.5 * (mt - _m) : ReleaseUsed * _m + (1 - ReleaseUsed) * mt;
        for (int i = 0; i < n; i++)
        {
            double w = m0 + (_m - m0) * (i + 1) / n;
            y[i] = (float)(r[i] + w * (k[i] - r[i]));
        }
    }
}
