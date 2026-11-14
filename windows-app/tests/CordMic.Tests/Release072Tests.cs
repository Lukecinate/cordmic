using System.Text.RegularExpressions;
using CordMic.Core;

/// <summary>
/// 0.7.2: Settings window crash (layout inside a constructor), headless page layout, tethering diagnosis and guidance,
/// tray update path, and "mic only" (no mobile data) routing.
/// </summary>
public class Release072Tests
{
    private static string RepoDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "CordMic.sln"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }

    private static string RepoFile(string rel) => File.ReadAllText(Path.Combine(RepoDir(), rel));

    private static IEnumerable<(string File, string Text)> AppSources() =>
        Directory.GetFiles(Path.Combine(RepoDir(), "src", "CordMic.App"), "*.cs").Select(f => (Path.GetFileName(f), File.ReadAllText(f)));

    /// <summary>Body of the first method/constructor whose signature matches <paramref name="signature"/> (brace matched).</summary>
    private static string Body(string src, string signature)
    {
        int i = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(i >= 0, "not found: " + signature);
        int open = src.IndexOf('{', i), depth = 0;
        for (int j = open; j < src.Length; j++)
        {
            if (src[j] == '{') depth++;
            else if (src[j] == '}' && --depth == 0) return src.Substring(open, j - open + 1);
        }
        throw new InvalidOperationException("unbalanced braces after " + signature);
    }

    // ---- Settings crash ------------------------------------------------------------------------------------------

    [Fact]
    public void LayoutGate_RefusesUntilBuiltAndWhileRunning()
    {
        var g = new LayoutGate();
        Assert.False(g.TryEnter());          // the 0.7.1 crash: layout during the constructor
        g.MarkBuilt();
        Assert.True(g.TryEnter());
        Assert.False(g.TryEnter());          // re-entrant pass (SetBounds raising Layout)
        g.Exit();
        Assert.True(g.TryEnter());
        g.Exit();
    }

    [Fact]
    public void PagePanel_CreatesEveryLabelBeforeAutoScroll()
    {
        var src = RepoFile("src/CordMic.App/SettingsForm.cs");
        var ctor = Body(src, "public PagePanel(SettingsForm f)");
        int auto = ctor.IndexOf("AutoScroll = true", StringComparison.Ordinal);
        Assert.True(auto > 0);
        foreach (var field in new[] { "_title = new Label", "_lead = new Label", "_keys = new Label", "_gate.MarkBuilt()" })
        {
            int at = ctor.IndexOf(field, StringComparison.Ordinal);
            Assert.True(at >= 0 && at < auto, field + " must come before AutoScroll");
        }
    }

    [Fact]
    public void EveryLayoutHandlerInTheAppIsGated()
    {
        // Audit of every form/control: a layout override or Resize handler must not touch fields before they exist.
        foreach (var (file, text) in AppSources())
        {
            foreach (Match m in Regex.Matches(text, @"protected override void OnLayout\(LayoutEventArgs e\)"))
            {
                var body = Body(text[m.Index..], "protected override void OnLayout");
                Assert.True(body.Contains("_gate.TryEnter()"), $"{file}: OnLayout must check its LayoutGate");
            }
            foreach (Match m in Regex.Matches(text, @"AutoScroll = true"))
            {
                // the gate must already be marked built in the same constructor
                var before = text[..m.Index];
                int ctorStart = before.LastIndexOf("public ", StringComparison.Ordinal);
                Assert.Contains("MarkBuilt()", before[ctorStart..]);
            }
        }
        var main = RepoFile("src/CordMic.App/MainForm.cs");
        Assert.Contains("if (!_gate.TryEnter()) return;", Body(main, "private void LayoutHero()"));
        Assert.Contains("_gate.MarkBuilt();", Body(main, "private void BuildUi()"));
        Assert.Contains("_gate = new LayoutGate();", Body(main, "private void RebuildUi()"));
    }

    [Fact]
    public void EveryCatalogItemHasARow()
    {
        var factory = Body(RepoFile("src/CordMic.App/SettingsForm.cs"), "public static Row? Create(SettingsForm f, SettingItem item)");
        foreach (var p in SettingsCatalog.Pages)
            foreach (var item in SettingsCatalog.Items(p))
                Assert.True(factory.Contains($"case \"{item.Id}\":"), $"no row for {p}/{item.Id}");
    }

    [Fact]
    public void ShowSettings_LogsAndExplainsAFailure()
    {
        var body = Body(RepoFile("src/CordMic.App/AppController.cs"), "public void ShowSettings(SettingsPage? page)");
        Assert.Contains("catch (Exception ex)", body);
        Assert.Contains("settings window failed", body);
        Assert.Contains("dlg.settingsFailed", body);
        Assert.False(string.IsNullOrEmpty(L.Table(L.English)["dlg.settingsFailed"]));
        Assert.False(string.IsNullOrEmpty(L.Table(L.Indonesian)["dlg.settingsFailed"]));
    }

    [Fact]
    public void UiSelfTest_IsWiredAndCoversAllPagesLanguagesAndScales()
    {
        var program = RepoFile("src/CordMic.App/Program.cs");
        Assert.Contains("UiSelfTest.Run(ctx", program);
        var st = RepoFile("src/CordMic.App/UiSelfTest.cs");
        Assert.Contains("\"--self-test-ui\"", st);
        Assert.Contains("{ 96, 144, 192 }", st);
        Assert.Contains("L.Indonesian", st);
        var form = RepoFile("src/CordMic.App/SettingsForm.cs");
        Assert.Contains("foreach (var p in SettingsCatalog.Pages)", Body(form, "internal IEnumerable<string> SelfTest(int dpi)"));
    }

    // ---- headless page layout --------------------------------------------------------------------------------------

    public static IEnumerable<object[]> PagesAndDpis() =>
        from p in SettingsCatalog.Pages from dpi in new[] { 96, 120, 144, 192 } from w in new[] { 380, 576, 900 } select new object[] { p, dpi, w };

    [Theory]
    [MemberData(nameof(PagesAndDpis))]
    public void Compose_StacksEveryPageWithoutOverlap(SettingsPage page, int dpi, int clientLogical)
    {
        int client = SettingsLayout.Px(clientLogical, dpi);
        // fake text measure: 20 logical px per line, wrapping at 9 px per character
        Func<int, int> lines(string text) => w => SettingsLayout.Px(20, dpi) * Math.Max(1, (int)Math.Ceiling(text.Length * SettingsLayout.Px(9, dpi) / (double)Math.Max(1, w)));
        var items = SettingsCatalog.Items(page).ToList();
        var rows = items.Select(i => lines(L.T(i.LabelKey) + (i.HelpKey is null ? "" : L.T(i.HelpKey)))).ToList();
        var g = SettingsLayout.Compose(client, dpi, lines(L.T(SettingsCatalog.TitleKey(page))), lines("lead text for the page"), rows, lines(L.T("set.keys")));
        Assert.Equal(items.Count, g.Rows.Length);
        Assert.True(g.TextWidth >= SettingsLayout.Px(200, dpi));
        Assert.True(g.Lead.Y >= g.Title.Bottom);
        int prev = g.Lead.Bottom;
        foreach (var r in g.Rows)
        {
            Assert.True(r.Y >= prev, "rows overlap");
            Assert.True(r.H > 0);
            Assert.Equal(g.TextWidth, r.W);
            prev = r.Bottom;
        }
        Assert.True(g.Keys.Y >= prev);
        Assert.Equal(g.Keys.Bottom + SettingsLayout.Px(SettingsLayout.ContentPadY, dpi), g.Total);
    }

    [Fact]
    public void Compose_ToleratesEmptyAndNegativeMeasures()
    {
        var g = SettingsLayout.Compose(0, 96, _ => -5, _ => 0, new List<Func<int, int>> { _ => -1, _ => 10 }, _ => 0);
        Assert.Equal(200, g.TextWidth);
        Assert.Equal(0, g.Title.H);
        Assert.Equal(0, g.Rows[0].H);
        Assert.True(g.Rows[1].Y >= g.Rows[0].Bottom);
        Assert.True(g.Total > 0);
    }

    // ---- tethering diagnosis ---------------------------------------------------------------------------------------

    private static UsbDeviceInfo Dev(string hw, string? desc, string[]? compat = null, bool net = false, int problem = 0) =>
        new(new[] { hw }, compat ?? Array.Empty<string>(), desc, null, net, problem);

    private static readonly UsbDeviceInfo SamsungComposite = Dev(@"USB\VID_04E8&PID_6860&REV_0400", "SAMSUNG Mobile USB Composite Device");
    private static readonly UsbDeviceInfo SamsungRndis = Dev(@"USB\VID_04E8&PID_6863&MI_00", "SAMSUNG Mobile USB Remote NDIS Network Device",
        new[] { @"USB\Class_E0&SubClass_01&Prot_03" }, net: true);
    private static readonly UsbDeviceInfo Mouse = Dev(@"USB\VID_046D&PID_C52B", "USB Input Device");
    private static readonly UsbDeviceInfo EthernetDongle = Dev(@"USB\VID_0BDA&PID_8153", "Realtek USB GbE Family Controller",
        new[] { @"USB\Class_FF&SubClass_FF&Prot_00" }, net: true);

    [Fact]
    public void Diagnose_ReadyWhenAnAdapterHasAnAddress()
    {
        var r = TetherDiagnosis.Diagnose(new[] { new TetherAdapterInfo("SAMSUNG Mobile USB Remote NDIS Network Device", "Ethernet 4", true, true) }, new[] { SamsungRndis });
        Assert.Equal(TetherLink.Ready, r.Link);
    }

    [Fact]
    public void Diagnose_PhoneOnUsbWithoutNetworkMeansTetheringOff()
    {
        // The user's report: phone plugged in, "Charging phone only", no RNDIS device.
        var r = TetherDiagnosis.Diagnose(Array.Empty<TetherAdapterInfo>(), new[] { Mouse, SamsungComposite });
        Assert.Equal(TetherLink.PhoneWithoutTethering, r.Link);
        Assert.Equal("SAMSUNG Mobile USB Composite Device", r.DeviceName);
        foreach (var vid in new[] { "18D1", "2717", "22D9", "2D95", "12D1" })
            Assert.Equal(TetherLink.PhoneWithoutTethering, TetherDiagnosis.Diagnose(Array.Empty<TetherAdapterInfo>(), new[] { Dev($@"USB\VID_{vid}&PID_0001", "MTP") }).Link);
    }

    [Fact]
    public void Diagnose_NothingWhenOnlyPcPeripheralsAreOnUsb()
    {
        Assert.Equal(TetherLink.NothingOnUsb, TetherDiagnosis.Diagnose(Array.Empty<TetherAdapterInfo>(), new[] { Mouse, EthernetDongle }).Link);
        Assert.Equal(TetherLink.NothingOnUsb, TetherDiagnosis.Diagnose(Array.Empty<TetherAdapterInfo>(), null).Link);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(28)]
    [InlineData(43)]
    public void Diagnose_DriverProblemCarriesTheCode(int code)
    {
        var broken = SamsungRndis with { ProblemCode = code };
        var r = TetherDiagnosis.Diagnose(Array.Empty<TetherAdapterInfo>(), new[] { SamsungComposite, broken });
        Assert.Equal(TetherLink.DriverProblem, r.Link);
        Assert.Equal(code, r.ProblemCode);
        Assert.Contains($"code {code}", L.Table(L.English)["status.tether.driver.detail"].Replace("{0}", code.ToString()));
        // a device without a driver often has no friendly name, only RNDIS class codes
        var noDriver = Dev(@"USB\VID_04E8&PID_6863&MI_00", "RNDIS", new[] { @"USB\Class_E0&SubClass_01&Prot_03" }, problem: 28);
        Assert.Equal(TetherLink.DriverProblem, TetherDiagnosis.Diagnose(Array.Empty<TetherAdapterInfo>(), new[] { noDriver }).Link);
    }

    [Fact]
    public void Diagnose_StartingWhileTheAdapterHasNoAddress()
    {
        Assert.Equal(TetherLink.Starting, TetherDiagnosis.Diagnose(new[] { new TetherAdapterInfo("Remote NDIS based Internet Sharing Device", "Ethernet 4", true, false) }, Array.Empty<UsbDeviceInfo>()).Link);
        Assert.Equal(TetherLink.Starting, TetherDiagnosis.Diagnose(new[] { new TetherAdapterInfo("Remote NDIS based Internet Sharing Device", "Ethernet 4", false, false) }, null).Link);
        Assert.Equal(TetherLink.Starting, TetherDiagnosis.Diagnose(Array.Empty<TetherAdapterInfo>(), new[] { SamsungRndis }).Link);
    }

    [Fact]
    public void Diagnose_LogLineNeverContainsAnAddress()
    {
        foreach (TetherLink l in Enum.GetValues<TetherLink>())
        {
            var line = new TetherDiagnosisResult(l, "SAMSUNG Mobile USB Remote NDIS Network Device", 10).LogLine();
            Assert.DoesNotMatch(@"\d+\.\d+\.\d+\.\d+", line);
        }
    }

    [Fact]
    public void Guidance_ExistsInBothLanguagesWithTheExactSamsungPath()
    {
        foreach (var code in new[] { L.English, L.Indonesian })
        {
            var t = L.Table(code);
            foreach (TetherLink l in Enum.GetValues<TetherLink>())
            {
                if (l == TetherLink.Ready) { Assert.Null(TetherDiagnosis.HelpKey(l)); continue; }
                var (a, b, c) = TetherDiagnosis.Keys(l);
                Assert.True(t.ContainsKey(a) && t.ContainsKey(b) && t.ContainsKey(c), $"{code}: missing keys for {l}");
                Assert.True(t.ContainsKey(TetherDiagnosis.HelpKey(l)!), $"{code}: missing help for {l}");
            }
        }
        var en = L.Table(L.English);
        Assert.Contains("Settings › Connections › Mobile Hotspot and Tethering › USB tethering", en["status.tether.off.detail"]);
        Assert.Contains("USB notification", en["status.tether.steps"]);
        Assert.Contains("unlocked", en["status.tether.steps"]);
        var id = L.Table(L.Indonesian);
        Assert.Contains("Pengaturan › Koneksi › Hotspot Seluler dan Tethering › Tethering USB", id["status.tether.off.detail"]);
        Assert.Contains("notifikasi USB", id["status.tether.steps"]);
        Assert.Contains("tidak terkunci", id["status.tether.steps"]);
    }

    [Fact]
    public void Status_UsesTheDiagnosisAndShowsGuidanceInThePhoneRow()
    {
        var ctl = RepoFile("src/CordMic.App/AppController.cs");
        Assert.Contains("? TetherStatus(Tether)", ctl);
        Assert.Contains("Log(\"tether: \" + line)", ctl);
        Assert.Contains("Guidance = () => app.Status.Help", RepoFile("src/CordMic.App/SettingsForm.cs"));
        // NetWatcher logs the first scan even when nothing is found (0.7.1 logged nothing at all)
        Assert.Contains("private string? _lastKey;", RepoFile("src/CordMic.App/NetWatcher.cs"));
        // read-only: the probe only queries Setup API
        var probe = RepoFile("src/CordMic.App/UsbProbe.cs");
        foreach (var write in new[] { "SetupDiSetDeviceRegistryProperty", "SetupDiCallClassInstaller", "SetupDiChangeState", "CM_Disable_DevNode", "CM_Enable_DevNode" })
            Assert.DoesNotContain(write, probe);
    }

    // ---- updates from the tray ---------------------------------------------------------------------------------------

    [Fact]
    public void TrayCheck_StartsBeforeTheSettingsWindowOpens()
    {
        var body = Body(RepoFile("src/CordMic.App/AppController.cs"), "private void OnTrayUpdateClicked()");
        int check = body.IndexOf("_updates.CheckAsync", StringComparison.Ordinal);
        int show = body.IndexOf("ShowSettings(SettingsPage.Updates);\n", StringComparison.Ordinal);
        Assert.True(check > 0 && show > check, "the check must not depend on the Settings window");
        Assert.Contains("UpdatePhase.Available", body);
        Assert.Contains("UpdateNowAsync", body);
    }

    // ---- mic only: never spend mobile data -----------------------------------------------------------------------------

    [Fact]
    public void TetherTask_RemovesDefaultRoutesAndDnsThroughThePhone()
    {
        var ps = RepoFile("installer/tools/set-tether-metric.ps1");
        Assert.Contains("-IgnoreDefaultRoutes $want", ps);
        Assert.Contains("Remove-NetRoute -InputObject $r", ps);
        Assert.Contains("'0.0.0.0/0', '::/0'", ps);
        Assert.Contains("-ServerAddresses '127.0.0.1'", ps);
        Assert.Contains("Set-CordMicMicOnly $a", ps);
        Assert.Contains("allow-phone-internet", ps);
        Assert.Contains("$metric  = 9999", ps);                          // kept as a second line of defence
        Assert.DoesNotContain("Disable-NetAdapter", ps);
        Assert.DoesNotContain("Restart-NetAdapter", ps);
        var setup = RepoFile("installer/tools/cordmic-setup.ps1");
        Assert.Contains("Reset-TetherAdapters", Body(setup, "if ($Action -eq 'Uninstall')"));
    }

    [Fact]
    public void ViaPhone_IsLoggedAndShownBeforeStreaming()
    {
        var ctl = RepoFile("src/CordMic.App/AppController.cs");
        Assert.Contains("routes PC internet through the phone (mobile data)", ctl);
        Assert.Contains("status.viaPhoneIdle.title", ctl);
        foreach (var code in new[] { L.English, L.Indonesian })
            foreach (var k in new[] { "status.viaPhoneIdle.title", "status.viaPhoneIdle.detail", "status.viaPhoneIdle.tip" })
                Assert.True(L.Table(code).ContainsKey(k), code + " " + k);
    }

    [Fact]
    public void Receiver_StillAcceptsOnlyTheTetherSubnetAndLoopback()
    {
        // Audio never leaves the USB link: sources outside the tether subnet are refused (0.6.1 policy, unchanged).
        var subnet = Ipv4Subnet.From(System.Net.IPAddress.Parse("192.168.42.10"), 24);
        var policy = new SourcePolicy(new[] { subnet });
        Assert.True(policy.IsTrustedNetwork(System.Net.IPAddress.Parse("192.168.42.129")));
        Assert.True(policy.IsTrustedNetwork(System.Net.IPAddress.Loopback));
        Assert.False(policy.IsTrustedNetwork(System.Net.IPAddress.Parse("10.0.0.5")));
    }
}
