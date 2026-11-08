namespace CordMic.Core;

/// <summary>What the phone (or a unit test) observed about the capture hardware.</summary>
public sealed record DeviceProfile(
    int ApiLevel,
    string Manufacturer = "",
    string Model = "",
    int MicCount = 0,
    bool HasFrontMic = false,
    bool DirectionApi = false,
    bool DirectionWorks = false,
    bool AAudioAvailable = false,
    bool AAudioExclusive = false,
    bool AAudioLowLatency = false,
    bool HasPlatformNs = false,
    bool HasPlatformAgc = false,
    bool HasPlatformAec = false,
    int NativeSampleRate = 48000,
    int FramesPerBurst = 0,
    bool StereoCapture = false,
    double? NoiseFloorDbfs = null,
    double? SpeechLevelDbfs = null);

/// <summary>Recommended capture settings for one <see cref="DeviceProfile"/>.</summary>
public sealed record CaptureAdvice(
    bool UseAAudio,
    bool PreferExclusive,
    int InputPreset,
    bool FrontFocus,
    NoiseMode NoiseMode,
    bool UsePlatformNs,
    int SuggestedGainDb,
    string Summary);

/// <summary>
/// Picks safe, low-latency defaults for a wide range of Android phones.
/// Values the user has already chosen are applied afterwards with
/// <see cref="WithOverrides"/> and are never overwritten here.
/// </summary>
public static class DeviceTune
{
    public const int PresetVoiceRecognition = 6;
    public const int PresetUnprocessed = 9;
    public const int PresetVoiceCommunication = 7;

    public static CaptureAdvice Advise(DeviceProfile p)
    {
        bool useAAudio = p.AAudioAvailable;
        bool exclusive = useAAudio && p.AAudioExclusive;
        // Voice recognition allows OEM beamforming on many devices without the
        // extra AEC/NS/AGC that VOICE_COMMUNICATION would stack on our own chain.
        int preset = PresetVoiceRecognition;
        // Front focus is recommended on every phone; unsupported devices no-op.
        bool frontFocus = true;
        var nc = NoiseMode.Light;
        int gain = SuggestedGain(p.SpeechLevelDbfs, p.NoiseFloorDbfs);
        string summary = FormatDetected(
            p.Model, useAAudio, exclusive, p.AAudioLowLatency, p.MicCount,
            p.DirectionWorks ? 1 : (p.ApiLevel >= 29 || p.DirectionApi ? 2 : 2),
            frontFocus);
        return new CaptureAdvice(useAAudio, exclusive, preset, frontFocus, nc, p.HasPlatformNs, gain, summary);
    }

    public static int SuggestedGain(double? speechDbfs, double? floorDbfs)
    {
        if (speechDbfs is { } s && s > -90)
            return Math.Clamp((int)Math.Round(-24.0 - s), 0, 18);
        if (floorDbfs is { } f)
        {
            if (f > -38) return 0;
            if (f > -46) return 3;
        }
        return VoiceChain.DefaultGainDb;
    }

    public static CaptureAdvice WithOverrides(CaptureAdvice advice, bool? useAAudio = null,
        bool? frontFocus = null, NoiseMode? noiseMode = null, int? gainDb = null, int? inputPreset = null)
    {
        int preset = inputPreset ?? advice.InputPreset;
        if (inputPreset is null && preset == PresetVoiceCommunication)
            preset = PresetVoiceRecognition;
        return advice with
        {
            UseAAudio = useAAudio ?? advice.UseAAudio,
            FrontFocus = frontFocus ?? advice.FrontFocus,
            NoiseMode = noiseMode ?? advice.NoiseMode,
            SuggestedGainDb = gainDb ?? advice.SuggestedGainDb,
            InputPreset = preset,
        };
    }

    public static string FormatDetected(string? model, bool aaudio, bool exclusive, bool lowLatency,
        int mics, int dirCode, bool frontFocus)
    {
        string path = !aaudio ? "AudioRecord" : exclusive ? "AAudio exclusive" : lowLatency ? "AAudio low-latency" : "AAudio";
        string dir = dirCode == 1 ? "front focus" : frontFocus ? "front focus unsupported" : "omni";
        string mic = mics > 0 ? $", {mics} mic{(mics == 1 ? "" : "s")}" : "";
        string who = string.IsNullOrWhiteSpace(model) ? "" : model.Trim() + " · ";
        return $"Detected: {who}{path}{mic}, {dir}";
    }
}
