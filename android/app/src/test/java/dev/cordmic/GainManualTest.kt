package dev.cordmic

import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.log10
import kotlin.math.round
import kotlin.math.sin
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** 1.1.1 manual gain: default 0 dB, signed range, no auto-boost, meter stays below yellow. */
class GainManualTest {
    private val sr = 48000

    private fun tone(n: Int, hz: Double, amp: Double) = ShortArray(n) { i ->
        round(sin(2 * PI * hz * i / sr) * amp).toInt().coerceIn(-32767, 32767).toShort()
    }

    private fun peak(s: ShortArray, off: Int, n: Int): Int {
        var p = 0
        for (i in off until minOf(off + n, s.size)) p = maxOf(p, abs(s[i].toInt()))
        return p
    }

    private fun peakDb(s: ShortArray, off: Int, n: Int) =
        20 * log10(maxOf(peak(s, off, n), 1) / 32768.0)

    private fun live(gain: Double = 0.0, podcast: Boolean = true, agc: Boolean = true) =
        VoiceChain(sr).also {
            it.gainDb = gain
            it.podcast = podcast
            it.agc = agc
            it.noiseCancellation = false
            it.voiceClarity = false
            it.highPass = false
            it.expander = NoiseMode.OFF
        }

    @Test fun defaultsAndRange() {
        assertEquals(0.0, VoiceChain.DEFAULT_GAIN_DB, 0.0)
        assertEquals(-24, VoiceChain.MIN_GAIN_DB)
        assertEquals(24, VoiceChain.MAX_GAIN_DB)
        assertEquals(-9.0, VoiceChain.METER_YELLOW_DBFS, 0.0)
        assertEquals(-1.0, VoiceChain.METER_RED_DBFS, 0.0)
        assertEquals(-12.0, VoiceChain.METER_PEAK_CEILING_DBFS, 0.0)
        assertEquals(-10.0, VoiceChain.CEILING_DBFS, 0.0)
        assertEquals(-9f, Level.YELLOW_DB)
        assertEquals(-1f, Level.RED_DB)
        assertEquals(0.0, VoiceChain(sr).gainDb, 0.0)
        assertEquals(-24.0, VoiceChain.clampGain(-99.0), 0.0)
        assertEquals(24.0, VoiceChain.clampGain(99.0), 0.0)
    }

    @Test fun nudgeSnapAndFormatAreTheSharedKnobSliderModel() {
        assertEquals(0.5, VoiceChain.nudgeGain(0.0, 1, fine = false), 1e-9)
        assertEquals(-0.5, VoiceChain.nudgeGain(0.0, -1, fine = false), 1e-9)
        assertEquals(0.1, VoiceChain.nudgeGain(0.0, 1, fine = true), 1e-9)
        assertEquals(-0.1, VoiceChain.nudgeGain(0.0, -1, fine = true), 1e-9)
        assertEquals(-24.0, VoiceChain.nudgeGain(-24.0, -1, fine = false), 1e-9)
        assertEquals(24.0, VoiceChain.nudgeGain(24.0, 1, fine = false), 1e-9)
        assertEquals("0.0 dB", VoiceChain.formatGain(0.0))
        assertEquals("+3.5 dB", VoiceChain.formatGain(3.5))
        assertEquals("−12.0 dB", VoiceChain.formatGain(-12.0))
        assertEquals("−0.5 dB", VoiceChain.formatGain(-0.5))
        assertEquals(0.0, VoiceChain.snapGain(VoiceChain.DEFAULT_GAIN_DB), 0.0)
    }

    @Test fun negativeGainAttenuatesAndLegacyByteNeverWraps() {
        assertEquals(0, VoiceChain.legacyGainByte(-6.0))
        assertEquals(0, VoiceChain.legacyGainByte(-0.1))
        assertEquals(0, VoiceChain.legacyGainByte(0.0))
        assertEquals(6, VoiceChain.legacyGainByte(6.2))
        val amp = 32768.0 * Math.pow(10.0, -18.0 / 20.0)
        val a = tone(sr / 2, 1000.0, amp)
        val b = a.copyOf()
        live(0.0, podcast = false, agc = false).process(a, a.size)
        live(-12.0, podcast = false, agc = false).process(b, b.size)
        val z = peakDb(a, 2000, 8000)
        val c = peakDb(b, 2000, 8000)
        assertTrue("delta ${z - c}", z - c in 10.0..14.0)
    }

    @Test fun agcNeverAutoRaisesOnQuietOrTypicalSpeech() {
        for (podcast in arrayOf(true, false)) {
            val amp = 32768.0 * Math.pow(10.0, -30.0 / 20.0)
            val s = tone(sr * 3, 180.0, amp)
            val chain = live(0.0, podcast, agc = true)
            chain.process(s, s.size)
            val outDb = peakDb(s, sr * 2, sr / 2)
            assertTrue("boosted to $outDb podcast=$podcast", outDb < -26)
            assertTrue(outDb < VoiceChain.METER_YELLOW_DBFS)
            if (podcast) assertTrue(chain.podcastAgcGainDb in -0.3..0.05)
        }
    }

    @Test fun loudAndFullScaleStayBelowYellow() {
        fun check(src: ShortArray, gain: Double, podcast: Boolean, label: String) {
            live(gain, podcast, agc = true).process(src, src.size)
            val skip = (sr * 0.08).toInt()
            val pk = peakDb(src, skip, src.size - skip)
            assertTrue("$label peak $pk dBFS reached yellow", pk < VoiceChain.METER_YELLOW_DBFS)
        }
        val shoutAmp = 32768.0 * Math.pow(10.0, -3.0 / 20.0)
        val shout = tone(sr, 1000.0, shoutAmp)
        val square = ShortArray(sr) { i -> if ((i / 80) % 2 == 0) 32767 else -32767 }
        for (podcast in arrayOf(true, false)) {
            check(shout.copyOf(), 0.0, podcast, "shout gain0 podcast=$podcast")
            check(shout.copyOf(), 12.0, podcast, "shout gain+12 podcast=$podcast")
            check(square.copyOf(), 0.0, podcast, "full-scale gain0 podcast=$podcast")
            check(square.copyOf(), 24.0, podcast, "full-scale gain+24 podcast=$podcast")
        }
    }

    @Test fun typicalSpeechStaysInHealthyGreenAtDefaultGain() {
        val amp = 32768.0 * Math.pow(10.0, -18.0 / 20.0)
        val s = tone(sr * 2, 180.0, amp)
        live(0.0, podcast = true, agc = true).process(s, s.size)
        val pk = peakDb(s, sr, sr / 2)
        assertTrue(pk < VoiceChain.METER_YELLOW_DBFS)
        assertTrue("typical $pk", pk in -24.0..-14.0)
    }

    @Test fun gainExtRoundTripsSignedTenths() {
        val buf = ByteArray(GainExt.SIZE)
        for (db in doubleArrayOf(0.0, 0.1, -0.5, -24.0, 24.0, 3.5, -12.3)) {
            assertEquals(4, GainExt.write(buf, 0, db))
            assertEquals(VoiceChain.snapGain(db), GainExt.tryRead(buf, 0)!!, 1e-3)
        }
    }

    @Test fun deviceTuneNeverSuggestsABoost() {
        assertEquals(0, DeviceTune.suggestedGain(-36.0, -60.0))
        assertEquals(0, DeviceTune.suggestedGain(-12.0, -40.0))
        assertEquals(0, DeviceTune.suggestedGain(null, null))
    }

    @Test fun yellowThresholdIsShared() {
        assertEquals(VoiceChain.METER_YELLOW_DBFS.toFloat(), Level.YELLOW_DB)
        assertEquals(VoiceChain.METER_RED_DBFS.toFloat(), Level.RED_DB)
    }
}
