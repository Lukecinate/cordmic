package dev.cordmic

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ManifestVerifierTest {
    private val testSpki =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE0kqPcNFN/FPgxce2q0V+5WHlXtUrg0wxKKx6vBDCUvTXnCCfOo38ZSOO1qtmQhgvQrBnVTjL/6AWwNM6bKPGSQ=="
    private val testKeyId = "17dd91eb43887abf"

    @Test fun productionKeysStayEmpty() {
        assertTrue(ReleaseKeys.production.isEmpty())
        assertFalse(ReleaseKeys.manifestSigningRequired)
    }

    @Test fun testVectorVerifies() {
        val man = javaClass.classLoader!!.getResourceAsStream("release-manifest.json")!!.readBytes()
        val sig = javaClass.classLoader!!.getResourceAsStream("release-manifest.json.sig")!!.readBytes()
        val trust = listOf(ReleaseKeys.Embedded(testKeyId, testSpki))
        val (st, data) = ManifestVerifier.verify(man, sig, "0.7.2", "stable", trust, emptyList())
        assertEquals(ManifestVerifyStatus.Ok, st)
        assertNotNull(data)
        assertEquals("0.8.0", data!!.version)
    }

    @Test fun rejectedWhenBytesChange() {
        val man = javaClass.classLoader!!.getResourceAsStream("release-manifest.json")!!.readBytes()
        val sig = javaClass.classLoader!!.getResourceAsStream("release-manifest.json.sig")!!.readBytes()
        man[man.size / 2] = (man[man.size / 2].toInt() xor 1).toByte()
        val trust = listOf(ReleaseKeys.Embedded(testKeyId, testSpki))
        val (st, _) = ManifestVerifier.verify(man, sig, "0.7.2", "stable", trust, emptyList())
        assertEquals(ManifestVerifyStatus.BadSignature, st)
    }

    @Test fun rejectedOnDowngrade() {
        val man = javaClass.classLoader!!.getResourceAsStream("release-manifest.json")!!.readBytes()
        val sig = javaClass.classLoader!!.getResourceAsStream("release-manifest.json.sig")!!.readBytes()
        val trust = listOf(ReleaseKeys.Embedded(testKeyId, testSpki))
        val (st, _) = ManifestVerifier.verify(man, sig, "0.9.0", "stable", trust, emptyList())
        assertEquals(ManifestVerifyStatus.Downgrade, st)
    }

    @Test fun rejectedOnUnknownKey() {
        val man = javaClass.classLoader!!.getResourceAsStream("release-manifest.json")!!.readBytes()
        val sig = javaClass.classLoader!!.getResourceAsStream("release-manifest.json.sig")!!.readBytes()
        val trust = listOf(ReleaseKeys.Embedded("0123456789abcdef", testSpki))
        val (st, _) = ManifestVerifier.verify(man, sig, "0.7.2", "stable", trust, emptyList())
        assertEquals(ManifestVerifyStatus.UnknownKey, st)
    }
}
