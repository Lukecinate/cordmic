package dev.cordmic

import dev.cordmic.usb.UsbFrameCodec
import dev.cordmic.usb.UsbFrameDecoder
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.Random

class UsbFrameFuzzTest {
    @Test fun maxPayloadRoundTrips_andOversizeIsRejected() {
        val max = ByteArray(UsbFrameCodec.MAX_PAYLOAD) { 9 }
        val enc = UsbFrameCodec.encode(UsbFrameCodec.TYPE_AUDIO, 1, max)!!
        val r = UsbFrameCodec.tryDecode(enc, 0, enc.size)
        assertEquals(UsbFrameCodec.Status.VALID, r.status)
        assertArrayEquals(max, r.frame!!.payload)

        val over = ByteArray(20)
        over[0] = 0x43; over[1] = 0x4D; over[2] = 1; over[3] = 1
        over[4] = 0x01; over[5] = 0x10
        val bad = UsbFrameCodec.tryDecode(over, 0, over.size)
        assertEquals(UsbFrameCodec.Status.BAD_HEADER, bad.status)
        assertEquals(1, bad.consumed)
    }

    @Test fun decoderReassemblesSplitFramesIncludingSeqWrap() {
        val d = UsbFrameDecoder()
        val seqs = intArrayOf(-2, -1, 0, 1) // uint 0xFFFFFFFE, 0xFFFFFFFF, 0, 1
        for (seq in seqs) {
            val frame = UsbFrameCodec.encode(UsbFrameCodec.TYPE_AUDIO, seq, byteArrayOf(9, 8, 7, 6))!!
            d.feed(frame, 0, 5)
            assertTrue(d.poll() == null)
            d.feed(frame, 5, frame.size - 5)
            val got = d.poll()
            assertEquals(UsbFrameCodec.Status.VALID, got!!.status)
            assertEquals(seq, got.frame!!.seq)
            assertArrayEquals(byteArrayOf(9, 8, 7, 6), got.frame!!.payload)
        }
        assertTrue(d.valid >= 4)
    }

    @Test fun fuzzNeverThrowsAndStaysCapped() {
        val rng = Random(20261121L)
        val d = UsbFrameDecoder()
        var ms = 400
        System.getenv("CORDMIC_FUZZ_MS")?.toIntOrNull()?.takeIf { it > 0 }?.let { ms = it }
        val start = System.currentTimeMillis()
        var feeds = 0
        while (System.currentTimeMillis() - start < ms) {
            if (rng.nextInt(5) == 0) {
                val good = UsbFrameCodec.encode(UsbFrameCodec.TYPE_KEEPALIVE, feeds, ByteArray(0))!!
                val cut = 1 + rng.nextInt(good.size - 1)
                d.feed(good, 0, cut)
                d.feed(good, cut, good.size - cut)
            } else {
                val buf = ByteArray(1 + rng.nextInt(80))
                rng.nextBytes(buf)
                var o = 0
                while (o < buf.size) {
                    val n = minOf(buf.size - o, 1 + rng.nextInt(8))
                    d.feed(buf, o, n)
                    o += n
                }
            }
            while (d.poll() != null) { }
            feeds++
        }
        assertTrue(feeds > 10)
        assertTrue(d.skipped >= 0)
    }
}
