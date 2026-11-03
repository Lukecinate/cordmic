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
/// WASAPI shared-mode output into the virtual cable's render endpoint ("CABLE Input").
/// Event-driven with a small buffer; NAudio sets AUTOCONVERTPCM so the 48 kHz stream is resampled
/// by Windows if the cable runs at another rate.
/// </summary>
internal sealed class AudioEngine : IDisposable, IMMNotificationClient
{
    private readonly MMDeviceEnumerator _enum = new();
    private WasapiOut? _out;
    private volatile bool _devicesDirty = true;
    public string? ActiveId { get; private set; }
    public string? ActiveName { get; private set; }
    public string? LastError { get; private set; }
    public int LatencyMs { get; private set; }
    public bool Playing => _out?.PlaybackState == PlaybackState.Playing;

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

    public void Start(string deviceId, Receiver rx, int channels, int latencyMs)
    {
        Stop();
        var dev = _enum.GetDevice(deviceId);
        var name = dev.FriendlyName;
        var o = new WasapiOut(dev, AudioClientShareMode.Shared, true, Math.Clamp(latencyMs, 3, 100));
        try
        {
            o.Init(new ReceiverWaveProvider(rx, channels));
        }
        catch
        {
            o.Dispose();
            if (channels != 1) { Start(deviceId, rx, 1, latencyMs); return; } // last resort: mono format
            throw;
        }
        o.PlaybackStopped += (_, e) =>
        {
            if (!ReferenceEquals(o, _out)) return;
            Failed?.Invoke(e.Exception?.Message ?? "playback stopped");
        };
        o.Play();
        _out = o;
        ActiveId = deviceId;
        ActiveName = name;
        LatencyMs = latencyMs;
        LastError = null;
    }

    public void Fail(string why) { LastError = why; Stop(); }

    public void Stop()
    {
        var o = _out;
        _out = null;
        ActiveId = null;
        ActiveName = null;
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
