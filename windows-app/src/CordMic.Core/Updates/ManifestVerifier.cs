namespace CordMic.Core.Updates;

public enum ManifestVerifyStatus
{
    Ok,
    TooLarge,
    BadSignatureFormat,
    UnknownKey,
    RevokedKey,
    BadSignature,
    BadManifest,
    WrongProduct,
    Downgrade,
    ChannelMismatch,
}

/// <summary>
/// Verifies a release manifest against an injected trust list (production or test keys).
/// Does not use clocks. Downgrade protection is "version must increase".
/// </summary>
public static class ManifestVerifier
{
    public static ManifestVerifyStatus Verify(
        ReadOnlySpan<byte> manifestBytes,
        ReadOnlySpan<byte> signatureBytes,
        string currentVersion,
        string expectedChannel,
        IReadOnlyList<EmbeddedReleaseKey> trusted,
        IReadOnlyList<string> embeddedRevoked,
        out ReleaseManifestData? manifest,
        out string detail)
    {
        manifest = null;
        detail = "";
        if (manifestBytes.Length > ReleaseManifest.MaxBytes || signatureBytes.Length > ManifestSignature.MaxBytes)
        {
            detail = "manifest or signature exceeds size limit";
            return ManifestVerifyStatus.TooLarge;
        }
        if (!ManifestSignature.TryParse(signatureBytes, out var sig, out var sigErr))
        {
            detail = sigErr;
            return ManifestVerifyStatus.BadSignatureFormat;
        }
        var trust = trusted.FirstOrDefault(k => string.Equals(k.KeyId, sig.KeyId, StringComparison.OrdinalIgnoreCase));
        if (trust.KeyId is null || trust.KeyId.Length == 0)
        {
            detail = "unknown key " + sig.KeyId;
            return ManifestVerifyStatus.UnknownKey;
        }
        var embedded = new HashSet<string>(embeddedRevoked.Select(x => x.ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
        if (embedded.Contains(sig.KeyId))
        {
            detail = "revoked key " + sig.KeyId;
            return ManifestVerifyStatus.RevokedKey;
        }
        byte[] spki;
        try { spki = Convert.FromBase64String(trust.SpkiBase64); }
        catch (FormatException) { detail = "embedded key is not Base64"; return ManifestVerifyStatus.UnknownKey; }
        if (!string.Equals(ReleaseCrypto.KeyId(spki), sig.KeyId, StringComparison.OrdinalIgnoreCase))
        {
            detail = "key id does not match SPKI";
            return ManifestVerifyStatus.UnknownKey;
        }
        if (!ReleaseCrypto.VerifyDer(spki, manifestBytes, sig.Der))
        {
            detail = "signature does not match the manifest bytes";
            return ManifestVerifyStatus.BadSignature;
        }
        if (!ReleaseManifest.TryParse(manifestBytes, out var parsed, out var parseErr))
        {
            detail = parseErr;
            return parseErr.Contains("CordMic", StringComparison.Ordinal) ? ManifestVerifyStatus.WrongProduct : ManifestVerifyStatus.BadManifest;
        }
        // Manifest-listed revoked keys are honoured only after a non-revoked signer has been verified.
        if (parsed.RevokedKeyIds.Any(id => string.Equals(id, sig.KeyId, StringComparison.OrdinalIgnoreCase)))
        {
            detail = "signer is listed in this manifest's revokedKeyIds";
            return ManifestVerifyStatus.RevokedKey;
        }
        if (!AppUpdate.IsNewer(parsed.Version, currentVersion) && AppUpdate.CompareVersions(parsed.Version, currentVersion) != 0)
        {
            // equal version is allowed for a verify-only check of the current build; callers that
            // install an update must also require IsNewer.
        }
        if (AppUpdate.IsDowngrade(parsed.Version, currentVersion))
        {
            detail = "refusing downgrade to " + parsed.Version;
            return ManifestVerifyStatus.Downgrade;
        }
        if (!string.Equals(parsed.Channel, expectedChannel, StringComparison.OrdinalIgnoreCase))
        {
            detail = "channel is " + parsed.Channel + ", expected " + expectedChannel;
            return ManifestVerifyStatus.ChannelMismatch;
        }
        manifest = parsed;
        detail = "ok";
        return ManifestVerifyStatus.Ok;
    }

    /// <summary>Install path: the manifest version must be strictly newer than the running app.</summary>
    public static ManifestVerifyStatus RequireNewer(ManifestVerifyStatus status, ReleaseManifestData? manifest, string currentVersion, out string detail)
    {
        detail = "ok";
        if (status != ManifestVerifyStatus.Ok || manifest is null) return status;
        if (!AppUpdate.IsNewer(manifest.Version, currentVersion))
        {
            detail = "version is not newer than " + currentVersion;
            return ManifestVerifyStatus.Downgrade;
        }
        return status;
    }

    public const string ManifestFileName = "release-manifest.json";
    public const string SignatureFileName = "release-manifest.json.sig";
}
