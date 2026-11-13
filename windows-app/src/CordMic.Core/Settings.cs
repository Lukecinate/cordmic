using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CordMic.Core;

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public int Port { get; set; } = Protocol.DefaultPort;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public JitterPreset Jitter { get; set; } = JitterPreset.Normal;
    /// <summary>MMDevice ID of the chosen render endpoint; null = auto (best virtual cable).</summary>
    public string? OutputDeviceId { get; set; }
    public string? OutputDeviceName { get; set; }
    public bool FirstRunDone { get; set; }
    /// <summary>Output channels sent to WASAPI (VB-Cable default is stereo).</summary>
    public int OutputChannels { get; set; } = 2;
    /// <summary>Nominal WASAPI buffer hint in ms. The renderer uses the device period; this remains for older settings files.</summary>
    public int OutputLatencyMs { get; set; } = 10;
    /// <summary>Phone noise suppression on/off. Kept so older settings files still load. Prefer <see cref="NoiseMode"/>.</summary>
    public bool NoiseCancellation { get; set; } = true;
    /// <summary>Off / Light / Strong. Missing from older files, so those default to Light when NC is on.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public NoiseMode NoiseMode { get; set; } = NoiseMode.Light;
    /// <summary>High-pass, low-shelf cut and a slight presence lift. Default on.</summary>
    public bool VoiceClarity { get; set; } = true;
    /// <summary>Automatic makeup gain toward −18 dBFS, with a noise gate so silence is not boosted.</summary>
    public bool AutoVolume { get; set; } = true;
    /// <summary>0.6.0: PC Studio noise reduction preset. Off keeps the 0.5.7 phone-side processing.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CordMic.Core.Dsp.StudioPreset StudioPreset { get; set; } = CordMic.Core.Dsp.StudioPreset.Off;
    /// <summary>0.6.0: Studio auto-volume gain at the end of the last session (next session's start gain), dB.</summary>
    public double StudioAgcGainDb { get; set; } = CordMic.Core.Dsp.AgcB3.DefaultStartGainDb;
    /// <summary>Ask the phone to prefer the user-facing microphone. Default on; unsupported phones no-op.</summary>
    public bool FrontFocus { get; set; } = true;
    /// <summary>Use the AudioRecord path so front focus can actually be applied (AAudio has no direction API).</summary>
    public bool FrontFocusAudioRecord { get; set; }
    /// <summary>Check GitHub Releases on launch and about every six hours. Off by default; the user still confirms install.</summary>
    public bool AutoUpdate { get; set; }
    /// <summary>UTC ticks of the last completed update check (0 = never). Used to honour the six-hour spacing.</summary>
    public long LastUpdateCheckUtcTicks { get; set; }
    /// <summary>Phone mic makeup gain in dB, 0 to +24. Default +6 on new installs; a saved value is left unchanged.</summary>
    public int MicGainDb { get; set; } = 6;
    /// <summary>How CordMic feeds Windows audio (added in 0.4.2; older files default to LowLatency).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OutputMode OutputMode { get; set; } = OutputMode.Auto;
    /// <summary>UI language: "auto" (follow Windows), "en" or "id".</summary>
    public string Language { get; set; } = L.Auto;
    /// <summary>
    /// 0.6.1: optional 6-digit pairing code. Null or empty (the default) means a phone on the USB tethering link is
    /// accepted without a code.
    /// </summary>
    public string? PairingCode { get; set; }

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static AppSettings Load(string path) => Load(path, out _);

    /// <summary>
    /// Loads settings. A file that cannot be parsed is kept as <c>settings.json.bad-&lt;timestamp&gt;</c> (never
    /// overwritten) and the last good copy (<c>settings.json.bak</c>, written by <see cref="Save"/>) is used instead,
    /// else defaults. <paramref name="problem"/> describes what happened, for the log.
    /// </summary>
    public static AppSettings Load(string path, out string? problem)
    {
        problem = null;
        if (!File.Exists(path))
        {
            if (TryParse(path + ".bak", out var fromBak, out _))
            {
                problem = "settings.json missing; restored the backup copy";
                return fromBak!;
            }
            return new AppSettings();
        }
        if (TryParse(path, out var s, out var why)) return s!;
        string kept = KeepBadCopy(path);
        if (TryParse(path + ".bak", out var bak, out _))
        {
            problem = $"settings.json could not be read ({why}); kept it as {Path.GetFileName(kept)} and restored the backup copy";
            return bak!;
        }
        problem = $"settings.json could not be read ({why}); kept it as {Path.GetFileName(kept)} and started with defaults";
        return new AppSettings();
    }

    private static bool TryParse(string path, out AppSettings? s, out string? why)
    {
        s = null; why = null;
        try
        {
            if (!File.Exists(path)) { why = "missing"; return false; }
            s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Opts);
            if (s is null) { why = "empty"; return false; }
            s.NormalizeAudio();
            if (!string.IsNullOrEmpty(s.PairingCode) && !Pairing.IsValidCode(s.PairingCode)) s.PairingCode = null;
            return true;
        }
        catch (Exception e) { why = e.GetType().Name; s = null; return false; }
    }

    private static string KeepBadCopy(string path)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        for (int i = 0; i < 100; i++)
        {
            string dst = path + ".bad-" + stamp + (i == 0 ? "" : "-" + i);
            if (File.Exists(dst)) continue;
            try { File.Copy(path, dst, overwrite: false); return dst; }
            catch (IOException) when (File.Exists(dst)) { }
            catch (Exception) { break; }
        }
        return path;
    }

    /// <summary>Reconcile the older <see cref="NoiseCancellation"/> bool with <see cref="NoiseMode"/>.</summary>
    public void NormalizeAudio()
    {
        if (!NoiseCancellation) NoiseMode = NoiseMode.Off;
        else if (NoiseMode == NoiseMode.Off) NoiseMode = NoiseMode.Light;
        NoiseCancellation = NoiseMode != NoiseMode.Off;
        if (!Enum.IsDefined(StudioPreset)) StudioPreset = CordMic.Core.Dsp.StudioPreset.Off;
        if (double.IsNaN(StudioAgcGainDb) || double.IsInfinity(StudioAgcGainDb)) StudioAgcGainDb = CordMic.Core.Dsp.AgcB3.DefaultStartGainDb;
        StudioAgcGainDb = Math.Clamp(StudioAgcGainDb, CordMic.Core.Dsp.AgcB3.GMin, CordMic.Core.Dsp.AgcB3.GMax);
    }

    public void ResetAudioDefaults()
    {
        NoiseCancellation = true;
        NoiseMode = NoiseMode.Light;
        VoiceClarity = true;
        AutoVolume = true;
        FrontFocus = true;
        FrontFocusAudioRecord = false;
        MicGainDb = VoiceChain.DefaultGainDb;
        StudioPreset = CordMic.Core.Dsp.StudioPreset.Off;
        StudioAgcGainDb = CordMic.Core.Dsp.AgcB3.DefaultStartGainDb;
    }

    /// <summary>
    /// Durable save: a uniquely named temp file is written and flushed to disk, then swapped in with
    /// <see cref="File.Replace(string, string, string?)"/>, which keeps the previous file as <c>settings.json.bak</c>.
    /// </summary>
    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $"{Path.GetFileName(path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(JsonSerializer.SerializeToUtf8Bytes(this, Opts));
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
            else File.Move(tmp, path);
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception) { }
            throw;
        }
    }
}

/// <summary>Upgrade helper for the pre-0.3.0 "UsbMic" brand.</summary>
public static class LegacySettings
{
    /// <summary>Copies the old settings file to the new location only if the new one does not exist yet.
    /// Returns true when a copy happened. The old file is left untouched.</summary>
    public static bool MigrateIfNeeded(string oldPath, string newPath)
    {
        if (File.Exists(newPath) || !File.Exists(oldPath)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
        File.Copy(oldPath, newPath, overwrite: false);
        return true;
    }
}

/// <summary>
/// Thread-safe file logger with one rollover (no dependency). Since 0.6.1 <see cref="Write"/> only queues the line
/// (bounded; excess lines are counted and reported), and a background thread does the file I/O, so the audio RX
/// thread never waits on the disk (audit L4).
/// </summary>
public sealed class FileLog : IDisposable
{
    public const int MaxQueued = 4096;
    private readonly object _io = new();
    private readonly ConcurrentQueue<string> _q = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread? _writer;
    private volatile bool _stop;
    private int _queued;
    private long _dropped;
    public string FilePath { get; }
    private readonly long _max;

    /// <param name="backgroundWriter">False only in tests: lines are written by explicit <see cref="Flush"/> calls.</param>
    public FileLog(string path, long maxBytes = 2_000_000, bool backgroundWriter = true)
    {
        FilePath = path; _max = maxBytes;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!backgroundWriter) return;
        _writer = new Thread(WriterLoop) { Name = "cordmic-log", IsBackground = true, Priority = ThreadPriority.BelowNormal };
        _writer.Start();
    }

    /// <summary>Lines dropped because the queue was full (diagnostics and tests).</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public void Write(string msg)
    {
        if (Interlocked.Increment(ref _queued) > MaxQueued)
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _dropped);
            return;
        }
        _q.Enqueue($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {msg}");
        try { _signal.Set(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Writes everything queued so far (called by the writer thread, tests and <see cref="Dispose"/>).</summary>
    public void Flush()
    {
        lock (_io)
        {
            if (_q.IsEmpty && Interlocked.Read(ref _dropped) == 0) return;
            try
            {
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > _max) File.Move(FilePath, FilePath + ".1", overwrite: true);
                using var w = new StreamWriter(FilePath, append: true);
                while (_q.TryDequeue(out var line))
                {
                    Interlocked.Decrement(ref _queued);
                    w.WriteLine(line);
                }
                long dropped = Interlocked.Exchange(ref _dropped, 0);
                if (dropped > 0) w.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ({dropped} log lines dropped: queue full)");
            }
            catch (Exception)
            {
                // Disk full or file locked: drop what is queued rather than grow without bound.
                while (_q.TryDequeue(out _)) Interlocked.Decrement(ref _queued);
            }
        }
    }

    private void WriterLoop()
    {
        while (!_stop)
        {
            _signal.WaitOne(500);
            Flush();
        }
    }

    public void Dispose()
    {
        _stop = true;
        try { _signal.Set(); } catch (ObjectDisposedException) { }
        _writer?.Join(2000);
        Flush();
    }
}
