package dev.cordmic

import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.log10
import kotlin.math.max
import kotlin.math.sin
import kotlin.math.sqrt
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 1.1.0 podcast chain: every stage under a chaotic environment (silence, full-scale square, DC, NaN/Inf, denormals,
 * random bursts and dropouts), at 8 / 16 / 44.1 / 48 kHz, mono and stereo, with parameters changed mid-stream.
 * Mirrors windows-app/tests/CordMic.Tests/PodcastChainTests.cs.
 */
class PodcastChainTest {
    private val rates = intArrayOf(8000, 16000, 44100, 48000)
    private val ceil = PodcastChain.dbToLin(VoiceChain.CEILING_DBFS)

    /** Deterministic xorshift so failures reproduce. */
    private class Rng(var s: Long) {
        fun next(): Double { s = s xor (s shl 13); s = s xor (s ushr 7); s = s xor (s shl 17); return ((s ushr 11).toDouble() / (1L shl 53)) * 2 - 1 }
    }

    /** A chaotic block sequence: speech-like tones, square at full scale, DC, NaN/Inf, denormals, noise, dropouts. */
    private fun chaos(sr: Int, ch: Int, seconds: Double, seed: Long): DoubleArray {
        val n = (sr * seconds).toInt()
        val x = DoubleArray(n * ch)
        val r = Rng(seed)
        var i = 0
        var seg = 0
        while (i < n) {
            val len = minOf(n - i, (sr * (0.02 + 0.2 * abs(r.next()))).toInt().coerceAtLeast(1))
            val kind = (seg++ * 7 + (abs(r.next()) * 100).toInt()) % 9
            val amp = Math.pow(10.0, (-60 + 66 * abs(r.next())) / 20)
            val f0 = 90 + 250 * abs(r.next())
            for (j in 0 until len) {
                val t = (i + j).toDouble() / sr
                val v = when (kind) {
                    0 -> 0.0
                    1 -> if (sin(2 * PI * 220 * t) >= 0) 1.0 else -1.0
                    2 -> 1.0
                    3 -> if (j % 17 == 0) Double.NaN else if (j % 23 == 0) Double.POSITIVE_INFINITY else if (j % 29 == 0) Double.NEGATIVE_INFINITY else 0.3 * r.next()
                    4 -> 1e-310 * (if (j % 2 == 0) 1 else -1)
                    5 -> amp * r.next()
                    6 -> { var s = 0.0; for (h in 1..8) s += sin(2 * PI * f0 * h * t) / h; amp * s * 0.5 }
                    7 -> amp * sin(2 * PI * 6500.0.coerceAtMost(sr * 0.45) * t) + amp * 0.3 * r.next()
                    else -> 4.0 * r.next() // over full scale
                }
                for (c in 0 until ch) x[(i + j) * ch + c] = if (c == 1) v * 0.7 else v
            }
            i += len
        }
        return x
    }

    private fun configure(pc: PodcastChain, stage: Int) {
        pc.agcEnabled = stage == 0 || stage == 4
        pc.dynamicEqEnabled = stage == 1 || stage == 4
        pc.compressorEnabled = stage == 2 || stage == 4
        pc.limiterEnabled = stage == 3 || stage == 4
    }

    @Test fun everyStageStaysFiniteAndBoundedInChaos() {
        for (sr in rates) for (ch in 1..2) for (stage in 0..4) {
            val pc = PodcastChain(sr, ch, PodcastPresets.STRONG)
            configure(pc, stage)
            val x = chaos(sr, ch, 2.0, 1234L + sr + ch * 7 + stage)
            var off = 0
            val block = 240 * ch
            var b = 0
            while (off < x.size) {
                val frames = minOf(block, x.size - off) / ch
                // parameter change mid-stream every few blocks
                if (b++ % 5 == 0) pc.params = listOf(PodcastPresets.OFF, PodcastPresets.LIGHT, PodcastPresets.STRONG)[b % 3]
                pc.process(x, frames, off)
                off += frames * ch
            }
            val lim = if (pc.limiterEnabled) ceil + 1e-12 else 4.0 * 16 + 1 // pre-limiter stages may exceed 0 dBFS
            for (v in x) {
                assertTrue("finite sr=$sr ch=$ch stage=$stage", v.isFinite())
                assertTrue("bounded sr=$sr ch=$ch stage=$stage v=$v", abs(v) <= lim)
            }
        }
    }

    @Test fun silenceStaysSilentAndDenormalsFlush() {
        for (sr in rates) for (ch in 1..2) {
            val pc = PodcastChain(sr, ch, PodcastPresets.LIGHT)
            val x = DoubleArray(sr * ch) { if (it % 2 == 0) 1e-310 else 0.0 }
            pc.process(x, sr)
            assertTrue(x.all { abs(it) < 1e-12 })
            assertEquals(0.0, pc.agcGainDb, 0.01) // silence never raises the AGC
        }
    }

    @Test fun dcIsRemovedByHighPass() {
        for (sr in rates) {
            val pc = PodcastChain(sr, 1, PodcastPresets.LIGHT); pc.agcEnabled = false; pc.compressorEnabled = false
            val x = DoubleArray(sr) { 0.5 }
            pc.process(x, sr)
            assertTrue("sr=$sr", abs(x[sr - 1]) < 1e-3)
        }
    }

    @Test fun fullScaleSquareIsLimitedAtCeiling() {
        for (sr in rates) for (ch in 1..2) {
            val pc = PodcastChain(sr, ch, PodcastPresets.STRONG)
            val n = sr
            val x = DoubleArray(n * ch) { val i = it / ch; if ((i / (sr / 200)) % 2 == 0) 1.0 else -1.0 }
            pc.process(x, n)
            var pk = 0.0; for (v in x) pk = max(pk, abs(v))
            assertTrue("sr=$sr peak=$pk", pk <= ceil + 1e-12)
            assertTrue(pk > ceil * 0.5)
        }
    }

    @Test fun agcDoesNotBoostQuietSpeechAndDoesNotLiftNoise() {
        val sr = 48000
        val pc = PodcastChain(sr, 1, PodcastPresets.LIGHT)
        pc.dynamicEqEnabled = false; pc.compressorEnabled = false
        val r = Rng(9)
        val noiseAmp = PodcastChain.dbToLin(-72.0) * sqrt(3.0)
        val n = (sr * 5.5).toInt()
        val x = DoubleArray(n) { i ->
            val t = i.toDouble() / sr
            val nz = noiseAmp * r.next()
            if (t < 1.5) nz else nz + PodcastChain.dbToLin(-35.0) * sqrt(2.0) * sin(2 * PI * 180 * t) * (0.8 + 0.2 * sin(2 * PI * 3 * t))
        }
        pc.process(x, n)
        val outNoise = rmsDb(x, sr / 2, (1.5 * sr).toInt())
        assertTrue("noise not boosted: $outNoise", outNoise < -52.0 + 0.5)
        val outSpeech = rmsDb(x, (4.5 * sr).toInt(), n)
        assertTrue("speech stays near -35: $outSpeech", outSpeech in -38.0..-32.0)
        assertTrue("agc may only reduce: ${pc.agcGainDb}", pc.agcGainDb in -0.3..0.05)
    }

    @Test fun agcNeverLiftsRoomFloorAboveNoiseCeiling() {
        val sr = 48000
        val pc = PodcastChain(sr, 1, PodcastPresets.STRONG)
        val r = Rng(31)
        val x = DoubleArray(sr * 6) { i ->
            val t = i.toDouble() / sr
            val nz = PodcastChain.dbToLin(-58.0) * sqrt(3.0) * r.next()
            if ((t.toInt() % 2) == 1) nz + 0.02 * sin(2 * PI * 150 * t) else nz
        }
        pc.process(x, x.size)
        assertTrue("pause ${rmsDb(x, 4 * sr + sr / 4, 5 * sr)}", rmsDb(x, 4 * sr + sr / 4, 5 * sr) < -52.0 + 0.5)
    }

    @Test fun agcHoldsGainThroughPausesNoPumping() {
        val sr = 48000
        val pc = PodcastChain(sr, 1, PodcastPresets.LIGHT)
        val tone = DoubleArray(sr * 3) { PodcastChain.dbToLin(-30.0) * sqrt(2.0) * sin(2 * PI * 200.0 * it / sr) }
        pc.process(tone, tone.size)
        val before = pc.agcGainDb
        val pause = DoubleArray(sr / 2)
        pc.process(pause, pause.size)
        assertTrue(pc.gated)
        assertEquals(before, pc.agcGainDb, 1.0)
    }

    @Test fun dynamicEqCutsMudAndSibilanceButLeavesMidrange() {
        val sr = 48000
        fun outDb(f: Double, preset: PodcastParams): Double {
            val pc = PodcastChain(sr, 1, preset); pc.agcEnabled = false; pc.compressorEnabled = false; pc.limiterEnabled = false
            val x = DoubleArray(sr) { 0.1 * sin(2 * PI * f * it / sr) }
            pc.process(x, sr)
            return rmsDb(x, sr / 2, sr) - 20 * log10(0.1 / sqrt(2.0))
        }
        assertEquals(0.0, outDb(1000.0, PodcastPresets.LIGHT), 0.5)
        assertTrue(outDb(30.0, PodcastPresets.LIGHT) < -10)
        val mud = outDb(250.0, PodcastPresets.LIGHT)
        assertTrue("mud $mud", mud < -1.0 && mud >= -PodcastPresets.LIGHT.mudMaxCutDb - 0.5)
        val ess = outDb(6500.0, PodcastPresets.LIGHT)
        assertTrue("ess $ess", ess < -2.0 && ess >= -PodcastPresets.LIGHT.essMaxCutDb - 0.5)
    }

    @Test fun gainComputerIsSoftKneeAndContinuous() {
        val t = -22.0; val r = 3.0; val w = 10.0
        assertEquals(0.0, PodcastChain.gainComputer(-40.0, t, r, w), 1e-12)
        assertEquals((1 / r - 1) * 20, PodcastChain.gainComputer(t + 20, t, r, w), 1e-12)
        for (edge in doubleArrayOf(t - w / 2, t + w / 2)) {
            assertEquals(PodcastChain.gainComputer(edge - 1e-9, t, r, w), PodcastChain.gainComputer(edge + 1e-9, t, r, w), 1e-6)
        }
        var prev = 0.0
        var l = -40.0
        while (l < 0) { val g = PodcastChain.gainComputer(l, t, r, w); assertTrue(g <= prev + 1e-12); prev = g; l += 0.25 }
    }

    @Test fun compressorReleaseIsProgramDependent() {
        val sr = 48000
        fun recoveryMs(burstSec: Double): Double {
            val pc = PodcastChain(sr, 1, PodcastPresets.LIGHT); pc.agcEnabled = false; pc.dynamicEqEnabled = false; pc.limiterEnabled = false
            val loud = DoubleArray((burstSec * sr).toInt()) { 0.5 * sin(2 * PI * 300.0 * it / sr) }
            pc.process(loud, loud.size)
            val q = DoubleArray(PodcastChain.CTRL)
            var t = 0
            while (pc.compGainReductionDb > 1.0 && t < sr * 3) { pc.process(q, q.size); t += q.size }
            return t * 1000.0 / sr
        }
        val short = recoveryMs(0.03)
        val long = recoveryMs(2.0)
        assertTrue("short=$short long=$long", long > short * 2)
    }

    @Test fun limiterLatencyMatchesLookaheadAndImpulseAligns() {
        for (sr in rates) for (p in listOf(PodcastPresets.OFF, PodcastPresets.LIGHT, PodcastPresets.STRONG)) {
            val pc = PodcastChain(sr, 1, p); pc.agcEnabled = false; pc.dynamicEqEnabled = false; pc.compressorEnabled = false
            val expect = Math.round(p.lookaheadMs * sr / 1000.0).toInt()
            assertEquals(expect, pc.latencyFrames)
            assertTrue(pc.latencyMs <= 3.0)
            val x = DoubleArray(sr / 10); x[10] = 0.5
            pc.process(x, x.size)
            var at = -1; for (i in x.indices) if (abs(x[i]) > 0.1) { at = i; break }
            assertEquals(10 + expect, at)
        }
    }

    @Test fun truePeakGuardHoldsCeilingWithTwoSampleDelay() {
        for (sr in rates) for (ch in 1..2) {
            val g = TruePeakGuard(sr, ch, VoiceChain.CEILING_DBFS)
            val r = Rng(77L + sr)
            val n = sr / 2
            val x = DoubleArray(n * ch) { i -> if (i % 101 == 0) Double.NaN else 3 * r.next() }
            for (f in 0 until n) g.processFrame(x, f * ch)
            assertTrue(x.all { it.isFinite() && abs(it) <= ceil + 1e-12 })
        }
        val g = TruePeakGuard(48000)
        val outs = DoubleArray(6) { g.process(if (it == 1) 0.25 else 0.0) }
        assertEquals(0.25, outs[3], 1e-12)
    }

    @Test fun spectralMasksStayWithinFloorsAndHandleChaos() {
        for (sr in rates) for (ch in 1..2) {
            val sv = Array(ch) { SpectralVoice(sr, 256, 128) }
            val bins = 129
            val r = Rng(5L + sr)
            val psd = DoubleArray(bins); val mag = DoubleArray(bins); val noise = DoubleArray(bins) { 1e-6 }; val out = DoubleArray(bins)
            val minG = PodcastChain.dbToLin(-12.0) * PodcastChain.dbToLin(-10.0)
            for (frame in 0 until 400) for (c in 0 until ch) {
                val loud = (frame / 40) % 2 == 0
                for (k in 0 until bins) {
                    val p = if (frame % 97 == 0) 0.0 else (if (loud) 1e-2 else 1e-6) * (1 + abs(r.next()))
                    psd[k] = p; mag[k] = sqrt(p)
                }
                sv[c].compute(psd, mag, noise, 1.0, 1.0, out)
                for (k in 0 until bins) assertTrue("sr=$sr g=${out[k]}", out[k].isFinite() && out[k] >= minG - 1e-12 && out[k] <= 1.0 + 1e-12)
            }
        }
    }

    @Test fun dereverbAttenuatesTailAndDialogKeepsVoice() {
        val sv = SpectralVoice(48000, 256, 128)
        val bins = 129
        val psd = DoubleArray(bins); val mag = DoubleArray(bins); val noise = DoubleArray(bins) { 1e-9 }; val out = DoubleArray(bins)
        var tailGain = 1.0
        val perFrame = Math.pow(10.0, -60.0 / 10 / (0.5 * 48000 / 128)) // RT60 0.5 s in power
        var p = 1e-2
        for (frame in 0 until 120) {
            p = if (frame < 20) 1e-2 else p * perFrame
            for (k in 0 until bins) { psd[k] = p; mag[k] = sqrt(p) }
            sv.compute(psd, mag, noise, 0.5, 0.0, out)
            if (frame == 60) tailGain = out[20]
        }
        assertTrue("tail gain $tailGain", tailGain < 0.8)
        val sv2 = SpectralVoice(48000, 256, 128)
        for (k in 0 until bins) noise[k] = 1e-6
        for (frame in 0 until 50) {
            for (k in 0 until bins) { val v = if (k in 2..20 && k % 2 == 0) 1e-3 else 2e-6; psd[k] = v; mag[k] = sqrt(v) }
            sv2.compute(psd, mag, noise, 0.0, 0.45, out)
        }
        assertTrue("voice bin ${out[4]}", out[4] > 0.97)
        for (frame in 0 until 200) {
            for (k in 0 until bins) { psd[k] = 1e-6; mag[k] = 1e-3 }
            sv2.compute(psd, mag, noise, 0.0, 0.45, out)
        }
        assertTrue("noise bin ${out[40]}", out[40] < 0.8 && out[40] >= PodcastChain.dbToLin(-4.5) - 1e-9)
    }

    @Test fun voiceChainNeverExceedsCeilingInChaosAllPresets() {
        val limit = Math.round(32768 * ceil).toInt() + 1
        for (sr in rates) for (mode in NoiseMode.values()) for (agc in listOf(true, false)) {
            val vc = VoiceChain(sr)
            vc.podcast = true; vc.mode = mode; vc.expander = mode; vc.agc = agc; vc.gainDb = 24.0
            vc.podcastParams = PodcastPresets.forNoise(mode)
            val x = chaos(sr, 1, 1.5, 99L + sr + mode.code)
            val s = ShortArray(x.size) { i -> val v = x[i]; if (!v.isFinite()) 0 else (v * 32767).toInt().coerceIn(-32768, 32767).toShort() }
            var off = 0
            val block = ShortArray(sr / 100)
            while (off < s.size) {
                val n = minOf(block.size, s.size - off)
                s.copyInto(block, 0, off, off + n)
                vc.process(block, n)
                for (i in 0 until n) assertTrue("sr=$sr mode=$mode v=${block[i]}", abs(block[i].toInt()) <= limit)
                off += n
            }
        }
    }

    @Test fun voiceChainLatencyIsReportedPerPreset() {
        val vc = VoiceChain(48000); vc.podcast = true
        vc.mode = NoiseMode.OFF; vc.podcastParams = PodcastPresets.OFF
        assertEquals(48 + 2, vc.chainDelayFrames) // 1 ms look-ahead + true-peak guard; no STFT
        vc.mode = NoiseMode.OFF; vc.podcastParams = PodcastPresets.LIGHT // Light with the platform suppressor
        assertEquals(256 + 72 + 2, vc.chainDelayFrames)
        vc.mode = NoiseMode.STRONG; vc.podcastParams = PodcastPresets.STRONG
        assertEquals(256 + 96 + 2, vc.chainDelayFrames)
        vc.podcast = false
        assertEquals(256, vc.chainDelayFrames)
    }

    @Test fun cpuGuardDropsSpectralStagesWhenTooSlow() {
        val vc = VoiceChain(48000); vc.podcast = true; vc.mode = NoiseMode.OFF; vc.podcastParams = PodcastPresets.LIGHT
        var t = 0L
        vc.clock = { t += 4_000_000L; t } // each 5 ms block "takes" 4 ms: far over the 30 % budget
        assertTrue(vc.spectralActive)
        val b = ShortArray(240)
        repeat(1000) { vc.process(b, b.size) }
        assertTrue(vc.cpu.tripped)
        assertFalse(vc.spectralActive)
        assertEquals(72 + 2, vc.chainDelayFrames) // STFT gone; 1.5 ms look-ahead + guard remain
    }

    @Test fun longBlocksAreProcessedInChunks() {
        val vc = VoiceChain(48000); vc.podcast = true; vc.mode = NoiseMode.STRONG; vc.podcastParams = PodcastPresets.STRONG
        val s = ShortArray(10000) { if ((it / 100) % 2 == 0) 32767 else -32768 }
        vc.process(s, s.size)
        val limit = Math.round(32768 * ceil).toInt() + 1
        assertTrue(s.all { abs(it.toInt()) <= limit })
        assertTrue(s.copyOfRange(9000, 10000).any { abs(it.toInt()) > 1000 }) // the tail past 4096 was processed, not left raw
    }

    @Test fun planTurnsChainOffForStudioAndFlag() {
        assertTrue(voicePlan(NoiseMode.LIGHT, true, false, false).podcast)
        assertFalse(voicePlan(NoiseMode.LIGHT, true, true, false).podcast)
        assertFalse(voicePlan(NoiseMode.STRONG, true, false, false, chain = false).podcast)
        assertEquals(128, SettingsSync.FLAG_CHAIN_OFF)
        assertTrue(SyncPayload(SettingsSync.FLAG_CHAIN_OFF, emptyArray()).chainOff)
        assertFalse(SyncPayload(SettingsSync.FLAG_STUDIO, emptyArray()).chainOff)
    }

    @Test fun cpuBenchmarkPerStage() {
        val sr = 48000
        val n = sr * 5
        val x0 = chaos(sr, 1, 5.0, 4242L).map { if (it.isFinite()) it.coerceIn(-1.0, 1.0) else 0.0 }.toDoubleArray()
        fun bench(name: String, run: (DoubleArray) -> Unit) {
            run(x0.copyOf()) // warm-up
            val y = x0.copyOf()
            val t0 = System.nanoTime(); run(y); val dt = System.nanoTime() - t0
            println("BENCH kotlin $name: %.1f ns/sample, %.3f %% of real time".format(dt.toDouble() / n, dt / (n * 1e9 / sr) * 100))
        }
        val names = arrayOf("agc", "dynamic-eq", "compressor", "limiter", "podcast-all")
        for (stage in 0..4) {
            bench(names[stage]) { x -> val pc = PodcastChain(sr, 1, PodcastPresets.STRONG); configure(pc, stage); var o = 0; while (o < n) { val f = minOf(240, n - o); pc.process(x, f, o); o += f } }
        }
        bench("true-peak-guard") { x -> val g = TruePeakGuard(sr); for (i in 0 until n) x[i] = g.process(x[i]) }
        val s0 = ShortArray(n) { (x0[it] * 32767).toInt().toShort() }
        fun vcBench(name: String, setup: (VoiceChain) -> Unit): Double {
            fun run(): Long {
                val vc = VoiceChain(sr); setup(vc); val s = s0.copyOf(); val b = ShortArray(240); var o = 0; val t0 = System.nanoTime()
                while (o < n) { val f = minOf(240, n - o); s.copyInto(b, 0, o, o + f); vc.process(b, f); o += f }
                return System.nanoTime() - t0
            }
            run(); val dt = run()
            val rt = dt / (n * 1e9 / sr) * 100
            println("BENCH kotlin $name: %.1f ns/sample, %.3f %% of real time".format(dt.toDouble() / n, rt))
            return rt
        }
        vcBench("voicechain-legacy-strong") { it.mode = NoiseMode.STRONG; it.expander = NoiseMode.STRONG }
        vcBench("voicechain-podcast-strong-ns-only") { it.mode = NoiseMode.STRONG; it.podcast = true; it.podcastParams = PodcastPresets.STRONG.copy(dereverb = 0.0, dialog = 0.0) }
        vcBench("voicechain-podcast-off-preset") { it.mode = NoiseMode.OFF; it.podcast = true; it.podcastParams = PodcastPresets.OFF }
        vcBench("voicechain-podcast-light-spectral-only") { it.mode = NoiseMode.OFF; it.podcast = true; it.podcastParams = PodcastPresets.LIGHT }
        val full = vcBench("voicechain-podcast-strong-full") { it.mode = NoiseMode.STRONG; it.expander = NoiseMode.STRONG; it.podcast = true; it.podcastParams = PodcastPresets.STRONG }
        assertTrue("full chain $full % of real time", full < 25.0)
    }

    private fun rmsDb(x: DoubleArray, from: Int, to: Int): Double {
        var s = 0.0; for (i in from until to) s += x[i] * x[i]
        return 10 * log10(s / (to - from) + 1e-20)
    }
}
