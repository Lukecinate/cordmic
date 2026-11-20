using System.Text;
using CordMic.Core;
using CordMic.Core.Usb;

namespace CordMic.Tests;

public class UsbM1Tests
{
    [Fact]
    public void Gate_defaultsToTether_untilHardwareDecision()
    {
        Assert.False(UsbEngineGate.HardwareGateGm1Passed);
        var s = new AppSettings();
        Assert.False(s.EnableExperimentalUsbRoutes);
        Assert.False(s.EnableExperimentalMidiRoute);
        Assert.False(s.UsbEngineOff);
        Assert.Equal(UsbConnectionModeName.Auto, s.UsbConnectionMode);
        Assert.False(UsbEngineGate.UsbRoutesAllowed(s, false, false, false));
        Assert.False(UsbEngineGate.MidiRouteAllowed(s, false, false, false));
        Assert.Equal("gate-pending", UsbEngineGate.WhyDisabled(s, false, false, false));
    }

    [Fact]
    public void Gate_killSwitchWinsOverExperimental()
    {
        var s = new AppSettings { EnableExperimentalUsbRoutes = true, UsbEngineOff = true };
        Assert.False(UsbEngineGate.UsbRoutesAllowed(s, false, false, false));
        Assert.Equal("settings", UsbEngineGate.WhyDisabled(s, false, false, false));
        s.UsbEngineOff = false;
        Assert.True(UsbEngineGate.UsbRoutesAllowed(s, false, false, false));
        Assert.False(UsbEngineGate.UsbRoutesAllowed(s, cliNoUsbEngine: true, false, false));
        Assert.False(UsbEngineGate.UsbRoutesAllowed(s, false, disableFilePresent: true, false));
        Assert.True(UsbEngineGate.UsbRoutesAllowed(new AppSettings(), false, false, experimentalFilePresent: true));
        Assert.False(UsbEngineGate.MidiRouteAllowed(s, false, false, false));
        s.EnableExperimentalMidiRoute = true;
        Assert.True(UsbEngineGate.MidiRouteAllowed(s, false, false, false));
    }

    [Fact]
    public void Settings_normalizesUsbMode_andIgnoresUnknown()
    {
        var s = new AppSettings { UsbConnectionMode = "USB" };
        s.NormalizeUsb();
        Assert.Equal(UsbConnectionModeName.UsbOnly, s.UsbConnectionMode);
        s.UsbConnectionMode = "nope";
        s.NormalizeUsb();
        Assert.Equal(UsbConnectionModeName.Auto, s.UsbConnectionMode);
    }

    [Fact]
    public void SerialHash_isStable_andNeverStoresRaw()
    {
        string salt = PhoneLinkHistory.NewSalt();
        string a = UsbSerialHash.Hash("ABC123", salt);
        string b = UsbSerialHash.Hash("ABC123", salt);
        Assert.Equal(a, b);
        Assert.Equal(16, a.Length);
        Assert.NotEqual(UsbSerialHash.Hash("ABC123", PhoneLinkHistory.NewSalt()), a);
        Assert.Equal("", UsbSerialHash.Hash(null, salt));
        Assert.False(UsbSerialHash.LooksLikeRawSerial(a));
        Assert.True(UsbSerialHash.LooksLikeRawSerial("R5CR123456789"));
    }

    [Fact]
    public void ProbeReport_roundTrips_andRedactsAddresses()
    {
        var rec = new UsbDeviceRecord
        {
            VendorId = 0x04E8,
            ProductId = 0x6860,
            HashedSerial = "aabbccddeeff0011",
            Name = "SAMSUNG Mobile USB Composite Device",
            BoundService = "usbccgp",
            Kind = UsbFunctionKind.ChargingOrComposite,
            MsOsDescriptorUnknown = true,
            Interfaces =
            {
                new UsbInterfaceRecord { Index = 0, InterfaceClass = 0x06, InterfaceSubClass = 0x01, InterfaceProtocol = 0x01, BoundService = "WUDFWpdMtp" },
            },
        };
        rec.Kind = UsbDeviceRecord.Classify(rec.VendorId, rec.ProductId, rec.BoundService, rec.Interfaces, rec.Name);
        var report = new UsbProbeReport
        {
            ToolVersion = "0.8.0-dev",
            ModeNote = "charging-only",
            Gate = "PENDING-HARDWARE",
            Devices = { rec },
        };
        string text = report.ToReportText();
        Assert.Contains("hashed_serial=aabbccddeeff0011", text);
        Assert.DoesNotContain("C:\\Users\\", text, StringComparison.OrdinalIgnoreCase);
        var back = UsbProbeReport.Parse(text);
        Assert.Single(back.Devices);
        Assert.Equal(0x04E8, back.Devices[0].VendorId);
        Assert.Equal(UsbFunctionKind.Mtp, back.Devices[0].Kind);
        Assert.Equal("PENDING-HARDWARE", back.SecureBoot);
        Assert.Throws<FormatException>(() => UsbProbeReport.Parse("device\n  hashed_serial=R5CR123456789\n"));
        Assert.Equal("[redacted]", UsbProbeReport.SanitizeLine(@"C:\Users\someone\log.txt"));
        Assert.Equal("[redacted]", UsbProbeReport.SanitizeLine("peer 192.168.42.2"));
        Assert.False(UsbProbeReport.LooksLikeAddressOrUser("SAMSUNG Mobile USB Composite Device"));
    }

    [Fact]
    public void Classify_accessoryAndTetherAndAdb()
    {
        Assert.Equal(UsbFunctionKind.Accessory, UsbDeviceRecord.Classify(0x18D1, 0x2D00, "", null, "CordMic"));
        Assert.Equal(UsbFunctionKind.Accessory, UsbDeviceRecord.Classify(0x18D1, 0x2D01, "WinUSB", null, ""));
        Assert.Equal(UsbFunctionKind.TetherRndis, UsbDeviceRecord.Classify(0x04E8, 0x6863, "RNDIS", null, "SAMSUNG Mobile USB Remote NDIS Network Device"));
        var adb = new[] { new UsbInterfaceRecord { InterfaceClass = 0xFF, BoundService = "WinUSB", MsOsCompatibleWinUsb = true } };
        Assert.Equal(UsbFunctionKind.AdbWinUsb, UsbDeviceRecord.Classify(0x18D1, 0x4EE7, "WinUSB", adb, "ADB"));
        var midi = new[] { new UsbInterfaceRecord { InterfaceClass = 0x01, InterfaceSubClass = 0x03 } };
        Assert.Equal(UsbFunctionKind.Midi, UsbDeviceRecord.Classify(0x04E8, 0x6860, "", midi, ""));
    }

    [Fact]
    public void AoaHandshake_orderAndTimeouts()
    {
        var setups = AoaProtocol.HandshakeSetups("0.8.0-beta.1", "deadbeefcafebabe");
        Assert.Equal(8, setups.Count);
        Assert.Equal(AoaProtocol.GetProtocol, setups[0].Request);
        Assert.Equal(AoaProtocol.DeviceToHostVendor, setups[0].RequestType);
        Assert.Equal(2, setups[0].Data.Length);
        Assert.Equal(1000, setups[0].TimeoutMs);
        Assert.Equal(AoaProtocol.Start, setups[^1].Request);
        Assert.All(setups, s => Assert.Equal(AoaProtocol.ControlTimeoutMs, s.TimeoutMs));
        Assert.Equal(5000, AoaProtocol.HandshakeBudgetMs);
        byte[] v = { 1, 0 };
        Assert.True(AoaProtocol.TryReadProtocolVersion(v, out int ver));
        Assert.Equal(1, ver);
        Assert.False(AoaProtocol.TryReadProtocolVersion(new byte[] { 0, 0 }, out _));
        Assert.Contains(UsbIds.Manufacturer, Encoding.UTF8.GetString(setups[1].Data));
        Assert.Contains(UsbIds.Model, Encoding.UTF8.GetString(setups[2].Data));
        Assert.Equal("start-sent", AoaProtocol.DescribeResult(true, 1, true));
        Assert.Equal(16, AoaProtocol.NewInstallSerial().Length);
    }

    [Fact]
    public void MidiProbe_roundTrip_andDetectsLossTruncation()
    {
        var payload = Encoding.UTF8.GetBytes("cordmic-midi-probe");
        var msg = MidiProbeCodec.Encode(42, 1234, payload);
        Assert.Equal(0xF0, msg[0]);
        Assert.Equal(0x7D, msg[1]);
        Assert.Equal(0xF7, msg[^1]);
        Assert.True(MidiProbeCodec.TryDecode(msg, out uint seq, out uint t, out var body, out var err));
        Assert.Null(err);
        Assert.Equal(42u, seq);
        Assert.Equal(1234u, t);
        Assert.Equal(payload, body);

        var trunc = msg.AsSpan(0, msg.Length - 1).ToArray(); // drop F7
        Assert.False(MidiProbeCodec.TryDecode(trunc, out _, out _, out _, out var terr));
        Assert.Equal("truncated", terr);

        var received = new List<(uint, uint, int, string?)>();
        for (uint i = 0; i < 10; i++)
        {
            if (i == 3) continue; // lost
            if (i == 7) { received.Add((i, i * 5, 0, "truncated")); continue; }
            received.Add((i, i * 5, payload.Length, null));
        }
        var st = MidiProbeCodec.Measure(received, 0, 10);
        Assert.Equal(2, st.Lost); // seq 3 skipped, seq 7 truncated (not counted as unique)
        Assert.Equal(1, st.Truncated);
        Assert.False(st.MeetsThroughputGate(1));
        // Perfect 10 ms stream at 32 bytes * 100 messages / second = 25.6 kbit — below 200.
        // A 40-byte payload every 1 ms for 1 s is 320 kbit/s.
        var ok = new List<(uint, uint, int, string?)>();
        for (uint i = 0; i < 1000; i++) ok.Add((i, i, 40, null));
        var good = MidiProbeCodec.Measure(ok, 0, 1000);
        Assert.True(good.MeetsThroughputGate(1.0));
        Assert.True(good.MeetsJitterGate(10));
        Assert.Equal(0, good.Lost);
    }

    [Fact]
    public void MidiPack7_preservesHighBits()
    {
        byte[] raw = { 0x00, 0x80, 0xFF, 0x7F, 0x81, 0x10, 0x90, 0x01 };
        var packed = MidiProbeCodec.Pack7(raw);
        Assert.All(packed, b => Assert.True(b <= 0x7F));
        Assert.Equal(raw, MidiProbeCodec.Unpack7(packed, raw.Length));
    }

    [Fact]
    public void CatalogPlan_refusesUnsigned_andBypassLanguage()
    {
        var missing = CatalogInstallPlan.Evaluate(false, false, false, false);
        Assert.False(missing.MayInstall);
        var unsigned = CatalogInstallPlan.Evaluate(true, false, false, false);
        Assert.False(unsigned.MayInstall);
        Assert.Equal(CatalogInstallPlan.PackageTrust.Unsigned, unsigned.Trust);
        var untrusted = CatalogInstallPlan.Evaluate(true, true, false, false);
        Assert.False(untrusted.MayInstall);
        var ok = CatalogInstallPlan.Evaluate(true, true, true, true);
        Assert.True(ok.MayInstall);
        Assert.True(CatalogInstallPlan.HardwareIdAllowed(UsbIds.HardwareIdAccessory));
        Assert.False(CatalogInstallPlan.HardwareIdAllowed(@"USB\VID_04E8&PID_6860"));
        Assert.True(CatalogInstallPlan.OemNamePlausible("oem42.inf"));
        Assert.False(CatalogInstallPlan.OemNamePlausible("..\\evil.inf"));
        Assert.True(CatalogInstallPlan.TextSuggestsSignatureBypass("turn off Secure Boot"));
        Assert.True(CatalogInstallPlan.TextSuggestsSignatureBypass("enable test-signing"));
        Assert.False(CatalogInstallPlan.TextSuggestsSignatureBypass("Windows may show an install prompt. Do not turn anything off."));
    }

    [Fact]
    public void AccessoryIds_areGoogleAoaOnly()
    {
        Assert.Equal(0x18D1, UsbIds.GoogleVid);
        Assert.Equal(0x2D00, UsbIds.AccessoryPid);
        Assert.Equal(0x2D01, UsbIds.AccessoryAdbPid);
    }
}
