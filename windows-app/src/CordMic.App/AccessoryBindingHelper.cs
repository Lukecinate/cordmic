using CordMic.Core.Usb;

namespace CordMic.App;

/// <summary>Elevated entry point for the optional accessory binding. Fixed verbs only.</summary>
internal static class AccessoryBindingHelper
{
    public static int Run(AccessoryBindingArgs.Verb verb)
    {
        try
        {
            var sourceDir = Path.Combine(Paths.ExeDir, "driver");
            if (!Directory.Exists(sourceDir)) sourceDir = Paths.ExeDir;
            var inf = Path.Combine(sourceDir, CatalogInstallPlan.InfFileName);
            var cat = Path.Combine(sourceDir, CatalogInstallPlan.CatFileName);
            var programData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CordMic");
            var records = new FileBindingRecordStore(Path.Combine(programData, AccessoryBindingService.RecordFileName));
            var svc = new AccessoryBindingService(new FolderStaging(programData), new UnsignedCatalogVerifier(), new LoggingPnp(), records);
            BindingOutcome result = verb switch
            {
                AccessoryBindingArgs.Verb.Install => svc.Install(inf, cat),
                AccessoryBindingArgs.Verb.Remove => svc.Remove(),
                AccessoryBindingArgs.Verb.Status => svc.Status(),
                _ => new BindingOutcome(false, AccessoryBindingArgs.ExitUsage, "unknown verb"),
            };
            if (!result.Ok) Console.Error.WriteLine(result.Message);
            else Console.Out.WriteLine(result.Message);
            return result.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return AccessoryBindingArgs.ExitIo;
        }
    }

    /// <summary>
    /// Until a SignPath-signed catalog is shipped with the app, every catalog is treated as untrusted.
    /// This verifier never reports trust for a missing or unsigned package.
    /// </summary>
    sealed class UnsignedCatalogVerifier : ICatalogSignatureVerifier
    {
        public CatalogSignature Verify(string stagedInfPath, string stagedCatPath)
        {
            if (!File.Exists(stagedCatPath) || new FileInfo(stagedCatPath).Length == 0)
                return new(false, null, "catalog missing");
            return new(false, null, "no SignPath-signed catalog is present in this build; the package was not installed");
        }
    }

    sealed class FolderStaging : IPackageStaging
    {
        readonly string _root;
        public FolderStaging(string programData) => _root = Path.Combine(programData, "binding-stage");
        public (string Inf, string Cat) Stage(string sourceInf, string sourceCat)
        {
            Directory.CreateDirectory(_root);
            var inf = Path.Combine(_root, CatalogInstallPlan.InfFileName);
            var cat = Path.Combine(_root, CatalogInstallPlan.CatFileName);
            File.Copy(sourceInf, inf, overwrite: true);
            if (File.Exists(sourceCat)) File.Copy(sourceCat, cat, overwrite: true);
            return (inf, cat);
        }
    }

    /// <summary>Does not invoke pnputil unless a later signed-catalog verifier says the package is trusted.</summary>
    sealed class LoggingPnp : IPnpDriverStore
    {
        public PnpAddResult Add(string stagedInfPath) =>
            new(false, null, "pnputil is not called until a signed catalog is trusted");
        public bool Remove(string oemName, out string detail)
        {
            detail = "pnputil is not called until a binding was recorded from a trusted catalog";
            return false;
        }
        public IReadOnlyList<PnpPackage> List() => Array.Empty<PnpPackage>();
    }
}
