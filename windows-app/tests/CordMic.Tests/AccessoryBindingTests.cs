using CordMic.Core.Usb;

namespace CordMic.Tests;

public class AccessoryBindingTests
{
    [Fact]
    public void Args_acceptOnlyASingleFixedVerb()
    {
        Assert.True(AccessoryBindingArgs.TryParse(new[] { AccessoryBindingArgs.Install }, out var v, out var err));
        Assert.Equal(AccessoryBindingArgs.Verb.Install, v);
        Assert.True(AccessoryBindingArgs.TryParse(new[] { AccessoryBindingArgs.Remove }, out v, out _));
        Assert.True(AccessoryBindingArgs.TryParse(new[] { AccessoryBindingArgs.Status }, out v, out _));
        Assert.False(AccessoryBindingArgs.TryParse(new[] { AccessoryBindingArgs.Install, "C:\\evil.inf" }, out v, out err));
        Assert.Equal(AccessoryBindingArgs.Verb.None, v);
        Assert.False(string.IsNullOrEmpty(err));
        Assert.False(AccessoryBindingArgs.TryParse(new[] { "--tray" }, out _, out _));
        Assert.True(AccessoryBindingArgs.Mentions(new[] { "x", AccessoryBindingArgs.Status }));
    }

    [Fact]
    public void UnsignedPackage_isRefused_andPnpIsNotCalled()
    {
        var dir = TempDir();
        var inf = Path.Combine(dir, CatalogInstallPlan.InfFileName);
        File.WriteAllText(inf, "fake inf");
        var fakes = Make();
        var r = fakes.Svc.Install(inf, Path.Combine(dir, CatalogInstallPlan.CatFileName));
        Assert.False(r.Ok);
        Assert.Equal(AccessoryBindingArgs.ExitRefused, r.ExitCode);
        Assert.Empty(fakes.Pnp.Added);
        Assert.False(CatalogInstallPlan.TextSuggestsSignatureBypass(r.Message));
    }

    [Fact]
    public void SignatureIsCheckedOnStagedCopy_notTheSource()
    {
        var dir = TempDir();
        var inf = Path.Combine(dir, CatalogInstallPlan.InfFileName);
        var cat = Path.Combine(dir, CatalogInstallPlan.CatFileName);
        File.WriteAllText(inf, "inf");
        File.WriteAllText(cat, "cat");
        var fakes = Make(trusted: true);
        var r = fakes.Svc.Install(inf, cat);
        Assert.True(r.Ok);
        Assert.Equal("oem12.inf", r.OemName);
        Assert.NotNull(fakes.Verify.LastInf);
        Assert.NotEqual(inf, fakes.Verify.LastInf);
        Assert.StartsWith(fakes.Stage.Root, fakes.Verify.LastInf, StringComparison.Ordinal);
        Assert.Contains(fakes.Verify.LastInf!, fakes.Pnp.Added);
    }

    [Fact]
    public void UntrustedSignatureAfterCopy_isRefused()
    {
        var dir = TempDir();
        var inf = Path.Combine(dir, CatalogInstallPlan.InfFileName);
        var cat = Path.Combine(dir, CatalogInstallPlan.CatFileName);
        File.WriteAllText(inf, "inf");
        File.WriteAllText(cat, "cat");
        var fakes = Make(trusted: false, publisher: "Someone Else");
        var r = fakes.Svc.Install(inf, cat);
        Assert.False(r.Ok);
        Assert.Empty(fakes.Pnp.Added);
        Assert.False(CatalogInstallPlan.TextSuggestsSignatureBypass(r.Message));
    }

    [Fact]
    public void WrongPublisher_isRefused()
    {
        var dir = TempDir();
        var inf = Path.Combine(dir, CatalogInstallPlan.InfFileName);
        var cat = Path.Combine(dir, CatalogInstallPlan.CatFileName);
        File.WriteAllText(inf, "inf");
        File.WriteAllText(cat, "cat");
        var fakes = Make(trusted: true, publisher: "Other Publisher");
        var r = fakes.Svc.Install(inf, cat);
        Assert.False(r.Ok);
        Assert.Empty(fakes.Pnp.Added);
    }

    [Fact]
    public void Remove_onlyDeletesTheRecordedOemAfterIdentityCheck()
    {
        var fakes = Make(trusted: true);
        fakes.Records.Write(new BindingRecord("oem12.inf", CatalogInstallPlan.Provider, CatalogInstallPlan.AllowedHardwareIds.ToArray()));
        fakes.Pnp.Packages.Add(new PnpPackage("oem12.inf", CatalogInstallPlan.Provider, CatalogInstallPlan.AllowedHardwareIds));
        var r = fakes.Svc.Remove();
        Assert.True(r.Ok);
        Assert.Equal(new[] { "oem12.inf" }, fakes.Pnp.Removed);
        Assert.Null(fakes.Records.Read());

        fakes.Pnp.Removed.Clear();
        fakes.Records.Write(new BindingRecord("oem99.inf", CatalogInstallPlan.Provider, CatalogInstallPlan.AllowedHardwareIds.ToArray()));
        fakes.Pnp.Packages.Add(new PnpPackage("oem99.inf", "Other", new[] { "USB\\VID_1234&PID_0001" }));
        r = fakes.Svc.Remove();
        Assert.False(r.Ok);
        Assert.Empty(fakes.Pnp.Removed);
    }

    [Fact]
    public void Status_reportsRecordedOem()
    {
        var fakes = Make();
        Assert.Equal("not installed", fakes.Svc.Status().Message);
        fakes.Records.Write(new BindingRecord("oem3.inf", CatalogInstallPlan.Provider, CatalogInstallPlan.AllowedHardwareIds.ToArray()));
        var s = fakes.Svc.Status();
        Assert.Equal("oem3.inf", s.OemName);
        Assert.Contains("oem3.inf", s.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_neverAllowsUnsigned()
    {
        Assert.False(CatalogInstallPlan.Evaluate(true, false, true, true).MayInstall);
        Assert.False(CatalogInstallPlan.Evaluate(true, true, false, true).MayInstall);
        Assert.True(CatalogInstallPlan.Evaluate(true, true, true, true).MayInstall);
    }

    [Theory]
    [InlineData("oem0.inf", true)]
    [InlineData("oem12.inf", true)]
    [InlineData("oem.inf", false)]
    [InlineData("other.inf", false)]
    [InlineData(@"C:\windows\inf\oem12.inf", false)]
    public void OemNamePlausible(string name, bool ok) => Assert.Equal(ok, CatalogInstallPlan.OemNamePlausible(name));

    static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "cordmic-bind-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(d);
        return d;
    }

    static Bundle Make(bool trusted = false, string? publisher = AccessoryBindingService.ExpectedPublisher)
    {
        var f = new Bundle();
        f.Verify.Result = new CatalogSignature(trusted, publisher, trusted ? "ok" : "not trusted");
        return f;
    }

    sealed class Bundle
    {
        public FakeStage Stage { get; } = new();
        public FakeVerify Verify { get; } = new();
        public FakePnp Pnp { get; } = new();
        public MemoryBindingRecordStore Records { get; } = new();
        public AccessoryBindingService Svc => new(Stage, Verify, Pnp, Records);
    }

    sealed class FakeStage : IPackageStaging
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cordmic-stage-" + Guid.NewGuid().ToString("n"));
        public (string Inf, string Cat) Stage(string sourceInf, string sourceCat)
        {
            Directory.CreateDirectory(Root);
            var inf = Path.Combine(Root, Path.GetFileName(sourceInf));
            var cat = Path.Combine(Root, Path.GetFileName(sourceCat));
            File.Copy(sourceInf, inf, overwrite: true);
            if (File.Exists(sourceCat)) File.Copy(sourceCat, cat, overwrite: true);
            return (inf, cat);
        }
    }

    sealed class FakeVerify : ICatalogSignatureVerifier
    {
        public CatalogSignature Result { get; set; }
        public string? LastInf { get; private set; }
        public string? LastCat { get; private set; }
        public CatalogSignature Verify(string stagedInfPath, string stagedCatPath)
        {
            LastInf = stagedInfPath;
            LastCat = stagedCatPath;
            return Result;
        }
    }

    sealed class FakePnp : IPnpDriverStore
    {
        public List<string> Added { get; } = new();
        public List<string> Removed { get; } = new();
        public List<PnpPackage> Packages { get; } = new();
        public PnpAddResult Add(string stagedInfPath)
        {
            Added.Add(stagedInfPath);
            return new(true, "oem12.inf", "added");
        }
        public bool Remove(string oemName, out string detail)
        {
            Removed.Add(oemName);
            detail = "removed";
            return true;
        }
        public IReadOnlyList<PnpPackage> List() => Packages;
    }
}
