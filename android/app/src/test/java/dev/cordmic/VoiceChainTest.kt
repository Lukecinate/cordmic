package dev.cordmic

import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.round
import kotlin.math.sin
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** Gain, limiter and the noise-suppressor delay. Mirrors the C# VoiceChain tests. */
class VoiceChainTest {
    private fun tone(n: Int, freq: Double, amp: Double) = ShortArray(n) { i ->
        round(sin(2 * PI * freq * i / 48000.0) * amp).toInt().coerceIn(-32767, 32767).toShort()
    }

    private fun peak(s: ShortArray, off: Int, n: Int): Int {
        var p = 0
        for (i in off until minOf(off + n, s.size)) p = maxOf(p, abs(s[i].toInt()))
        return p
    }

    @Test fun noiseSuppressionCostsOneBlock() {
        val chain = VoiceChain(48000)
        chain.noiseCancellation = true
        chain.agc = false
        chain.highPass = false
        chain.gainDb = 0
        assertEquals(256, chain.delayFrames)
        assertTrue(chain.noiseLatencyMs in 5.3..5.4)
        val input = ShortArray(256 * 3)
        input[40] = 16000
        chain.process(input, input.size)
        assertEquals(0, peak(input, 0, 256))
        assertTrue(peak(input, 256, 256) > 8000)
    }

    @Test fun defaultIsLouderAndLimited() {
        val amp = 32768.0 * Math.pow(10.0, -40.0 / 20.0)
        val input = tone(24000, 1000.0, amp)
        val inPeak = peak(input, 0, input.size)
        VoiceChain(48000).process(input, input.size)
        val outPeak = peak(input, 12000, 6000)
        assertTrue("out $outPeak in $inPeak", outPeak > inPeak * 8)
        assertTrue(outPeak < 32000)
    }

    @Test fun limiterHoldsAHotSignal() {
        val chain = VoiceChain(48000)
        chain.noiseCancellation = false
        chain.agc = false
        chain.highPass = false
        chain.gainDb = 24
        val s = tone(2400, 1000.0, 20000.0)
        chain.process(s, s.size)
        val p = peak(s, 0, s.size)
        assertTrue(p in 20000..31000)
    }
}