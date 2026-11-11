namespace CordMic.Core.Dsp;

/// <summary>Direct-form-II-transposed biquad (a0 = 1), double precision.</summary>
public struct Biquad2
{
    private readonly double _b0, _b1, _b2, _a1, _a2;
    private double _z1, _z2;
    public Biquad2(double b0, double b1, double b2, double a1, double a2) { _b0 = b0; _b1 = b1; _b2 = b2; _a1 = a1; _a2 = a2; _z1 = _z2 = 0; }
    public double Process(double x)
    {
        double y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        return y;
    }
    public void Reset() { _z1 = _z2 = 0; }
}

public static class DspFilters
{
    /// <summary>4th-order Butterworth band-pass 300–8000 Hz at 48 kHz as four second-order sections (b0 b1 b2 1 a1 a2).</summary>
    public static readonly double[][] Band300To8000 =
    {
        new[] { 0.023062627688301328, 0.046125255376602656, 0.023062627688301328, 1.0, -0.615845640854099, 0.13245707503613985 },
        new[] { 1.0, 2.0, 1.0, 1.0, -0.7730652184035276, 0.5257829751470703 },
        new[] { 1.0, -2.0, 1.0, 1.0, -1.9249914184156756, 0.926666941959353 },
        new[] { 1.0, -2.0, 1.0, 1.0, -1.9702944019713264, 0.9718420063081804 },
    };

    public static Biquad2[] FromSos(double[][] sos)
    {
        var b = new Biquad2[sos.Length];
        for (int i = 0; i < sos.Length; i++) b[i] = new Biquad2(sos[i][0], sos[i][1], sos[i][2], sos[i][4], sos[i][5]);
        return b;
    }

    /// <summary>ITU-R BS.1770 K-weighting at 48 kHz (pre-filter shelf, then RLB high-pass).</summary>
    public static Biquad2 KShelf() => new(1.53512485958697, -2.69169618940638, 1.19839281085285, -1.69065929318241, 0.73248077421585);
    public static Biquad2 KHighPass() => new(1.0, -2.0, 1.0, -1.99004745483398, 0.99007225036621);
}

/// <summary>−1 dBFS peak limiter with instant attack and 50 ms release (same constants as the 0.5.7 voice chain), hard clip at ±0.99.</summary>
public sealed class PeakLimiter
{
    private readonly double _pr = 1 - Math.Exp(-1 / (0.03 * 48000)), _lr = 1 - Math.Exp(-1 / (0.05 * 48000));
    private const double T = 0.8912509381337456;
    private double _peak, _lim = 1;
    public void Reset() { _peak = 0; _lim = 1; }
    public double Step(double y)
    {
        double a = Math.Abs(y);
        _peak = a > _peak ? a : _peak + (a - _peak) * _pr;
        double gr = _peak > T ? T / _peak : 1.0;
        _lim = gr < _lim ? gr : _lim + (gr - _lim) * _lr;
        y *= _lim;
        return y > 0.99 ? 0.99 : y < -0.99 ? -0.99 : y;
    }
}
