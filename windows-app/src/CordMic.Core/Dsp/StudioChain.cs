namespace CordMic.Core.Dsp;

/// <summary>
/// PC-side Studio noise reduction for one 48 kHz mono stream, 480-sample frames (0.6.0 spec §3–4):
/// RNNoise in parallel with a PC instance of the 0.5.7 Strong suppressor, WhisperGuard mix (Balanced / Whisper)
/// or the Max blend, then the B3 loudness AGC and the −1 dBFS limiter, then (0.6.2) a comfort-noise floor (at most −65 dBFS). The input and the Strong output are delayed
/// to line up with RNNoise. Allocation-free after construction; not thread-safe (one worker thread owns it).
/// </summary>
public sealed class StudioChain : IDisposable
{
    public const int Frame = 480;
    /// <summary>RNNoise input-to-output delay with 480-sample framing, in samples (asserted by a unit test).</summary>
    public const int RnnoiseDelay = 960;
    /// <summary>
    /// Input-to-output delay of the 0.5.7 Strong suppressor (<see cref="VoiceChain"/>, 256-point STFT, hop 128): one full
    /// FFT block, 256 samples (asserted by a unit test). <see cref="VoiceChain.NoiseDelayFrames"/> reports one hop.
    /// </summary>
    public const int StrongDelay = 256;

    private readonly Rnnoise _rn;
    private readonly VoiceChain _strong;
    private readonly WhisperGuard? _guard;
    private readonly AgcB3 _agc;
    private readonly ComfortNoise _cn = new();
    private readonly float[] _r = new float[Frame], _xd = new float[Frame], _kd = new float[Frame], _y = new float[Frame];
    private readonly short[] _ks = new short[Frame];
    private readonly DelayLine _dx = new(RnnoiseDelay), _dk = new(RnnoiseDelay - StrongDelay);
    // 1.1.0 podcast voice chain: AGC, dynamic EQ, compressor and look-ahead limiter, then the spectral dereverb /
    // dialog masks, all before RNNoise; a true-peak guard after the comfort floor. B3 auto volume is not stacked on top.
    private readonly PodcastChain? _pod;
    private readonly VoiceChain? _spectral;
    private readonly TruePeakGuard? _tpg;
    private readonly double[] _pd = new double[Frame];
    private readonly short[] _ps = new short[Frame];

    /// <summary>True when the 1.1.0 podcast voice chain runs in front of RNNoise.</summary>
    public bool VoiceChainOn { get; }
    public PodcastChain? Podcast => _pod;
    /// <summary>Samples the podcast chain adds in front of RNNoise (look-ahead + STFT block + guards); 0 when off.</summary>
    public int VoiceChainDelay => VoiceChainOn ? _pod!.LatencyFrames + _spectral!.ChainDelayFrames + TruePeakGuard.LatencyFrames : 0;
    /// <summary>Added latency of the whole chain (RNNoise look-ahead plus the podcast stages), in ms.</summary>
    public double ChainLatencyMs => (RnnoiseDelay + VoiceChainDelay) / 48.0;

    public StudioPreset Preset { get; }
    public AgcB3 Agc => _agc;
    public ComfortNoise Comfort => _cn;
    /// <summary>Comfort floor on (default; settable for evaluation).</summary>
    public bool ComfortEnabled { get; set; } = true;
    /// <summary>Comfort level relative to the running output speech level, in dB.</summary>
    public const double ComfortBelowSpeechDb = 55, ComfortInitialDbfs = -75, ComfortBelowRoomDb = 10;
    private double _inFloorDb = double.NaN, _inMin = double.MaxValue;
    private int _inFrames;
    /// <summary>Minimum-statistics floor of the raw input (non-silent frames), dBFS; NaN until measured.</summary>
    public double InputFloorDbfs => _inFloorDb;

    private void TrackInputFloor(ReadOnlySpan<float> x)
    {
        double p = 0;
        for (int i = 0; i < x.Length; i++) p += x[i] * (double)x[i];
        double l = 10 * Math.Log10(p / x.Length + 1e-20);
        if (l < -100) return; // digital silence is not a room floor
        if (_inFrames < 30) { _inFrames++; _inMin = Math.Min(_inMin, l); _inFloorDb = _inMin; return; }
        _inFloorDb = l < _inFloorDb ? l : _inFloorDb + 0.03; // falls at once, rises 3 dB/s
    }
    /// <summary>Comfort offset in use (default <see cref="ComfortBelowSpeechDb"/>; settable for evaluation).</summary>
    public double ComfortBelowDbUsed { get; set; } = ComfortBelowSpeechDb;
    public WhisperGuard? Guard => _guard;
    public double LastVad { get; private set; }
    /// <summary>Native RNNoise frames run so far (one per <see cref="Process"/> call; used by the CPU-budget test).</summary>
    public long RnnoiseFrames => _rn.NativeFrames;
    /// <summary>0.5.7 Strong suppressor frames run so far (Balanced/Whisper: one per call; Max: none).</summary>
    public long StrongFrames { get; private set; }

    /// <summary>Added latency of the chain itself (RNNoise look-ahead), in ms. The worker adds one more frame.</summary>
    public static double LatencyMs => RnnoiseDelay / 48.0;

    /// <param name="voiceChain">1.1.0 podcast voice chain in front of RNNoise (the app passes the user setting, default on).</param>
    public StudioChain(StudioPreset preset, bool autoVolume, double startGainDb = AgcB3.DefaultStartGainDb, bool voiceChain = false)
    {
        if (preset == StudioPreset.Off) throw new ArgumentException("preset");
        Preset = preset;
        _rn = new Rnnoise();
        _strong = new VoiceChain(48000) { Mode = NoiseMode.Strong, Agc = false, Expander = NoiseMode.Off, VoiceClarity = false, GainDb = 0 };
        _guard = preset == StudioPreset.Max ? null : WhisperGuard.ForPreset(preset);
        VoiceChainOn = voiceChain;
        if (voiceChain)
        {
            var p = PodcastPresets.ForStudio(preset);
            _pod = new PodcastChain(48000, 1, p) { AgcEnabled = autoVolume };
            _spectral = new VoiceChain(48000)
            {
                Mode = NoiseMode.Off, Agc = false, Expander = NoiseMode.Off, VoiceClarity = false, GainDb = 0,
                Podcast = true, PodcastSpectralOnly = true, PodcastParams = p,
            };
            _tpg = new TruePeakGuard(48000, 1, VoiceChain.CeilingDbfs);
            // the podcast AGC levels the voice before RNNoise; B3 only meters loudness (comfort floor) at 0 dB
            _agc = new AgcB3(0) { Enabled = false };
        }
        else _agc = new AgcB3(autoVolume ? startGainDb : 0) { Enabled = autoVolume };
    }

    /// <summary>Processes one frame in place.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public void Process(Span<float> x)
    {
        if (x.Length != Frame) throw new ArgumentException("frame length", nameof(x));
        TrackInputFloor(x);
        if (VoiceChainOn)
        {
            for (int i = 0; i < Frame; i++) _pd[i] = x[i];
            _pod!.Process(_pd, Frame);
            for (int i = 0; i < Frame; i++) _ps[i] = (short)Math.Clamp((int)Math.Round(_pd[i] * 32768.0), short.MinValue, short.MaxValue);
            _spectral!.Process(_ps);
            for (int i = 0; i < Frame; i++) x[i] = _ps[i] / 32768f;
        }
        LastVad = _rn.Process(x, _r);
        if (_guard is not null)
        {
            for (int i = 0; i < Frame; i++) _ks[i] = (short)Math.Clamp((int)Math.Round(x[i] * 32768.0), short.MinValue, short.MaxValue);
            _strong.Process(_ks);
            StrongFrames++;
            for (int i = 0; i < Frame; i++) _kd[i] = _ks[i] / 32768f;
            _dk.Process(_kd);
        }
        x.CopyTo(_xd);
        _dx.Process(_xd);
        if (_guard is not null) _guard.Process(_xd, _r, _kd, _y, LastVad);
        else for (int i = 0; i < Frame; i++) _y[i] = 0.9f * _r[i] + 0.1f * _xd[i];
        _agc.Process(_y, LastVad);
        if (ComfortEnabled)
        {
            // Comfort level: 55 dB below the output speech loudness, at most −65 dBFS (about −76 at the −21 LUFS Auto
            // volume target; 45 dB cost about 0.2 PESQ in quiet rooms), and never more than the room's own noise floor (input floor + gain) minus 10 dB, so a quiet
            // room is not made noisier. At least −90 dBFS. It never follows the pause itself, so it cannot pump.
            double sp = _agc.OutputLoudnessLufs;
            double lvl = double.IsNaN(sp) ? ComfortInitialDbfs : sp - ComfortBelowDbUsed;
            if (!double.IsNaN(_inFloorDb)) lvl = Math.Min(lvl, _inFloorDb + _agc.GainDb - ComfortBelowRoomDb);
            lvl = Math.Clamp(lvl, -90, ComfortNoise.DefaultDbfs);
            _cn.SetLevel(lvl);
            _cn.AddTo(_y);
        }
        if (_tpg is not null) for (int i = 0; i < Frame; i++) _y[i] = (float)_tpg.Process(_y[i]);
        _y.CopyTo(x);
    }

    public void Dispose() => _rn.Dispose();
}

/// <summary>Fixed integer delay applied in place to successive blocks. Allocation-free.</summary>
public sealed class DelayLine
{
    private readonly float[] _buf;
    private int _pos;
    public int Delay { get; }
    public DelayLine(int delay) { if (delay < 0) throw new ArgumentOutOfRangeException(nameof(delay)); Delay = delay; _buf = new float[Math.Max(1, delay)]; }
    public void Process(Span<float> x)
    {
        if (Delay == 0) return;
        for (int i = 0; i < x.Length; i++)
        {
            float v = _buf[_pos]; _buf[_pos] = x[i]; x[i] = v;
            if (++_pos == Delay) _pos = 0;
        }
    }
}
