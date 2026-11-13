namespace CordMic.Core.Dsp;

/// <summary>
/// Low-level comfort noise (0.6.2): a soft, slightly low-passed noise bed added after the Studio AGC so pauses never
/// fall to digital zero and the step between "noise reduced" and "silent" stays small. The chain sets the level
/// (at most −65 dBFS RMS), far below speech and below typical voice-activity thresholds. Deterministic (xorshift),
/// allocation-free, not thread-safe.
/// </summary>
public sealed class ComfortNoise
{
    public const double DefaultDbfs = -65;
    private const double A = 0.6; // one-pole low-pass on white noise: gentle high-frequency roll-off
    private uint _s = 0x9E3779B9u;
    private double _lp;
    private double _scale, _scale0;
    private readonly double _unitRms;
    public double LevelDbfs { get; private set; }

    public ComfortNoise(double levelDbfs = DefaultDbfs)
    {
        // Uniform [-1, 1] has variance 1/3; the one-pole y = A·y + (1−A)·w scales it by (1−A)/(1+A).
        _unitRms = Math.Sqrt(1.0 / 3.0 * (1 - A) / (1 + A));
        SetLevel(levelDbfs);
        _scale0 = _scale;
    }

    /// <summary>Changes the level; the next <see cref="AddTo"/> ramps to it over the block (no step).</summary>
    public void SetLevel(double levelDbfs)
    {
        LevelDbfs = levelDbfs;
        _scale = double.IsFinite(levelDbfs) ? Math.Pow(10, levelDbfs / 20) / _unitRms : 0;
    }

    public void Reset() { _s = 0x9E3779B9u; _lp = 0; }

    /// <summary>Adds the noise bed to <paramref name="x"/> in place.</summary>
    public void AddTo(Span<float> x)
    {
        int n = x.Length;
        for (int i = 0; i < n; i++)
        {
            _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5;
            double w = _s / 2147483648.0 - 1.0;
            _lp = A * _lp + (1 - A) * w;
            double g = _scale0 + (_scale - _scale0) * (i + 1) / n;
            x[i] = (float)(x[i] + g * _lp);
        }
        _scale0 = _scale;
    }
}
