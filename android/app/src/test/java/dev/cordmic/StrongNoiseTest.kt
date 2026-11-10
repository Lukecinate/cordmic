package dev.cordmic

import java.util.Random
import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.cos
import kotlin.math.log10
import kotlin.math.max
import kotlin.math.pow
import kotlin.math.roundToInt
import kotlin.math.sin
import kotlin.math.sqrt
import org.junit.Assert.assertTrue
import org.junit.Test

/** 0.5.7 Strong noise reduction: no musical noise, whispers and fricatives kept. Mirrors the C# StrongNoiseTests. */
class StrongNoiseTest {
    private data class Stats(val hopJitterDb: Double, val jumpRate: Double, val harmonicDb: Double, val meanDb: Double)

    private fun chain(mode: NoiseMode) = VoiceChain(48000).also {
        it.mode = mode; it.agc = false; it.voiceClarity = false; it.gainDb = 0; it.expander = NoiseMode.OFF
    }

    private fun run(mode: NoiseMode, noiseDb: Double, speechAmp: Double, seed: Long): Stats {
        val rng = Random(seed)
        val a = 32768 * 10.0.pow(noiseDb / 20) * sqrt(3.0)
        val c = chain(mode)
        val buf = ShortArray(128)
        val g = DoubleArray(129)
        val hist = ArrayList<DoubleArray>()
        for (h in 0 until 375 * 5) {
            for (i in 0 until 128) {
                val t = h * 128 + i
                var v = (rng.nextDouble() * 2 - 1) * a
                val env = if (t < 48000) 0.0 else 0.5 - 0.5 * cos(2 * PI * 3.0 * t / 48000)
                if (speechAmp > 0) for (m in 1..20) v += env * 32768 * speechAmp / m * sin(2 * PI * 200 * m * t / 48000)
                buf[i] = v.coerceIn(-32767.0, 32767.0).toInt().toShort()
            }
            c.process(buf, 128)
            if (h > 375 * 2) { c.copyBinGains(g); hist.add(DoubleArray(129) { 20 * log10(max(g[it], 1e-6)) }) }
        }
        var mean = 0.0; var nb = 0; var jumps = 0L; var pairs = 0L; var dsq = 0.0
        for (k in 5 until 120) {
            mean += hist.sumOf { it[k] } / hist.size; nb++
            for (i in 1 until hist.size) {
                val d = hist[i][k] - hist[i - 1][k]
                pairs++; dsq += d * d; if (abs(d) > 8) jumps++
            }
        }
        var harm = 0.0; var nh = 0
        if (speechAmp > 0) for (m in 1..10) {
            val k = (200.0 * m / 187.5).roundToInt(); if (k < 5) continue
            harm += hist.sumOf { it[k] } / hist.size; nh++
        }
        return Stats(sqrt(dsq / pairs), jumps.toDouble() / pairs, if (nh > 0) harm / nh else Double.NaN, mean / nb)
    }

    @Test fun strongGainDoesNotFlutterOnSpeech() {
        for ((noise, amp) in listOf(-50.0 to 0.02, -45.0 to 0.05)) {
            val s = run(NoiseMode.STRONG, noise, amp, 1)
            assertTrue("hop jitter ${s.hopJitterDb}", s.hopJitterDb < 1.5)
            assertTrue("jumps ${s.jumpRate}", s.jumpRate < 0.005)
            assertTrue("harmonics ${s.harmonicDb}", s.harmonicDb > -8.0)
        }
    }

    @Test fun strongLeavesALittleNaturalBackground() {
        val s = run(NoiseMode.STRONG, -50.0, 0.0, 3)
        assertTrue("mean gain ${s.meanDb}", s.meanDb in -16.5..-12.0)
    }

    private fun rms(s: ShortArray, start: Int, len: Int): Double {
        var sum = 0.0
        for (i in start until start + len) sum += s[i].toDouble() * s[i]
        return sqrt(sum / len)
    }

    private fun burst(highPass: Boolean, burstDbfs: Double): Pair<Double, Double> {
        val rng = Random(9)
        val n = 96000; val b0 = 48000; val b1 = 48000 + 7200
        val hiss = 32768 * 10.0.pow(-55.0 / 20) * sqrt(3.0)
        val b = DoubleArray(n)
        var p1 = 0.0; var p2 = 0.0; var sum = 0.0
        for (i in b0 until b1) {
            val w = rng.nextDouble() * 2 - 1
            val v = if (highPass) w - 2 * p1 + p2 else w
            p2 = p1; p1 = w; b[i] = v; sum += v * v
        }
        val scale = 32768 * 10.0.pow(burstDbfs / 20) / sqrt(sum / (b1 - b0))
        val x = ShortArray(n); val dry = ShortArray(n)
        for (i in 0 until n) {
            val h = (rng.nextDouble() * 2 - 1) * hiss
            x[i] = (h + b[i] * scale).coerceIn(-32767.0, 32767.0).toInt().toShort()
            dry[i] = (b[i] * scale).coerceIn(-32767.0, 32767.0).toInt().toShort()
        }
        chain(NoiseMode.STRONG).process(x, n)
        val d = VoiceChain.NOISE_DELAY_FRAMES; val skip = 960
        val outDb = 20 * log10(rms(x, b0 + d + skip, b1 - b0 - skip) / rms(dry, b0 + skip, b1 - b0 - skip))
        val after = 20 * log10(max(rms(x, b1 + 24000, 12000), 1e-3) / 32768)
        return outDb to after
    }

    @Test fun fricativeAndWhisperAreKept() {
        for ((hp, level) in listOf(true to -38.0, false to -40.0)) {
            val (db, after) = burst(hp, level)
            assertTrue("burst ${if (hp) "fricative" else "whisper"} $db dB", db > -3.0)
            assertTrue("hiss after burst $after dBFS", after < -66.0)
        }
    }
}
