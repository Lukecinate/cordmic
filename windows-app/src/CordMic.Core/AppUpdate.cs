using System.Security.Cryptography;
using System.Text.Json;

namespace CordMic.Core;

/// <summary>
/// Parse GitHub Releases metadata, compare versions, and verify SHA-256SUMS.
/// Network I/O lives in the app layer so this stays unit-testable.
/// </summary>
public static class AppUpdate
{
    public const string Owner = "Lukecinate";
    public const string Repo = "cordmic";
    public const string LatestApiUrl = "https://api.github.com/repos/Lukecinate/cordmic/releases/latest";
    public const string UserAgent = "CordMic-Updater";
    public static readonly TimeSpan MinCheckInterval = TimeSpan.FromHours(6);

    public readonly record struct SemVer(int Major, int Minor, int Patch, string Pre = "") : IComparable<SemVer>
    {
        public bool IsPrerelease => Pre.Length > 0;

        public static bool TryParse(string? raw, out SemVer v)
        {
            v = default;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var s = raw.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s[1..];
            int plus = s.IndexOf('+');
            if (plus >= 0) s = s[..plus]; // SemVer build metadata is ignored
            string pre = "";
            int dash = s.IndexOf('-');
            if (dash >= 0)
            {
                pre = s[(dash + 1)..];
                s = s[..dash];
                if (pre.Length == 0) return false;
                foreach (var id in pre.Split('.'))
                {
                    if (id.Length == 0) return false;
                    foreach (char ch in id)
                        if (!(char.IsAsciiLetterOrDigit(ch) || ch == '-')) return false;
                }
            }
            var p = s.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2) return false;
            if (!int.TryParse(p[0], out int a) || !int.TryParse(p[1], out int b)) return false;
            int c = 0;
            if (p.Length >= 3 && !int.TryParse(p[2], out c)) return false;
            v = new SemVer(a, b, c, pre);
            return true;
        }

        public int CompareTo(SemVer other)
        {
            int d = Major.CompareTo(other.Major);
            if (d != 0) return d;
            d = Minor.CompareTo(other.Minor);
            if (d != 0) return d;
            d = Patch.CompareTo(other.Patch);
            if (d != 0) return d;
            if (!IsPrerelease && !other.IsPrerelease) return 0;
            if (!IsPrerelease) return 1;  // 1.0.0 > 1.0.0-beta
            if (!other.IsPrerelease) return -1;
            return ComparePre(Pre, other.Pre);
        }

        /// <summary>Numeric identifiers compare numerically; a numeric id is less than a non-numeric one; a longer list wins if the common prefix matches.</summary>
        static int ComparePre(string a, string b)
        {
            var aa = a.Split('.');
            var bb = b.Split('.');
            int n = Math.Min(aa.Length, bb.Length);
            for (int i = 0; i < n; i++)
            {
                bool an = IsNumericId(aa[i]), bn = IsNumericId(bb[i]);
                if (an && bn)
                {
                    int c = long.Parse(aa[i], System.Globalization.CultureInfo.InvariantCulture)
                        .CompareTo(long.Parse(bb[i], System.Globalization.CultureInfo.InvariantCulture));
                    if (c != 0) return c;
                }
                else if (an) return -1;
                else if (bn) return 1;
                else
                {
                    int c = string.CompareOrdinal(aa[i], bb[i]);
                    if (c != 0) return c;
                }
            }
            return aa.Length.CompareTo(bb.Length);
        }

        static bool IsNumericId(string id)
        {
            if (id.Length == 0) return false;
            foreach (char c in id) if (!char.IsAsciiDigit(c)) return false;
            return true;
        }

        public override string ToString() =>
            string.IsNullOrEmpty(Pre) ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Pre}";
    }

    public static int CompareVersions(string a, string b) =>
        SemVer.TryParse(a, out var va) && SemVer.TryParse(b, out var vb) ? va.CompareTo(vb) : 0;

    public static bool IsNewer(string candidate, string current) => CompareVersions(candidate, current) > 0;

    public static bool IsDowngrade(string candidate, string current) => CompareVersions(candidate, current) < 0;

    /// <summary>True when the tag or <c>prerelease</c> flag marks a GitHub pre-release (auto-update must ignore it).</summary>
    public static bool IsPrereleaseRelease(string? tag, bool prereleaseFlag = false) =>
        prereleaseFlag || (SemVer.TryParse(tag, out var v) && v.IsPrerelease);

    /// <summary>
    /// Whether the auto-updater may offer this release. GitHub <c>/releases/latest</c> already omits pre-releases;
    /// this is the in-app guard if a pre-release JSON is ever supplied, and it keeps a stable install from jumping
    /// to a pre-release tag such as 0.8.0-beta.1 (which must not be treated as 0.8.0).
    /// </summary>
    public static bool ShouldOffer(ReleaseInfo info, string currentVersion)
    {
        if (info.Draft || IsPrereleaseRelease(info.Tag, info.Prerelease)) return false;
        return IsNewer(info.Tag, currentVersion);
    }

    /// <summary>Label for the running app: informational version (including -beta.N, without +build) when present.</summary>
    public static string DisplayVersion(string? informational, Version? assembly)
    {
        if (SemVer.TryParse(informational, out var v)) return v.ToString();
        if (assembly is not null) return $"{assembly.Major}.{assembly.Minor}.{assembly.Build}";
        return "";
    }

    public static Dictionary<string, string> ParseSha256Sums(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.StartsWith('#') || line.Length < 66) continue;
            var hash = line[..64];
            if (!IsHex(hash)) continue;
            var rest = line[64..].Trim();
            if (rest.StartsWith('*')) rest = rest[1..].Trim();
            var name = Path.GetFileName(rest.Trim().Trim('"'));
            if (name.Length > 0) d[name] = hash.ToLowerInvariant();
        }
        return d;
    }

    public static bool HashMatches(ReadOnlySpan<byte> data, string expectedHex)
    {
        if (string.IsNullOrWhiteSpace(expectedHex)) return false;
        var got = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        return string.Equals(got, expectedHex.Trim().ToLowerInvariant(), StringComparison.Ordinal);
    }

    public static bool HashMatchesFile(string path, string expectedHex)
    {
        using var fs = File.OpenRead(path);
        var got = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        return string.Equals(got, expectedHex.Trim().ToLowerInvariant(), StringComparison.Ordinal);
    }

    public readonly record struct ReleaseInfo(
        string Tag,
        string? ApkName,
        string? ApkUrl,
        string? SetupName,
        string? SetupUrl,
        string? SumsUrl,
        IReadOnlyDictionary<string, string> AssetUrls)
    {
        /// <summary>0.7.0: portable zip (<c>CordMic-Portable-&lt;version&gt;.zip</c>), used by a portable copy to update itself.</summary>
        public string? PortableName { get; init; }
        public string? PortableUrl { get; init; }
        /// <summary>Release page, shown when an update cannot be installed automatically.</summary>
        public string? HtmlUrl { get; init; }
        /// <summary>Asset sizes in bytes from the release metadata (progress fallback and size guard).</summary>
        public IReadOnlyDictionary<string, long>? AssetSizes { get; init; }
        /// <summary>GitHub <c>prerelease</c> flag. Auto-update must not offer these.</summary>
        public bool Prerelease { get; init; }
        /// <summary>GitHub <c>draft</c> flag. Auto-update must not offer these.</summary>
        public bool Draft { get; init; }
    }

    /// <summary>Release page used as the fallback whenever an automatic update is not possible.</summary>
    public const string ReleasesPageUrl = "https://github.com/Lukecinate/cordmic/releases/latest";

    /// <summary>Installer asset name a release must carry for <paramref name="version"/>.</summary>
    public static string SetupAssetName(string version) => $"CordMic-Setup-{Bare(version)}.exe";

    /// <summary>Portable zip asset name a release must carry for <paramref name="version"/> (0.7.0+ portable updates).</summary>
    public static string PortableAssetName(string version) => $"CordMic-Portable-{Bare(version)}.zip";

    private static string Bare(string v) => SemVer.TryParse(v, out var s) ? s.ToString() : v.TrimStart('v', 'V');

    /// <summary>
    /// Downloads (SHA256SUMS and assets) must be HTTPS, with no user-info in the URL.
    /// The in-app updater still verifies SHA-256 after the transfer; this only stops cleartext or
    /// credential-bearing URLs that a compromised or unexpected latest-JSON might supply.
    /// </summary>
    public static bool IsAllowedDownloadUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        if (uri.Host.Length == 0) return false;
        return true;
    }

    public static ReleaseInfo ParseLatestJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        string? html = root.TryGetProperty("html_url", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
        bool prerelease = root.TryGetProperty("prerelease", out var preEl) && preEl.ValueKind == JsonValueKind.True;
        bool draft = root.TryGetProperty("draft", out var draftEl) && draftEl.ValueKind == JsonValueKind.True;
        var urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(url))
                {
                    urls[name] = url;
                    if (a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number && sz.TryGetInt64(out var len)) sizes[name] = len;
                }
            }
        }
        string? Name(Func<string, bool> pred) => urls.Keys.FirstOrDefault(pred);
        string? Url(string? name) => name != null && urls.TryGetValue(name, out var u) ? u : null;
        var apk = Name(n => n.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));
        var setup = Name(n => n.StartsWith("CordMic-Setup-", StringComparison.OrdinalIgnoreCase)
                              && n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        var sums = Name(n => n.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase)
                             || n.StartsWith("SHA256SUMS", StringComparison.OrdinalIgnoreCase));
        // Prefer the asset whose name carries the release version; fall back to any matching name.
        var exactSetup = tag.Length > 0 ? SetupAssetName(tag) : null;
        if (exactSetup != null && urls.ContainsKey(exactSetup)) setup = exactSetup;
        var portable = tag.Length > 0 && urls.ContainsKey(PortableAssetName(tag))
            ? PortableAssetName(tag)
            : Name(n => n.StartsWith("CordMic-Portable-", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        return new ReleaseInfo(tag, apk, Url(apk), setup, Url(setup), Url(sums), urls)
        {
            PortableName = portable, PortableUrl = Url(portable),
            HtmlUrl = html != null && html.StartsWith("https://github.com/", StringComparison.Ordinal) ? html : ReleasesPageUrl,
            AssetSizes = sizes,
            Prerelease = prerelease,
            Draft = draft,
        };
    }

    /// <summary>
    /// File name for a downloaded asset: the release name if it is a plain file name (letters, digits, '.', '-', '_',
    /// not starting with '.', at most 100 characters), else <paramref name="fallback"/>.
    /// </summary>
    public static string SafeAssetFileName(string? name, string fallback)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 100 || name[0] == '.') return fallback;
        foreach (char c in name)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) return fallback;
        return name;
    }

    public static string? HashForAsset(IReadOnlyDictionary<string, string> sums, string assetName)
    {
        if (sums.TryGetValue(assetName, out var h)) return h;
        var file = Path.GetFileName(assetName);
        return file.Length > 0 && sums.TryGetValue(file, out h) ? h : null;
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
        {
            if (c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F')) continue;
            return false;
        }
        return s.Length > 0;
    }
}
