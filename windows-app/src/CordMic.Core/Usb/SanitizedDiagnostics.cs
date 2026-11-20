using System.Text;
using System.Text.RegularExpressions;

namespace CordMic.Core.Usb;

/// <summary>
/// Builds a pasteable support report. IPv4/IPv6 addresses and Windows user profile paths
/// are replaced. Voice recordings are never included.
/// </summary>
public static class SanitizedDiagnostics
{
    static readonly Regex Ipv4 = new(@"\b(?:(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\.){3}(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\b", RegexOptions.Compiled);
    static readonly Regex Ipv6 = new(@"\b(?:[0-9a-fA-F]{1,4}:){2,7}[0-9a-fA-F]{1,4}\b", RegexOptions.Compiled);
    static readonly Regex Users = new(@"([A-Za-z]:\\Users\\|\\\\[^\\\s]+\\Users\\|/Users/|/home/)([^\s\\/]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Ipv6Compressed = new(@"\b[0-9a-fA-F]{0,4}::[0-9a-fA-F:]*\b", RegexOptions.Compiled);

    public static string Mask(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string s = Ipv4.Replace(text, "x.x.x.x");
        s = Ipv6.Replace(s, "x:x:x:x");
        s = Ipv6Compressed.Replace(s, "x:x:x:x");
        s = Users.Replace(s, m => m.Groups[1].Value + "[user]");
        return s;
    }

    public static string Build(string version, string? os, IEnumerable<string>? logLines, IEnumerable<string>? routeLines, string? extra = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("CordMic diagnostics (sanitized). No recordings included.");
        sb.AppendLine("version=" + Mask(version));
        if (!string.IsNullOrEmpty(os)) sb.AppendLine("os=" + Mask(os));
        sb.AppendLine("gate=" + (UsbEngineGate.HardwareGateGm1Passed ? "g-m1-passed" : "PENDING-HARDWARE"));
        if (routeLines is not null)
        {
            sb.AppendLine("route-log:");
            foreach (var l in routeLines.Take(80)) sb.AppendLine("  " + Mask(l));
        }
        if (logLines is not null)
        {
            sb.AppendLine("log:");
            foreach (var l in logLines.TakeLast(200)) sb.AppendLine("  " + Mask(l));
        }
        if (!string.IsNullOrEmpty(extra)) sb.AppendLine(Mask(extra));
        return sb.ToString();
    }

    static readonly Regex UnmaskedUser = new(
        @"([A-Za-z]:\\Users\\|\\\\[^\\\s]+\\Users\\|/Users/|/home/)(?!\[user\])([^\s\\/]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool LooksSafe(string text) =>
        !Ipv4.IsMatch(text) && !UnmaskedUser.IsMatch(text) &&
        text.IndexOf(".m4a", StringComparison.OrdinalIgnoreCase) < 0 &&
        text.IndexOf(".wav", StringComparison.OrdinalIgnoreCase) < 0;
}
