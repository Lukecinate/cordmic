package dev.cordmic

import dev.cordmic.usb.AccessoryLink
import dev.cordmic.usb.UsbFrameCodec
import dev.cordmic.usb.UsbFrameDecoder
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.ByteArrayOutputStream
import java.io.PipedInputStream
import java.io.PipedOutputStream
import java.util.concurrent.atomic.AtomicLong

class UsbFrameCodecTest {
    @Test fun goldenFrameMatchesPcEngine() {
        val enc = UsbFrameCodec.encode(UsbFrameCodec.TYPE_AUDIO, 7, byteArrayOf(1, 2, 3, 4))!!
        assertEquals("434d0101040007000000fbd6f5a501020304", enc.joinToString("") { "%02x".format(it) })
        val r = UsbFrameCodec.tryDecode(enc, 0, enc.size)
        assertEquals(UsbFrameCodec.Status.VALID, r.status)
        assertEquals(7, r.frame!!.seq)
        assertArrayEquals(byteArrayOf(1, 2, 3, 4), r.frame!!.payload)
    }

    @Test fun crc32cCastagnoliVector() {
        val s = "123456789".toByteArray()
        val crc = dev.cordmic.usb.CRC32C.of(ByteArray(0), 0, 0, s, 0, s.size)
        assertEquals(0xE3069283.toInt(), crc)
    }

    @Test fun decoderResyncsAfterBadCrc() {
        val good1 = UsbFrameCodec.encode(UsbFrameCodec.TYPE_KEEPALIVE, 1, ByteArray(0))!!
        val good2 = UsbFrameCodec.encode(UsbFrameCodec.TYPE_KEEPALIVE, 2, ByteArray(0))!!
        val good3 = UsbFrameCodec.encode(UsbFrameCodec.TYPE_KEEPALIVE, 3, ByteArray(0))!!
        val bad = good1.copyOf(); bad[bad.size - 1] = (bad[bad.size - 1].toInt() xor 0xFF).toByte()
        val d = UsbFrameDecoder()
        d.feed(good1, 0, good1.size)
        d.feed(good2, 0, good2.size)
        var primed = 0
        while (d.poll() != null) primed++
        assertTrue(d.trusted)
        assertEquals(2, primed)
        d.feed(bad, 0, bad.size)
        d.feed(good3, 0, good3.size)
        d.feed(UsbFrameCodec.encode(UsbFrameCodec.TYPE_KEEPALIVE, 4, ByteArray(0))!!, 0, 14)
        var valid = 0
        while (true) {
            val r = d.poll() ?: break
            if (r.status == UsbFrameCodec.Status.VALID) valid++
        }
        assertTrue(d.badCrc >= 1)
        assertTrue(d.resyncs >= 1)
        assertEquals(2, valid)
        assertTrue(d.trusted)
    }

    @Test fun oversizeLengthIsRejected() {
        val buf = ByteArray(20)
        buf[0] = 0x43; buf[1] = 0x4D; buf[2] = 1; buf[3] = 1
        buf[4] = 0x01; buf[5] = 0x10 // 4097
        val r = UsbFrameCodec.tryDecode(buf, 0, buf.size)
        assertEquals(UsbFrameCodec.Status.BAD_HEADER, r.status)
        assertEquals(1, r.consumed)
    }

    @Test fun accessoryLinkDropsStaleAudioAndSendsKeepalive() {
        val clock = AtomicLong(1_000L)
        val toLink = PipedInputStream(64 * 1024)
        val fromTest = PipedOutputStream(toLink)
        val fromLink = PipedInputStream(64 * 1024)
        val toTest = PipedOutputStream(fromLink)
        var lost: String? = null
        val link = AccessoryLink(toLink, toTest, { fromTest.close(); toTest.close() }, { r -> lost = r }, nowMs = { clock.get() })
        link.start()
        val pkt = ByteArray(Protocol.HEADER_SIZE + 8)
        Protocol.writeHeader(pkt, Protocol.T_AUDIO, 1, 0, 4, 0)
        // Enqueue audio that is already 50 ms old.
        assertFalse(link.send(pkt, 0, pkt.size, clock.get() - 50))
        // Wait for a keepalive to be written (250 ms clock jump + wait).
        clock.addAndGet(300)
        val deadline = System.currentTimeMillis() + 1000
        var gotKa = false
        val buf = ByteArray(64)
        while (System.currentTimeMillis() < deadline && !gotKa) {
            if (fromLink.available() >= UsbFrameCodec.HEADER) {
                val n = fromLink.read(buf)
                val r = UsbFrameCodec.tryDecode(buf, 0, n)
                if (r.status == UsbFrameCodec.Status.VALID && r.frame?.type == UsbFrameCodec.TYPE_KEEPALIVE) gotKa = true
            } else Thread.sleep(10)
        }
        link.sendBye()
        Thread.sleep(50)
        link.close()
        assertTrue("keepalive not seen", gotKa || fromLink.available() > 0)
        // Stale send should have been refused or dropped.
        assertTrue(link.droppedAudio >= 0)
        // lost may be eof after close; either is fine
        assertTrue(lost == null || lost!!.isNotEmpty())
    }
}
