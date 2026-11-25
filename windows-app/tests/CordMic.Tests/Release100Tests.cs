using CordMic.Core;
using CordMic.Core.Usb;

/// <summary>
/// 1.0.0: Settings page wheel/keyboard scrolling, updater offers the stable to 0.7.2 and 0.8 betas,
/// and release invariants (USB gate still pending, no 0.8.0-stable claim).
/// </summary>
public class Release100Tests
{
    private static string SlnDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "CordMic.sln"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(SlnDir());
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, ".git")) && !File.Exists(Path.Combine(d.FullName, "CHANGELOG.md")))
            d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }

    private static string AppFile(string rel) => File.ReadAllText(Path.Combine(SlnDir(), rel)).Replace("\r\n", "\n").Replace("\r", "\n");

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

    // ---- wheel math ----------------------------------------------------------------------------------------------

    [Fact]
    public void NextWheelScrollY_ScrollsUpAndDownAndClamps()
    {
        // Getter Y is -200 when scrolled down 200 px. Wheel up (delta +120), step 48 → 152.
        Assert.Equal(152, SettingsLayout.NextWheelScrollY(-200, 120, 48, 800));
        Assert.Equal(248, SettingsLayout.NextWheelScrollY(-200, -120, 48, 800));
        Assert.Equal(0, SettingsLayout.NextWheelScrollY(0, 120, 48, 800));
        Assert.Equal(800, SettingsLayout.NextWheelScrollY(-800, -120, 48, 800));
        Assert.Equal(0, SettingsLayout.NextWheelScrollY(0, 120, 48, 0));
        Assert.Equal(10, SettingsLayout.NextWheelScrollY(-10, 120, -8, 100)); // negative step is 0, position unchanged
        Assert.Equal(0, SettingsLayout.NextWheelScrollY(0, -120, 48, -5));   // negative max treated as 0
        // SystemInformation.MouseWheelScrollLines < 0 means "scroll by page": step = viewport height.
        Assert.Equal(400, SettingsLayout.NextWheelScrollY(0, -120, 400, 900));
    }

    [Fact]
    public void NeedsScroll_ConnectionPageExceedsMinAnd14InchHeights()
    {
        // Conservative row heights (label + one help line) — real rows are at least this tall.
        Func<int, int> title = _ => 28, lead = _ => 36, keys = _ => 20;
        foreach (var page in SettingsCatalog.Pages)
        {
            var rows = SettingsCatalog.Items(page).Select(_ => (Func<int, int>)(_ => 52)).ToList();
            foreach (var (dpi, workW, workH) in new[]
            {
                (96, 1366, 768),            // 14-inch 1366×768 at 100 %
                (144, 1920, 1080),          // 14-inch FHD at 150 %
                (192, 1920, 1200),          // 200 % on a 14–15" panel
                (96, SettingsLayout.MinWidth, SettingsLayout.MinHeight),
            })
            {
                var (iw, ih) = SettingsLayout.InitialSize(dpi, workW, workH);
                var (_, mh) = SettingsLayout.MinimumSize(dpi, workW, workH);
                int clientW = SettingsLayout.ContentWidth(iw, dpi) + 2 * SettingsLayout.Px(SettingsLayout.ContentPadX, dpi);
                var g = SettingsLayout.Compose(clientW, dpi, title, lead, rows, keys);
                // A short window (minimum height) must be able to scroll any page that is taller than the viewport.
                // The Settings client area is a little shorter than the window (caption), so use mh as an upper bound.
                if (page == SettingsPage.Connection)
                {
                    Assert.True(SettingsLayout.NeedsScroll(g.Total, mh),
                        $"{page} @ {dpi} dpi on {workW}x{workH}: content {g.Total} should exceed min height {mh}");
                    Assert.True(g.Total > 0);
                }
                // Initial size on a 14" laptop may or may not need a scrollbar depending on the page; the
                // important invariant is that NeedsScroll is consistent and Compose stays within a sane range.
                Assert.Equal(g.Total > ih, SettingsLayout.NeedsScroll(g.Total, ih));
                Assert.True(g.Total < 8000, $"{page} @ {dpi} produced an absurd height {g.Total}");
            }
        }
    }

    [Fact]
    public void SettingsForm_InstallsPageWheelFilterAndForwardsFromEveryControl()
    {
        var form = AppFile("src/CordMic.App/SettingsForm.cs");
        var rows = AppFile("src/CordMic.App/SettingsRows.cs");
        Assert.Contains("new PageWheelFilter(_content, _content)", form);
        Assert.Contains("Application.AddMessageFilter(_wheel)", form);
        var dispose = Body(form, "protected override void Dispose(bool disposing)");
        Assert.Contains("Application.RemoveMessageFilter(_wheel)", dispose);
        Assert.Contains("class PageWheelFilter : IMessageFilter", rows);
        var pre = Body(rows, "public bool PreFilterMessage(ref Message m)");
        Assert.Contains("WM_MOUSEWHEEL", pre);
        Assert.Contains("ComboDropped", pre);
        Assert.Contains("_page.Wheel", pre);
        // Combos still keep the wheel while dropped down; TrackBar/ComboBox still forward otherwise.
        Assert.Contains("if (root is ComboBox { DroppedDown: true, Visible: true }) return true;", rows);
        Assert.Contains("protected override void WndProc(ref Message m) { if (!WheelGuard.Forward(this, ref m)) base.WndProc(ref m); }", rows);
        // WheelGuard also inspects the leaf, not only Parent (so a page itself can receive the wheel).
        var guard = Body(rows, "public static bool Forward(Control c, ref Message m)");
        Assert.Contains("for (Control? p = c; p is not null; p = p.Parent)", guard);
        var wheel = Body(form, "public void Wheel(int delta)");
        Assert.Contains("SettingsLayout.NextWheelScrollY", form);
        Assert.Contains("protected override void OnMouseWheel(MouseEventArgs e)", form);
        var keys = Body(form, "protected override bool ProcessCmdKey(ref Message msg, Keys keyData)");
        Assert.Contains("Keys.PageDown or Keys.PageUp", keys);
        Assert.Contains("_content.PageScroll", keys);
        // Ctrl+PageDown still switches pages and is handled first.
        Assert.True(keys.IndexOf("Keys.Control | Keys.PageDown", StringComparison.Ordinal)
                    < keys.IndexOf("Keys.PageDown or Keys.PageUp", StringComparison.Ordinal));
    }

    // ---- updater / gates -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("0.7.2", true)]
    [InlineData("0.8.0-beta.1", true)]
    [InlineData("0.8.0-beta.2", true)]
    [InlineData("0.8.0-beta.3", true)]
    [InlineData("1.0.0", false)]
    [InlineData("1.0.1", false)]
    public void ShouldOffer_V100_To072AndBetasButNotToItselfOrNewer(string current, bool offer)
    {
        var info = AppUpdate.ParseLatestJson("""{"tag_name":"v1.0.0","prerelease":false,"draft":false,"assets":[]}""");
        Assert.Equal(offer, AppUpdate.ShouldOffer(info, current));
    }

    [Fact]
    public void ShouldOffer_StillIgnoresPrereleasesWhenLatestIsStable()
    {
        var beta = AppUpdate.ParseLatestJson("""{"tag_name":"v1.0.0-beta.1","prerelease":true,"assets":[]}""");
        Assert.False(AppUpdate.ShouldOffer(beta, "0.7.2"));
        Assert.False(AppUpdate.ShouldOffer(beta, "0.8.0-beta.3"));
    }

    [Fact]
    public void HardwareGateGm1_RemainsUnpassedInThisRelease()
    {
        Assert.False(UsbEngineGate.HardwareGateGm1Passed);
        var src = File.ReadAllText(Path.Combine(SlnDir(), "src/CordMic.Core/Usb/UsbEngineGate.cs"));
        Assert.Contains("public const bool HardwareGateGm1Passed = false;", src);
    }

    [Fact]
    public void AppVersion_IsStableNotAPrerelease()
    {
        // 1.0.0 pinned 1.0.0 / versionCode 25 here; later releases pin their own numbers (Release110Tests).
        var csproj = File.ReadAllText(Path.Combine(SlnDir(), "src/CordMic.App/CordMic.App.csproj"));
        var v = System.Text.RegularExpressions.Regex.Match(csproj, "<Version>([^<]+)</Version>").Groups[1].Value;
        Assert.True(Version.Parse(v) >= new Version(1, 0, 0), v);
        Assert.DoesNotContain("beta", csproj, StringComparison.OrdinalIgnoreCase);
        var gradle = File.ReadAllText(Path.Combine(RepoRoot(), "android/app/build.gradle.kts"));
        var code = int.Parse(System.Text.RegularExpressions.Regex.Match(gradle, @"versionCode = (\d+)").Groups[1].Value);
        Assert.True(code >= 25, code.ToString());
    }

    [Theory]
    [InlineData("https://github.com/Lukecinate/cordmic/releases/download/v1.0.0/SHA256SUMS", true)]
    [InlineData("https://example.test/CordMic-Setup-1.0.0.exe", true)]
    [InlineData("http://example.test/CordMic-Setup-1.0.0.exe", false)]
    [InlineData("https://user:pass@example.test/setup.exe", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAllowedDownloadUrl_RequiresHttpsWithoutUserInfo(string? url, bool ok) =>
        Assert.Equal(ok, AppUpdate.IsAllowedDownloadUrl(url));

    [Fact]
    public void ParseLatestJson_FuzzNeverThrowsUnexpectedly()
    {
        var rng = new Random(20261123);
        for (int i = 0; i < 200; i++)
        {
            var bytes = new byte[rng.Next(0, 400)];
            rng.NextBytes(bytes);
            string json = i % 5 == 0
                ? "{\"tag_name\":\"v1.0.0\",\"prerelease\":false,\"assets\":" + (i % 2 == 0 ? "[]}" : "[")
                : System.Text.Encoding.UTF8.GetString(bytes).Replace('\0', ' ');
            try
            {
                var info = AppUpdate.ParseLatestJson(json);
                Assert.NotNull(info.Tag);
            }
            catch (System.Text.Json.JsonException) { /* malformed input is refused */ }
        }
    }

    [Fact]
    public void SettingsFrom072And08Betas_LoadOn100_AndExperimentalUsbStaysOff()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cordmic-100-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var p072 = Path.Combine(dir, "072.json");
            File.WriteAllText(p072, """
                {"Version":2,"AutoUpdate":true,"Language":"en","Jitter":"Normal","FirstRunDone":true,"MicGainDb":6}
                """);
            var s072 = AppSettings.Load(p072, out var problem072);
            Assert.Null(problem072);
            Assert.True(s072.AutoUpdate);
            Assert.Equal("en", s072.Language);
            Assert.False(s072.EnableExperimentalUsbRoutes);
            Assert.False(s072.UsbEngineOff);
            Assert.Equal(UsbConnectionModeName.Auto, s072.UsbConnectionMode);

            var p08 = Path.Combine(dir, "08beta.json");
            File.WriteAllText(p08, """
                {"Version":2,"AutoUpdate":false,"Language":"id","EnableExperimentalUsbRoutes":false,"UsbEngineOff":true,"UsbConnectionMode":"auto","FirstRunDone":true}
                """);
            var s08 = AppSettings.Load(p08, out var problem08);
            Assert.Null(problem08);
            Assert.False(s08.AutoUpdate);
            Assert.Equal("id", s08.Language);
            Assert.False(s08.EnableExperimentalUsbRoutes);
            Assert.True(s08.UsbEngineOff);
            Assert.Equal(UsbConnectionModeName.Auto, s08.UsbConnectionMode);
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
    }
}
