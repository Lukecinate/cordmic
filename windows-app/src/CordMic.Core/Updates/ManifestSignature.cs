using System.Text;

namespace CordMic.Core.Updates;

/// <summary>Text format for <c>release-manifest.json.sig</c>: header, key id, algorithm, Base64 DER signature.</summary>
public static class ManifestSignature
{
    public const string Header = "CORDMIC-MANIFEST-SIG-1";
    public const int MaxBytes = 4 * 1024;

    public readonly record struct Parsed(string KeyId, string Alg, byte[] Der);

    public static string Format(string keyId, byte[] der)
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append('\n');
        sb.Append("keyId=").Append(keyId.Trim().ToLowerInvariant()).Append('\n');
        sb.Append("alg=").Append(ReleaseCrypto.AlgorithmName).Append('\n');
        sb.Append("sig=").Append(Convert.ToBase64String(der)).Append('\n');
        return sb.ToString();
    }

    public static bool TryParse(ReadOnlySpan<byte> bytes, out Parsed parsed, out string error)
    {
        parsed = default;
        error = "";
        if (bytes.Length == 0) { error = "empty signature"; return false; }
        if (bytes.Length > MaxBytes) { error = "signature too large"; return false; }
        string text = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n");
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 4 || lines[0] != Header) { error = "bad signature header"; return false; }
        string? keyId = null, alg = null, sig = null;
        for (int i = 1; i < lines.Length; i++)
        {
            int eq = lines[i].IndexOf('=');
            if (eq <= 0) continue;
            string k = lines[i][..eq], v = lines[i][(eq + 1)..];
            if (k == "keyId") keyId = v.Trim().ToLowerInvariant();
            else if (k == "alg") alg = v.Trim();
            else if (k == "sig") sig = v.Trim();
        }
        if (string.IsNullOrEmpty(keyId) || keyId.Length != 16) { error = "bad key id"; return false; }
        foreach (char c in keyId)
            if (!Uri.IsHexDigit(c)) { error = "bad key id"; return false; }
        if (alg != ReleaseCrypto.AlgorithmName) { error = "unsupported algorithm"; return false; }
        if (string.IsNullOrEmpty(sig)) { error = "missing signature"; return false; }
        try
        {
            var der = Convert.FromBase64String(sig);
            if (der.Length < 8) { error = "signature too short"; return false; }
            parsed = new Parsed(keyId, alg, der);
            return true;
        }
        catch (FormatException) { error = "signature is not Base64"; return false; }
    }
}
