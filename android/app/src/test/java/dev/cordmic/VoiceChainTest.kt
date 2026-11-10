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
        assertEquals(128, chain.delayFrames)
        assertTrue(chain.noiseLatencyMs in 2.6..2.75)
        val input = ShortArray(256 * 3)
        input[40] = 16000
        chain.process(input, input.size)
        assertEquals(0, peak(input, 0, 128))
        assertTrue(peak(input, 128, 256) > 8000)
    }

    @Test fun defaultIsLouderAndLimited() {
        val amp = 32768.0 * Math.pow(10.0, -40.0 / 20.0)
        val input = tone(24000, 1000.0, amp)
        val inPeak = peak(input, 0, input.size)
        VoiceChain(48000).process(input, input.size)
        val outPeak = peak(input, 12000, 6000)
        assertTrue("out $outPeak in $inPeak", outPeak > inPeak * 5)
        assertTrue(outPeak < 30000)
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

    private fun hiss(n: Int, dbfs: Double, seed: Long): ShortArray {
        val rng = java.util.Random(seed)
        val a = 32768.0 * Math.pow(10.0, dbfs / 20.0) * Math.sqrt(3.0)
        return ShortArray(n) { ((rng.nextDouble() * 2 - 1) * a).toInt().coerceIn(-32767, 32767).toShort() }
    }

    private fun rmsDb(s: ShortArray, off: Int, n: Int): Double {
        var sum = 0.0
        for (i in off until off + n) sum += s[i].toDouble() * s[i]
        return 20 * Math.log10(maxOf(Math.sqrt(sum / n), 1e-3) / 32768.0)
    }

    @Test fun raisedGainDoesNotOpenTheGateOnHiss() {
        val s = hiss(48000 * 4, -60.0, 3)
        val chain = VoiceChain(48000)
        chain.noiseCancellation = false
        chain.voiceClarity = false
        chain.gainDb = 12
        chain.process(s, s.size)
        val out = rmsDb(s, 48000 * 3, 48000)
        assertTrue("hiss $out", out < -50)
        assertTrue("floor ${chain.noiseFloorDbfs}", chain.noiseFloorDbfs in -64.0..-56.0)
    }

    @Test fun pauseExpanderDepthMatchesMode() {
        fun run(m: NoiseMode): Double {
            val s = hiss(48000 * 3, -58.0, 5)
            val chain = VoiceChain(48000)
            chain.noiseCancellation = false
            chain.voiceClarity = false
            chain.expander = m
            chain.gainDb = 6
            chain.process(s, s.size)
            return rmsDb(s, 48000 * 2, 48000)
        }
        val off = run(NoiseMode.OFF)
        assertTrue(off - run(NoiseMode.LIGHT) in 4.5..7.5)
        assertTrue(off - run(NoiseMode.STRONG) in 10.0..13.5)
    }

    @Test fun liveGainChangeIsRamped() {
        val chain = VoiceChain(48000)
        chain.noiseCancellation = false
        chain.voiceClarity = false
        chain.agc = false
        chain.gainDb = 0
        val amp = 32768.0 * Math.pow(10.0, -20.0 / 20.0)
        val a = tone(4800, 200.0, amp)
        val b = a.copyOf()
        chain.process(a, a.size)
        chain.gainDb = 18
        chain.process(b, b.size)
        var step = 0
        for (i in 1 until 480) step = maxOf(step, abs(b[i] - b[i - 1]))
        assertTrue("step $step", step < 400)
    }
}
