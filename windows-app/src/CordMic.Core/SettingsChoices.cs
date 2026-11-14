using CordMic.Core.Dsp;

namespace CordMic.Core;

/// <summary>0.7.0 single "Noise reduction" choice: the phone's suppression or one of the PC Studio presets.</summary>
public enum NoiseChoice { Off, PhoneLight, PhoneStrong, StudioBalanced, StudioWhisper, StudioMax }

/// <summary>0.7.0 "Delay or stability" choice, a preset over the jitter buffer and the Windows output mode.</summary>
public enum DelayChoice { Lowest, Balanced, Stable, Custom }

/// <summary>
/// Maps the simplified 0.7.0 choices onto the settings that already exist, so old files load unchanged and the
/// exact values stay editable under Advanced. Nothing is stored twice.
/// </summary>
public static class SettingsChoices
{
    public static readonly NoiseChoice[] NoiseChoices = Enum.GetValues<NoiseChoice>();
    public static readonly DelayChoice[] DelayChoices = { DelayChoice.Lowest, DelayChoice.Balanced, DelayChoice.Stable };

    /// <summary>Studio wins when it is on (the phone bypasses its own suppression then).</summary>
    public static NoiseChoice Noise(NoiseMode phone, StudioPreset studio) => studio switch
    {
        StudioPreset.Balanced => NoiseChoice.StudioBalanced,
        StudioPreset.Whisper => NoiseChoice.StudioWhisper,
        StudioPreset.Max => NoiseChoice.StudioMax,
        _ => phone switch { NoiseMode.Off => NoiseChoice.Off, NoiseMode.Strong => NoiseChoice.PhoneStrong, _ => NoiseChoice.PhoneLight },
    };

    /// <summary>
    /// Settings for a choice. Picking a Studio preset keeps the phone mode as it was (it is the fallback when Studio
    /// cannot run); picking a phone mode turns Studio off.
    /// </summary>
    public static (NoiseMode Phone, StudioPreset Studio) Apply(NoiseChoice c, NoiseMode currentPhone) => c switch
    {
        NoiseChoice.Off => (NoiseMode.Off, StudioPreset.Off),
        NoiseChoice.PhoneLight => (NoiseMode.Light, StudioPreset.Off),
        NoiseChoice.PhoneStrong => (NoiseMode.Strong, StudioPreset.Off),
        NoiseChoice.StudioBalanced => (currentPhone, StudioPreset.Balanced),
        NoiseChoice.StudioWhisper => (currentPhone, StudioPreset.Whisper),
        NoiseChoice.StudioMax => (currentPhone, StudioPreset.Max),
        _ => (currentPhone, StudioPreset.Off),
    };

    public static bool IsStudio(NoiseChoice c) => c is NoiseChoice.StudioBalanced or NoiseChoice.StudioWhisper or NoiseChoice.StudioMax;

    public static DelayChoice Delay(JitterPreset jitter, OutputMode mode) => (jitter, mode) switch
    {
        (JitterPreset.Low, OutputMode.Auto) => DelayChoice.Lowest,
        (JitterPreset.Normal, OutputMode.Auto) => DelayChoice.Balanced,
        (JitterPreset.Safe, OutputMode.Stable) => DelayChoice.Stable,
        _ => DelayChoice.Custom,
    };

    /// <summary>Settings for a delay choice; <see cref="DelayChoice.Custom"/> keeps the current values.</summary>
    public static (JitterPreset Jitter, OutputMode Mode) Apply(DelayChoice c, JitterPreset jitter, OutputMode mode) => c switch
    {
        DelayChoice.Lowest => (JitterPreset.Low, OutputMode.Auto),
        DelayChoice.Balanced => (JitterPreset.Normal, OutputMode.Auto),
        DelayChoice.Stable => (JitterPreset.Safe, OutputMode.Stable),
        _ => (jitter, mode),
    };

    public static string NoiseKey(NoiseChoice c) => c switch
    {
        NoiseChoice.Off => "set.noise.off",
        NoiseChoice.PhoneLight => "set.noise.light",
        NoiseChoice.PhoneStrong => "set.noise.strong",
        NoiseChoice.StudioBalanced => "set.noise.studioBalanced",
        NoiseChoice.StudioWhisper => "set.noise.studioWhisper",
        _ => "set.noise.studioMax",
    };

    public static string DelayKey(DelayChoice c) => c switch
    {
        DelayChoice.Lowest => "set.delay.lowest",
        DelayChoice.Balanced => "set.delay.balanced",
        DelayChoice.Stable => "set.delay.stable",
        _ => "set.delay.custom",
    };
}

/// <summary>Reads the app log to tell an explicit "auto update off" (0.6.1+) from a never-touched default.</summary>
public static class AutoUpdateMigration
{
    /// <summary>
    /// True when the last automatic-update toggle logged by CordMic 0.6.1 or later was "off". Toggles logged by older
    /// versions are ignored (0.5.x switched it while its updater failed, which says nothing about the user's wish now).
    /// </summary>
    public static bool ExplicitOffSince061(IEnumerable<string> lines)
    {
        AppUpdate.SemVer.TryParse("0.6.1", out var min);
        bool modern = false;
        bool? last = null;
        foreach (var line in lines)
        {
            int c = line.IndexOf(" CordMic ", StringComparison.Ordinal);
            if (c >= 0 && line.Contains(" start", StringComparison.Ordinal))
            {
                var rest = line[(c + 9)..];
                int sp = rest.IndexOf(' ');
                var ver = sp > 0 ? rest[..sp] : rest;
                modern = AppUpdate.SemVer.TryParse(ver, out var v) && v.CompareTo(min) >= 0;
                continue;
            }
            if (!modern) continue;
            if (line.EndsWith("auto update -> off", StringComparison.Ordinal)) last = false;
            else if (line.EndsWith("auto update -> on", StringComparison.Ordinal)) last = true;
        }
        return last == false;
    }

    /// <summary>Reads <paramref name="paths"/> oldest first (missing or unreadable files are skipped).</summary>
    public static bool ExplicitOffSince061(params string[] paths)
    {
        var lines = new List<string>();
        foreach (var p in paths)
        {
            try { if (File.Exists(p)) lines.AddRange(File.ReadAllLines(p)); } catch (Exception) { }
        }
        return ExplicitOffSince061(lines);
    }
}
