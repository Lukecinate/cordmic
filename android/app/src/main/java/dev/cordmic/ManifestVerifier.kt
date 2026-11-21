package dev.cordmic

import java.security.KeyFactory
import java.security.Signature
import java.security.spec.X509EncodedKeySpec
import java.util.Base64

enum class ManifestVerifyStatus {
    Ok, TooLarge, BadSignatureFormat, UnknownKey, RevokedKey,
    BadSignature, BadManifest, WrongProduct, Downgrade, ChannelMismatch
}

data class ManifestFile(val name: String, val size: Long, val sha256Hex: String)
data class ReleaseManifestData(
    val product: String,
    val version: String,
    val channel: String,
    val files: List<ManifestFile>,
    val revokedKeyIds: List<String>
)

object ManifestVerifier {
    const val MAX_MANIFEST_BYTES = 64 * 1024
    const val MAX_SIG_BYTES = 4 * 1024
    const val HEADER = "CORDMIC-MANIFEST-SIG-1"
    const val ALG = "ECDSA-P256-SHA256"
    const val FILE_NAME = "release-manifest.json"
    const val SIG_FILE_NAME = "release-manifest.json.sig"

    data class ParsedSig(val keyId: String, val der: ByteArray)

    fun parseSignature(bytes: ByteArray): ParsedSig? {
        if (bytes.isEmpty() || bytes.size > MAX_SIG_BYTES) return null
        val lines = bytes.toString(Charsets.UTF_8).replace("\r\n", "\n")
            .split('\n').map { it.trim() }.filter { it.isNotEmpty() }
        if (lines.isEmpty() || lines[0] != HEADER) return null
        var keyId: String? = null
        var alg: String? = null
        var sig: String? = null
        for (line in lines.drop(1)) {
            val eq = line.indexOf('=')
            if (eq <= 0) continue
            when (line.substring(0, eq)) {
                "keyId" -> keyId = line.substring(eq + 1).trim().lowercase()
                "alg" -> alg = line.substring(eq + 1).trim()
                "sig" -> sig = line.substring(eq + 1).trim()
            }
        }
        if (keyId == null || keyId.length != 16 || keyId.any { it !in "0123456789abcdef" }) return null
        if (alg != ALG || sig.isNullOrEmpty()) return null
        val der = try { Base64.getDecoder().decode(sig) } catch (_: IllegalArgumentException) { return null }
        if (der.size < 8) return null
        return ParsedSig(keyId, der)
    }

    fun parseManifest(bytes: ByteArray): ReleaseManifestData? {
        if (bytes.isEmpty() || bytes.size > MAX_MANIFEST_BYTES) return null
        val json = bytes.toString(Charsets.UTF_8)
        val product = stringField(json, "product") ?: return null
        if (product != "CordMic") return null
        val version = stringField(json, "version") ?: return null
        if (AppUpdate.SemVer.parse(version) == null) return null
        val channel = stringField(json, "channel") ?: return null
        if (channel != "stable" && channel != "beta") return null
        val filesBlock = arrayBlock(json, "files") ?: return null
        val fileMatches = Regex("\"name\"\\s*:\\s*\"([^\"]+)\"\\s*,\\s*\"size\"\\s*:\\s*(\\d+)\\s*,\\s*\"sha256\"\\s*:\\s*\"([0-9a-fA-F]{64})\"")
            .findAll(filesBlock).toList()
        if (fileMatches.isEmpty() || fileMatches.size > 32) return null
        val files = ArrayList<ManifestFile>(fileMatches.size)
        val names = HashSet<String>()
        for (m in fileMatches) {
            val name = m.groupValues[1]
            if (name.isEmpty() || name.length > 100 || !names.add(name.lowercase())) return null
            val size = m.groupValues[2].toLong()
            val hash = m.groupValues[3].lowercase()
            files.add(ManifestFile(name, size, hash))
        }
        val revoked = ArrayList<String>()
        val revokedBlock = arrayBlock(json, "revokedKeyIds")
        if (revokedBlock != null) {
            for (m in Regex("\"([0-9a-fA-F]{16})\"").findAll(revokedBlock)) {
                revoked.add(m.groupValues[1].lowercase())
            }
        }
        return ReleaseManifestData("CordMic", version, channel, files, revoked)
    }

    private fun stringField(json: String, key: String): String? =
        Regex("\"" + Regex.escape(key) + "\"\\s*:\\s*\"([^\"]*)\"").find(json)?.groupValues?.get(1)

    private fun arrayBlock(json: String, key: String): String? {
        val start = Regex("\"" + Regex.escape(key) + "\"\\s*:\\s*\\[").find(json) ?: return null
        val i = start.range.last + 1
        val j = json.indexOf(']', i)
        if (j < 0) return null
        return json.substring(i, j)
    }

    fun keyIdFromSpki(spki: ByteArray): String {
        val md = java.security.MessageDigest.getInstance("SHA-256").digest(spki)
        return md.copyOfRange(0, 8).joinToString("") { b -> "%02x".format(b) }
    }

    fun verifyDer(spki: ByteArray, data: ByteArray, der: ByteArray): Boolean = try {
        val key = KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(spki))
        val sig = Signature.getInstance("SHA256withECDSA")
        sig.initVerify(key)
        sig.update(data)
        sig.verify(der)
    } catch (_: Exception) {
        false
    }

    fun verify(
        manifestBytes: ByteArray,
        signatureBytes: ByteArray,
        currentVersion: String,
        expectedChannel: String,
        trusted: List<ReleaseKeys.Embedded>,
        embeddedRevoked: List<String>
    ): Pair<ManifestVerifyStatus, ReleaseManifestData?> {
        if (manifestBytes.size > MAX_MANIFEST_BYTES || signatureBytes.size > MAX_SIG_BYTES)
            return ManifestVerifyStatus.TooLarge to null
        val sig = parseSignature(signatureBytes) ?: return ManifestVerifyStatus.BadSignatureFormat to null
        val trust = trusted.firstOrNull { it.keyId.equals(sig.keyId, ignoreCase = true) }
            ?: return ManifestVerifyStatus.UnknownKey to null
        if (embeddedRevoked.any { it.equals(sig.keyId, ignoreCase = true) })
            return ManifestVerifyStatus.RevokedKey to null
        val spki = try { Base64.getDecoder().decode(trust.spkiBase64) } catch (_: IllegalArgumentException) {
            return ManifestVerifyStatus.UnknownKey to null
        }
        if (!keyIdFromSpki(spki).equals(sig.keyId, ignoreCase = true))
            return ManifestVerifyStatus.UnknownKey to null
        if (!verifyDer(spki, manifestBytes, sig.der))
            return ManifestVerifyStatus.BadSignature to null
        val parsed = parseManifest(manifestBytes) ?: return ManifestVerifyStatus.BadManifest to null
        if (parsed.product != "CordMic") return ManifestVerifyStatus.WrongProduct to null
        if (parsed.revokedKeyIds.any { it.equals(sig.keyId, ignoreCase = true) })
            return ManifestVerifyStatus.RevokedKey to null
        if (AppUpdate.isDowngrade(parsed.version, currentVersion))
            return ManifestVerifyStatus.Downgrade to null
        if (!parsed.channel.equals(expectedChannel, ignoreCase = true))
            return ManifestVerifyStatus.ChannelMismatch to null
        return ManifestVerifyStatus.Ok to parsed
    }
}
