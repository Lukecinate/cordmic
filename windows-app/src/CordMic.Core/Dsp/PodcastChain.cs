namespace CordMic.Core.Dsp;

// 1.1.0 podcast voice chain (original MIT code, written from the RBJ Audio EQ Cookbook formulas and textbook
// one-pole / dB-domain gain-computer math; no third-party DSP code). Mirrors android/.../PodcastChain.kt.
//
//   gated AGC -> dynamic EQ (HP 80 Hz, dynamic mud cut ~250 Hz, dynamic de-ess ~6.5 kHz)
//   -> soft-knee 3:1 compressor with program-dependent release -> look-ahead brickwall limiter (-1 dBFS)
//
// The spectral dereverb and dialog-isolation masks (SpectralVoice) run inside the noise-suppression STFT of
// VoiceChain, so there is one mask and one set of smoothing; a TruePeakGuard follows the noise stage.

/// <summary>Per-preset tuning of the podcast chain and its spectral stages. Strengths are 0..1.</summary>
public sealed record PodcastParams
{
    public double AgcTargetDbfs { get; init; } = -17;
    public double AgcMaxDb { get; init; } = 24;
    public double AgcMinDb { get; init; } = -12;
    public double AgcRiseSec { get; init; } = 0.6;
    public double AgcFallSec { get; init; } = 0.18;
    public double HpHz { get; init; } = 80;
    public double MudHz { get; init; } = 250;
    public double MudMaxCutDb { get; init; } = 4;
    public double MudThreshDb { get; init; } = -6;
    public double EssHz { get; init; } = 6500;
    public double EssMaxCutDb { get; init; } = 6;
    public double EssThreshDb { get; init; } = -12;
    public double CompThreshDbfs { get; init; } = -22;
    public double CompRatio { get; init; } = 3;
    public double CompKneeDb { get; init; } = 10;
    public double CompMakeupDb { get; init; } = 2;
    public double LimCeilingDbfs { get; init; } = -1;
    public double LookaheadMs { get; init; } = 1.5;
    public double LimReleaseMs { get; init; } = 60;
    public double Dereverb { get; init; }
    public double Dialog { get; init; }
}

/// <summary>Per-preset podcast tuning. Phone presets match the Kotlin PodcastPresets exactly.</summary>
public static class PodcastPresets
{
    /// <summary>Noise Off: lowest latency, no STFT (dereverb and dialog off), 1 ms look-ahead, lighter EQ.</summary>
    public static readonly PodcastParams Off = new() { MudMaxCutDb = 3, EssMaxCutDb = 5, LookaheadMs = 1.0, LimReleaseMs = 50 };
    public static readonly PodcastParams Light = new() { LookaheadMs = 1.5, Dereverb = 0.35, Dialog = 0.30 };
    public static readonly PodcastParams Strong = new() { LookaheadMs = 2.0, LimReleaseMs = 80, Dereverb = 0.50, Dialog = 0.45 };
    /// <summary>PC Studio presets (raw phone capture; the chain runs before RNNoise).</summary>
    public static readonly PodcastParams StudioBalanced = new() { LookaheadMs = 2.0, Dereverb = 0.35, Dialog = 0.30 };
    /// <summary>Whisper: more lift and a lower compressor threshold, gentle de-ess and masks so whispers are not thinned.</summary>
    public static readonly PodcastParams StudioWhisper = new()
    {
        AgcTargetDbfs = -16, AgcMaxDb = 27, MudMaxCutDb = 2, EssMaxCutDb = 3, CompThreshDbfs = -26,
        LookaheadMs = 2.0, Dereverb = 0.25, Dialog = 0.15,
    };
    public static readonly PodcastParams StudioMax = new() { LookaheadMs = 2.0, LimReleaseMs = 80, Dereverb = 0.50, Dialog = 0.40 };

    public static PodcastParams ForNoise(NoiseMode mode) => mode switch
    {
        NoiseMode.Strong => Strong,
        NoiseMode.Light => Light,
        _ => Off,
    };

    public static PodcastParams ForStudio(StudioPreset preset) => preset switch
    {
        StudioPreset.Whisper => StudioWhisper,
        StudioPreset.Max => StudioMax,
        _ => StudioBalanced,
    };
}

/// <summary>RBJ cookbook biquad (direct form I) whose coefficients can change mid-stream without resetting state.</summary>
public sealed class DspBiquad
{
    private double _b0 = 1, _b1, _b2, _a1, _a2, _x1, _x2, _y1, _y2;

    public double Process(double x)
    {
        double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
        if (Math.Abs(y) < 1e-25) y = 0; // flush denormals
        _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
        return y;
    }

    public void Reset() { _x1 = _x2 = _y1 = _y2 = 0; }

    private void Set(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0; _a1 = a1 / a0; _a2 = a2 / a0;
    }

    private static double ClampF(double f, int sr) => Math.Clamp(f, 10, 0.45 * sr);

    public void HighPass(int sr, double f, double q)
    {
        double w = 2 * Math.PI * ClampF(f, sr) / sr, c = Math.Cos(w), alpha = Math.Sin(w) / (2 * q);
        Set((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + alpha, -2 * c, 1 - alpha);
    }

    /// <summary>Band-pass with 0 dB peak gain. x + (g − 1)·bp(x) is a peaking cut of g at <paramref name="f"/>.</summary>
    public void BandPass(int sr, double f, double q)
    {
        double w = 2 * Math.PI * ClampF(f, sr) / sr, c = Math.Cos(w), alpha = Math.Sin(w) / (2 * q);
        Set(alpha, 0, -alpha, 1 + alpha, -2 * c, 1 - alpha);
    }

    public void Peaking(int sr, double f, double gainDb, double q)
    {
        double a = Math.Pow(10, gainDb / 40), w = 2 * Math.PI * ClampF(f, sr) / sr, c = Math.Cos(w), alpha = Math.Sin(w) / (2 * q);
        Set(1 + alpha * a, -2 * c, 1 - alpha * a, 1 + alpha / a, -2 * c, 1 - alpha / a);
    }
}

/// <summary>
/// Stages 1–4 of the podcast chain, interleaved <see cref="Channels"/> (1 or 2), in place on doubles in −1..1.
/// Detectors are linked across channels. Allocation-free after construction. Latency: <see cref="LatencyFrames"/>.
/// </summary>
public sealed class PodcastChain
{
    public const int Ctrl = 16;
    private static readonly double OpenOverFloor = Db(9), CloseOverFloor = Db(5), OpenMin = Db(-62), CloseMin = Db(-68), FloorBias = Db(2);

    public int SampleRate { get; }
    public int Channels { get; }
    private PodcastParams _p;
    public PodcastParams Params { get => _p; set { _p = value ?? throw new ArgumentNullException(nameof(value)); Configure(); } }
    public bool AgcEnabled { get; set; } = true;
    public bool DynamicEqEnabled { get; set; } = true;
    public bool CompressorEnabled { get; set; } = true;
    public bool LimiterEnabled { get; set; } = true;
    /// <summary>Runtime AGC cap in dB (the host lowers it when the user gain is high).</summary>
    public double AgcMaxDb { get; set; }

    public int LatencyFrames => LimiterEnabled ? _look - 1 : 0;
    public double LatencyMs => LatencyFrames * 1000.0 / SampleRate;
    public double AgcGainDb => 20 * Math.Log10(Math.Max(_agcG, 1e-9));
    public double CompGainReductionDb => _compGr;
    public double LimiterGainDb => 20 * Math.Log10(Math.Max(_limOut, 1e-9));
    public bool Gated => _gate;
    public double FloorDbfs => 20 * Math.Log10(Math.Max(_floor, 1e-9));

    // gate + AGC
    private double _gateMs, _floor = Db(-54), _agcMs, _agcG = 1;
    private bool _floorKnown, _gate = true, _active;
    private int _below, _hold;
    private readonly double _gateCoeff, _floorDown, _floorUp, _floorUpOpen, _agcMsCoeff, _agcGateRel;
    private readonly int _holdN;
    private double _agcRise, _agcFall;

    // dynamic EQ
    private readonly DspBiquad[] _hp, _mudBp, _essBp;
    private bool _essOn = true;
    private double _broadEnv, _mudEnv, _essEnv, _mudG = 1, _essG = 1, _mudGs = 1, _essGs = 1;
    private readonly double _broadAtk, _broadRel, _mudAtk, _mudRel, _essAtk, _essRel, _eqSmooth;

    // compressor
    private double _compEnv, _grFast, _grSlow, _compGr, _compG = 1, _compGs = 1, _makeup = 1;
    private readonly double _compEnvAtk, _compEnvRel, _grFastAtk, _grFastRel, _grSlowAtk, _grSlowRel, _compSmooth;
    private int _tick;

    // limiter
    private readonly int _lookMax;
    private int _look = 1;
    private readonly double[] _delay, _dqVal, _box;
    private readonly long[] _dqIdx;
    private int _dPos, _dqHead, _dqSize, _boxPos, _boxTick;
    private double _boxSum, _limHeld = 1, _limRel, _limOut = 1, _ceil = Db(-1);
    private long _n;

    public PodcastChain(int sampleRate, int channels = 1, PodcastParams? initial = null)
    {
        if (sampleRate < 8000 || sampleRate > 192000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels < 1 || channels > 2) throw new ArgumentOutOfRangeException(nameof(channels));
        SampleRate = sampleRate; Channels = channels;
        _p = initial ?? new PodcastParams();
        AgcMaxDb = _p.AgcMaxDb;
        _gateCoeff = Coeff(0.040); _floorDown = Coeff(0.30);
        _floorUp = Math.Pow(10, 1.5 / 20.0 / sampleRate); _floorUpOpen = Math.Pow(10, 0.5 / 20.0 / sampleRate);
        _holdN = (int)(0.30 * sampleRate);
        _agcMsCoeff = Coeff(0.20); _agcGateRel = Coeff(0.25);
        _hp = new DspBiquad[channels]; _mudBp = new DspBiquad[channels]; _essBp = new DspBiquad[channels];
        for (int c = 0; c < channels; c++) { _hp[c] = new(); _mudBp[c] = new(); _essBp[c] = new(); }
        _broadAtk = Coeff(0.005); _broadRel = Coeff(0.080); _mudAtk = Coeff(0.010); _mudRel = Coeff(0.100);
        _essAtk = Coeff(0.001); _essRel = Coeff(0.050); _eqSmooth = Coeff(0.002);
        _compEnvAtk = Coeff(0.005); _compEnvRel = Coeff(0.025);
        _grFastAtk = CoeffCtl(0.008); _grFastRel = CoeffCtl(0.080); _grSlowAtk = CoeffCtl(0.400); _grSlowRel = CoeffCtl(0.900);
        _compSmooth = Coeff(0.001);
        _lookMax = (int)(0.005 * sampleRate) + 2;
        _delay = new double[_lookMax * channels]; _dqVal = new double[_lookMax + 1]; _dqIdx = new long[_lookMax + 1]; _box = new double[_lookMax];
        Configure();
        Reset();
    }

    public static double Db(double db) => Math.Pow(10, db / 20.0);
    private double Coeff(double sec) => 1 - Math.Exp(-1.0 / (sec * SampleRate));
    private double CoeffCtl(double sec) => 1 - Math.Exp(-(double)Ctrl / (sec * SampleRate));

    private void Configure()
    {
        var p = _p;
        _agcRise = Coeff(Math.Max(p.AgcRiseSec, 0.01));
        _agcFall = Coeff(Math.Max(p.AgcFallSec, 0.005));
        _essOn = SampleRate >= 16000 && p.EssMaxCutDb > 0;
        for (int c = 0; c < Channels; c++)
        {
            _hp[c].HighPass(SampleRate, p.HpHz, 0.7071067811865476);
            _mudBp[c].BandPass(SampleRate, p.MudHz, 1.0);
            _essBp[c].BandPass(SampleRate, Math.Min(p.EssHz, 0.4 * SampleRate), 1.4);
        }
        _makeup = Db(Math.Clamp(p.CompMakeupDb, 0, 12));
        _ceil = Db(Math.Clamp(p.LimCeilingDbfs, -12, 0));
        _limRel = Coeff(Math.Max(p.LimReleaseMs, 5) / 1000.0);
        int look = Math.Clamp((int)Math.Round(Math.Clamp(p.LookaheadMs, 0, 3) * SampleRate / 1000.0, MidpointRounding.ToEven) + 1, 1, _lookMax);
        if (look != _look) { _look = look; ResetLimiter(); }
    }

    public void Reset()
    {
        _gateMs = 0; _floor = Db(-54); _floorKnown = false; _below = 0; _gate = true; _active = false; _hold = 0;
        double t = Db(_p.AgcTargetDbfs); _agcMs = t * t; _agcG = 1;
        for (int c = 0; c < Channels; c++) { _hp[c].Reset(); _mudBp[c].Reset(); _essBp[c].Reset(); }
        _broadEnv = _mudEnv = _essEnv = 0; _mudG = _essG = _mudGs = _essGs = 1;
        _compEnv = _grFast = _grSlow = _compGr = 0; _compG = _compGs = 1; _tick = 0;
        ResetLimiter();
    }

    private void ResetLimiter()
    {
        Array.Clear(_delay); _dPos = 0; _dqHead = 0; _dqSize = 0;
        Array.Fill(_box, 1.0); _boxPos = 0; _boxSum = _look; _boxTick = 0;
        _limHeld = 1; _limOut = 1;
    }

    /// <summary>In place on <paramref name="frames"/> interleaved frames starting at <paramref name="off"/>. Non-finite input is silence.</summary>
    public void Process(double[] x, int frames, int off = 0)
    {
        var p = _p;
        double target = Db(p.AgcTargetDbfs);
        double maxLin = Db(Math.Max(Math.Min(p.AgcMaxDb, AgcMaxDb), 0));
        double minLin = Db(Math.Min(p.AgcMinDb, 0));
        double noiseCeil = Db(VoiceChain.NoiseCeilingDbfs);
        int ch = Channels;
        for (int f = 0; f < frames; f++)
        {
            int b = off + f * ch;
            double sq = 0;
            for (int c = 0; c < ch; c++)
            {
                double v = x[b + c];
                if (!double.IsFinite(v)) v = 0; else if (v > 4) v = 4; else if (v < -4) v = -4;
                x[b + c] = v;
                if (v * v > sq) sq = v * v;
            }
            GateStep(sq);
            // 1: gated AGC. The gain only moves while the input is clearly above the floor; in pauses, the gate hold
            // and word gaps it is held (and only pulled down to the noise cap), so it cannot pump or lift the room.
            if (AgcEnabled)
            {
                // the minimum-tracked floor reads ~1–1.5 dB under the true room RMS, so compensate before capping
                double cap = _floorKnown ? Math.Clamp(noiseCeil / Math.Max(_floor * FloorBias, 1e-9), 1.0, Math.Max(1.0, maxLin)) : maxLin;
                double want, k;
                if (_active)
                {
                    _agcMs += (sq - _agcMs) * _agcMsCoeff;
                    want = Math.Clamp(target / Math.Max(Math.Sqrt(_agcMs), 1e-5), minLin, Math.Max(minLin, cap));
                    k = want > _agcG ? _agcRise : _agcFall;
                }
                else { want = Math.Min(_agcG, cap); k = _agcGateRel; }
                _agcG += (want - _agcG) * k;
                for (int c = 0; c < ch; c++) x[b + c] *= _agcG;
            }
            // 2: dynamic EQ
            if (DynamicEqEnabled)
            {
                double bSq = 0, mSq = 0, eSq = 0;
                for (int c = 0; c < ch; c++)
                {
                    double h = _hp[c].Process(x[b + c]);
                    double bm = _mudBp[c].Process(h);
                    double be = _essOn ? _essBp[c].Process(h) : 0;
                    x[b + c] = h + (_mudGs - 1) * bm + (_essGs - 1) * be;
                    if (h * h > bSq) bSq = h * h;
                    if (bm * bm > mSq) mSq = bm * bm;
                    if (be * be > eSq) eSq = be * be;
                }
                _broadEnv += (bSq - _broadEnv) * (bSq > _broadEnv ? _broadAtk : _broadRel);
                _mudEnv += (mSq - _mudEnv) * (mSq > _mudEnv ? _mudAtk : _mudRel);
                _essEnv += (eSq - _essEnv) * (eSq > _essEnv ? _essAtk : _essRel);
                _mudGs += (_mudG - _mudGs) * _eqSmooth;
                _essGs += (_essG - _essGs) * _eqSmooth;
            }
            // 3: compressor detector (linked mean square)
            if (CompressorEnabled)
            {
                double cSq = 0;
                for (int c = 0; c < ch; c++) { double v = x[b + c]; if (v * v > cSq) cSq = v * v; }
                _compEnv += (cSq - _compEnv) * (cSq > _compEnv ? _compEnvAtk : _compEnvRel);
            }
            if (++_tick >= Ctrl) { _tick = 0; Control(p); }
            if (CompressorEnabled)
            {
                _compGs += (_compG - _compGs) * _compSmooth;
                double g = _compGs * _makeup;
                for (int c = 0; c < ch; c++) x[b + c] *= g;
            }
            // 4: look-ahead brickwall limiter
            if (LimiterEnabled) LimitFrame(x, b);
            else for (int c = 0; c < ch; c++) x[b + c] = Math.Clamp(x[b + c], -1, 1);
        }
    }

    private void GateStep(double sq)
    {
        _gateMs += (sq - _gateMs) * _gateCoeff;
        double level = Math.Sqrt(Math.Max(_gateMs, 0));
        if (level < _floor)
        {
            _floor += (level - _floor) * _floorDown;
            if (!_floorKnown && ++_below >= SampleRate / 4) _floorKnown = true;
        }
        else _floor *= _gate ? _floorUp : _floorUpOpen;
        if (_floor < 1e-6) _floor = 1e-6; else if (_floor > 0.3) _floor = 0.3;
        double open = Math.Max(_floor * OpenOverFloor, OpenMin), close = Math.Max(_floor * CloseOverFloor, CloseMin);
        _active = level > open;
        if (_active) { _gate = false; _hold = _holdN; }
        else if (level < close) { if (_hold > 0) _hold--; else _gate = true; }
    }

    private void Control(PodcastParams p)
    {
        if (DynamicEqEnabled)
        {
            double bDb = 10 * Math.Log10(_broadEnv + 1e-20), mDb = 10 * Math.Log10(_mudEnv + 1e-20);
            double mudCut = mDb > -70 ? Math.Clamp((mDb - bDb - p.MudThreshDb) * 0.5, 0, Math.Max(p.MudMaxCutDb, 0)) : 0;
            _mudG = Db(-mudCut);
            if (_essOn)
            {
                double eDb = 10 * Math.Log10(_essEnv + 1e-20);
                double cut = eDb > -70 ? Math.Clamp((eDb - bDb - p.EssThreshDb) * 0.75, 0, Math.Max(p.EssMaxCutDb, 0)) : 0;
                _essG = Db(-cut);
            }
            else _essG = 1;
        }
        else { _mudG = 1; _essG = 1; }
        if (CompressorEnabled)
        {
            double level = 10 * Math.Log10(_compEnv + 1e-20);
            double target = -GainComputer(level, p.CompThreshDbfs, Math.Max(p.CompRatio, 1), Math.Max(p.CompKneeDb, 0));
            _grFast += (target - _grFast) * (target > _grFast ? _grFastAtk : _grFastRel);
            // The slow envelope only charges under sustained compression, so short peaks recover fast and dense
            // passages recover slowly (program-dependent release, a variable-mu-like feel).
            _grSlow += (target - _grSlow) * (target > _grSlow ? _grSlowAtk : _grSlowRel);
            _compGr = Math.Max(_grFast, _grSlow);
            _compG = Db(-_compGr);
        }
        else { _compGr = 0; _compG = 1; }
    }

    private void LimitFrame(double[] x, int b)
    {
        int ch = Channels;
        double pk = 0;
        for (int c = 0; c < ch; c++) { double a = Math.Abs(x[b + c]); if (a > pk) pk = a; }
        double r = pk > _ceil ? _ceil / pk : 1;
        // release on the required gain itself: still <= r, so the brickwall guarantee holds
        _limHeld = Math.Min(r, _limHeld + (1 - _limHeld) * _limRel);
        double g;
        if (_look <= 1) g = _limHeld;
        else
        {
            int cap = _dqVal.Length;
            while (_dqSize > 0 && _dqVal[(_dqHead + _dqSize - 1) % cap] >= _limHeld) _dqSize--;
            int t = (_dqHead + _dqSize) % cap;
            _dqVal[t] = _limHeld; _dqIdx[t] = _n; _dqSize++;
            while (_dqIdx[_dqHead] <= _n - _look) { _dqHead = (_dqHead + 1) % cap; _dqSize--; }
            double m = _dqVal[_dqHead];
            // boxcar of length L over the minimum: reaches the needed gain exactly when the peak leaves the delay
            _boxSum += m - _box[_boxPos];
            _box[_boxPos] = m;
            if (++_boxPos >= _look) _boxPos = 0;
            if (++_boxTick >= 4096) { _boxTick = 0; double s = 0; for (int i = 0; i < _look; i++) s += _box[i]; _boxSum = s; }
            g = Math.Min(_boxSum / _look, 1);
        }
        _n++;
        _limOut = g;
        int d = _look - 1;
        for (int c = 0; c < ch; c++)
        {
            double v;
            if (d == 0) v = x[b + c];
            else { int idx = _dPos * ch + c; v = _delay[idx]; _delay[idx] = x[b + c]; }
            x[b + c] = Math.Clamp(v * g, -_ceil, _ceil);
        }
        if (d > 0 && ++_dPos >= d) _dPos = 0;
    }

    /// <summary>Soft-knee static curve: the gain change in dB (≤ 0) for <paramref name="levelDb"/>.</summary>
    public static double GainComputer(double levelDb, double threshDb, double ratio, double kneeDb)
    {
        double over = levelDb - threshDb, slope = 1.0 / ratio - 1.0;
        if (kneeDb > 0 && 2 * Math.Abs(over) <= kneeDb) return slope * (over + kneeDb / 2) * (over + kneeDb / 2) / (2 * kneeDb);
        return over > 0 ? slope * over : 0;
    }
}

/// <summary>
/// Thin true-peak clip guard after noise suppression: 4-tap half-sample interpolation for inter-sample peaks,
/// instant gain reduction, 40 ms release, clamp at the ceiling. Delay: 2 samples.
/// </summary>
public sealed class TruePeakGuard
{
    public const int LatencyFrames = 2;
    private readonly int _ch;
    private readonly double _ceil, _rel;
    private readonly double[] _h, _prevMid, _scratch;
    private double _g = 1;
    public double GainDb => 20 * Math.Log10(Math.Max(_g, 1e-9));

    public TruePeakGuard(int sampleRate, int channels = 1, double ceilingDbfs = -1)
    {
        _ch = channels; _ceil = PodcastChain.Db(ceilingDbfs); _rel = 1 - Math.Exp(-1.0 / (0.040 * sampleRate));
        _h = new double[4 * channels]; _prevMid = new double[channels]; _scratch = new double[channels];
    }

    public void Reset() { Array.Clear(_h); Array.Clear(_prevMid); _g = 1; }

    public void ProcessFrame(double[] x, int b)
    {
        double tp = 0;
        for (int c = 0; c < _ch; c++)
        {
            double v = x[b + c];
            if (!double.IsFinite(v)) v = 0;
            int o = c * 4;
            _h[o] = _h[o + 1]; _h[o + 1] = _h[o + 2]; _h[o + 2] = _h[o + 3]; _h[o + 3] = v;
            double mid = (-_h[o] + 9 * _h[o + 1] + 9 * _h[o + 2] - _h[o + 3]) / 16.0;
            double a = Math.Max(Math.Abs(_h[o + 1]), Math.Max(Math.Abs(mid), Math.Abs(_prevMid[c])));
            _prevMid[c] = mid;
            if (a > tp) tp = a;
        }
        double want = tp > _ceil ? _ceil / tp : 1;
        _g = want < _g ? want : _g + (want - _g) * _rel;
        for (int c = 0; c < _ch; c++) x[b + c] = Math.Clamp(_h[c * 4 + 1] * _g, -_ceil, _ceil);
    }

    public double Process(double x) { _scratch[0] = x; ProcessFrame(_scratch, 0); return _scratch[0]; }
}

/// <summary>
/// Spectral dereverb and dialog-isolation masks for one STFT channel (same math as the Kotlin SpectralVoice).
/// Dereverb: P_late(t) = exp(−2δTd)·P(t − Td), δ = 3 ln 10 / RT60 (0.5 s assumed), Td ≈ 50 ms; Wiener-style gain
/// SRR/(1+SRR) floored by strength. Dialog: frame VAD (voice-band SNR, flatness check, 150 ms hangover) and per-bin
/// speech presence (posterior SNR; local voice-band peaks treated as harmonics) weighted by a voice-band prior.
/// </summary>
public sealed class SpectralVoice
{
    public int Bins { get; }
    public int TdFrames { get; }
    private readonly double _decay, _vadRel;
    private readonly double[] _hist, _prior;
    private readonly int _vbLo, _vbHi;
    private int _histPos, _frames;
    public double Vad { get; private set; }

    public SpectralVoice(int sampleRate, int fftSize, int hop, double rt60Sec = 0.5)
    {
        Bins = fftSize / 2 + 1;
        TdFrames = Math.Max(1, (int)Math.Round(0.050 * sampleRate / hop, MidpointRounding.ToEven));
        double delta = 3 * Math.Log(10) / rt60Sec;
        _decay = Math.Exp(-2 * delta * TdFrames * hop / (double)sampleRate);
        _hist = new double[(TdFrames + 1) * Bins];
        _prior = new double[Bins];
        double df = sampleRate / (double)fftSize;
        for (int k = 0; k < Bins; k++)
        {
            double f = k * df;
            _prior[k] = f < 70 ? 0.3 : f <= 8000 ? 1.0 : f <= 10000 ? 1.0 - 0.5 * (f - 8000) / 2000 : 0.5;
        }
        _vbLo = Math.Max(1, (int)Math.Round(100 / df, MidpointRounding.ToEven));
        _vbHi = Math.Min(Bins - 2, Math.Max(_vbLo + 1, (int)Math.Round(4000 / df, MidpointRounding.ToEven)));
        _vadRel = Math.Exp(-hop / (0.150 * sampleRate));
    }

    public void Reset() { Array.Clear(_hist); _histPos = 0; _frames = 0; Vad = 0; }

    public void Compute(double[] psd, double[] mag, double[] noise, double dereverb, double dialog, double[] output)
    {
        double dr = Math.Clamp(dereverb, 0, 1), dl = Math.Clamp(dialog, 0, 1);
        double derevFloor = PodcastChain.Db(-12 * dr), dialogFloor = PodcastChain.Db(-10 * dl);
        if (dl > 0)
        {
            double s = 0, nz = 0, logSum = 0, linSum = 0;
            for (int k = _vbLo; k <= _vbHi; k++)
            {
                s += psd[k]; nz += noise[k];
                double m = mag[k] + 1e-12;
                logSum += Math.Log(m); linSum += m;
            }
            double cnt = _vbHi - _vbLo + 1;
            double flat = Math.Exp(logSum / cnt) / (linSum / cnt);
            double snrDb = nz > 0 ? 10 * Math.Log10(s / nz + 1e-12) : 0;
            double pf = 1.0 / (1.0 + Math.Exp(-(snrDb - 5.0) / 1.5));
            if (flat > 0.65) pf *= 0.5;
            Vad = pf > Vad ? pf : Vad * _vadRel + pf * (1 - _vadRel);
        }
        int n1 = TdFrames + 1;
        int past = ((_histPos - TdFrames) % n1 + n1) % n1;
        bool haveLate = _frames > TdFrames;
        for (int k = 0; k < Bins; k++)
        {
            double g = 1;
            if (dr > 0 && haveLate)
            {
                double late = _decay * _hist[past * Bins + k];
                if (late > 1e-20)
                {
                    double srr = Math.Max(psd[k] - late, 0) / late;
                    g = Math.Max(srr / (1 + srr), derevFloor);
                }
            }
            if (dl > 0)
            {
                double nz = noise[k];
                double post = nz > 1e-20 ? mag[k] * mag[k] / nz : 100;
                double spp = Math.Clamp(10 * Math.Log10(post + 1e-12) / 9.0, 0, 1);
                if (k >= _vbLo && k <= _vbHi && post > 2.0 && mag[k] >= mag[k - 1] && mag[k] >= mag[k + 1]) spp = Math.Max(spp, 0.9);
                double speech = Math.Max(spp, Vad * _prior[k]);
                g *= dialogFloor + (1 - dialogFloor) * speech;
            }
            output[k] = g;
        }
        Array.Copy(psd, 0, _hist, _histPos * Bins, Bins);
        _histPos = (_histPos + 1) % n1;
        _frames++;
    }
}

/// <summary>
/// Real-time CPU watchdog: feed the wall time each block took. Once the smoothed share of real time stays above the
/// budget for 1.5 s (after a 1 s warm-up), <see cref="Tripped"/> latches and the host drops dereverb / dialog.
/// </summary>
public sealed class CpuGuard
{
    private readonly int _sr;
    private readonly double _budget;
    private long _seen, _over;
    public double Load { get; private set; }
    public bool Tripped { get; private set; }
    public CpuGuard(int sampleRate, double budget = 0.30) { _sr = sampleRate; _budget = budget; }
    public void Reset() { Load = 0; Tripped = false; _seen = 0; _over = 0; }

    public void Record(long nanos, int frames)
    {
        if (frames <= 0 || Tripped) return;
        _seen += frames;
        if (_seen < _sr) return;
        double share = nanos / (frames * 1e9 / _sr);
        double a = Math.Min(1.0, frames / (_sr * 0.5));
        Load += (share - Load) * a;
        if (Load > _budget) { _over += frames; if (_over >= _sr * 3L / 2) Tripped = true; }
        else _over = 0;
    }
}
