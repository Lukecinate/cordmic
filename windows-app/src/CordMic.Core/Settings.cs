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
    /// <summary>Phone noise suppression, pushed over the ping payload (0.5.0). Default on.</summary>
    public bool NoiseCancellation { get; set; } = true;
    /// <summary>Phone mic makeup gain in dB, 0 to +24. Default +12, with AGC able to use the rest of the +24 cap.</summary>
    public int MicGainDb { get; set; } = 12;
    /// <summary>How CordMic feeds Windows audio (added in 0.4.2; older files default to LowLatency).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OutputMode OutputMode { get; set; } = OutputMode.LowLatency;
    /// <summary>UI language: "auto" (follow Windows), "en" or "id".</summary>
    public string Language { get; set; } = L.Auto;

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Opts) ?? new AppSettings();
        }
        catch (Exception) { /* corrupted file: fall back to defaults */ }
        return new AppSettings();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
        File.Move(tmp, path, overwrite: true);
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

/// <summary>Tiny thread-safe file logger with one rollover (no dependency).</summary>
public sealed class FileLog
{
    private readonly object _l = new();
    public string FilePath { get; }
    private readonly long _max;

    public FileLog(string path, long maxBytes = 2_000_000)
    {
        FilePath = path; _max = maxBytes;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public void Write(string msg)
    {
        lock (_l)
        {
            try
            {
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > _max) File.Move(FilePath, FilePath + ".1", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {msg}{Environment.NewLine}");
            }
            catch (Exception) { }
        }
    }
}
