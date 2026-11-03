using System.Text.RegularExpressions;
using CordMic.Core;

namespace CordMic.Tests;

public class LocalizationTests
{
    [Fact]
    public void EnglishAndIndonesianHaveIdenticalKeys()
    {
        var en = L.En.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var id = L.Id.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.Equal(en, id);
    }

    [Fact]
    public void PlaceholdersMatchBetweenLanguages()
    {
        var ph = new Regex(@"\{\d+\}");
        foreach (var k in L.En.Keys)
        {
            var a = ph.Matches(L.En[k]).Select(m => m.Value).Distinct().OrderBy(x => x);
            var b = ph.Matches(L.Id[k]).Select(m => m.Value).Distinct().OrderBy(x => x);
            Assert.True(a.SequenceEqual(b), $"placeholder mismatch for '{k}'");
        }
    }

    [Theory]
    [InlineData("auto", "id-ID", "id")]
    [InlineData("auto", "id", "id")]
    [InlineData("auto", "in", "id")]
    [InlineData("auto", "en-US", "en")]
    [InlineData("auto", "", "en")]
    [InlineData(null, "ja-JP", "en")]
    [InlineData("en", "id-ID", "en")]
    [InlineData("id", "en-US", "id")]
    public void ResolveFollowsSettingThenOs(string? setting, string os, string expected) =>
        Assert.Equal(expected, L.Resolve(setting, os));

    [Fact]
    public void LookupUsesActiveLanguage()
    {
        try
        {
            L.Set(L.Indonesian);
            Assert.Equal("Keluar", L.T("menu.quit"));
            Assert.Equal("Langkah 2 dari 3", L.T("wiz.step", 2));
            L.Set(L.English);
            Assert.Equal("Exit", L.T("menu.quit"));
            Assert.Equal("Normal (10 ms) – recommended", Presets.Label(JitterPreset.Normal));
            Assert.Equal("missing.key", L.T("missing.key"));
        }
        finally { L.Set(L.English); }
    }
}
