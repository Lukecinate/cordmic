namespace CordMic.Core;

/// <summary>
/// "Move the phone closer" hint (0.6.0 spec §7). Fed with the received audio in 10 ms frames, it tracks the noise
/// floor (mean power of frames near the 2 s minimum) and the speech level (mean power of frames 8 dB above the
/// floor, smoothed over about 10 s of speech). The hint turns on after the estimated SNR stays below 15 dB for 10 s
/// and off after it stays above 18 dB for 10 s. With phone noise suppression on, the received floor is already
/// reduced, so the estimate reads high and the hint appears less often; with Studio on the phone sends raw audio.
/// </summary>
public sealed class SnrHint
{
    public const double ShowBelowDb = 15, HideAboveDb = 18, HoldSec = 10;
    private const int Frame = 480, Hist = 200;
    private readonly double[] _hist = new double[Hist];
    private int _hn, _hp, _fi, _acc;
    private double _accP, _min = double.NaN, _noiseP = double.NaN, _speechP = double.NaN;
    private double _lowSec, _highSec;

    public bool Visible { get; private set; }
    /// <summary>Estimated speech-to-noise ratio in dB, NaN until both levels are known.</summary>
    public double SnrDb => double.IsNaN(_noiseP) || double.IsNaN(_speechP) ? double.NaN
        : 10 * Math.Log10(Math.Max(_speechP - _noiseP, 1e-20) / Math.Max(_noiseP, 1e-20));

    public void Reset()
    {
        Array.Clear(_hist); _hn = _hp = _fi = _acc = 0; _accP = 0;
        _min = _noiseP = _speechP = double.NaN; _lowSec = _highSec = 0; Visible = false;
    }

    public void AddS16(ReadOnlySpan<byte> pcm)
    {
        for (int i = 0; i + 1 < pcm.Length; i += 2)
        {
            double v = (short)(pcm[i] | (pcm[i + 1] << 8)) / 32768.0;
            _accP += v * v;
            if (++_acc == Frame) { AddFramePower(_accP / Frame); _acc = 0; _accP = 0; }
        }
    }

    public void AddFramePower(double p)
    {
        double lvl = 10 * Math.Log10(p + 1e-20);
        _hist[_hp] = lvl; _hp = (_hp + 1) % Hist; if (_hn < Hist) _hn++;
        if (++_fi % 5 == 0)
        {
            double mn = double.MaxValue; int start = (_hp - _hn + Hist) % Hist;
            for (int b = 0; b < _hn / 5; b++)
            {
                double a = 0; for (int j = 0; j < 5; j++) a += _hist[(start + b * 5 + j) % Hist];
                mn = Math.Min(mn, a / 5);
            }
            if (mn < double.MaxValue) _min = double.IsNaN(_min) || mn < _min ? mn : _min + 0.1 * (mn - _min);
        }
        if (double.IsNaN(_min)) return;
        if (lvl < _min + 4) _noiseP = double.IsNaN(_noiseP) ? p : _noiseP + 0.02 * (p - _noiseP);
        else if (lvl > _min + 8) _speechP = double.IsNaN(_speechP) ? p : _speechP + 0.001 * (p - _speechP);
        double snr = SnrDb;
        if (double.IsNaN(snr)) return;
        const double dt = 0.01;
        if (snr < ShowBelowDb) { _lowSec += dt; _highSec = 0; }
        else if (snr > HideAboveDb) { _highSec += dt; _lowSec = 0; }
        else { _lowSec = 0; _highSec = 0; }
        if (!Visible && _lowSec >= HoldSec) Visible = true;
        else if (Visible && _highSec >= HoldSec) Visible = false;
    }
}
