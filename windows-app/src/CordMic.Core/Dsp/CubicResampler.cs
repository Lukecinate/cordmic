namespace CordMic.Core.Dsp;

/// <summary>
/// 4-point Lagrange (Farrow) fractional resampler used for clock-drift correction (0.6.0), replacing single-sample
/// slips. <c>ratio</c> is input samples consumed per output sample (1 ± 1e-3). At ratio 1 the output equals the
/// input delayed by one sample. State in double. Allocation-free.
/// </summary>
public sealed class CubicResampler
{
    private double _xm1, _x0, _x1, _x2, _mu;
    private bool _primed;

    public void Reset() { _xm1 = _x0 = _x1 = _x2 = _mu = 0; _primed = false; }

    /// <summary>Input samples held inside the interpolator beyond the current read position (for fill accounting).</summary>
    public double HeldSamples => _primed ? 2 - _mu : 0;

    /// <summary>Generates <c>dst.Length</c> samples. <paramref name="read"/> returns the next input sample, or null on underrun (the last sample is held).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public void Process(Span<float> dst, double ratio, IFloatSource read)
    {
        if (!_primed)
        {
            _xm1 = read.Next() ?? 0; _x0 = read.Next() ?? 0; _x1 = read.Next() ?? 0; _x2 = read.Next() ?? 0;
            _primed = true;
        }
        for (int n = 0; n < dst.Length; n++)
        {
            double c0 = _x0;
            double c1 = _x1 - _xm1 / 3.0 - _x0 / 2.0 - _x2 / 6.0;
            double c2 = (_xm1 + _x1) / 2.0 - _x0;
            double c3 = (_x2 - _xm1) / 6.0 + (_x0 - _x1) / 2.0;
            dst[n] = (float)(((c3 * _mu + c2) * _mu + c1) * _mu + c0);
            _mu += ratio;
            while (_mu >= 1.0)
            {
                _mu -= 1.0;
                double s = read.Next() ?? _x2;
                _xm1 = _x0; _x0 = _x1; _x1 = _x2; _x2 = s;
            }
        }
    }

    /// <summary>Double-precision variant used by the test vectors.</summary>
    public void Process(Span<double> dst, double ratio, Func<double?> read)
    {
        if (!_primed) { _xm1 = read() ?? 0; _x0 = read() ?? 0; _x1 = read() ?? 0; _x2 = read() ?? 0; _primed = true; }
        for (int n = 0; n < dst.Length; n++)
        {
            double c0 = _x0, c1 = _x1 - _xm1 / 3.0 - _x0 / 2.0 - _x2 / 6.0, c2 = (_xm1 + _x1) / 2.0 - _x0, c3 = (_x2 - _xm1) / 6.0 + (_x0 - _x1) / 2.0;
            dst[n] = ((c3 * _mu + c2) * _mu + c1) * _mu + c0;
            _mu += ratio;
            while (_mu >= 1.0) { _mu -= 1.0; double s = read() ?? _x2; _xm1 = _x0; _x0 = _x1; _x1 = _x2; _x2 = s; }
        }
    }
}

/// <summary>Sample source for <see cref="CubicResampler"/> (an interface, so the hot path does not allocate closures).</summary>
public interface IFloatSource { float? Next(); }

/// <summary>
/// PI loop on jitter-buffer fill (0.6.0 spec §2): EMA τ = 2 s, kp = 5e-7, ki = 1e-9 per 10 ms call, ratio limited to
/// 1 ± 1e-3. <see cref="Update(double, double)"/> scales the EMA and the integrator by the actual call interval so
/// render periods other than 10 ms behave the same per second. A fill more than <see cref="ResyncSamples"/> away from
/// the target resets the loop (the jitter buffer's overflow trim or rebuffering does the splice).
/// </summary>
public sealed class DriftController
{
    /// <summary>Reference gains from the spec (port/test_vectors.json).</summary>
    public const double Kp = 5e-7, Ki = 1e-9, Limit = 1e-3, TauSec = 2.0, ResyncSamples = 960;
    /// <summary>
    /// Gains used in the jitter buffer. The reference pair (kp 5e-7, ki 1e-9) gives a damping ratio of about 0.17
    /// (proportional time constant 1 / (48000·kp) ≈ 42 s), so a start-up or drift step rings for minutes. kp = 2e-6 with
    /// the same ki gives a damping ratio of about 0.7; ratio jitter stays well under 100 ppm (RS-3).
    /// </summary>
    public const double KpTuned = 2e-6;
    private double _fillF, _integ;
    private bool _init;
    private readonly double _kp, _ki;

    public double Target { get; set; }
    public double Ratio { get; private set; } = 1.0;
    public long Resyncs { get; private set; }

    public DriftController(double target, double kp = Kp, double ki = Ki) { Target = target; _fillF = target; _init = true; _kp = kp; _ki = ki; }

    public void Reset() { _fillF = Target; _integ = 0; Ratio = 1.0; _init = false; }

    /// <summary>One 10 ms update (matches the reference vectors).</summary>
    public double Update(double fill) => Update(fill, 0.01);

    public double Update(double fill, double dtSec)
    {
        if (dtSec <= 0) return Ratio;
        double calls = dtSec / 0.01;
        if (!_init) { _fillF = fill; _init = true; }
        if (Math.Abs(fill - Target) > ResyncSamples && Math.Abs(_fillF - Target) > ResyncSamples)
        {
            _fillF = fill; _integ = 0; Resyncs++;
        }
        double a = Math.Min(1.0, dtSec / TauSec);
        _fillF += a * (fill - _fillF);
        double e = _fillF - Target;
        double im = Limit / _ki;
        _integ = Math.Clamp(_integ + e * calls, -im, im);
        Ratio = Math.Clamp(1 + _kp * e + _ki * _integ, 1 - Limit, 1 + Limit);
        return Ratio;
    }
}
