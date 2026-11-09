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
