namespace CordMic.Core.Usb;

/// <summary>
/// Describes the optional INF-only WinUSB binding. Never installs anything by itself.
/// Refuses unsigned packages and never suggests test-signing, a self-signed root,
/// or turning off Secure Boot / Memory Integrity / Driver Signature Enforcement.
/// </summary>
public static class CatalogInstallPlan
{
    public const string InfFileName = "cordmic-accessory.inf";
    public const string CatFileName = "cordmic-accessory.cat";
    public const string Provider = "CordMic contributors";
    public const string VerbInstall = "install-accessory-binding";
    public const string VerbRemove = "remove-accessory-binding";
    public const string VerbStatus = "accessory-binding-status";

    public static readonly string[] AllowedHardwareIds =
    {
        UsbIds.HardwareIdAccessory,
        UsbIds.HardwareIdAccessoryIf0,
    };

    public static bool HardwareIdAllowed(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        foreach (var a in AllowedHardwareIds)
            if (string.Equals(a, id.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public enum PackageTrust { Missing, Unsigned, SignatureUnverified, Trusted }

    public readonly record struct Readiness(PackageTrust Trust, string Reason, bool MayInstall);

    public static Readiness Evaluate(bool infPresent, bool catPresent, bool catalogSignatureTrusted, bool publisherMatches)
    {
        if (!infPresent) return new(PackageTrust.Missing, "accessory INF is not installed with this copy of CordMic", false);
        if (!catPresent) return new(PackageTrust.Unsigned, "catalog file is missing; unsigned packages are never installed", false);
        if (!catalogSignatureTrusted)
            return new(PackageTrust.SignatureUnverified, "catalog signature was not trusted; CordMic will not install it and will not disable signature checks", false);
        if (!publisherMatches)
            return new(PackageTrust.SignatureUnverified, "catalog publisher is not the expected signer", false);
        return new(PackageTrust.Trusted, "package signature is trusted; Windows will still show its own install prompt", true);
    }

    public static string PnputilAddCommand(string infFullPath) =>
        "pnputil.exe /add-driver " + Quote(infFullPath) + " /install";

    public static string PnputilDeleteCommand(string oemName) =>
        "pnputil.exe /delete-driver " + oemName + " /uninstall /force";

    public static bool OemNamePlausible(string? oem)
    {
        if (string.IsNullOrWhiteSpace(oem)) return false;
        oem = oem.Trim();
        if (!oem.StartsWith("oem", StringComparison.OrdinalIgnoreCase)) return false;
        if (!oem.EndsWith(".inf", StringComparison.OrdinalIgnoreCase)) return false;
        var mid = oem[3..^4];
        return mid.Length is > 0 and < 6 && mid.All(char.IsDigit);
    }

    /// <summary>True if a text would tell the user to weaken platform security — those words must never ship.</summary>
    public static bool TextSuggestsSignatureBypass(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        string t = text.ToLowerInvariant();
        string[] banned =
        {
            "test-signing", "testsigning", "bcdedit", "nointegritychecks",
            "disable driver signature", "disable secure boot", "turn off secure boot",
            "turn off memory integrity", "disable memory integrity", "disable hvci",
            "trusted root", "self-signed root",
        };
        foreach (var b in banned)
            if (t.Contains(b, StringComparison.Ordinal)) return true;
        return false;
    }

    private static string Quote(string p) => p.Contains(' ') ? "\"" + p + "\"" : p;
}
