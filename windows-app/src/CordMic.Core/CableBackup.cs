using System.Text.Json;
using System.Text.Json.Serialization;

namespace CordMic.Core;

public sealed class CableBackupFile
{
    public int Version { get; set; } = CurrentVersion;
    public string CreatedUtc { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public List<CableEndpointBackup> Endpoints { get; set; } = new();
    public const int CurrentVersion = 1;
}

public sealed class CableEndpointBackup
{
    public string DeviceId { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    public bool IsRender { get; set; }
    public List<CablePropertyBackup> Properties { get; set; } = new();
}

public sealed class CablePropertyBackup
{
    public string Key { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Hex { get; set; }
    public uint? Dword { get; set; }
}

/// <summary>JSON backup of the few endpoint properties the helper is allowed to touch.</summary>
public static class CableBackup
{
    public const string DeviceFormatKey = "{f19f064d-082c-4e27-bc73-6882a1bb8e4c},0";
    public const string OemFormatKey = "{e4870e26-3cc5-4cd2-ba46-ca0a9a70ed04},3";
    public const string DisableSysFxKey = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";

    public static readonly string[] AllowedKeys = { DeviceFormatKey, OemFormatKey, DisableSysFxKey };

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static bool IsAllowedKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        foreach (var k in AllowedKeys)
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static bool TryParseKey(string key, out Guid fmtid, out uint pid)
    {
        fmtid = default; pid = 0;
        if (string.IsNullOrEmpty(key)) return false;
        int comma = key.LastIndexOf(',');
        if (comma <= 0 || comma == key.Length - 1) return false;
        if (!Guid.TryParse(key[..comma], out fmtid)) return false;
        return uint.TryParse(key[(comma + 1)..], out pid);
    }

    public static string Serialize(CableBackupFile file) =>
        JsonSerializer.Serialize(file, JsonOpts);

    public static bool TryDeserialize(string json, out CableBackupFile? file, out string? error)
    {
        file = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "empty backup";
            return false;
        }
        try
        {
            file = JsonSerializer.Deserialize<CableBackupFile>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            error = "invalid JSON: " + ex.Message;
            return false;
        }
        if (file is null)
        {
            error = "backup deserialized to null";
            return false;
        }
        if (file.Version != CableBackupFile.CurrentVersion)
        {
            error = $"unsupported backup version {file.Version}";
            file = null;
            return false;
        }
        if (file.Endpoints is null)
        {
            error = "backup has no endpoints list";
            file = null;
            return false;
        }
        foreach (var ep in file.Endpoints)
        {
            if (ep is null || !CableIdentity.TryParseEndpointId(ep.DeviceId, out bool isRender, out _))
            {
                error = "backup contains an invalid device id";
                file = null;
                return false;
            }
            if (ep.IsRender != isRender)
            {
                error = "backup device flow does not match its id";
                file = null;
                return false;
            }
            if (ep.Properties is null)
            {
                error = "backup endpoint has no properties list";
                file = null;
                return false;
            }
            foreach (var p in ep.Properties)
            {
                if (p is null || !IsAllowedKey(p.Key) || !TryParseKey(p.Key, out _, out _))
                {
                    error = "backup contains a property key that is not allowed";
                    file = null;
                    return false;
                }
                if (p.Type == "blob")
                {
                    if (string.IsNullOrEmpty(p.Hex) || p.Hex.Length % 2 != 0 || !TryParseHex(p.Hex, out _))
                    {
                        error = "backup blob is not valid hex";
                        file = null;
                        return false;
                    }
                }
                else if (p.Type == "ui4")
                {
                    if (p.Dword is null)
                    {
                        error = "backup ui4 value is missing";
                        file = null;
                        return false;
                    }
                }
                else
                {
                    error = "backup property type is not blob or ui4";
                    file = null;
                    return false;
                }
            }
        }
        return true;
    }

    public static string ToHex(ReadOnlySpan<byte> data)
    {
        var c = new char[data.Length * 2];
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            c[i * 2] = HexChar(b >> 4);
            c[i * 2 + 1] = HexChar(b & 0xF);
        }
        return new string(c);
    }

    public static bool TryParseHex(string hex, out byte[] data)
    {
        data = Array.Empty<byte>();
        if (string.IsNullOrEmpty(hex) || (hex.Length & 1) != 0) return false;
        var buf = new byte[hex.Length / 2];
        for (int i = 0; i < buf.Length; i++)
        {
            int hi = HexVal(hex[i * 2]);
            int lo = HexVal(hex[i * 2 + 1]);
            if (hi < 0 || lo < 0) return false;
            buf[i] = (byte)((hi << 4) | lo);
        }
        data = buf;
        return true;
    }

    static char HexChar(int v) => (char)(v < 10 ? '0' + v : 'a' + (v - 10));
    static int HexVal(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
