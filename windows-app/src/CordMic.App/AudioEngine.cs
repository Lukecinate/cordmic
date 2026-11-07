using System.Runtime.InteropServices;
using CordMic.Core;

namespace CordMic.App;

internal sealed record RenderDevice(string Id, string Name);

/// <summary>Pulls mono s16 48 kHz from the receiver and up-mixes to the output channel count. The read buffer is reused.</summary>
internal sealed class ReceiverWaveProvider
{
    private readonly Receiver _rx;
    private readonly int _ch;
    private byte[] _mono;
    private bool _boosted;
    public int SampleRate => 48000;
    public int Channels => _ch;

    public ReceiverWaveProvider(Receiver rx, int channels)
    {
        _rx = rx;
        _ch = Math.Clamp(channels, 1, 2);
        _mono = new byte[48000 / 5 * 2]; // 200 ms, grown once if a device asks for more
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        if (!_boosted) { Native.BoostAudioThread(); _boosted = true; }
        int frames = count / (2 * _ch);
        int monoBytes = frames * 2;
        if (_mono.Length < monoBytes) _mono = new byte[monoBytes];
        var mono = _mono.AsSpan(0, monoBytes);
        _rx.Pull(mono);
        if (_ch == 1) { mono.CopyTo(buffer.AsSpan(offset, monoBytes)); return monoBytes; }
        var dst = buffer.AsSpan(offset, frames * 4);
        for (int i = 0, j = 0; i < monoBytes; i += 2, j += 4)
        {
            dst[j] = mono[i]; dst[j + 1] = mono[i + 1];
            dst[j + 2] = mono[i]; dst[j + 3] = mono[i + 1];
        }
        return frames * 4;
    }
}

/// <summary>
/// Event-driven render on an MMCSS "Pro Audio" thread.
/// Low latency asks IAudioClient3 for the engine's minimum shared period and then writes at most one
/// period per callback, keeping a couple of milliseconds already queued so a late wake does not glitch.
/// 0.4.2 wrote (period + margin − padding), so a late wake pulled more than the jitter target held.
/// </summary>
internal sealed class LowLatencyRenderer : IDisposable
{
    private readonly IMMDevice _dev;
    private readonly ReceiverWaveProvider _src;
    private readonly OutputMode _mode;
    private IAudioClient3? _client;
    private IAudioRenderClient? _render;
    private EventWaitHandle? _evt;
    private Thread? _thread;
    private volatile bool _stop;
    private uint _bufferFrames, _periodFrames, _marginFrames;
    private int _bytesPerFrame, _rate, _outCh;
    private bool _outFloat;
    private readonly Action<string>? _log;
    private byte[] _tmp = Array.Empty<byte>();
    private double _fillSum, _fillWeight;
    private readonly object _statLock = new();
    public bool UsedAudioClient3 { get; private set; }
    public uint EngineMinPeriodFrames { get; private set; }
    public double EngineMinMs => _rate == 0 || EngineMinPeriodFrames == 0 ? 0 : EngineMinPeriodFrames * 1000.0 / _rate;

    public bool Running => _thread is { IsAlive: true } && !_stop;
    public double PeriodMs => _rate == 0 ? 0 : _periodFrames * 1000.0 / _rate;
    public double BufferMs => _rate == 0 ? 0 : _bufferFrames * 1000.0 / _rate;
    public OutputMode Mode => _mode;
    public event Action<string>? Failed;

    public LowLatencyRenderer(IMMDevice dev, ReceiverWaveProvider src, OutputMode mode, Action<string>? log = null)
    {
        _dev = dev; _src = src; _mode = mode; _log = log;
        _rate = src.SampleRate;
        _outCh = src.Channels;
        _bytesPerFrame = src.Channels * 2;
        var iid = Wasapi.IidAudioClient3;
        Wasapi.Check(dev.Activate(ref iid, Wasapi.ClsCtxAll, IntPtr.Zero, out var obj), "Activate");
        _client = (IAudioClient3)obj;
        var fmt = WaveFormatEx.Pcm(_rate, src.Channels);
        int fmtBytes = Marshal.SizeOf<WaveFormatEx>();
        IntPtr pFmt = Marshal.AllocHGlobal(fmtBytes);
        try
        {
            Marshal.StructureToPtr(fmt, pFmt, false);
            if (mode == OutputMode.Exclusive) InitExclusive(pFmt, fmt);
            else InitShared(pFmt, fmt, mode != OutputMode.Stable, log);
            _evt = new EventWaitHandle(false, EventResetMode.AutoReset);
            Wasapi.Check(_client.SetEventHandle(_evt.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
            var renderId = Wasapi.IidAudioRenderClient;
            Wasapi.Check(_client.GetService(ref renderId, out var r), "GetService");
            _render = (IAudioRenderClient)r;
            int need = (int)_bufferFrames * Math.Max(_bytesPerFrame, src.Channels * 2);
            _tmp = new byte[need];
        }
        catch
        {
            TryRelease(_client);
            _client = null;
            throw;
        }
        finally { Marshal.FreeHGlobal(pFmt); }
    }

    private void InitShared(IntPtr pOur, WaveFormatEx our, bool lowLatency, Action<string>? log)
    {
        var client = _client!;
        uint ev = Wasapi.StreamEventCallback;
        uint convert = Wasapi.StreamAutoConvertPcm | Wasapi.StreamSrcDefaultQuality;
        uint period = 0;
        IntPtr pMix = IntPtr.Zero;
        bool mixUsable = false;
        int mixCh = 0, mixRate = 0, mixBits = 0;
        bool mixFloat = false;
        if (client.GetMixFormat(out pMix) >= 0 && pMix != IntPtr.Zero)
            mixUsable = TryParseMix(pMix, out mixCh, out mixRate, out mixBits, out mixFloat)
                        && mixRate == _rate && mixCh is 1 or 2
                        && ((mixFloat && mixBits == 32) || (!mixFloat && mixBits == 16));
        try
        {
            if (lowLatency)
            {
                NoteEnginePeriod(client, mixUsable ? pMix : pOur, log, mixUsable ? "mix" : "pcm16");
                if (mixUsable)
                    TryClient3(ref client, pMix, ev, log, "mix/no-convert", out period, mixCh, mixFloat);
                if (!UsedAudioClient3)
                    TryClient3(ref client, pOur, ev, log, "pcm16/no-convert", out period, our.Channels, false);
                if (!UsedAudioClient3)
                    TryClient3(ref client, pOur, ev | convert, log, "pcm16/convert", out period, our.Channels, false);
            }
            if (!UsedAudioClient3)
            {
                uint flags = ev | convert;
                Wasapi.Check(client.GetDevicePeriod(out long def100ns, out _), "GetDevicePeriod");
                if (def100ns <= 0) def100ns = 100_000;
                long dur = lowLatency ? def100ns * 2 : def100ns * 3;
                int hr = client.Initialize(Wasapi.ShareShared, flags, dur, 0, pOur, IntPtr.Zero);
                if (hr < 0)
                {
                    log?.Invoke($"IAudioClient.Initialize flags=0x{flags:X} hr=0x{(uint)hr:X8}");
                    Wasapi.Check(hr, "Initialize");
                }
                SetOutputLayout(our.Channels, ieeeFloat: false);
                period = (uint)Math.Max(1, (int)Math.Round(def100ns / 1e7 * our.SamplesPerSec));
            }
        }
        finally
        {
            if (pMix != IntPtr.Zero) Wasapi.CoTaskMemFree(pMix);
        }
        Wasapi.Check(client.GetBufferSize(out _bufferFrames), "GetBufferSize");
        _periodFrames = period == 0 ? _bufferFrames : period;
        _marginFrames = (uint)Math.Max(1, (int)Math.Round(0.002 * _rate));
    }

    void NoteEnginePeriod(IAudioClient3 client, IntPtr fmt, Action<string>? log, string tag)
    {
        if (client.GetSharedModeEnginePeriod(fmt, out uint def, out _, out uint min, out _) < 0 || min == 0) return;
        if (EngineMinPeriodFrames == 0 || min < EngineMinPeriodFrames) EngineMinPeriodFrames = min;
        log?.Invoke($"IAudioClient3 {tag} engine default={def} min={min} ({min * 1000.0 / _rate:F1} ms)");
    }

    bool TryClient3(ref IAudioClient3 client, IntPtr fmt, uint flags, Action<string>? log, string tag, out uint period, int ch, bool ieeeFloat)
    {
        period = 0;
        if (client.GetSharedModeEnginePeriod(fmt, out uint def, out _, out uint min, out _) < 0 || min == 0)
            return false;
        if (EngineMinPeriodFrames == 0 || min < EngineMinPeriodFrames) EngineMinPeriodFrames = min;
        foreach (var (p, which) in new[] { (min, "min"), (def, "default") })
        {
            if (p == 0) continue;
            if (which == "default" && p == min) continue;
            int hr = client.InitializeSharedAudioStream(flags, p, fmt, IntPtr.Zero);
            if (hr >= 0)
            {
                UsedAudioClient3 = true;
                period = p;
                SetOutputLayout(ch, ieeeFloat);
                log?.Invoke($"IAudioClient3 {tag}/{which} period={p} ({p * 1000.0 / _rate:F1} ms) ok");
                return true;
            }
            log?.Invoke($"IAudioClient3 {tag}/{which} period={p} ({p * 1000.0 / _rate:F1} ms) flags=0x{flags:X} hr=0x{(uint)hr:X8}");
            ReplaceClient();
            client = _client!;
        }
        return false;
    }

    void SetOutputLayout(int ch, bool ieeeFloat)
    {
        _outCh = Math.Max(1, ch);
        _outFloat = ieeeFloat;
        _bytesPerFrame = _outCh * (ieeeFloat ? 4 : 2);
    }

    static bool TryParseMix(IntPtr p, out int ch, out int rate, out int bits, out bool ieeeFloat)
    {
        ch = rate = bits = 0; ieeeFloat = false;
        if (p == IntPtr.Zero) return false;
        var head = new byte[40];
        Marshal.Copy(p, head, 0, 18);
        var wf = Marshal.PtrToStructure<WaveFormatEx>(p);
        int n = Math.Min(40, 18 + wf.Size);
        if (n > 18) Marshal.Copy(p, head, 0, n);
        return WaveFormatBlob.TryParse(head.AsSpan(0, Math.Max(16, n)), out ch, out rate, out bits, out ieeeFloat);
    }

    private void ReplaceClient()
    {
        TryRelease(_client);
        var iid = Wasapi.IidAudioClient3;
        Wasapi.Check(_dev.Activate(ref iid, Wasapi.ClsCtxAll, IntPtr.Zero, out var obj), "Activate");
        _client = (IAudioClient3)obj;
    }

    private void InitExclusive(IntPtr pFmt, WaveFormatEx fmt)
    {
        var client = _client!;
        Wasapi.Check(client.GetDevicePeriod(out _, out long min100ns), "GetDevicePeriod");
        if (min100ns < 30_000) min100ns = 30_000;
        int hr = client.Initialize(Wasapi.ShareExclusive, Wasapi.StreamEventCallback, min100ns, min100ns, pFmt, IntPtr.Zero);
        if (hr == Wasapi.AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED)
        {
            Wasapi.Check(client.GetBufferSize(out uint aligned), "GetBufferSize");
            TryRelease(client);
            var iid = Wasapi.IidAudioClient3;
            Wasapi.Check(_dev.Activate(ref iid, Wasapi.ClsCtxAll, IntPtr.Zero, out var obj), "Activate");
            _client = client = (IAudioClient3)obj;
            long period = (long)(1e7 / fmt.SamplesPerSec * aligned + 0.5);
            Wasapi.Check(client.Initialize(Wasapi.ShareExclusive, Wasapi.StreamEventCallback, period, period, pFmt, IntPtr.Zero), "Initialize");
        }
        else Wasapi.Check(hr, "Initialize");
        Wasapi.Check(client.GetBufferSize(out _bufferFrames), "GetBufferSize");
        _periodFrames = _bufferFrames;
        _marginFrames = 0;
    }

    public void Start()
    {
        _stop = false;
        _thread = new Thread(Loop) { Name = "cordmic-render", IsBackground = true, Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    public double TakeAvgFillMs()
    {
        lock (_statLock)
        {
            if (_fillWeight <= 0) return double.NaN;
            double ms = _fillSum / _fillWeight * 1000.0 / _rate;
            _fillSum = 0; _fillWeight = 0;
            return ms;
        }
    }

    private void Write(uint frames, bool silent)
    {
        var render = _render!;
        Wasapi.Check(render.GetBuffer(frames, out var ptr), "GetBuffer");
        if (!silent)
        {
            int srcBytes = (int)frames * _src.Channels * 2;
            int outBytes = (int)frames * _bytesPerFrame;
            if (_tmp.Length < Math.Max(srcBytes, outBytes))
                _tmp = new byte[Math.Max(srcBytes, outBytes)];
            int got = _src.Read(_tmp, 0, srcBytes);
            if (got < srcBytes) Array.Clear(_tmp, got, srcBytes - got);
            if (_outFloat)
                PcmConvert.ExpandS16ToF32(_tmp, (int)frames, _src.Channels, _outCh);
            Marshal.Copy(_tmp, 0, ptr, outBytes);
        }
        Wasapi.Check(render.ReleaseBuffer(frames, silent ? Wasapi.BufferSilent : 0), "ReleaseBuffer");
    }

    private void Loop()
    {
        Exception? err = null;
        var client = _client!;
        try
        {
            Native.BoostAudioThread();
            bool exclusive = _mode == OutputMode.Exclusive;
            uint prime = exclusive ? _bufferFrames : Math.Min(_bufferFrames, _periodFrames + _marginFrames);
            if (prime > 0) Write(prime, silent: true);
            Wasapi.Check(client.Start(), "Start");
            while (!_stop)
            {
                if (!_evt!.WaitOne(200)) continue;
                if (_stop) break;
                uint frames;
                uint padding = 0;
                if (exclusive) frames = _bufferFrames;
                else
                {
                    Wasapi.Check(client.GetCurrentPadding(out padding), "GetCurrentPadding");
                    uint room = _bufferFrames > padding ? _bufferFrames - padding : 0;
                    if (room == 0) continue;
                    if (_mode == OutputMode.Stable)
                        frames = room;
                    else
                    {
                        uint target = Math.Min(_bufferFrames, _periodFrames + _marginFrames);
                        uint want = padding >= target ? 0 : target - padding;
                        if (want > _periodFrames) want = _periodFrames;
                        frames = Math.Min(want, room);
                    }
                }
                if (frames == 0) continue;
                Write(frames, silent: false);
                double level = exclusive ? _bufferFrames : (padding + frames) / 2.0;
                lock (_statLock) { _fillSum += level; _fillWeight += 1; }
            }
        }
        catch (Exception e) { err = e; }
        finally { try { client.Stop(); } catch (Exception) { } }
        if (err is not null && !_stop) Failed?.Invoke(err.Message);
    }

    public void Dispose()
    {
        _stop = true;
        try { _evt?.Set(); } catch (Exception) { }
        _thread?.Join(1000);
        TryRelease(_render);
        TryRelease(_client);
        _render = null; _client = null;
        try { _evt?.Dispose(); } catch (Exception) { }
    }

    private static void TryRelease(object? com)
    {
        if (com is null) return;
        try { Marshal.ReleaseComObject(com); } catch (Exception) { }
    }
}

/// <summary>Owns the render endpoint. There is no NAudio fallback: shared, low-latency shared and exclusive are all this loop.</summary>
internal sealed class AudioEngine : IDisposable
{
    private readonly MMDeviceEnumeratorCom _enumObj = Wasapi.CreateEnumerator();
    private readonly IMMDeviceEnumerator _enum;
    private readonly WasapiNotifications _notes = new();
    private LowLatencyRenderer? _ll;
    private volatile bool _devicesDirty = true;
    private double _lastFillMs = double.NaN;

    public string? ActiveId { get; private set; }
    public string? ActiveName { get; private set; }
    public string? LastError { get; private set; }
    public OutputMode? ActiveMode { get; private set; }
    public string Describe { get; private set; } = "";
    public int LatencyMs { get; private set; }
    public double PeriodMs => _ll?.PeriodMs ?? 0;
    public double EngineMinMs => _ll?.EngineMinMs ?? 0;
    public bool UsedAudioClient3 => _ll?.UsedAudioClient3 ?? false;
    public bool Playing => _ll?.Running == true;
    public event Action<string>? Failed;

    public AudioEngine()
    {
        _enum = (IMMDeviceEnumerator)_enumObj;
        _notes.Changed += () => _devicesDirty = true;
        try { _enum.RegisterEndpointNotificationCallback(_notes); } catch (Exception) { }
    }

    public bool ConsumeDevicesDirty()
    {
        if (!_devicesDirty) return false;
        _devicesDirty = false;
        return true;
    }

    public List<RenderDevice> ListRender() => WasapiDevices.ListRender(_enum);

    public double OutputMs()
    {
        if (_ll is { } r)
        {
            var v = r.TakeAvgFillMs();
            if (!double.IsNaN(v)) _lastFillMs = v;
            return double.IsNaN(_lastFillMs) ? r.PeriodMs : _lastFillMs;
        }
        return 0;
    }

    public void Start(string deviceId, Receiver rx, int channels, int latencyMs, OutputMode mode, Action<string>? log = null)
    {
        Stop();
        Wasapi.Check(_enum.GetDevice(deviceId, out var dev), "GetDevice");
        try { StartOn(dev, deviceId, rx, channels, latencyMs, mode, log); }
        finally { try { Marshal.ReleaseComObject(dev); } catch (Exception) { } }
    }

    private void StartOn(IMMDevice dev, string deviceId, Receiver rx, int channels, int latencyMs, OutputMode mode, Action<string>? log)
    {
        string name;
        try { name = WasapiDevices.NameOf(dev); }
        catch (Exception) { name = deviceId; }
        var chs = channels == 1 ? new[] { 1 } : new[] { channels, 1 };
        if (mode == OutputMode.Auto)
        {
            OpenAuto(dev, deviceId, name, rx, chs, latencyMs, log);
            return;
        }
        var tries = mode switch
        {
            OutputMode.Exclusive => new[] { OutputMode.Exclusive, OutputMode.LowLatency },
            OutputMode.Stable => new[] { OutputMode.Stable },
            _ => new[] { OutputMode.LowLatency },
        };
        Exception? last = null;
        foreach (var m in tries)
            foreach (var ch in chs)
            {
                try
                {
                    Adopt(OpenRenderer(dev, rx, ch, m, log), deviceId, name, m, ch, latencyMs);
                    return;
                }
                catch (Exception e)
                {
                    last = e;
                    log?.Invoke($"output {m}/{ch}ch on '{name}' failed: {e.Message}");
                }
            }
        throw last ?? new InvalidOperationException("no output path opened");
    }

    void OpenAuto(IMMDevice dev, string deviceId, string name, Receiver rx, int[] chs, int latencyMs, Action<string>? log)
    {
        Exception? last = null;
        LowLatencyRenderer? shared = null;
        int sharedCh = chs[0];
        foreach (var ch in chs)
        {
            try { shared = OpenRenderer(dev, rx, ch, OutputMode.LowLatency, log); sharedCh = ch; break; }
            catch (Exception e) { last = e; log?.Invoke($"output Auto/shared/{ch}ch on '{name}' failed: {e.Message}"); }
        }
        double sharedPeriod = shared?.PeriodMs ?? double.PositiveInfinity;
        double exclEst = EstimateExclusiveMs(dev);
        bool tryExcl = shared is null || sharedPeriod > exclEst + 0.4;
        if (tryExcl)
        {
            shared?.Dispose();
            shared = null;
            LowLatencyRenderer? excl = null;
            int exclCh = chs[0];
            foreach (var ch in chs)
            {
                try { excl = OpenRenderer(dev, rx, ch, OutputMode.Exclusive, log); exclCh = ch; break; }
                catch (Exception e) { last = e; log?.Invoke($"output Auto/exclusive/{ch}ch on '{name}' failed: {e.Message}"); }
            }
            if (excl is not null && excl.PeriodMs + 0.15 < sharedPeriod)
            {
                log?.Invoke($"auto chose Exclusive ({excl.PeriodMs:F1} ms) over shared ({(double.IsInfinity(sharedPeriod) ? 0 : sharedPeriod):F1} ms)");
                Adopt(excl, deviceId, name, OutputMode.Exclusive, exclCh, latencyMs);
                return;
            }
            excl?.Dispose();
            foreach (var ch in chs)
            {
                try { shared = OpenRenderer(dev, rx, ch, OutputMode.LowLatency, log); sharedCh = ch; break; }
                catch (Exception e) { last = e; log?.Invoke($"output Auto/shared-retry/{ch}ch on '{name}' failed: {e.Message}"); }
            }
        }
        if (shared is not null)
        {
            if (tryExcl && !double.IsInfinity(sharedPeriod))
                log?.Invoke($"auto kept shared ({shared.PeriodMs:F1} ms)");
            Adopt(shared, deviceId, name, OutputMode.LowLatency, sharedCh, latencyMs);
            return;
        }
        throw last ?? new InvalidOperationException("no output path opened");
    }

    static LowLatencyRenderer OpenRenderer(IMMDevice dev, Receiver rx, int ch, OutputMode mode, Action<string>? log)
    {
        var r = new LowLatencyRenderer(dev, new ReceiverWaveProvider(rx, ch), mode, log);
        r.Start();
        return r;
    }

    static double EstimateExclusiveMs(IMMDevice dev)
    {
        var iid = Wasapi.IidAudioClient3;
        if (dev.Activate(ref iid, Wasapi.ClsCtxAll, IntPtr.Zero, out var obj) < 0) return 3;
        var client = (IAudioClient3)obj;
        try
        {
            if (client.GetDevicePeriod(out _, out long min100ns) < 0 || min100ns <= 0) return 3;
            double ms = min100ns / 10_000.0;
            return ms < 3 ? 3 : ms;
        }
        finally { try { Marshal.ReleaseComObject(client); } catch (Exception) { } }
    }

    void Adopt(LowLatencyRenderer r, string deviceId, string name, OutputMode mode, int ch, int latencyMs)
    {
        r.Failed += why => { if (ReferenceEquals(r, _ll)) Failed?.Invoke(why); };
        _ll = r;
        _lastFillMs = double.NaN;
        ActiveMode = mode;
        string api = r.UsedAudioClient3 ? "IAudioClient3" : "IAudioClient";
        string min = r.EngineMinMs > 0 ? $", engine min {r.EngineMinMs:F1} ms" : "";
        Describe = $"{mode} {(mode == OutputMode.Exclusive ? "exclusive" : "shared")}, {ch} ch, {api} period {r.PeriodMs:F1} ms, buffer {r.BufferMs:F1} ms{min}";
        ActiveId = deviceId;
        ActiveName = name;
        LatencyMs = latencyMs;
        LastError = null;
    }

    public void Fail(string why) { LastError = why; Stop(); }

    public void Stop()
    {
        var ll = _ll;
        _ll = null;
        ActiveId = null;
        ActiveName = null;
        ActiveMode = null;
        if (ll is not null) try { ll.Dispose(); } catch (Exception) { }
    }

    public void Dispose()
    {
        Stop();
        try { _enum.UnregisterEndpointNotificationCallback(_notes); } catch (Exception) { }
        try { Marshal.ReleaseComObject(_enumObj); } catch (Exception) { }
    }
}
