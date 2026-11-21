using System.Text.Json;

namespace CordMic.Core.Usb;

public readonly record struct CatalogSignature(bool Trusted, string? Publisher, string Detail);
public readonly record struct PnpAddResult(bool Ok, string? OemName, string Detail);
public readonly record struct PnpPackage(string OemName, string Provider, IReadOnlyList<string> HardwareIds);
public readonly record struct BindingRecord(string OemName, string Provider, string[] HardwareIds);
public readonly record struct BindingOutcome(bool Ok, int ExitCode, string Message, string? OemName = null);

public interface ICatalogSignatureVerifier
{
    /// <summary>Must be called on the staged copies (after the copy, before pnputil), not on the source files.</summary>
    CatalogSignature Verify(string stagedInfPath, string stagedCatPath);
}

public interface IPnpDriverStore
{
    PnpAddResult Add(string stagedInfPath);
    bool Remove(string oemName, out string detail);
    IReadOnlyList<PnpPackage> List();
}

public interface IBindingRecordStore
{
    BindingRecord? Read();
    void Write(BindingRecord record);
    void Clear();
}

/// <summary>Copies the package into an admin-only staging folder and returns those paths.</summary>
public interface IPackageStaging
{
    (string Inf, string Cat) Stage(string sourceInf, string sourceCat);
}

/// <summary>
/// Optional WinUSB accessory binding. Unsigned or untrusted packages are never
/// installed. Signature is checked on the staged copies (TOCTOU). Removal only
/// deletes the recorded oem*.inf after checking provider and hardware IDs.
/// </summary>
public sealed class AccessoryBindingService
{
    public const string ExpectedPublisher = "SignPath Foundation";
    public const string RecordFileName = "driver.json";

    readonly IPackageStaging _stage;
    readonly ICatalogSignatureVerifier _verify;
    readonly IPnpDriverStore _pnp;
    readonly IBindingRecordStore _records;

    public AccessoryBindingService(IPackageStaging stage, ICatalogSignatureVerifier verify, IPnpDriverStore pnp, IBindingRecordStore records)
    {
        _stage = stage; _verify = verify; _pnp = pnp; _records = records;
    }

    public BindingOutcome Install(string sourceInf, string sourceCat)
    {
        bool inf = File.Exists(sourceInf), cat = File.Exists(sourceCat);
        var ready = CatalogInstallPlan.Evaluate(inf, cat, catalogSignatureTrusted: false, publisherMatches: false);
        if (!inf || !cat)
            return new(false, AccessoryBindingArgs.ExitRefused, ready.Reason);

        var staged = _stage.Stage(sourceInf, sourceCat);
        var sig = _verify.Verify(staged.Inf, staged.Cat);
        ready = CatalogInstallPlan.Evaluate(
            infPresent: true, catPresent: true,
            catalogSignatureTrusted: sig.Trusted,
            publisherMatches: string.Equals(sig.Publisher, ExpectedPublisher, StringComparison.Ordinal));
        if (!ready.MayInstall)
            return new(false, AccessoryBindingArgs.ExitRefused, ready.Reason + (string.IsNullOrEmpty(sig.Detail) ? "" : " (" + sig.Detail + ")"));

        var add = _pnp.Add(staged.Inf);
        if (!add.Ok || !CatalogInstallPlan.OemNamePlausible(add.OemName))
            return new(false, AccessoryBindingArgs.ExitIo, add.Detail, add.OemName);

        _records.Write(new BindingRecord(add.OemName!, CatalogInstallPlan.Provider, CatalogInstallPlan.AllowedHardwareIds.ToArray()));
        return new(true, AccessoryBindingArgs.ExitOk, "installed " + add.OemName, add.OemName);
    }

    public BindingOutcome Remove()
    {
        if (_records.Read() is not { } rec || !CatalogInstallPlan.OemNamePlausible(rec.OemName))
            return new(false, AccessoryBindingArgs.ExitRefused, "no accessory binding is recorded on this PC");

        var listed = _pnp.List().FirstOrDefault(p => string.Equals(p.OemName, rec.OemName, StringComparison.OrdinalIgnoreCase));
        if (listed.OemName is null)
        {
            _records.Clear();
            return new(false, AccessoryBindingArgs.ExitRefused, rec.OemName + " is no longer in the driver store; the record was cleared");
        }
        if (!string.Equals(listed.Provider, CatalogInstallPlan.Provider, StringComparison.Ordinal)
            && !string.Equals(listed.Provider, rec.Provider, StringComparison.Ordinal))
            return new(false, AccessoryBindingArgs.ExitRefused, "refusing to remove " + rec.OemName + ": provider does not match");
        if (!listed.HardwareIds.Any(CatalogInstallPlan.HardwareIdAllowed))
            return new(false, AccessoryBindingArgs.ExitRefused, "refusing to remove " + rec.OemName + ": hardware IDs do not match the CordMic accessory");

        if (!_pnp.Remove(rec.OemName, out var detail))
            return new(false, AccessoryBindingArgs.ExitIo, detail, rec.OemName);
        _records.Clear();
        return new(true, AccessoryBindingArgs.ExitOk, "removed " + rec.OemName, rec.OemName);
    }

    public BindingOutcome Status()
    {
        if (_records.Read() is not { } rec)
            return new(true, AccessoryBindingArgs.ExitOk, "not installed");
        return new(true, AccessoryBindingArgs.ExitOk, "installed (" + rec.OemName + ")", rec.OemName);
    }

    public static BindingRecord? ParseRecord(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var oem = r.TryGetProperty("oem", out var o) ? o.GetString() : null;
            var provider = r.TryGetProperty("provider", out var p) ? p.GetString() ?? "" : "";
            var ids = new List<string>();
            if (r.TryGetProperty("hardwareIds", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var i in arr.EnumerateArray())
                    if (i.ValueKind == JsonValueKind.String && i.GetString() is { Length: > 0 } s)
                        ids.Add(s);
            if (!CatalogInstallPlan.OemNamePlausible(oem)) return null;
            return new BindingRecord(oem!, provider, ids.ToArray());
        }
        catch (JsonException) { return null; }
    }

    public static string FormatRecord(BindingRecord rec)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("oem", rec.OemName);
            w.WriteString("provider", rec.Provider);
            w.WriteStartArray("hardwareIds");
            foreach (var id in rec.HardwareIds) w.WriteStringValue(id);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}

public sealed class MemoryBindingRecordStore : IBindingRecordStore
{
    BindingRecord? _rec;
    public BindingRecord? Read() => _rec;
    public void Write(BindingRecord record) => _rec = record;
    public void Clear() => _rec = null;
}

public sealed class FileBindingRecordStore : IBindingRecordStore
{
    readonly string _path;
    public FileBindingRecordStore(string path) => _path = path;
    public BindingRecord? Read()
    {
        if (!File.Exists(_path)) return null;
        try { return AccessoryBindingService.ParseRecord(File.ReadAllText(_path)); }
        catch (IOException) { return null; }
    }
    public void Write(BindingRecord record)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, AccessoryBindingService.FormatRecord(record));
    }
    public void Clear()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch (IOException) { }
    }
}
