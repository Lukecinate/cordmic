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

    public readonly record struct SemVer(int Major, int Minor, int Patch) : IComparable<SemVer>
    {
        public static bool TryParse(string? raw, out SemVer v)
        {
            v = default;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var s = raw.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s[1..];
            int cut = 0;
            while (cut < s.Length && (char.IsDigit(s[cut]) || s[cut] == '.')) cut++;
            s = cut > 0 ? s[..cut] : "";
            var p = s.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2) return false;
            if (!int.TryParse(p[0], out int a) || !int.TryParse(p[1], out int b)) return false;
            int c = 0;
            if (p.Length >= 3 && !int.TryParse(p[2], out c)) return false;
            v = new SemVer(a, b, c);
            return true;
        }

        public int CompareTo(SemVer other)
        {
            int d = Major.CompareTo(other.Major);
            if (d != 0) return d;
            d = Minor.CompareTo(other.Minor);
            return d != 0 ? d : Patch.CompareTo(other.Patch);
        }

        public override string ToString() => $"{Major}.{Minor}.{Patch}";
    }

    public static int CompareVersions(string a, string b) =>
        SemVer.TryParse(a, out var va) && SemVer.TryParse(b, out var vb) ? va.CompareTo(vb) : 0;

    public static bool IsNewer(string candidate, string current) => CompareVersions(candidate, current) > 0;

    public static bool IsDowngrade(string candidate, string current) => CompareVersions(candidate, current) < 0;

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
        IReadOnlyDictionary<string, string> AssetUrls);

    public static ReleaseInfo ParseLatestJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(url))
                    urls[name] = url;
            }
        }
        string? Name(Func<string, bool> pred) => urls.Keys.FirstOrDefault(pred);
        string? Url(string? name) => name != null && urls.TryGetValue(name, out var u) ? u : null;
        var apk = Name(n => n.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));
        var setup = Name(n => n.StartsWith("CordMic-Setup-", StringComparison.OrdinalIgnoreCase)
                              && n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        var sums = Name(n => n.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase)
                             || n.StartsWith("SHA256SUMS", StringComparison.OrdinalIgnoreCase));
        return new ReleaseInfo(tag, apk, Url(apk), setup, Url(setup), Url(sums), urls);
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
