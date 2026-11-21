using System.Security.Cryptography;
using System.Text;
using CordMic.Core;
using CordMic.Core.Updates;

namespace CordMic.Tests;

public class ManifestVerifierTests
{
    [Fact]
    public void ProductionKeys_areEmpty_untilMaintainerSendsThem()
    {
        Assert.Empty(ReleaseKeys.Production);
        Assert.Empty(ReleaseKeys.ProductionRevoked);
        Assert.False(ReleaseKeys.ManifestSigningRequired);
    }

    [Fact]
    public void SignAndVerify_acceptsAValidManifest()
    {
        var (trust, man, sig) = Signed("0.8.0", "stable");
        var st = ManifestVerifier.Verify(man, sig, "0.7.2", "stable", trust, [], out var parsed, out var detail);
        Assert.Equal(ManifestVerifyStatus.Ok, st);
        Assert.Equal("ok", detail);
        Assert.Equal("0.8.0", parsed!.Version);
        Assert.Equal(ManifestVerifyStatus.Ok, ManifestVerifier.RequireNewer(st, parsed, "0.7.2", out _));
    }

    [Fact]
    public void RequireNewer_rejectsSameVersionOnInstall()
    {
        var (trust, man, sig) = Signed("0.8.0", "stable");
        var st = ManifestVerifier.Verify(man, sig, "0.8.0", "stable", trust, [], out var parsed, out _);
        Assert.Equal(ManifestVerifyStatus.Ok, st);
        Assert.Equal(ManifestVerifyStatus.Downgrade, ManifestVerifier.RequireNewer(st, parsed, "0.8.0", out _));
    }

    [Fact]
    public void Verify_rejectsDowngrade()
    {
        var (trust, man, sig) = Signed("0.7.1", "stable");
        var st = ManifestVerifier.Verify(man, sig, "0.7.2", "stable", trust, [], out _, out _);
        Assert.Equal(ManifestVerifyStatus.Downgrade, st);
    }

    [Fact]
    public void Verify_rejectsWrongKey()
    {
        var (trust, man, sig) = Signed("0.8.0", "stable");
        var other = NewTrust();
        var st = ManifestVerifier.Verify(man, sig, "0.7.2", "stable", other, [], out _, out _);
        Assert.Equal(ManifestVerifyStatus.UnknownKey, st);
    }

    [Fact]
    public void Verify_rejectsRevokedEmbeddedKey()
    {
        var (trust, man, sig) = Signed("0.8.0", "stable");
        var st = ManifestVerifier.Verify(man, sig, "0.7.2", "stable", trust, [trust[0].KeyId], out _, out _);
        Assert.Equal(ManifestVerifyStatus.RevokedKey, st);
    }

    [Fact]
    public void Verify_rejectsTruncatedSignature()
    {
        var (trust, man, sig) = Signed("0.8.0", "stable");
        var st = ManifestVerifier.Verify(man, sig.AsSpan(0, Math.Min(20, sig.Length)).ToArray(), "0.7.2", "stable", trust, [], out _, out _);
        Assert.Equal(ManifestVerifyStatus.BadSignatureFormat, st);
    }

    [Fact]
    public void Verify_rejectsChangedManifestBytes()
    {
        var (trust, man, sig) = Signed("0.8.0", "stable");
        man[man.Length / 2] ^= 0x01;
        var st = ManifestVerifier.Verify(man, sig, "0.7.2", "stable", trust, [], out _, out _);
        Assert.Equal(ManifestVerifyStatus.BadSignature, st);
    }

    [Fact]
    public void Verify_rejectsOversizedManifest()
    {
        var (trust, _, sig) = Signed("0.8.0", "stable");
        var huge = new byte[ReleaseManifest.MaxBytes + 1];
        var st = ManifestVerifier.Verify(huge, sig, "0.7.2", "stable", trust, [], out _, out _);
        Assert.Equal(ManifestVerifyStatus.TooLarge, st);
    }

    [Fact]
    public void Verify_ignoresUnknownManifestFields()
    {
        using var ecdsa = ReleaseCrypto.CreateKey();
        var trust = TrustOf(ecdsa);
        // extra field "comment" must be ignored
        string json = """
            {"product":"CordMic","version":"0.8.0","channel":"stable","files":[{"name":"CordMic-Setup-0.8.0.exe","size":10,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}],"revokedKeyIds":[],"comment":"hello"}
            """;
        var man = Encoding.UTF8.GetBytes(json);
        var sig = Encoding.UTF8.GetBytes(ManifestSignature.Format(trust[0].KeyId, ReleaseCrypto.SignDer(ecdsa, man)));
        var st = ManifestVerifier.Verify(man, sig, "0.7.2", "stable", trust, [], out var parsed, out _);
        Assert.Equal(ManifestVerifyStatus.Ok, st);
        Assert.Equal("0.8.0", parsed!.Version);
    }

    [Fact]
    public void Verify_rejectsWrongProductAndChannel()
    {
        using var ecdsa = ReleaseCrypto.CreateKey();
        var trust = TrustOf(ecdsa);
        var badProduct = Encoding.UTF8.GetBytes("""
            {"product":"Other","version":"0.8.0","channel":"stable","files":[{"name":"a.exe","size":1,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}
            """);
        var sig = Encoding.UTF8.GetBytes(ManifestSignature.Format(trust[0].KeyId, ReleaseCrypto.SignDer(ecdsa, badProduct)));
        var st = ManifestVerifier.Verify(badProduct, sig, "0.7.2", "stable", trust, [], out _, out _);
        Assert.Equal(ManifestVerifyStatus.WrongProduct, st);

        var (trust2, man, sig2) = Signed("0.8.0", "beta");
        st = ManifestVerifier.Verify(man, sig2, "0.7.2", "stable", trust2, [], out _, out _);
        Assert.Equal(ManifestVerifyStatus.ChannelMismatch, st);
    }

    [Fact]
    public void EncryptedPkcs8_roundTripsAndRefusesWrongPassphrase()
    {
        using var ecdsa = ReleaseCrypto.CreateKey();
        var p8 = ReleaseCrypto.EncryptPkcs8(ecdsa, "correct horse battery staple test only");
        using var ok = ReleaseCrypto.DecryptPkcs8(p8, "correct horse battery staple test only");
        Assert.Equal(ReleaseCrypto.SpkiBase64(ecdsa), ReleaseCrypto.SpkiBase64(ok));
        Assert.ThrowsAny<CryptographicException>(() => ReleaseCrypto.DecryptPkcs8(p8, "wrong passphrase -----"));
    }

    [Fact]
    public void PassphraseStrongEnough_requiresSixWordsOrLength()
    {
        Assert.False(ReleaseCrypto.PassphraseStrongEnough("one two"));
        Assert.True(ReleaseCrypto.PassphraseStrongEnough("one1 two2 three4 four5 five6 six7"));
        Assert.True(ReleaseCrypto.PassphraseStrongEnough(new string('x', 20)));
    }

    static (EmbeddedReleaseKey[] Trust, byte[] Manifest, byte[] Sig) Signed(string version, string channel, string? file = "CordMic-Setup-0.8.0.exe")
    {
        using var ecdsa = ReleaseCrypto.CreateKey();
        var trust = TrustOf(ecdsa);
        var data = new ReleaseManifestData
        {
            Product = "CordMic",
            Version = version,
            Channel = channel,
            Files = [new ManifestFile(file!, 10, new string('a', 64))],
        };
        var man = ReleaseManifest.ToUtf8(data);
        var sig = Encoding.UTF8.GetBytes(ManifestSignature.Format(trust[0].KeyId, ReleaseCrypto.SignDer(ecdsa, man)));
        return (trust, man, sig);
    }

    [Fact]
    public void SharedTestVector_verifiesOnDotNet()
    {
        var root = RepoRoot();
        var man = File.ReadAllBytes(Path.Combine(root, "tools", "keytool", "testdata", "release-manifest.json"));
        var sig = File.ReadAllBytes(Path.Combine(root, "tools", "keytool", "testdata", "release-manifest.json.sig"));
        const string spki = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE0kqPcNFN/FPgxce2q0V+5WHlXtUrg0wxKKx6vBDCUvTXnCCfOo38ZSOO1qtmQhgvQrBnVTjL/6AWwNM6bKPGSQ==";
        var trust = new[] { new EmbeddedReleaseKey(ReleaseCrypto.KeyIdFromBase64(spki), spki) };
        Assert.Equal("17dd91eb43887abf", trust[0].KeyId);
        var st = ManifestVerifier.Verify(man, sig, "0.7.2", "stable", trust, [], out var parsed, out _);
        Assert.Equal(ManifestVerifyStatus.Ok, st);
        Assert.Equal("0.8.0", parsed!.Version);
    }

    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            if (File.Exists(Path.Combine(d.FullName, "RELEASING.md")) && Directory.Exists(Path.Combine(d.FullName, "tools", "keytool", "testdata")))
                return d.FullName;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException("repository root with tools/keytool/testdata not found");
    }

    static EmbeddedReleaseKey[] NewTrust()
    {
        using var ecdsa = ReleaseCrypto.CreateKey();
        return TrustOf(ecdsa);
    }

    static EmbeddedReleaseKey[] TrustOf(ECDsa ecdsa)
    {
        var spki = ReleaseCrypto.SpkiBase64(ecdsa);
        return [new EmbeddedReleaseKey(ReleaseCrypto.KeyIdFromBase64(spki), spki)];
    }
}
