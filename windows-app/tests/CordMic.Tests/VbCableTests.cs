using System.Security.Cryptography;
using CordMic.Core;
using Xunit;

public class VbCableTests
{
    [Fact]
    public void PinIsASha256HexAndOfficialHttpsUrl()
    {
        Assert.StartsWith("https://download.vb-audio.com/", VbCable.DownloadUrl);
        Assert.Contains("VBCABLE_Driver_Pack46.zip", VbCable.DownloadUrl);
        Assert.Equal(64, VbCable.Sha256.Length);
        Assert.Equal(VbCable.Sha256, VbCable.Sha256.ToLowerInvariant());
        Assert.Equal("https://vb-audio.com/Cable/", VbCable.PageUrl);
    }

    [Fact]
    public void HashMatchesRejectsWrongBytes()
    {
        var fake = RandomNumberGenerator.GetBytes(64);
        Assert.False(VbCable.HashMatches(fake));
    }

    [Fact]
    public void TrustedSignerNeedlesCoverVbAudioNames()
    {
        Assert.True(VbCable.SignerLooksTrusted("CN=VB-Audio Software, O=VB-Audio"));
        Assert.True(VbCable.SignerLooksTrusted("CN=Vincent Burel"));
        Assert.False(VbCable.SignerLooksTrusted("CN=Evil Corp"));
        Assert.False(VbCable.SignerLooksTrusted(null));
    }
}
