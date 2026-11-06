namespace CordMic.Core;

/// <summary>
/// Voice chain used on the phone (the C# class is the tested reference; the Kotlin port matches it):
/// high-pass, optional noise suppression, user gain, AGC, soft limiter.
/// Noise suppression is a 256-point spectral gate. It delays audio by exactly
/// <see cref="NoiseDelayFrames"/> samples (5.33 ms at 48 kHz) and nothing else in the chain adds samples.
/// Total boost is capped at +24 dB. The limiter never passes a sample hotter than about -0.5 dBFS.
/// </summary>
public sealed class VoiceChain
{
    public const int NoiseFft = 256;
    public const int NoiseDelayFrames = 256;
    public const int MaxGainDb = 24;
    public const int DefaultGainDb = 12;
    /// <summary>AGC envelope target. -14 dBFS sits well above the -36..-70 dBFS peaks in the 0.4.2 log.</summary>
    public const double TargetDbfs = -14;
    private const int Fade = 16;
    private const double GainFloor = 0.20;

    private readonly int _sr;
    private readonly double _b0, _b1, _b2, _a1, _a2;
    private double _x1, _x2, _y1, _y2;
    private readonly double[] _blockIn = new double[NoiseFft];
    private readonly double[] _blockOut = new double[NoiseFft];
    private readonly double[] _re = new double[NoiseFft];
    private readonly double[] _im = new double[NoiseFft];
    private readonly double[] _mag = new double[NoiseFft / 2 + 1];
    private readonly double[] _noise = new double[NoiseFft / 2 + 1];
    private readonly double[] _binGain = new double[NoiseFft / 2 + 1];
    private readonly double[] _tail = new double[Fade];
    private readonly double _envRelease;     // one-pole, ~200 ms, applied when the envelope is falling
    private readonly double _agcAttack;      // ~8 ms
    private readonly double _agcRelease;     // ~180 ms
    private int _fill, _emit;
    private bool _haveBlock;
    private double _env, _agc = 1;

    public int SampleRate => _sr;
    public bool NoiseCancellation { get; set; } = true;
    public bool Agc { get; set; } = true;
    public bool HighPass { get; set; } = true;
    private int _gainDb = DefaultGainDb;
    public int GainDb
    {
        get => _gainDb;
        set => _gainDb = Math.Clamp(value, 0, MaxGainDb);
    }

    /// <summary>Samples of delay the chain is inserting right now.</summary>
    public int DelayFrames => NoiseCancellation ? NoiseDelayFrames : 0;

    /// <summary>Added latency of the noise suppressor. 0 when it is off.</summary>
    public double NoiseLatencyMs => DelayFrames * 1000.0 / _sr;

    public VoiceChain(int sampleRate = 48000)
    {
        if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _sr = sampleRate;
        // Butterworth-ish high-pass at 80 Hz (RBJ cookbook).
        double w0 = 2 * Math.PI * 80.0 / sampleRate;
        double cos = Math.Cos(w0);
        double alpha = Math.Sin(w0) / (2 * 0.7071067811865476);
        double a0 = 1 + alpha;
        _b0 = ((1 + cos) / 2) / a0;
        _b1 = (-(1 + cos)) / a0;
        _b2 = _b0;
        _a1 = (-2 * cos) / a0;
        _a2 = (1 - alpha) / a0;
        _envRelease = 1 - Math.Exp(-1.0 / (0.20 * sampleRate));
        _agcAttack = 1 - Math.Exp(-1.0 / (0.008 * sampleRate));
        _agcRelease = 1 - Math.Exp(-1.0 / (0.18 * sampleRate));
    }

    public void Reset()
    {
        _x1 = _x2 = _y1 = _y2 = 0;
        _fill = _emit = 0;
        _haveBlock = false;
        _env = 0; _agc = 1;
        Array.Clear(_blockIn); Array.Clear(_blockOut); Array.Clear(_noise); Array.Clear(_tail);
    }

    public void Process(Span<short> samples)
    {
        double user = DbToLin(_gainDb);
        double maxAgc = DbToLin(MaxGainDb - _gainDb);
        double target = DbToLin(TargetDbfs);
        for (int i = 0; i < samples.Length; i++)
        {
            double x = samples[i] / 32768.0;
            if (HighPass) x = HighPassStep(x);
            if (NoiseCancellation) x = NoiseStep(x);
            else { _fill = 0; _emit = 0; _haveBlock = false; }
            double y = x * user;
            if (Agc)
            {
                double ay = Math.Abs(y);
                if (ay > _env) _env = ay;
                else _env += (ay - _env) * _envRelease;
                double want = Math.Clamp(target / Math.Max(_env, 1e-5), 1.0, maxAgc);
                _agc += (want - _agc) * (want > _agc ? _agcAttack : _agcRelease);
                y *= _agc;
            }
            y = SoftLimit(y);
            int s = (int)Math.Round(y * 32768.0);
            if (s > 32767) s = 32767;
            else if (s < -32768) s = -32768;
            samples[i] = (short)s;
        }
    }

    private double HighPassStep(double x)
    {
        double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
        _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
        return y;
    }

    private double NoiseStep(double x)
    {
        double y = 0;
        if (_haveBlock) y = _blockOut[_emit];
        _blockIn[_fill] = x;
        _fill++;
        _emit++;
        if (_fill == NoiseFft)
        {
            SuppressBlock(_blockIn, _blockOut);
            _fill = 0;
            _emit = 0;
            _haveBlock = true;
        }
        return y;
    }

    private void SuppressBlock(double[] input, double[] output)
    {
        Array.Clear(_re);
        Array.Clear(_im);
        input.AsSpan(0, NoiseFft).CopyTo(_re);
        Fft(_re, _im, false);
        double maxBin = 0, sumBin = 0;
        for (int k = 0; k <= NoiseFft / 2; k++)
        {
            double m = Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]);
            _mag[k] = m;
            if (k > 0)
            {
                if (m > maxBin) maxBin = m;
                sumBin += m;
            }
        }
        double mean = sumBin / (NoiseFft / 2);
        // A pure tone piles energy in one bin (ratio ~ 100). White noise stays under ~8.
        bool noiseLike = maxBin < mean * 12.0 + 1e-12;
        for (int k = 0; k <= NoiseFft / 2; k++)
        {
            double m = _mag[k];
            double n = _noise[k];
            if (noiseLike) n = 0.75 * n + 0.25 * m;
            else if (m < n) n = 0.50 * n + 0.50 * m;
            else n = 0.998 * n + 0.002 * m;
            _noise[k] = n;
            double g = 1.0 - 1.5 * n / (m + 1e-12);
            if (g < GainFloor) g = GainFloor;
            else if (g > 1) g = 1;
            _binGain[k] = g;
        }
        for (int k = 0; k < NoiseFft; k++)
        {
            int bin = k <= NoiseFft / 2 ? k : NoiseFft - k;
            _re[k] *= _binGain[bin];
            _im[k] *= _binGain[bin];
        }
        Fft(_re, _im, true);
        for (int i = 0; i < Fade; i++)
        {
            double w = (i + 1) / (double)(Fade + 1);
            output[i] = _tail[i] * (1 - w) + _re[i] * w;
        }
        for (int i = Fade; i < NoiseFft; i++) output[i] = _re[i];
        Array.Copy(output, NoiseFft - Fade, _tail, 0, Fade);
    }

    /// <summary>Cubic-knee soft clip. |x| below 0.70 passes; louder samples approach 0.94 and do not reach full scale.</summary>
    public static double SoftLimit(double x)
    {
        double a = Math.Abs(x);
        const double knee = 0.70;
        const double ceiling = 0.94;
        if (a <= knee) return x;
        double over = (a - knee) / (1.0 - knee);
        double y = knee + (ceiling - knee) * Math.Tanh(over);
        return Math.CopySign(y, x);
    }

    public static double DbToLin(double db) => Math.Pow(10.0, db / 20.0);

    /// <summary>In-place radix-2 FFT. <paramref name="invert"/> divides by n.</summary>
    public static void Fft(double[] re, double[] im, bool invert)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2 * Math.PI / len * (invert ? -1 : 1);
            double wlenRe = Math.Cos(ang), wlenIm = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double wr = 1, wi = 0;
                int half = len >> 1;
                for (int k = 0; k < half; k++)
                {
                    int u = i + k, v = u + half;
                    double vr = re[v] * wr - im[v] * wi;
                    double vi = re[v] * wi + im[v] * wr;
                    re[v] = re[u] - vr;
                    im[v] = im[u] - vi;
                    re[u] += vr;
                    im[u] += vi;
                    double nwr = wr * wlenRe - wi * wlenIm;
                    wi = wr * wlenIm + wi * wlenRe;
                    wr = nwr;
                }
            }
        }
        if (invert)
            for (int i = 0; i < n; i++) { re[i] /= n; im[i] /= n; }
    }
}
