package dev.cordmic

import java.io.File
import java.security.MessageDigest

/** Version compare, GitHub release JSON, and SHA-256SUMS verification (no network). */
object AppUpdate {
    const val OWNER = "Lukecinate"
    const val REPO = "cordmic"
    const val LATEST_API = "https://api.github.com/repos/Lukecinate/cordmic/releases/latest"
    const val USER_AGENT = "CordMic-Updater"
    const val MIN_CHECK_MS = 6L * 60 * 60 * 1000

    data class SemVer(val major: Int, val minor: Int, val patch: Int, val pre: String = "") : Comparable<SemVer> {
        val isPrerelease: Boolean get() = pre.isNotEmpty()
        override fun compareTo(other: SemVer): Int {
            val a = major.compareTo(other.major); if (a != 0) return a
            val b = minor.compareTo(other.minor); if (b != 0) return b
            val c = patch.compareTo(other.patch); if (c != 0) return c
            if (!isPrerelease && !other.isPrerelease) return 0
            if (!isPrerelease) return 1
            if (!other.isPrerelease) return -1
            return comparePre(pre, other.pre)
        }
        override fun toString() = if (pre.isEmpty()) "$major.$minor.$patch" else "$major.$minor.$patch-$pre"

        companion object {
            fun parse(raw: String?): SemVer? {
                if (raw.isNullOrBlank()) return null
                var s = raw.trim()
                if (s.startsWith("v") || s.startsWith("V")) s = s.substring(1)
                val plus = s.indexOf('+')
                if (plus >= 0) s = s.substring(0, plus)
                var pre = ""
                val dash = s.indexOf('-')
                if (dash >= 0) {
                    pre = s.substring(dash + 1)
                    s = s.substring(0, dash)
                    if (pre.isEmpty()) return null
                    for (id in pre.split('.')) {
                        if (id.isEmpty() || id.any { it !in '0'..'9' && it !in 'a'..'z' && it !in 'A'..'Z' && it != '-' }) return null
                    }
                }
                val parts = s.split('.').filter { it.isNotEmpty() }
                if (parts.size < 2) return null
                val a = parts[0].toIntOrNull() ?: return null
                val b = parts[1].toIntOrNull() ?: return null
                val c = if (parts.size >= 3) parts[2].toIntOrNull() ?: 0 else 0
                return SemVer(a, b, c, pre)
            }

            private fun comparePre(a: String, b: String): Int {
                val aa = a.split('.')
                val bb = b.split('.')
                val n = minOf(aa.size, bb.size)
                for (i in 0 until n) {
                    val an = aa[i].all { it.isDigit() } && aa[i].isNotEmpty()
                    val bn = bb[i].all { it.isDigit() } && bb[i].isNotEmpty()
                    val c = when {
                        an && bn -> aa[i].toLong().compareTo(bb[i].toLong())
                        an -> -1
                        bn -> 1
                        else -> aa[i].compareTo(bb[i])
                    }
                    if (c != 0) return c
                }
                return aa.size.compareTo(bb.size)
            }
        }
    }

    fun compareVersions(a: String, b: String): Int {
        val va = SemVer.parse(a) ?: return 0
        val vb = SemVer.parse(b) ?: return 0
        return va.compareTo(vb)
    }

    fun isNewer(candidate: String, current: String) = compareVersions(candidate, current) > 0
    fun isDowngrade(candidate: String, current: String) = compareVersions(candidate, current) < 0

    fun isPrereleaseRelease(tag: String?, prereleaseFlag: Boolean = false): Boolean =
        prereleaseFlag || (SemVer.parse(tag)?.isPrerelease == true)

    /** Auto-update may offer this release. Pre-releases and drafts are ignored. */
    fun shouldOffer(info: ReleaseInfo, currentVersion: String): Boolean {
        if (info.draft || isPrereleaseRelease(info.tag, info.prerelease)) return false
        return isNewer(info.tag, currentVersion)
    }

    fun parseSha256Sums(text: String): Map<String, String> {
        val d = LinkedHashMap<String, String>()
        for (raw in text.split('\r', '\n')) {
            val line = raw.trim()
            if (line.startsWith("#") || line.length < 66) continue
            val hash = line.substring(0, 64)
            if (!hash.all { it in '0'..'9' || it in 'a'..'f' || it in 'A'..'F' }) continue
            var rest = line.substring(64).trim()
            if (rest.startsWith("*")) rest = rest.substring(1).trim()
            val name = rest.trim().trim('"').substringAfterLast('/').substringAfterLast('\\')
            if (name.isNotEmpty()) d[name] = hash.lowercase()
        }
        return d
    }

    fun hashMatches(data: ByteArray, expectedHex: String): Boolean {
        if (expectedHex.isBlank()) return false
        val md = MessageDigest.getInstance("SHA-256")
        val got = md.digest(data).joinToString("") { "%02x".format(it) }
        return got.equals(expectedHex.trim(), ignoreCase = true)
    }

    fun hashMatchesFile(file: File, expectedHex: String): Boolean =
        hashMatches(file.readBytes(), expectedHex)

    data class ReleaseInfo(
        val tag: String,
        val apkName: String?,
        val apkUrl: String?,
        val setupName: String?,
        val setupUrl: String?,
        val sumsUrl: String?,
        val assetUrls: Map<String, String>,
        val prerelease: Boolean = false,
        val draft: Boolean = false,
    )

    fun parseLatestJson(json: String): ReleaseInfo {
        val tag = jsonString(json, "tag_name") ?: ""
        val prerelease = jsonBool(json, "prerelease")
        val draft = jsonBool(json, "draft")
        val urls = LinkedHashMap<String, String>()
        val start = json.indexOf("\"assets\"")
        val slice = if (start >= 0) json.substring(start) else json
        val names = Regex("\"name\"\\s*:\\s*\"([^\"]+)\"").findAll(slice).map { it.groupValues[1] }.toList()
        val urlsFound = Regex("\"browser_download_url\"\\s*:\\s*\"([^\"]+)\"").findAll(slice).map { it.groupValues[1] }.toList()
        // Pair each download URL with the nearest preceding file name.
        for (url in urlsFound) {
            val at = slice.indexOf(url)
            val before = if (at >= 0) slice.substring(0, at) else slice
            val name = Regex("\"name\"\\s*:\\s*\"([^\"]+)\"").findAll(before).lastOrNull()?.groupValues?.get(1)
            if (!name.isNullOrEmpty()) urls[name] = url
        }
        if (urls.isEmpty()) {
            for (i in names.indices) if (i < urlsFound.size) urls[names[i]] = urlsFound[i]
        }
        val apk = urls.keys.firstOrNull { it.endsWith(".apk", ignoreCase = true) }
        val setup = urls.keys.firstOrNull { it.startsWith("CordMic-Setup-", ignoreCase = true) && it.endsWith(".exe", ignoreCase = true) }
        val sums = urls.keys.firstOrNull { it.equals("SHA256SUMS", ignoreCase = true) || it.startsWith("SHA256SUMS") }
        return ReleaseInfo(tag, apk, urls[apk], setup, urls[setup], urls[sums], urls)
    }

    private fun jsonString(json: String, key: String): String? {
        val m = Regex("\"" + Regex.escape(key) + "\"\\s*:\\s*\"((?:\\\\.|[^\"])*)\"").find(json)
        return m?.groupValues?.get(1)
    }

    fun hashForAsset(sums: Map<String, String>, assetName: String): String? {
        sums[assetName]?.let { return it }
        val file = assetName.substringAfterLast('/').substringAfterLast('\\')
        return sums[file]
    }
}
