using CordMic.Core;
using Xunit;

/// <summary>Device-profile table: each row is a phone class mapped to the capture defaults DeviceTune picks.</summary>
public class DeviceTuneTests
{
    public static TheoryData<string, DeviceProfile, bool, int, bool, NoiseMode, int> Profiles => new()
    {
        {
            "flagship exclusive",
            new DeviceProfile(34, "samsung", "SM-S721B", MicCount: 3, HasFrontMic: true, DirectionApi: true,
                DirectionWorks: false, AAudioAvailable: true, AAudioExclusive: true, AAudioLowLatency: true,
                HasPlatformNs: true, NativeSampleRate: 48000, FramesPerBurst: 96, SpeechLevelDbfs: -30),
            true, DeviceTune.PresetVoiceRecognition, true, NoiseMode.Light, 6
        },
        {
            "midrange shared AAudio",
            new DeviceProfile(30, "Xiaomi", "2201116SG", MicCount: 2, HasFrontMic: true, DirectionApi: true,
                AAudioAvailable: true, AAudioExclusive: false, AAudioLowLatency: true, HasPlatformNs: true),
            true, DeviceTune.PresetVoiceRecognition, true, NoiseMode.Light, VoiceChain.DefaultGainDb
        },
        {
            "AudioRecord fallback",
            new DeviceProfile(29, "Generic", "OEM-A1", MicCount: 1, HasFrontMic: true, DirectionApi: true,
                DirectionWorks: true, AAudioAvailable: false, HasPlatformNs: false),
            false, DeviceTune.PresetVoiceRecognition, true, NoiseMode.Light, VoiceChain.DefaultGainDb
        },
        {
            "hot noise floor",
            new DeviceProfile(33, Model: "Pixel 8", MicCount: 3, AAudioAvailable: true, AAudioExclusive: true,
                AAudioLowLatency: true, NoiseFloorDbfs: -32),
            true, DeviceTune.PresetVoiceRecognition, true, NoiseMode.Light, 0
        },
        {
            "quiet speech",
            new DeviceProfile(31, Model: "Pixel 7a", MicCount: 3, AAudioAvailable: true, AAudioExclusive: true,
                AAudioLowLatency: true, SpeechLevelDbfs: -36),
            true, DeviceTune.PresetVoiceRecognition, true, NoiseMode.Light, 12
        },
        {
            "already near target",
            new DeviceProfile(34, Model: "SM-S921B", MicCount: 4, AAudioAvailable: true, AAudioExclusive: true,
                AAudioLowLatency: true, SpeechLevelDbfs: -18),
            true, DeviceTune.PresetVoiceRecognition, true, NoiseMode.Light, 0
        },
    };

    [Theory]
    [MemberData(nameof(Profiles))]
    public void AdviseMatchesProfile(string name, DeviceProfile profile, bool useAAudio, int preset,
        bool frontFocus, NoiseMode nc, int gain)
    {
        var advice = DeviceTune.Advise(profile);
        Assert.Equal(useAAudio, advice.UseAAudio);
        Assert.Equal(preset, advice.InputPreset);
        Assert.Equal(frontFocus, advice.FrontFocus);
        Assert.Equal(nc, advice.NoiseMode);
        Assert.Equal(gain, advice.SuggestedGainDb);
        Assert.StartsWith("Detected:", advice.Summary);
        Assert.DoesNotContain(name, advice.Summary); // name is the row label, not a device id
    }

    [Fact]
    public void UserOverridesAreNeverReplaced()
    {
        var advice = DeviceTune.Advise(new DeviceProfile(34, Model: "SM-S721B", AAudioAvailable: true,
            AAudioExclusive: true, SpeechLevelDbfs: -36));
        var kept = DeviceTune.WithOverrides(advice, useAAudio: false, frontFocus: false,
            noiseMode: NoiseMode.Strong, gainDb: 9, inputPreset: DeviceTune.PresetVoiceCommunication);
        Assert.False(kept.UseAAudio);
        Assert.False(kept.FrontFocus);
        Assert.Equal(NoiseMode.Strong, kept.NoiseMode);
        Assert.Equal(9, kept.SuggestedGainDb);
        Assert.Equal(DeviceTune.PresetVoiceCommunication, kept.InputPreset);
    }

    [Fact]
    public void AutomaticPathAvoidsVoiceCommunication()
    {
        var advice = DeviceTune.Advise(new DeviceProfile(29, AAudioAvailable: true));
        Assert.Equal(DeviceTune.PresetVoiceRecognition, advice.InputPreset);
        var still = DeviceTune.WithOverrides(advice);
        Assert.Equal(DeviceTune.PresetVoiceRecognition, still.InputPreset);
    }

    [Fact]
    public void FormatDetectedIsShortAndLocal()
    {
        Assert.Equal("Detected: SM-S721B · AAudio exclusive, 3 mics, front focus",
            DeviceTune.FormatDetected("SM-S721B", true, true, true, 3, 1, true));
        Assert.Equal("Detected: Pixel 8 · AAudio exclusive, 3 mics, front focus unsupported",
            DeviceTune.FormatDetected("Pixel 8", true, true, true, 3, 2, true));
        Assert.Equal("Detected: AudioRecord, omni",
            DeviceTune.FormatDetected(" ", false, false, false, 0, 0, false));
    }

    [Fact]
    public void PongPacksEngineDirectionAndMicCountWithoutBreakingAAudioFlag()
    {
        var pong = new byte[64];
        int n = LinkControl.WritePong(pong, true, true, aaudio: true, exclusive: true, lowLatency: true,
            gainDb: 6, deviceName: "SM-S721B", mode: NoiseMode.Light, frontFocus: true, dirCode: 2, micCount: 3);
        Assert.True(n > 4);
        Assert.True(LinkControl.TryReadPong(pong.AsSpan(0, n), out var phone));
        Assert.True(phone.AAudio);
        Assert.True(phone.Exclusive);
        Assert.True(phone.FrontFocus);
        Assert.Equal(2, phone.DirCode);
        Assert.Equal(3, phone.MicCount);
        Assert.Equal("unsupported", phone.DirText);
        Assert.Equal("3", phone.MicText);
        Assert.Contains("SM-S721B", phone.DetectedSummary);
        Assert.Contains("AAudio exclusive", phone.DetectedSummary);
        // An older reader that only looks at flags bit 2 still sees AAudio.
        Assert.Equal(1, pong[3] & 3);
        Assert.True((pong[1] & 4) != 0);
        Assert.True((pong[1] & 32) != 0);
    }
}
