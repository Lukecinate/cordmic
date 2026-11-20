using System.Security.Cryptography;
using System.Text;

namespace CordMic.Core;

/// <summary>Learned jitter target and clock drift for one phone, stored without raw identifiers.</summary>
public sealed class PhoneLinkSeed
{
    /// <summary>model + ':' + hex of a salted hash. Never a raw install id or serial.</summary>
    public string Key { get; set; } = "";
    public double TargetMs { get; set; }
    public double DriftPpm { get; set; }
    /// <summary>0–1, from how long the session that produced this seed ran.</summary>
    public double Confidence { get; set; }
    /// <summary>UTC ticks of last use, only for LRU eviction (not used as a duration).</summary>
    public long LastUsedUtcTicks { get; set; }
}

/// <summary>
/// 0.8.0 M0 (F2): remember the converged jitter target and drift per phone, start the next session from them,
/// and raise the target sooner in the first minute. Keys are a model name plus a salted hash of the phone's
/// install id; the raw id is never stored.
/// </summary>
public static class PhoneLinkHistory
{
    public const int MaxPhones = 32;
    public const double MinTargetMs = 10, MaxTargetMs = 40;
    public const double MaxAbsPpm = 1000;
    public const double MinStreamSecToSave = 60;
    public const int HashHexChars = 16;

    public static double ClampTargetMs(double ms) =>
        double.IsFinite(ms) ? Math.Clamp(ms, MinTargetMs, MaxTargetMs) : MinTargetMs;

    public static double ClampPpm(double ppm) =>
        double.IsFinite(ppm) ? Math.Clamp(ppm, -MaxAbsPpm, MaxAbsPpm) : 0;

    public static double ClampConfidence(double c) =>
        double.IsFinite(c) ? Math.Clamp(c, 0, 1) : 0;

    /// <summary>Confidence after a finished session of <paramref name="streamedSec"/> seconds (0 below 60 s).</summary>
    public static double ConfidenceFromDuration(double streamedSec) =>
        streamedSec < MinStreamSecToSave ? 0 : Math.Clamp((streamedSec - 30) / 180.0, 0.2, 1);

    /// <summary>Generate a 16-byte salt, returned as 32 hex characters.</summary>
    public static string NewSalt() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>
    /// Build the stored key. When the phone did not send an install id (older app), the key is
    /// <c>model-only:&lt;model&gt;</c> so two phones of the same model share history — a fallback, not ideal.
    /// </summary>
    public static string Key(string? model, byte[]? installId, string? saltHex)
    {
        string m = DeviceNames.Clean(model).ToLowerInvariant();
        if (m.Length > 40) m = m[..40];
        if (installId is not { Length: > 0 } || string.IsNullOrEmpty(saltHex))
            return string.IsNullOrEmpty(m) ? "" : "model-only:" + m;
        byte[] salt;
        try { salt = Convert.FromHexString(saltHex); }
        catch (FormatException) { return string.IsNullOrEmpty(m) ? "" : "model-only:" + m; }
        if (salt.Length == 0) return string.IsNullOrEmpty(m) ? "" : "model-only:" + m;
        byte[] data = new byte[salt.Length + installId.Length];
        Buffer.BlockCopy(salt, 0, data, 0, salt.Length);
        Buffer.BlockCopy(installId, 0, data, salt.Length, installId.Length);
        string hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()[..HashHexChars];
        return (string.IsNullOrEmpty(m) ? "id" : m) + ":" + hash;
    }

    public static PhoneLinkSeed? Find(IReadOnlyList<PhoneLinkSeed>? list, string key)
    {
        if (list is null || string.IsNullOrEmpty(key)) return null;
        foreach (var s in list)
            if (s?.Key == key) return s;
        return null;
    }

    /// <summary>Insert or update, evict the least-recently-used entry when over <see cref="MaxPhones"/>.</summary>
    public static List<PhoneLinkSeed> Upsert(IEnumerable<PhoneLinkSeed>? list, PhoneLinkSeed seed, long nowUtcTicks)
    {
        var result = new List<PhoneLinkSeed>();
        if (list != null)
        {
            foreach (var s in list)
                if (s is not null && !string.IsNullOrEmpty(s.Key) && s.Key != seed.Key) result.Add(s);
        }
        seed.TargetMs = ClampTargetMs(seed.TargetMs);
        seed.DriftPpm = ClampPpm(seed.DriftPpm);
        seed.Confidence = ClampConfidence(seed.Confidence);
        seed.LastUsedUtcTicks = nowUtcTicks;
        result.Add(seed);
        while (result.Count > MaxPhones)
        {
            int oldest = 0;
            for (int i = 1; i < result.Count; i++)
                if (result[i].LastUsedUtcTicks < result[oldest].LastUsedUtcTicks) oldest = i;
            result.RemoveAt(oldest);
        }
        return result;
    }

    public static string SeedLogLine(double targetMs, double driftPpm, bool fromHistory, double confidence)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return fromHistory
            ? $"seed: target={targetMs.ToString("F1", inv)}ms, drift={driftPpm.ToString("F0", inv)}ppm (from history, confidence={confidence.ToString("F2", inv)})"
            : $"seed: target={targetMs.ToString("F1", inv)}ms, drift={driftPpm.ToString("F0", inv)}ppm (from default)";
    }
}
