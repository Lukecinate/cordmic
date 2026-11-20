using System.Security.Cryptography;
using System.Text;
using CordMic.Core;
using Xunit;

public class AppUpdateTests
{
    [Theory]
    [InlineData("0.5.5", "0.5.4", true)]
    [InlineData("v0.5.5", "0.5.5", false)]
    [InlineData("0.5.4", "0.5.5", false)]
    [InlineData("1.0.0", "0.9.9", true)]
    public void NewerDetection(string cand, string cur, bool newer)
    {
        Assert.Equal(newer, AppUpdate.IsNewer(cand, cur));
        Assert.Equal(AppUpdate.CompareVersions(cand, cur) < 0, AppUpdate.IsDowngrade(cand, cur));
    }

    [Fact]
    public void NeverTreatsSameVersionAsUpgradeOrDowngrade()
    {
        Assert.False(AppUpdate.IsNewer("v0.5.5", "0.5.5"));
        Assert.False(AppUpdate.IsDowngrade("0.5.5", "v0.5.5"));
    }

    [Fact]
    public void ParseSha256SumsAcceptsStarAndPlainLines()
    {
        var text = """
            # comment
            0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  CordMic-0.5.5-release.apk
            fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210 *CordMic-Setup-0.5.5.exe
            """.Replace("\n", "\n");
        var d = AppUpdate.ParseSha256Sums(text);
        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", d["CordMic-0.5.5-release.apk"]);
        Assert.Equal("fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210", d["CordMic-Setup-0.5.5.exe"]);
        Assert.Null(AppUpdate.HashForAsset(d, "missing.bin"));
        Assert.Equal(d["CordMic-Setup-0.5.5.exe"], AppUpdate.HashForAsset(d, "dir/CordMic-Setup-0.5.5.exe"));
    }

    [Fact]
    public void HashMismatchIsRejected()
    {
        var payload = Encoding.UTF8.GetBytes("hello");
        var good = Convert.ToHexString(SHA256.HashData(payload));
        Assert.True(AppUpdate.HashMatches(payload, good));
        Assert.False(AppUpdate.HashMatches(payload, "00" + good[2..]));
        Assert.False(AppUpdate.HashMatches(payload, ""));
    }

    [Fact]
    public void ParseLatestJsonPicksApkSetupAndSums()
    {
        const string json = """
            {
              "tag_name": "v0.5.5",
              "assets": [
                {"name": "CordMic-0.5.5-release.apk", "browser_download_url": "https://example.test/apk"},
                {"name": "CordMic-Setup-0.5.5.exe", "browser_download_url": "https://example.test/setup"},
                {"name": "SHA256SUMS", "browser_download_url": "https://example.test/sums"}
              ]
            }
            """;
        var info = AppUpdate.ParseLatestJson(json);
        Assert.Equal("v0.5.5", info.Tag);
        Assert.Equal("https://example.test/apk", info.ApkUrl);
        Assert.Equal("https://example.test/setup", info.SetupUrl);
        Assert.Equal("https://example.test/sums", info.SumsUrl);
        Assert.Equal("CordMic-Setup-0.5.5.exe", info.SetupName);
    }


    [Theory]
    [InlineData("0.8.0-beta.1", "0.7.2", true)]
    [InlineData("0.8.0", "0.8.0-beta.1", true)]
    [InlineData("0.8.0-beta.1", "0.8.0", false)]
    [InlineData("0.8.0-beta.2", "0.8.0-beta.1", true)]
    [InlineData("0.8.0-beta.1", "0.8.0-beta.1", false)]
    [InlineData("v0.8.0-beta.1+abc", "0.8.0-beta.1", false)]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", false)]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha", true)]
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta", false)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11", false)]
    [InlineData("1.0.0-rc.1", "1.0.0", false)]
    public void SemVer_PreReleaseOrdering(string cand, string cur, bool newer)
    {
        Assert.Equal(newer, AppUpdate.IsNewer(cand, cur));
        Assert.True(AppUpdate.SemVer.TryParse(cand, out _));
        Assert.True(AppUpdate.SemVer.TryParse(cur, out _));
    }

    [Fact]
    public void SemVer_DoesNotTreatBetaAsTheStableVersion()
    {
        Assert.True(AppUpdate.SemVer.TryParse("v0.8.0-beta.1", out var v));
        Assert.True(v.IsPrerelease);
        Assert.Equal("0.8.0-beta.1", v.ToString());
        Assert.Equal(0, v.Major);
        Assert.Equal(8, v.Minor);
        Assert.Equal(0, v.Patch);
        Assert.True(AppUpdate.IsNewer("0.8.0", "0.8.0-beta.1"));
        Assert.False(AppUpdate.IsNewer("0.8.0-beta.1", "0.8.0"));
        Assert.Equal("CordMic-Setup-0.8.0-beta.1.exe", AppUpdate.SetupAssetName("v0.8.0-beta.1"));
        Assert.Equal("CordMic-Portable-0.8.0-beta.1.zip", AppUpdate.PortableAssetName("0.8.0-beta.1"));
        Assert.Equal("CordMic-Setup-0.7.2.exe", AppUpdate.SetupAssetName("v0.7.2"));
    }

    [Fact]
    public void ShouldOffer_IgnoresPrereleasesAndDraftsSoStableUsersStayOnLatest()
    {
        const string beta = """
            {
              "tag_name": "v0.8.0-beta.1",
              "prerelease": true,
              "draft": false,
              "assets": [
                {"name": "CordMic-Setup-0.8.0-beta.1.exe", "browser_download_url": "https://example.test/setup"}
              ]
            }
            """;
        var info = AppUpdate.ParseLatestJson(beta);
        Assert.True(info.Prerelease);
        Assert.False(info.Draft);
        Assert.False(AppUpdate.ShouldOffer(info, "0.7.2"));
        Assert.False(AppUpdate.ShouldOffer(info, "0.8.0-beta.1"));
        var byTag = AppUpdate.ParseLatestJson("""{"tag_name":"v0.8.0-beta.1","assets":[]}""");
        Assert.False(byTag.Prerelease);
        Assert.False(AppUpdate.ShouldOffer(byTag, "0.7.2"));
        var draft = AppUpdate.ParseLatestJson("""{"tag_name":"v0.8.0","draft":true,"assets":[]}""");
        Assert.True(draft.Draft);
        Assert.False(AppUpdate.ShouldOffer(draft, "0.7.2"));
        var stable = AppUpdate.ParseLatestJson("""{"tag_name":"v0.8.0","prerelease":false,"assets":[]}""");
        Assert.True(AppUpdate.ShouldOffer(stable, "0.7.2"));
        Assert.True(AppUpdate.ShouldOffer(stable, "0.8.0-beta.1"));
        Assert.False(AppUpdate.ShouldOffer(stable, "0.8.0"));
        Assert.False(AppUpdate.ShouldOffer(stable, "0.8.1"));
    }

    [Fact]
    public void DisplayVersion_KeepsPreReleaseLabelAndDropsBuildMetadata()
    {
        Assert.Equal("0.8.0-beta.1", AppUpdate.DisplayVersion("0.8.0-beta.1+abc123", new Version(0, 8, 0, 0)));
        Assert.Equal("0.7.2", AppUpdate.DisplayVersion("0.7.2", new Version(0, 7, 2, 0)));
        Assert.Equal("0.7.2", AppUpdate.DisplayVersion(null, new Version(0, 7, 2, 0)));
    }
}
