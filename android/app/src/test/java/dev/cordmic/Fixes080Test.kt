package dev.cordmic

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** 0.8.0 M0: install-id block, stop-reason BYE paths, flag compatibility. */
class Fixes080Test {

    @Test
    fun installId_hexRoundTripAndRejectsBadInput() {
        val id = ByteArray(16) { it.toByte() }
        val hex = InstallId.toHex(id)
        assertEquals(32, hex.length)
        assertArrayEquals(id, InstallId.decode(hex))
        assertNull(InstallId.decode(null))
        assertNull(InstallId.decode("zz"))
        assertNull(InstallId.decode("ab"))
        assertNull(InstallId.decode("g".repeat(32)))
    }

    @Test
    fun phoneIdBlock_roundTripAndRejectsZeroOrTruncated() {
        val id = ByteArray(16) { (it + 3).toByte() }
        val buf = ByteArray(PhoneIdBlock.SIZE)
        assertEquals(PhoneIdBlock.SIZE, PhoneIdBlock.write(buf, 0, id))
        assertArrayEquals(id, PhoneIdBlock.tryRead(buf, 0))
        assertEquals(0, PhoneIdBlock.write(ByteArray(4), 0, id))
        assertEquals(0, PhoneIdBlock.write(buf, 0, ByteArray(16))) // all zero
        assertNull(PhoneIdBlock.tryRead(ByteArray(PhoneIdBlock.SIZE).also {
            it[0] = PhoneIdBlock.MAGIC; it[1] = PhoneIdBlock.VERSION
        }, 0))
        assertNull(PhoneIdBlock.tryRead(byteArrayOf(PhoneIdBlock.MAGIC), 0))
    }

    @Test
    fun pong_includesInstallIdOnlyWhenAskedAndKeepsTheName() {
        val sync = SyncClock()
        val id = ByteArray(16) { (it + 1).toByte() }
        val buf = ByteArray(128)
        val n = Protocol.writePongStatus(
            buf, 0, true, true, true, false, true, 6, "SM-S721B ",
            NoiseMode.LIGHT, frontFocus = true, dirCode = 1, micCount = 2,
            sync = sync, installId = id,
        )
        assertTrue(n > SettingsSync.SIZE + PhoneIdBlock.SIZE)
        val got = PhoneIdBlock.tryRead(buf, n - PhoneIdBlock.SIZE)
        assertArrayEquals(id, got)
        // Without an id, a 0.7.2-sized trailing sync still parses and is not followed by the block.
        val old = ByteArray(128)
        val o = Protocol.writePongStatus(
            old, 0, true, true, true, false, true, 6, "Pixel 8",
            NoiseMode.LIGHT, frontFocus = true, dirCode = 1, micCount = 1, sync = sync,
        )
        assertNull(PhoneIdBlock.tryRead(old, o - PhoneIdBlock.SIZE, PhoneIdBlock.SIZE))
        val payload = SettingsSync.tryRead(old, o - SettingsSync.SIZE)
        assertNotNull(payload)
        assertEquals(SettingsSync.FLAG_PEER_SYNC or SettingsSync.FLAG_VOICE_CLARITY, payload!!.flags2 and
            (SettingsSync.FLAG_PEER_SYNC or SettingsSync.FLAG_VOICE_CLARITY or SettingsSync.FLAG_WANT_PHONE_KEY))
    }

    @Test
    fun wantPhoneKey_isBit6AndUnknownFlagsAreIgnored() {
        assertEquals(1 shl 6, SettingsSync.FLAG_WANT_PHONE_KEY)
        val flags = SettingsSync.FLAG_PEER_SYNC or SettingsSync.FLAG_WANT_PHONE_KEY or SettingsSync.FLAG_WANT_DIAG or 0x80
        val buf = ByteArray(SettingsSync.SIZE)
        SettingsSync.write(buf, 0, flags, Array(SettingsSync.FIELD_COUNT) { FieldRev.None })
        val p = SettingsSync.tryRead(buf, 0)!!
        assertTrue(p.wantPhoneKey)
        assertTrue(p.wantDiag)
        assertTrue(p.peerSync)
        assertFalse(p.studio)
        // Flag word keeps the unknown high bit for a newer peer; older fields still decode.
        assertEquals(flags, p.flags2)
    }

    @Test
    fun micService_sendsByeOnTaskRemovedAndDestroy() {
        val src = java.io.File("src/main/java/dev/cordmic/MicService.kt").takeIf { it.exists() }
            ?: java.io.File("../src/main/java/dev/cordmic/MicService.kt")
        val text = if (src.exists()) src.readText() else {
            // Gradle unit tests run with the module directory as the working tree or from app/.
            val here = java.io.File(".").canonicalFile
            var d: java.io.File? = here
            var found: String? = null
            while (d != null) {
                val f = java.io.File(d, "app/src/main/java/dev/cordmic/MicService.kt")
                if (f.exists()) { found = f.readText(); break }
                val f2 = java.io.File(d, "android/app/src/main/java/dev/cordmic/MicService.kt")
                if (f2.exists()) { found = f2.readText(); break }
                d = d.parentFile
            }
            found ?: error("MicService.kt not found from $here")
        }
        assertTrue(text.contains("override fun onTaskRemoved"))
        assertTrue(text.contains("shutdown(\"task-removed\")"))
        assertTrue(text.contains("shutdown(\"destroy\")"))
        assertTrue(text.contains("shutdown(\"notification\")"))
        val st = src.parentFile?.resolve("Streamer.kt")?.takeIf { it.exists() }?.readText()
            ?: java.io.File(src.absolutePath.replace("MicService.kt", "Streamer.kt")).readText()
        assertTrue(st.contains("fun stop(reason: String"))
        assertTrue(st.contains("T_BYE"))
        assertTrue(st.contains("stop reason:"))
    }
}
