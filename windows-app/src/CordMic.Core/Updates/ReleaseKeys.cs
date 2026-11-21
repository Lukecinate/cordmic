namespace CordMic.Core.Updates;

/// <summary>A release-signing public key embedded in the app (SubjectPublicKeyInfo, Base64).</summary>
public readonly record struct EmbeddedReleaseKey(string KeyId, string SpkiBase64);

/// <summary>
/// Production K1/K2 public keys are filled in only after the maintainer sends them.
/// While this list is empty the updater keeps the existing SHA-256SUMS path.
/// Test keys must never be added here.
/// </summary>
public static class ReleaseKeys
{
    public static readonly EmbeddedReleaseKey[] Production = [];
    public static readonly string[] ProductionRevoked = [];

    /// <summary>True only when at least one production public key is embedded.</summary>
    public static bool ManifestSigningRequired => Production.Length > 0;
}
