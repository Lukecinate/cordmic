package dev.cordmic

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.security.MessageDigest

class AppUpdateTest {
    @Test fun newerDetection() {
        assertTrue(AppUpdate.isNewer("0.5.5", "0.5.4"))
        assertFalse(AppUpdate.isNewer("v0.5.5", "0.5.5"))
        assertTrue(AppUpdate.isDowngrade("0.5.4", "0.5.5"))
        assertFalse(AppUpdate.isDowngrade("0.5.5", "v0.5.5"))
    }

    @Test fun parseSumsAndVerifyHash() {
        val text = """
            # c
            0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  CordMic-0.5.5-release.apk
            fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210 *CordMic-Setup-0.5.5.exe
        """.trimIndent()
        val d = AppUpdate.parseSha256Sums(text)
        assertEquals("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", d["CordMic-0.5.5-release.apk"])
        assertEquals("fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210", d["CordMic-Setup-0.5.5.exe"])
        val payload = "hello".toByteArray()
        val good = MessageDigest.getInstance("SHA-256").digest(payload).joinToString("") { "%02x".format(it) }
        assertTrue(AppUpdate.hashMatches(payload, good))
        assertFalse(AppUpdate.hashMatches(payload, "00" + good.substring(2)))
    }

    @Test fun parseLatestJson() {
        val json = """
            {"tag_name":"v0.5.5","assets":[
              {"name":"CordMic-0.5.5-release.apk","browser_download_url":"https://example.test/apk"},
              {"name":"SHA256SUMS","browser_download_url":"https://example.test/sums"}
            ]}
        """.trimIndent()
        val info = AppUpdate.parseLatestJson(json)
        assertEquals("v0.5.5", info.tag)
        assertEquals("https://example.test/apk", info.apkUrl)
        assertEquals("https://example.test/sums", info.sumsUrl)
    }
}

    @Test fun preReleaseOrdering() {
        assertTrue(AppUpdate.isNewer("0.8.0-beta.1", "0.7.2"))
        assertTrue(AppUpdate.isNewer("0.8.0", "0.8.0-beta.1"))
        assertFalse(AppUpdate.isNewer("0.8.0-beta.1", "0.8.0"))
        assertTrue(AppUpdate.isNewer("0.8.0-beta.2", "0.8.0-beta.1"))
        assertFalse(AppUpdate.isNewer("v0.8.0-beta.1+abc", "0.8.0-beta.1"))
        assertFalse(AppUpdate.isNewer("1.0.0-beta.2", "1.0.0-beta.11"))
        assertFalse(AppUpdate.isNewer("1.0.0-rc.1", "1.0.0"))
        val v = AppUpdate.SemVer.parse("v0.8.0-beta.1")!!
        assertTrue(v.isPrerelease)
        assertEquals("0.8.0-beta.1", v.toString())
    }

    @Test fun shouldOffer_ignoresPrereleases() {
        val beta = AppUpdate.parseLatestJson(
            """{"tag_name":"v0.8.0-beta.1","prerelease":true,"draft":false,"assets":[]}"""
        )
        assertTrue(beta.prerelease)
        assertFalse(AppUpdate.shouldOffer(beta, "0.7.2"))
        assertFalse(AppUpdate.shouldOffer(beta, "0.8.0-beta.1"))
        val byTag = AppUpdate.parseLatestJson("""{"tag_name":"v0.8.0-beta.1","assets":[]}""")
        assertFalse(byTag.prerelease)
        assertFalse(AppUpdate.shouldOffer(byTag, "0.7.2"))
        val draft = AppUpdate.parseLatestJson("""{"tag_name":"v0.8.0","draft":true,"assets":[]}""")
        assertTrue(draft.draft)
        assertFalse(AppUpdate.shouldOffer(draft, "0.7.2"))
        val stable = AppUpdate.parseLatestJson("""{"tag_name":"v0.8.0","prerelease":false,"assets":[]}""")
        assertTrue(AppUpdate.shouldOffer(stable, "0.7.2"))
        assertTrue(AppUpdate.shouldOffer(stable, "0.8.0-beta.1"))
        assertFalse(AppUpdate.shouldOffer(stable, "0.8.0"))
    }
}
