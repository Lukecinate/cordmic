using System.Text.RegularExpressions;

namespace CordMic.Core;

/// <summary>
/// Compiled allowlist for VB-Audio Virtual Cable stereo endpoints.
/// The helper never takes a device id, path or name from its command line;
/// it re-enumerates MMDevices and keeps only endpoints that pass these checks.
/// </summary>
public static class CableIdentity
{
    public static readonly Regex EndpointIdRegex = new(
        @"^\{0\.0\.([01])\.00000000\}\.\{([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsSixteenChannel(string? name) =>
        !string.IsNullOrEmpty(name) && Regex.IsMatch(name, @"16\s*-?\s*ch", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool LooksLikeVbAudio(string? friendlyName, string? manufacturer)
    {
        static bool Has(string? s) =>
            !string.IsNullOrEmpty(s) && s.IndexOf("VB-Audio", StringComparison.OrdinalIgnoreCase) >= 0;
        return Has(friendlyName) || Has(manufacturer);
    }

    public static bool IsStereoCableInput(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || IsSixteenChannel(name)) return false;
        return Regex.IsMatch(name, @"^CABLE Input\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
               && name.IndexOf("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static bool IsStereoCableOutput(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || IsSixteenChannel(name)) return false;
        return Regex.IsMatch(name, @"^CABLE Output\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
               && name.IndexOf("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static bool TryParseEndpointId(string? deviceId, out bool isRender, out string endpointGuid)
    {
        isRender = false;
        endpointGuid = "";
        if (string.IsNullOrEmpty(deviceId)) return false;
        var m = EndpointIdRegex.Match(deviceId);
        if (!m.Success) return false;
        isRender = m.Groups[1].Value == "0";
        endpointGuid = m.Groups[2].Value;
        return Guid.TryParse(endpointGuid, out _);
    }

    /// <summary>True only for the stereo CABLE Input (render) or CABLE Output (capture) of VB-Audio Virtual Cable.</summary>
    public static bool IsHelperTarget(string deviceId, string friendlyName, string? manufacturer, bool wantRender)
    {
        if (!TryParseEndpointId(deviceId, out bool isRender, out _)) return false;
        if (isRender != wantRender) return false;
        if (!LooksLikeVbAudio(friendlyName, manufacturer)) return false;
        return wantRender ? IsStereoCableInput(friendlyName) : IsStereoCableOutput(friendlyName);
    }

    public static string RegistryPropertiesPath(bool isRender, string endpointGuid)
    {
        if (!Guid.TryParse(endpointGuid, out var g))
            throw new ArgumentException("endpoint guid", nameof(endpointGuid));
        string flow = isRender ? "Render" : "Capture";
        return $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\{flow}\{{{g}}}";
    }
}
