using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using CordMic.Core;

namespace CordMic.App;

internal sealed record RenderDevice(string Id, string Name);

/// <summary>Pulls mono s16 48 kHz from the receiver's jitter buffer and up-mixes to the output channel count.</summary>
internal sealed class ReceiverWaveProvider : IWaveProvider
{
    private readonly Receiver _rx;
    private readonly int _ch;
    private byte[] _mono = new byte[480 * 8];
    private bool _boosted;
    public WaveFormat WaveFormat { get; }

    public ReceiverWaveProvider(Receiver rx, int channels)
    {
        _rx = rx;
        _ch = Math.Clamp(channels, 1, 2);
        WaveFormat = new WaveFormat(48000, 16, _ch);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        if (!_boosted) { Native.BoostAudioThread(); _boosted = true; }
        int frames = count / (2 * _ch);
        int monoBytes = frames * 2;
        if (_mono.Length < monoBytes) _mono = new byte[monoBytes];
        var mono = _mono.AsSpan(0, monoBytes);
        _rx.Pull(mono);
        if (_ch == 1) { mono.CopyTo(buffer.AsSpan(offset)); return monoBytes; }
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
/// Event-driven WASAPI render loop on its own MMCSS "Pro Audio" thread (replaces NAudio's WasapiOut in 0.4.2).
/// NAudio's WasapiOut tops the whole endpoint buffer up on every event, so the audio always waits roughly a
/// full buffer (often about two device periods) before Windows reads it. Here the fill level is capped:
/// <list type="bullet">
/// <item><see cref="OutputMode.LowLatency"/>: shared mode, keep about one device period plus a small margin queued.</item>
/// <item><see cref="OutputMode.Stable"/>: shared mode, fill the whole buffer (old behaviour, most tolerant of a busy PC).</item>
/// <item><see cref="OutputMode.Exclusive"/>: exclusive event mode at a short period, bypasses the shared mixer;
/// needs a device that accepts 48 kHz 16-bit in exclusive mode.</item>
/// </list>
/// </summary>
internal sealed class LowLatencyRenderer : IDisposable
{
    private const int AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED = unchecked((int)0x88890019);
    private readonly MMDevice _dev;
    private readonly IWaveProvider _src;
    private readonly OutputMode _mode;
    private AudioClient _client = null!;
    private AudioRenderClient _render = null!;
    private EventWaitHandle _evt = null!;
    private Thread? _thread;
    private volatile bool _stop;
    private int _bufferFrames, _periodFrames, _capFrames, _bytesPerFrame, _rate;
    private byte[] _tmp = Array.Empty<byte>();
    // stats (written by the render thread, read by the UI timer)
    private double _fillSum, _fillWeight;
    private readonly object _statLock = new();

    public bool Running => _thread is { IsAlive: true } && !_stop;
    public double PeriodMs => _periodFrames * 1000.0 / _rate;
    public double BufferMs => _bufferFrames * 1000.0 / _rate;
    public OutputMode Mode => _mode;
    public event Action<string>? Failed;

    public LowLatencyRenderer(MMDevice dev, IWaveProvider src, OutputMode mode, int marginMs)
    {
        _dev = dev; _src = src; _mode = mode;
        var fmt = src.WaveFormat;
        _rate = fmt.SampleRate;
        _bytesPerFrame = fmt.BlockAlign;
        _client = dev.AudioClient;
        try
        {
            if (mode == OutputMode.Exclusive) InitExclusive(fmt);
            else InitShared(fmt, marginMs);
            _evt = new EventWaitHandle(false, EventResetMode.AutoReset);
            _client.SetEventHandle(_evt.SafeWaitHandle.DangerousGetHandle());
            _render = _client.AudioRenderClient;
            _tmp = new byte[_bufferFrames * _bytesPerFrame];
        }
        catch { _client.Dispose(); throw; }
    }

    private void InitShared(WaveFormat fmt, int marginMs)
    {
        long period = _client.DefaultDevicePeriod;           // 100 ns units, usually 10 ms
        if (period <= 0) period = 100_000;
        var flags = AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality;
        // Ask for room for ~3 periods; how much is actually kept queued is decided by _capFrames.
        _client.Initialize(AudioClientShareMode.Shared, flags, 3 * period, 0, fmt, Guid.Empty);
        _bufferFrames = _client.BufferSize;
        _periodFrames = (int)Math.Round(period / 1e7 * _rate);
        int margin = (int)Math.Round(Math.Clamp(marginMs, 1, 20) / 1000.0 * _rate);
        _capFrames = _mode == OutputMode.Stable ? _bufferFrames : Math.Min(_bufferFrames, _periodFrames + margin);
    }

    private void InitExclusive(WaveFormat fmt)
    {
        if (!_client.IsFormatSupported(AudioClientShareMode.Exclusive, fmt))
            throw new NotSupportedException($"device does not accept {fmt.SampleRate} Hz {fmt.BitsPerSample}-bit {fmt.Channels} ch in exclusive mode");
        long period = Math.Max(_client.MinimumDevicePeriod, 30_000); // >= 3 ms
        try
        {
            _client.Initialize(AudioClientShareMode.Exclusive, AudioClientStreamFlags.EventCallback, period, period, fmt, Guid.Empty);
        }
        catch (System.Runtime.InteropServices.COMException ex) when (ex.ErrorCode == AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED)
        {
            int frames = _client.BufferSize;
            _client.Dispose();
            _client = _dev.AudioClient;
            period = (long)(1e7 / fmt.SampleRate * frames + 0.5);
            _client.Initialize(AudioClientShareMode.Exclusive, AudioClientStreamFlags.EventCallback, period, period, fmt, Guid.Empty);
        }
        _bufferFrames = _client.BufferSize;
        _periodFrames = _bufferFrames;
        _capFrames = _bufferFrames;
    }

    public void Start()
    {
        _stop = false;
        _thread = new Thread(Loop) { Name = "cordmic-render", IsBackground = true, Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    /// <summary>Average time our samples sit in the WASAPI endpoint buffer before Windows takes them (ms); NaN until measured. Resets the average.</summary>
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

    private void Write(int frames)
    {
        int bytes = frames * _bytesPerFrame;
        int got = _src.Read(_tmp, 0, bytes);
        if (got < bytes) Array.Clear(_tmp, got, bytes - got);
        var ptr = _render.GetBuffer(frames);
        System.Runtime.InteropServices.Marshal.Copy(_tmp, 0, ptr, bytes);
        _render.ReleaseBuffer(frames, AudioClientBufferFlags.None);
    }

    private void Loop()
    {
        Exception? err = null;
        try
        {
            Native.BoostAudioThread();
            // Prefill with silence so the first engine pass has data without draining the jitter buffer.
            var ptr = _render.GetBuffer(_capFrames);
            _render.ReleaseBuffer(_capFrames, AudioClientBufferFlags.Silent);
            _client.Start();
            bool exclusive = _mode == OutputMode.Exclusive;
            while (!_stop)
            {
                if (!_evt.WaitOne(200)) continue;
                if (_stop) break;
                int padding = exclusive ? 0 : _client.CurrentPadding;
                int frames = exclusive ? _bufferFrames : _capFrames - padding;
                if (frames <= 0) continue;
                Write(frames);
                // Time-average queued audio between two events ~ (level after write + level before next write) / 2;
                // approximate with (padding + cap) / 2 per event, weighted equally.
                double level = exclusive ? _bufferFrames : (padding + _capFrames) / 2.0;
                lock (_statLock) { _fillSum += level; _fillWeight += 1; }
            }
        }
        catch (Exception e) { err = e; }
        finally
        {
            try { _client.Stop(); } catch (Exception) { }
        }
        if (err is not null && !_stop) Failed?.Invoke(err.Message);
    }

    public void Dispose()
    {
        _stop = true;
        try { _evt?.Set(); } catch (Exception) { }
        _thread?.Join(1000);
        try { _render?.Dispose(); } catch (Exception) { }
        try { _client.Dispose(); } catch (Exception) { }
        try { _evt?.Dispose(); } catch (Exception) { }
    }
}

/// <summary>Owns the render endpoint: CordMic's own low-latency loop first, NAudio's WasapiOut as a last resort.</summary>
internal sealed class AudioEngine : IDisposable, IMMNotificationClient
{
    private readonly MMDeviceEnumerator _enum = new();
    private LowLatencyRenderer? _ll;
    private WasapiOut? _legacy;
    private volatile bool _devicesDirty = true;
    private double _lastFillMs = double.NaN;
    public string? ActiveId { get; private set; }
    public string? ActiveName { get; private set; }
    public string? LastError { get; private set; }
    /// <summary>Mode actually running (may differ from the requested one after a fallback).</summary>
    public OutputMode? ActiveMode { get; private set; }
    /// <summary>Human-readable description of the running output path, for the log.</summary>
    public string Describe { get; private set; } = "";
    public int LatencyMs { get; private set; }
    /// <summary>Safety margin kept queued on top of one device period in <see cref="OutputMode.LowLatency"/>.</summary>
    public const int LowLatencyMarginMs = 3;
    public bool Playing => _ll?.Running == true || _legacy?.PlaybackState == PlaybackState.Playing;

    /// <summary>Raised on a WASAPI thread when playback stops unexpectedly (device removed, format change...).</summary>
    public event Action<string>? Failed;

    public AudioEngine() { _enum.RegisterEndpointNotificationCallback(this); }

    /// <summary>True once after any endpoint add/remove/state change (set from a COM thread, read on the UI timer).</summary>
    public bool ConsumeDevicesDirty()
    {
        if (!_devicesDirty) return false;
        _devicesDirty = false;
        return true;
    }

    public List<RenderDevice> ListRender()
    {
        var list = new List<RenderDevice>();
        foreach (var d in _enum.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try { list.Add(new RenderDevice(d.ID, d.FriendlyName)); }
            catch (Exception) { /* device vanished mid-enumeration */ }
            finally { d.Dispose(); }
        }
        return list;
    }

    /// <summary>Estimated time our audio waits in the Windows output buffer (ms), averaged since the last call.</summary>
    public double OutputMs()
    {
        if (_ll is { } r)
        {
            var v = r.TakeAvgFillMs();
            if (!double.IsNaN(v)) _lastFillMs = v;
            return double.IsNaN(_lastFillMs) ? r.PeriodMs : _lastFillMs;
        }
        return _legacy is null ? 0 : LatencyMs;
    }

    public void Start(string deviceId, Receiver rx, int channels, int latencyMs, OutputMode mode, Action<string>? log = null)
    {
        Stop();
        var dev = _enum.GetDevice(deviceId);
        var name = dev.FriendlyName;
        var tries = mode switch
        {
            OutputMode.Exclusive => new[] { OutputMode.Exclusive, OutputMode.LowLatency },
            OutputMode.Stable => new[] { OutputMode.Stable },
            _ => new[] { OutputMode.LowLatency },
        };
        foreach (var m in tries)
            foreach (var ch in channels == 1 ? new[] { 1 } : new[] { channels, 1 })
            {
                try
                {
                    var r = new LowLatencyRenderer(dev, new ReceiverWaveProvider(rx, ch), m, LowLatencyMarginMs);
                    r.Failed += why => { if (ReferenceEquals(r, _ll)) Failed?.Invoke(why); };
                    r.Start();
                    _ll = r;
                    _lastFillMs = double.NaN;
                    ActiveMode = m;
                    Describe = $"{m} {(m == OutputMode.Exclusive ? "exclusive" : "shared")}, {ch} ch, period {r.PeriodMs:F1} ms, buffer {r.BufferMs:F1} ms";
                    Activate(deviceId, name, latencyMs);
                    return;
                }
                catch (Exception e) { log?.Invoke($"output {m}/{ch}ch on '{name}' failed: {e.Message}"); }
            }

        // Last resort: NAudio's WasapiOut (pre-0.4.2 path).
        var o = new WasapiOut(dev, AudioClientShareMode.Shared, true, Math.Clamp(latencyMs, 3, 100));
        try { o.Init(new ReceiverWaveProvider(rx, channels)); }
        catch
        {
            o.Dispose();
            if (channels == 1) throw;
            o = new WasapiOut(dev, AudioClientShareMode.Shared, true, Math.Clamp(latencyMs, 3, 100));
            o.Init(new ReceiverWaveProvider(rx, 1));
        }
        o.PlaybackStopped += (_, e) =>
        {
            if (!ReferenceEquals(o, _legacy)) return;
            Failed?.Invoke(e.Exception?.Message ?? "playback stopped");
        };
        o.Play();
        _legacy = o;
        ActiveMode = OutputMode.Stable;
        Describe = $"NAudio WasapiOut shared fallback, {latencyMs} ms";
        Activate(deviceId, name, latencyMs);
    }

    private void Activate(string id, string name, int latencyMs)
    {
        ActiveId = id;
        ActiveName = name;
        LatencyMs = latencyMs;
        LastError = null;
    }

    public void Fail(string why) { LastError = why; Stop(); }

    public void Stop()
    {
        var ll = _ll; var o = _legacy;
        _ll = null; _legacy = null;
        ActiveId = null;
        ActiveName = null;
        ActiveMode = null;
        if (ll is not null) try { ll.Dispose(); } catch (Exception) { }
        if (o is null) return;
        try { o.Stop(); } catch (Exception) { }
        try { o.Dispose(); } catch (Exception) { }
    }

    public void Dispose()
    {
        Stop();
        try { _enum.UnregisterEndpointNotificationCallback(this); } catch (Exception) { }
        _enum.Dispose();
    }

    // IMMNotificationClient: called on COM worker threads -> only flip a flag.
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _devicesDirty = true;
    public void OnDeviceAdded(string pwstrDeviceId) => _devicesDirty = true;
    public void OnDeviceRemoved(string deviceId) => _devicesDirty = true;
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
}
