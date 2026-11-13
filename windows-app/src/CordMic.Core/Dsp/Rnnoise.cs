using System.Runtime.InteropServices;

namespace CordMic.Core.Dsp;

/// <summary>
/// P/Invoke wrapper for the native RNNoise library (BSD-3-Clause, third_party/rnnoise). 48 kHz mono only,
/// 480-sample frames, input and output in 16-bit scale floats. One instance per stream; not thread-safe.
/// </summary>
public sealed class Rnnoise : IDisposable
{
    public const int FrameSize = 480;
    /// <summary>Native frames processed (deterministic work accounting for tests).</summary>
    public long NativeFrames { get; private set; }
    private const string Lib = "rnnoise";
    private IntPtr _st;
    private readonly float[] _in = new float[FrameSize];
    private readonly float[] _out = new float[FrameSize];

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr rnnoise_create(IntPtr model);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern void rnnoise_destroy(IntPtr st);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int rnnoise_get_frame_size();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern unsafe float rnnoise_process_frame(IntPtr st, float* outp, float* inp);

    private static readonly Lazy<string?> LoadError = new(() =>
    {
        try { return rnnoise_get_frame_size() == FrameSize ? null : "unexpected RNNoise frame size"; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return e.Message; }
    });

    /// <summary>True when the native library loads and reports 480-sample frames.</summary>
    public static bool IsAvailable => LoadError.Value is null;
    public static string? UnavailableReason => LoadError.Value;

    public Rnnoise()
    {
        if (!IsAvailable) throw new InvalidOperationException("RNNoise unavailable: " + UnavailableReason);
        _st = rnnoise_create(IntPtr.Zero);
        if (_st == IntPtr.Zero) throw new InvalidOperationException("rnnoise_create failed");
    }

    /// <summary>Denoises one frame (input in −1..1). Returns RNNoise's voice-activity probability for the frame.</summary>
    public unsafe float Process(ReadOnlySpan<float> input, Span<float> output)
    {
        if (input.Length != FrameSize || output.Length != FrameSize) throw new ArgumentException("RNNoise needs 480-sample frames");
        if (_st == IntPtr.Zero) throw new ObjectDisposedException(nameof(Rnnoise));
        for (int i = 0; i < FrameSize; i++) _in[i] = input[i] * 32768f;
        float vad;
        fixed (float* pi = _in) fixed (float* po = _out) vad = rnnoise_process_frame(_st, po, pi);
        NativeFrames++;
        for (int i = 0; i < FrameSize; i++) output[i] = _out[i] * (1f / 32768f);
        return vad;
    }

    public void Dispose()
    {
        if (_st != IntPtr.Zero) { rnnoise_destroy(_st); _st = IntPtr.Zero; }
    }
}
