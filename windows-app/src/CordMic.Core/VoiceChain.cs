namespace CordMic.Core;

/// <summary>How strongly the phone should suppress steady noise. Default is <see cref="Light"/>.</summary>
public enum NoiseMode
{
    Off = 0,
    Light = 1,
    Strong = 2,
}

/// <summary>
/// Voice chain used on the phone (the C# class is the tested reference; the Kotlin port matches it):
/// optional voice-clarity EQ (high-pass, low shelf, presence), optional STFT noise suppression,
/// user gain (smoothed), gated AGC, a pause expander, and a peak limiter at −1 dBFS.
/// The gate and the noise floor are measured before the user gain and the floor is tracked
/// continuously, so raising the gain cannot open the gate on hiss. AGC makeup is limited so the
/// tracked floor stays at or below <see cref="NoiseCeilingDbfs"/> after gain.
/// Light/Strong suppression is a Hann-windowed 256-point STFT with 50 % overlap (hop 128, 2.67 ms
/// of added delay at 48 kHz). Gains are smoothed and floored so bins are never fully muted.
/// </summary>
public sealed class VoiceChain
{
    public const int NoiseFft = 256;
    public const int NoiseHop = 128;
    public const int NoiseDelayFrames = NoiseHop;
    public const int MaxGainDb = 24;
    public const int DefaultGainDb = 6;
    /// <summary>AGC envelope target. −18 dBFS leaves headroom for Discord/game mic sliders around 50–60 %.</summary>
    public const double TargetDbfs = -18;
    public const double MaxAgcDb = 24;
    public const double CeilingDbfs = -1;
    private const double LightFloor = 0.32;   // −10 dB — milder than Strong at the same speech level
    private const double StrongFloor = 0.05;  // −26 dB
    public const double DefaultGateOpenDbfs = -42;
    public const double DefaultGateCloseDbfs = -50;
    public const double SpeechLowDbfs = -40;
    /// <summary>AGC makeup never lifts the tracked noise floor above this level.</summary>
    public const double NoiseCeilingDbfs = -52;
    /// <summary>Floor assumed before any measurement (gives the −44 / −50 dBFS default gates).</summary>
    public const double InitialFloorDbfs = -54;
    /// <summary>Pause attenuation applied by the expander: Light −6 dB, Strong −12 dB, Off none.</summary>
    public const double LightPauseDb = -6, StrongPauseDb = -12;
    private const double FloorRiseDbPerSec = 1.5;
    /// <summary>Slower rise while the gate is open, so sustained speech does not lift the floor.</summary>
    private const double FloorRiseOpenDbPerSec = 0.5;
    private const double GateHoldSec = 0.20;
    private const int GateUpdateFrames = 64;

    private readonly int _sr;
    private Biquad _hp, _shelf, _presence;
    private readonly double[] _blockIn = new double[NoiseFft];
    private readonly double[] _hopOut = new double[NoiseHop];
    private readonly double[] _ola = new double[NoiseHop];
    private readonly double[] _hann = new double[NoiseFft];
    private readonly double[] _re = new double[NoiseFft];
    private readonly double[] _im = new double[NoiseFft];
    private readonly double[] _mag = new double[NoiseFft / 2 + 1];
    private readonly double[] _noise = new double[NoiseFft / 2 + 1];
    private readonly double[] _binGain = new double[NoiseFft / 2 + 1];
    private readonly double _rmsCoeff, _agcRise, _agcFall, _agcGateRel, _peakRel, _limRel;
    private readonly double _floorDown, _floorUp, _floorUpOpen, _gainSmooth, _expOpen, _expClose;
    private readonly int _holdFrames;
    private double _rmsPre, _floor = DbToLin(InitialFloorDbfs), _userGain = double.NaN, _exp = 1;
    private int _hold, _gateTick, _belowFloor;
    private bool _floorKnown;
    private int _fill = NoiseHop;
    private int _emit;
    private bool _haveHop;
    private double _rms, _agc = 1, _peak, _lim = 1;
    private double _gateOpen = DbToLin(DefaultGateOpenDbfs);
    private double _gateClose = DbToLin(DefaultGateCloseDbfs);
    private bool _gated = true;
    private NoiseMode _mode = NoiseMode.Light;
    private int _gainDb = DefaultGainDb;

    public int SampleRate => _sr;

    public NoiseMode Mode
    {
        get => _mode;
        set => _mode = value;
    }

    /// <summary>True when suppression is not <see cref="NoiseMode.Off"/>. Setting false turns it off; setting true selects Light if it was off.</summary>
    public bool NoiseCancellation
    {
        get => _mode != NoiseMode.Off;
        set => _mode = value ? (_mode == NoiseMode.Off ? NoiseMode.Light : _mode) : NoiseMode.Off;
    }

    public bool Agc { get; set; } = true;
    /// <summary>Pause expander depth. The phone sets this to the user's noise mode even when the platform suppressor replaces the STFT.</summary>
    public NoiseMode Expander { get; set; } = NoiseMode.Light;
    /// <summary>Tracked pause floor before user gain, in dBFS.</summary>
    public double NoiseFloorDbfs => 20 * Math.Log10(Math.Max(_floor, 1e-6));
    public bool VoiceClarity { get; set; } = true;
    /// <summary>Alias for <see cref="VoiceClarity"/> so existing tests can disable the EQ section.</summary>
    public bool HighPass
    {
        get => VoiceClarity;
        set => VoiceClarity = value;
    }

    public int GainDb
    {
        get => _gainDb;
        set => _gainDb = Math.Clamp(value, 0, MaxGainDb);
    }

    /// <summary>
    /// Place the AGC gate relative to a measured pause floor. Open ≈ floor + 10 dB,
    /// close ≈ floor + 4 dB, clamped so a very high floor cannot unmute hiss.
    /// A missing or implausible floor keeps the −42 / −50 dBFS defaults.
    /// </summary>
    public void SetNoiseFloorDbfs(double? floorDbfs)
    {
        var (open, close) = GatesFromFloor(floorDbfs);
        _gateOpen = open;
        _gateClose = close;
        if (floorDbfs is { } f && !double.IsNaN(f) && f >= -85 && f <= -15) { _floor = DbToLin(f); _floorKnown = true; }
    }

    public static (double Open, double Close) GatesFromFloor(double? floorDbfs)
    {
        if (floorDbfs is null || double.IsNaN(floorDbfs.Value) || floorDbfs.Value < -85 || floorDbfs.Value > -15)
            return (DbToLin(DefaultGateOpenDbfs), DbToLin(DefaultGateCloseDbfs));
        double openDb = Math.Clamp(floorDbfs.Value + 10.0, -55, -28);
        double closeDb = Math.Clamp(floorDbfs.Value + 4.0, -65, -38);
        if (closeDb > openDb - 4) closeDb = openDb - 6;
        return (DbToLin(openDb), DbToLin(closeDb));
    }

    /// <summary>Samples of delay the chain is inserting right now (STFT hop, or 0 when suppression is off).</summary>
    public int DelayFrames => _mode == NoiseMode.Off ? 0 : NoiseDelayFrames;

    /// <summary>Added latency of the noise suppressor. 0 when it is off.</summary>
    public double NoiseLatencyMs => DelayFrames * 1000.0 / _sr;

    public VoiceChain(int sampleRate = 48000)
    {
        if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _sr = sampleRate;
        _hp = Biquad.HighPass(sampleRate, 120, 0.7071067811865476);
        _shelf = Biquad.LowShelf(sampleRate, 220, -5.0);
        _presence = Biquad.Peaking(sampleRate, 3000, 2.5, 1.0);
        for (int i = 0; i < NoiseFft; i++)
            _hann[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / NoiseFft);
        for (int i = 0; i < _binGain.Length; i++) _binGain[i] = 1;
        _rmsCoeff = 1 - Math.Exp(-1.0 / (0.040 * sampleRate)); // ~40 ms mean-square window
        _agcRise = 1 - Math.Exp(-1.0 / (0.22 * sampleRate));  // reach target within about a second of speech
        _agcFall = 1 - Math.Exp(-1.0 / (0.025 * sampleRate)); // faster downward when speech is hot
        _agcGateRel = 1 - Math.Exp(-1.0 / (0.080 * sampleRate)); // drop makeup during pauses so hiss is not held up
        _peakRel = 1 - Math.Exp(-1.0 / (0.030 * sampleRate));
        _limRel = 1 - Math.Exp(-1.0 / (0.050 * sampleRate));
        _floorDown = 1 - Math.Exp(-1.0 / (0.30 * sampleRate));
        _floorUp = Math.Pow(10, FloorRiseDbPerSec / 20.0 / sampleRate);
        _floorUpOpen = Math.Pow(10, FloorRiseOpenDbPerSec / 20.0 / sampleRate);
        _gainSmooth = 1 - Math.Exp(-1.0 / (0.020 * sampleRate));
        _expOpen = 1 - Math.Exp(-1.0 / (0.005 * sampleRate));
        _expClose = 1 - Math.Exp(-1.0 / (0.150 * sampleRate));
        _holdFrames = (int)(GateHoldSec * sampleRate);
    }

    public void Reset()
    {
        _hp.Reset(); _shelf.Reset(); _presence.Reset();
        _fill = NoiseHop; _emit = 0; _haveHop = false;
        _rms = 0; _agc = 1; _peak = 0; _lim = 1; _gated = true;
        _rmsPre = 0; _floor = DbToLin(InitialFloorDbfs); _userGain = double.NaN; _exp = 1; _hold = 0; _gateTick = 0; _belowFloor = 0; _floorKnown = false;
        (_gateOpen, _gateClose) = GatesFromFloor(InitialFloorDbfs);
        Array.Clear(_blockIn); Array.Clear(_hopOut); Array.Clear(_ola); Array.Clear(_noise);
        for (int i = 0; i < _binGain.Length; i++) _binGain[i] = 1;
    }

    public void Process(Span<short> samples)
    {
        double user = DbToLin(_gainDb);
        if (double.IsNaN(_userGain)) _userGain = user;
        double maxAgc = DbToLin(Math.Min(MaxAgcDb, MaxGainDb - _gainDb));
        double target = DbToLin(TargetDbfs);
        double thresh = DbToLin(CeilingDbfs);
        double noiseCeil = DbToLin(NoiseCeilingDbfs);
        double pause = Expander switch
        {
            NoiseMode.Strong => DbToLin(StrongPauseDb),
            NoiseMode.Light => DbToLin(LightPauseDb),
            _ => 1.0,
        };
        bool stft = _mode != NoiseMode.Off;
        for (int i = 0; i < samples.Length; i++)
        {
            double x = samples[i] / 32768.0;
            if (VoiceClarity)
            {
                x = _hp.Process(x);
                x = _shelf.Process(x);
                x = _presence.Process(x);
            }
            if (stft) x = NoiseStep(x);
            else { _fill = NoiseHop; _emit = 0; _haveHop = false; }
            GateStep(x);
            _userGain += (user - _userGain) * _gainSmooth;
            double y = x * _userGain;
            if (Agc)
            {
                // RMS envelope so typical speech (not just a plosive peak) sits near −18 dBFS.
                _rms += (y * y - _rms) * _rmsCoeff;
                double level = Math.Sqrt(Math.Max(_rms, 0));
                // Do not lift the pause floor above the noise ceiling (no hiss pumping on quiet phones).
                // Until a pause has been observed the floor is a guess, so only the fixed cap applies.
                double cap = _floorKnown ? Math.Clamp(noiseCeil / Math.Max(_floor * _userGain, 1e-9), 1.0, maxAgc) : maxAgc;
                double want = _gated ? 1.0 : Math.Clamp(target / Math.Max(level, 1e-5), 0.25, cap);
                double coeff = _gated ? _agcGateRel : (want > _agc ? _agcRise : _agcFall);
                _agc += (want - _agc) * coeff;
                y *= _agc;
            }
            double expWant = _gated ? pause : 1.0;
            _exp += (expWant - _exp) * (expWant > _exp ? _expOpen : _expClose);
            y *= _exp;
            y = LimitStep(y, thresh);
            int s = (int)Math.Round(y * 32768.0);
            if (s > 32767) s = 32767;
            else if (s < -32768) s = -32768;
            samples[i] = (short)s;
        }
    }

    /// <summary>Pre-gain envelope, continuous floor tracking, and the gate with a short hold.</summary>
    private void GateStep(double x)
    {
        _rmsPre += (x * x - _rmsPre) * _rmsCoeff;
        double level = Math.Sqrt(Math.Max(_rmsPre, 0));
        if (level < _floor)
        {
            _floor += (level - _floor) * _floorDown;
            if (!_floorKnown && ++_belowFloor >= _sr / 4) _floorKnown = true;
        }
        else _floor *= _gated ? _floorUp : _floorUpOpen;
        if (_floor < 1e-5) _floor = 1e-5;
        else if (_floor > 0.1) _floor = 0.1;
        if (++_gateTick >= GateUpdateFrames)
        {
            _gateTick = 0;
            (_gateOpen, _gateClose) = GatesFromFloor(20 * Math.Log10(_floor));
        }
        if (level > _gateOpen) { _gated = false; _hold = _holdFrames; }
        else if (level < _gateClose)
        {
            if (_hold > 0) _hold--;
            else _gated = true;
        }
    }

    private double LimitStep(double y, double thresh)
    {
        double ay = Math.Abs(y);
        if (ay > _peak) _peak = ay;
        else _peak += (ay - _peak) * _peakRel;
        double gr = _peak > thresh ? thresh / _peak : 1.0;
        // Lookahead-less: take gain reduction immediately so a shout cannot sneak past −1 dBFS.
        if (gr < _lim) _lim = gr;
        else _lim += (gr - _lim) * _limRel;
        y *= _lim;
        if (y > 0.99) y = 0.99;
        if (y < -0.99) y = -0.99;
        return y;
    }

    private double NoiseStep(double x)
    {
        double y = _haveHop ? _hopOut[_emit] : 0;
        _blockIn[_fill] = x;
        _fill++;
        _emit++;
        if (_fill == NoiseFft)
        {
            SuppressHop();
            Array.Copy(_blockIn, NoiseHop, _blockIn, 0, NoiseHop);
            _fill = NoiseHop;
            _emit = 0;
            _haveHop = true;
        }
        return y;
    }

    private void SuppressHop()
    {
        for (int i = 0; i < NoiseFft; i++)
        {
            _re[i] = _blockIn[i] * _hann[i];
            _im[i] = 0;
        }
        Fft(_re, _im, false);
        double maxBin = 0, sumBin = 0;
        int half = NoiseFft / 2;
        for (int k = 0; k <= half; k++)
        {
            double m = Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]);
            _mag[k] = m;
            if (k > 0)
            {
                if (m > maxBin) maxBin = m;
                sumBin += m;
            }
        }
        double mean = sumBin / half;
        bool noiseLike = maxBin < mean * 12.0 + 1e-12;
        double floor = _mode == NoiseMode.Strong ? StrongFloor : LightFloor;
        double oversub = _mode == NoiseMode.Strong ? 1.85 : 1.0;
        double smooth = _mode == NoiseMode.Strong ? 0.38 : 0.70;
        for (int k = 0; k <= half; k++)
        {
            double m = _mag[k];
            double n = _noise[k];
            // Only raise the noise estimate on noise-like frames. A slow upward leak on
            // tonal bins was eating sustained vowels (and the 1 kHz quality probe) over a few seconds.
            if (noiseLike) n = 0.80 * n + 0.20 * m;
            else if (m < n) n = 0.70 * n + 0.30 * m;
            _noise[k] = n;
            double g = 1.0 - oversub * n / (m + 1e-12);
            if (g < floor) g = floor;
            else if (g > 1) g = 1;
            _binGain[k] = smooth * _binGain[k] + (1 - smooth) * g;
        }
        for (int k = 0; k < NoiseFft; k++)
        {
            int bin = k <= half ? k : NoiseFft - k;
            _re[k] *= _binGain[bin];
            _im[k] *= _binGain[bin];
        }
        Fft(_re, _im, true);
        for (int i = 0; i < NoiseHop; i++)
        {
            _hopOut[i] = _ola[i] + _re[i];
            _ola[i] = _re[i + NoiseHop];
        }
    }

    /// <summary>Legacy name kept for callers. The runtime limiter is <see cref="LimitStep"/>; this maps onto the −1 dBFS ceiling without tanh.</summary>
    public static double SoftLimit(double x)
    {
        const double ceiling = 0.8912509381337456; // 10^(-1/20)
        double a = Math.Abs(x);
        if (a <= ceiling) return x;
        return Math.CopySign(ceiling, x);
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

    private struct Biquad
    {
        private double _b0, _b1, _b2, _a1, _a2;
        private double _x1, _x2, _y1, _y2;

        public static Biquad HighPass(int sr, double freq, double q)
        {
            double w0 = 2 * Math.PI * freq / sr;
            double cos = Math.Cos(w0);
            double alpha = Math.Sin(w0) / (2 * q);
            double a0 = 1 + alpha;
            return new Biquad
            {
                _b0 = ((1 + cos) / 2) / a0,
                _b1 = (-(1 + cos)) / a0,
                _b2 = ((1 + cos) / 2) / a0,
                _a1 = (-2 * cos) / a0,
                _a2 = (1 - alpha) / a0,
            };
        }

        public static Biquad LowShelf(int sr, double freq, double gainDb)
        {
            double A = Math.Pow(10, gainDb / 40.0);
            double w0 = 2 * Math.PI * freq / sr;
            double cos = Math.Cos(w0);
            double sin = Math.Sin(w0);
            double alpha = sin / 2 * Math.Sqrt(2);
            double twoSqrtAalpha = 2 * Math.Sqrt(A) * alpha;
            double a0 = (A + 1) + (A - 1) * cos + twoSqrtAalpha;
            return new Biquad
            {
                _b0 = A * ((A + 1) - (A - 1) * cos + twoSqrtAalpha) / a0,
                _b1 = 2 * A * ((A - 1) - (A + 1) * cos) / a0,
                _b2 = A * ((A + 1) - (A - 1) * cos - twoSqrtAalpha) / a0,
                _a1 = -2 * ((A - 1) + (A + 1) * cos) / a0,
                _a2 = ((A + 1) + (A - 1) * cos - twoSqrtAalpha) / a0,
            };
        }

        public static Biquad Peaking(int sr, double freq, double gainDb, double q)
        {
            double A = Math.Pow(10, gainDb / 40.0);
            double w0 = 2 * Math.PI * freq / sr;
            double cos = Math.Cos(w0);
            double alpha = Math.Sin(w0) / (2 * q);
            double a0 = 1 + alpha / A;
            return new Biquad
            {
                _b0 = (1 + alpha * A) / a0,
                _b1 = (-2 * cos) / a0,
                _b2 = (1 - alpha * A) / a0,
                _a1 = (-2 * cos) / a0,
                _a2 = (1 - alpha / A) / a0,
            };
        }

        public double Process(double x)
        {
            double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
            return y;
        }

        public void Reset() => _x1 = _x2 = _y1 = _y2 = 0;
    }
}
