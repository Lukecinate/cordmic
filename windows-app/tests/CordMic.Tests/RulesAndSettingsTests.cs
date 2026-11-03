using CordMic.Core;
using Xunit;

public class RulesAndSettingsTests
{
    [Theory]
    [InlineData("Remote NDIS based Internet Sharing Device", true)]
    [InlineData("Remote NDIS Compatible Device", true)]
    [InlineData("SAMSUNG Mobile USB Remote NDIS Network Device", true)]
    [InlineData("UsbNcm Host Device", true)]
    [InlineData("Intel(R) Ethernet Connection (17) I219-V", false)]
    [InlineData("Realtek PCIe GbE Family Controller", false)]
    [InlineData("Intel(R) Wi-Fi 6E AX211 160MHz", false)]
    [InlineData("Hyper-V Virtual Ethernet Adapter", false)]
    public void TetheringAdapterDetection(string desc, bool expected) =>
        Assert.Equal(expected, DeviceRules.IsTetheringAdapter(desc));

    [Fact]
    public void PicksVbCableFirst()
    {
        var names = new[] { "Speakers (Realtek(R) Audio)", "VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)",
                            "CABLE Input (VB-Audio Virtual Cable)", "Headphones (HyperX Cloud II)" };
        Assert.Equal("CABLE Input (VB-Audio Virtual Cable)", DeviceRules.PickVirtualCable(names));
        Assert.Equal("CABLE Output (VB-Audio Virtual Cable)", DeviceRules.MicNameFor(names[2]));
    }

    [Fact]
    public void NoCableReturnsNull() =>
        Assert.Null(DeviceRules.PickVirtualCable(new[] { "Speakers (Realtek(R) Audio)", "NVIDIA High Definition Audio" }));

    [Fact]
    public void Presets() =>
        Assert.Equal((5.0, 10.0, 20.0), (CordMic.Core.Presets.Ms(JitterPreset.Low), CordMic.Core.Presets.Ms(JitterPreset.Normal), CordMic.Core.Presets.Ms(JitterPreset.Safe)));

    [Fact]
    public void SettingsRoundTripAndCorruptFallback()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cordmic-test-" + Guid.NewGuid());
        var p = Path.Combine(dir, "settings.json");
        new AppSettings { Jitter = JitterPreset.Low, OutputDeviceName = "CABLE Input", FirstRunDone = true }.Save(p);
        var s = AppSettings.Load(p);
        Assert.Equal((JitterPreset.Low, "CABLE Input", true), (s.Jitter, s.OutputDeviceName, s.FirstRunDone));
        Assert.Contains("\"Low\"", File.ReadAllText(p));
        File.WriteAllText(p, "{not json");
        Assert.Equal(JitterPreset.Normal, AppSettings.Load(p).Jitter);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void LegacyUsbMicSettingsAreMigratedOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), "cordmic-mig-" + Guid.NewGuid());
        var oldP = Path.Combine(root, "UsbMic", "settings.json");
        var newP = Path.Combine(root, "CordMic", "settings.json");
        Assert.False(LegacySettings.MigrateIfNeeded(oldP, newP)); // nothing to migrate
        new AppSettings { Jitter = JitterPreset.Safe, FirstRunDone = true }.Save(oldP);
        Assert.True(LegacySettings.MigrateIfNeeded(oldP, newP));
        Assert.Equal((JitterPreset.Safe, true), (AppSettings.Load(newP).Jitter, AppSettings.Load(newP).FirstRunDone));
        new AppSettings { Jitter = JitterPreset.Low }.Save(newP);
        Assert.False(LegacySettings.MigrateIfNeeded(oldP, newP)); // never overwrites existing CordMic settings
        Assert.Equal(JitterPreset.Low, AppSettings.Load(newP).Jitter);
        Assert.True(File.Exists(oldP));
        Directory.Delete(root, true);
    }
}
