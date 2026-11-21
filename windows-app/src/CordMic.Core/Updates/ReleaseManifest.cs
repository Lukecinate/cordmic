using System.Text;
using System.Text.Json;

namespace CordMic.Core.Updates;

public readonly record struct ManifestFile(string Name, long Size, string Sha256Hex);

public sealed class ReleaseManifestData
{
    public string Product { get; init; } = "";
    public string Version { get; init; } = "";
    public string Channel { get; init; } = "";
    public ManifestFile[] Files { get; init; } = [];
    public string[] RevokedKeyIds { get; init; } = [];
}

/// <summary>Strict-enough parse of <c>release-manifest.json</c>. Unknown fields are ignored. At most 64 KiB.</summary>
public static class ReleaseManifest
{
    public const int MaxBytes = 64 * 1024;
    public const string ProductName = "CordMic";
    public const string ChannelStable = "stable";
    public const string ChannelBeta = "beta";
    public const int MaxFiles = 32;

    public static bool TryParse(ReadOnlySpan<byte> utf8, out ReleaseManifestData data, out string error)
    {
        data = null!;
        error = "";
        if (utf8.Length == 0) { error = "empty manifest"; return false; }
        if (utf8.Length > MaxBytes) { error = "manifest too large"; return false; }
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(utf8), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "manifest must be an object"; return false; }
            if (!TryString(root, "product", out var product) || product != ProductName) { error = "product is not CordMic"; return false; }
            if (!TryString(root, "version", out var version) || !AppUpdate.SemVer.TryParse(version, out _)) { error = "bad version"; return false; }
            if (!TryString(root, "channel", out var channel) || (channel != ChannelStable && channel != ChannelBeta)) { error = "bad channel"; return false; }
            if (!root.TryGetProperty("files", out var filesEl) || filesEl.ValueKind != JsonValueKind.Array) { error = "files missing"; return false; }
            if (filesEl.GetArrayLength() == 0 || filesEl.GetArrayLength() > MaxFiles) { error = "bad files list"; return false; }
            var files = new List<ManifestFile>(filesEl.GetArrayLength());
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in filesEl.EnumerateArray())
            {
                if (f.ValueKind != JsonValueKind.Object) { error = "bad file entry"; return false; }
                if (!TryString(f, "name", out var name) || name.Length == 0 || name.Length > 100) { error = "bad file name"; return false; }
                if (!names.Add(name)) { error = "duplicate file name"; return false; }
                if (!f.TryGetProperty("size", out var sz) || sz.ValueKind != JsonValueKind.Number || !sz.TryGetInt64(out var size) || size < 0) { error = "bad size"; return false; }
                if (!TryString(f, "sha256", out var hash) || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) { error = "bad sha256"; return false; }
                files.Add(new ManifestFile(name, size, hash.ToLowerInvariant()));
            }
            var revoked = new List<string>();
            if (root.TryGetProperty("revokedKeyIds", out var rev) && rev.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in rev.EnumerateArray())
                {
                    if (r.ValueKind != JsonValueKind.String) { error = "bad revokedKeyIds"; return false; }
                    var id = (r.GetString() ?? "").Trim().ToLowerInvariant();
                    if (id.Length != 16 || !id.All(Uri.IsHexDigit)) { error = "bad revoked key id"; return false; }
                    revoked.Add(id);
                }
            }
            data = new ReleaseManifestData
            {
                Product = product,
                Version = version,
                Channel = channel,
                Files = files.ToArray(),
                RevokedKeyIds = revoked.ToArray(),
            };
            return true;
        }
        catch (JsonException ex) { error = "unreadable manifest: " + ex.Message; return false; }
    }

    public static byte[] ToUtf8(ReleaseManifestData data)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("product", data.Product);
            w.WriteString("version", data.Version);
            w.WriteString("channel", data.Channel);
            w.WriteStartArray("files");
            foreach (var f in data.Files)
            {
                w.WriteStartObject();
                w.WriteString("name", f.Name);
                w.WriteNumber("size", f.Size);
                w.WriteString("sha256", f.Sha256Hex);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("revokedKeyIds");
            foreach (var id in data.RevokedKeyIds) w.WriteStringValue(id);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    static bool TryString(JsonElement el, string name, out string value)
    {
        value = "";
        if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String) return false;
        value = p.GetString() ?? "";
        return true;
    }
}
