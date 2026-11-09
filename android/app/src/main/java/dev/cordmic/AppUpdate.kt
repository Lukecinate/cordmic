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

    data class SemVer(val major: Int, val minor: Int, val patch: Int) : Comparable<SemVer> {
        override fun compareTo(other: SemVer): Int {
            val a = major.compareTo(other.major); if (a != 0) return a
            val b = minor.compareTo(other.minor); return if (b != 0) b else patch.compareTo(other.patch)
        }
        override fun toString() = "$major.$minor.$patch"

        companion object {
            fun parse(raw: String?): SemVer? {
                if (raw.isNullOrBlank()) return null
                var s = raw.trim()
                if (s.startsWith("v") || s.startsWith("V")) s = s.substring(1)
                val cut = s.indexOfFirst { !it.isDigit() && it != '.' }.let { if (it < 0) s.length else it }
                val parts = s.substring(0, cut).split('.').filter { it.isNotEmpty() }
                if (parts.size < 2) return null
                val a = parts[0].toIntOrNull() ?: return null
                val b = parts[1].toIntOrNull() ?: return null
                val c = if (parts.size >= 3) parts[2].toIntOrNull() ?: 0 else 0
                return SemVer(a, b, c)
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
    )

    fun parseLatestJson(json: String): ReleaseInfo {
        val tag = jsonString(json, "tag_name") ?: ""
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
