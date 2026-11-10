package dev.cordmic

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** 0.5.7 diagnostics block. Mirrors the C# PhoneDiagTests (same golden bytes). */
class PhoneDiagTest {
    private val golden = byteArrayOf(0xC7.toByte(), 0xC2.toByte(), 0x11, 0x07, 0xF4.toByte(), 0xBD.toByte())

    @Test fun diagMatchesTheWireLayout() {
        val d = VoiceDiag(-62.0, gated = true, agc = true, agcGainDb = 8.5, expanderDb = -6.0, floorKnown = true)
        val buf = ByteArray(Protocol.DIAG_SIZE)
        assertEquals(Protocol.DIAG_SIZE, Protocol.writeDiag(buf, 0, d))
        assertArrayEquals(golden, buf)
        assertEquals(d, Protocol.readDiag(buf, 0, buf.size))
        buf[2] = (buf[2].toInt() xor 1).toByte()
        assertNull(Protocol.readDiag(buf, 0, buf.size))
    }

    private fun pong(diag: VoiceDiag?, sync: SyncClock?): Pair<ByteArray, Int> {
        val buf = ByteArray(128)
        val n = Protocol.writePongStatus(buf, 0, true, true, true, false, true, 6, "Pixel 8",
            NoiseMode.STRONG, sync = sync, diag = diag)
        return buf to n
    }

    @Test fun diagGoesBetweenNameAndSyncOnlyWithSync() {
        val d = VoiceDiag(-60.0, false, true, 4.0, 0.0, true)
        val sync = SyncClock()
        val (b, n) = pong(d, sync)
        val name = "Pixel 8".encodeToByteArray()
        val at = Protocol.LINK_SIZE + name.size
        assertEquals(0, b[at].toInt())
        assertEquals(d, Protocol.readDiag(b, at + 1, n - at - 1))
        assertNotNull(SettingsSync.tryRead(b, n - SettingsSync.SIZE))
        assertEquals(Protocol.LINK_SIZE + name.size + 1 + Protocol.DIAG_SIZE + SettingsSync.SIZE, n)

        // Without the PC's request (no diag) the layout is exactly the 0.5.6 one.
        val (_, legacy) = pong(null, sync)
        assertEquals(Protocol.LINK_SIZE + name.size + 1 + SettingsSync.SIZE, legacy)
        // A pre-sync PC never gets either block.
        val (_, classic) = pong(d, null)
        assertEquals(Protocol.LINK_SIZE + name.size, classic)
    }

    @Test fun pingFlagIsReadAndOlderPcsDoNotAsk() {
        val buf = ByteArray(Protocol.HEADER_SIZE + Protocol.LINK_SIZE + SettingsSync.SIZE)
        Protocol.writeHeader(buf, Protocol.T_PING, 1, 0, 0, 0)
        buf[Protocol.HEADER_SIZE] = Protocol.LINK_MAGIC
        buf[Protocol.HEADER_SIZE + 1] = 32
        SettingsSync.write(buf, Protocol.HEADER_SIZE + Protocol.LINK_SIZE,
            SettingsSync.FLAG_PEER_SYNC or SettingsSync.FLAG_WANT_DIAG, SyncClock().revs)
        assertTrue(Protocol.readPingControl(buf, buf.size)!!.sync!!.wantDiag)
        SettingsSync.write(buf, Protocol.HEADER_SIZE + Protocol.LINK_SIZE, SettingsSync.FLAG_PEER_SYNC, SyncClock().revs)
        assertTrue(!Protocol.readPingControl(buf, buf.size)!!.sync!!.wantDiag)
    }

    @Test fun chainReportsFloorGateAndAgc() {
        val c = VoiceChain(48000)
        c.mode = NoiseMode.OFF; c.agc = true; c.gainDb = 0
        val hiss = ShortArray(48000) { ((Math.random() * 2 - 1) * 32768 * 0.001).toInt().toShort() } // about −65 dBFS
        c.process(hiss, hiss.size)
        val d = c.diagnostics
        assertTrue("gated", d.gated)
        assertTrue("floor ${d.floorDbfs}", d.floorDbfs in -75.0..-58.0)
        assertTrue(d.agc)
        assertTrue(d.floorKnown)
        assertTrue("expander ${d.expanderDb}", d.expanderDb < -3.0)
    }
}
