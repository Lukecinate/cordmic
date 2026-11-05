package dev.cordmic

import org.junit.Assert.assertEquals
import org.junit.Test

/** Same vector as windows-receiver/tests/test_protocol.py::test_header_layout_matches_android. */
class ProtocolTest {
    @Test fun headerLayoutMatchesPython() {
        val b = ByteArray(Protocol.HEADER_SIZE)
        Protocol.writeHeader(b, Protocol.T_PING, 0x01020304, 0x1122334455667788L, 0, 0)
        assertEquals("554d010404030201887766554433221100000000", b.joinToString("") { "%02x".format(it) })
    }

    @Test fun audioFlagsMono48k() = assertEquals(0, Protocol.flags(1, 0))

    @Test fun statusBitsStayOutOfTheLegacyField() {
        val f = Protocol.flags(1, 0, noiseCancellation = true, agc = true, aaudio = true)
        assertEquals(0, f and 0x0FFF)
        assertEquals(1 shl 12, f and (1 shl 12))
        assertEquals(1 shl 14, f and (1 shl 14))
    }

    @Test fun pingControlRoundTrip() {
        val buf = ByteArray(Protocol.HEADER_SIZE + 4)
        buf[Protocol.HEADER_SIZE] = Protocol.LINK_MAGIC
        buf[Protocol.HEADER_SIZE + 1] = 3 // NC + AGC
        buf[Protocol.HEADER_SIZE + 2] = 12
        buf[Protocol.HEADER_SIZE + 3] = 4
        val c = Protocol.readPingControl(buf, buf.size)!!
        assertEquals(true, c.noiseCancellation)
        assertEquals(12, c.gainDb)
        assertEquals(4, c.changeSeq)
        assertEquals(null, Protocol.readPingControl(ByteArray(Protocol.HEADER_SIZE), Protocol.HEADER_SIZE))
    }

    @Test fun parseRoundTrip() {
        val b = ByteArray(Protocol.HEADER_SIZE)
        Protocol.writeHeader(b, Protocol.T_PONG, -5, 123456789L, 0, 0)
        val h = Protocol.parse(b, b.size)!!
        assertEquals(Protocol.T_PONG, h.type); assertEquals(-5, h.seq); assertEquals(123456789L, h.tsUs)
    }
}
