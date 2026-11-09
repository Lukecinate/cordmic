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
}
