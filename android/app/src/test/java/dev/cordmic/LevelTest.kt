package dev.cordmic

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class LevelTest {
    @Test fun peakOfLittleEndianPcm16() {
        // samples: 1000, -32768, 200
        val b = byteArrayOf(0, 0, 0xE8.toByte(), 0x03, 0x00, 0x80.toByte(), 0xC8.toByte(), 0x00)
        assertEquals(32768, pcm16Peak(b, 2, 3))
        assertEquals(1000, pcm16Peak(b, 2, 1))
    }

    @Test fun dbScale() {
        assertEquals(0f, Level.peakToDb(32768), 0.01f)
        assertEquals(-6.02f, Level.peakToDb(16384), 0.05f)
        assertEquals(Level.FLOOR_DB, Level.peakToDb(0), 0f)
        assertNull(Level.displayDb(Level.FLOOR_DB))
        assertEquals(-18, Level.displayDb(-17.6f))
    }

    @Test fun ipv4Validation() {
        assert(MainActivity.isIpv4("192.168.42.10"))
        assert(!MainActivity.isIpv4("192.168.42"))
        assert(!MainActivity.isIpv4("256.1.1.1"))
    }
}
