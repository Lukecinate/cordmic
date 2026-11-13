namespace CordMic.Core.Dsp;

/// <summary>
/// PC-side Studio noise reduction for one 48 kHz mono stream, 480-sample frames (0.6.0 spec §3–4):
/// RNNoise in parallel with a PC instance of the 0.5.7 Strong suppressor, WhisperGuard mix (Balanced / Whisper)
/// or the Max blend, then the B3 loudness AGC and the −1 dBFS limiter, then (0.6.2) a −65 dBFS comfort-noise floor. The input and the Strong output are delayed
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

    public StudioPreset Preset { get; }
    public AgcB3 Agc => _agc;
    public ComfortNoise Comfort => _cn;
    /// <summary>Comfort floor on (default; settable for evaluation).</summary>
    public bool ComfortEnabled { get; set; } = true;
    /// <summary>Comfort level relative to the running output speech level, in dB.</summary>
    public const double ComfortBelowSpeechDb = 45, ComfortInitialDbfs = -75;
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

    public StudioChain(StudioPreset preset, bool autoVolume, double startGainDb = AgcB3.DefaultStartGainDb)
    {
        if (preset == StudioPreset.Off) throw new ArgumentException("preset");
        Preset = preset;
        _rn = new Rnnoise();
        _strong = new VoiceChain(48000) { Mode = NoiseMode.Strong, Agc = false, Expander = NoiseMode.Off, VoiceClarity = false, GainDb = 0 };
        _guard = preset == StudioPreset.Max ? null : WhisperGuard.ForPreset(preset);
        _agc = new AgcB3(autoVolume ? startGainDb : 0) { Enabled = autoVolume };
    }

    /// <summary>Processes one frame in place.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public void Process(Span<float> x)
    {
        if (x.Length != Frame) throw new ArgumentException("frame length", nameof(x));
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
            // Comfort level follows the output speech loudness: 45 dB below it, at most −65 dBFS (Auto volume at −21 LUFS
            // gives about −66), at least −90 dBFS. It never depends on the pause itself, so it cannot pump.
            double sp = _agc.OutputLoudnessLufs;
            double lvl = double.IsNaN(sp) ? ComfortInitialDbfs : Math.Clamp(sp - ComfortBelowDbUsed, -90, ComfortNoise.DefaultDbfs);
            _cn.SetLevel(lvl);
            _cn.AddTo(_y);
        }
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
