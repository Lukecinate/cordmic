package dev.cordmic

import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.cos
import kotlin.math.exp
import kotlin.math.ln
import kotlin.math.log10
import kotlin.math.max
import kotlin.math.min
import kotlin.math.round
import kotlin.math.sin
import kotlin.math.sqrt

/*
 * 1.1.0 podcast voice chain (original MIT code, written from the RBJ Audio EQ Cookbook formulas and textbook
 * one-pole / dB-domain gain-computer math; no third-party DSP code). Mirrors CordMic.Core.Dsp.PodcastChain (C#).
 *
 *   gated AGC -> dynamic EQ (HP 80 Hz, dynamic mud cut ~250 Hz, dynamic de-ess ~6.5 kHz)
 *   -> soft-knee 3:1 compressor with program-dependent release -> look-ahead brickwall limiter (-10 dBFS)
 *
 * The spectral dereverb and dialog-isolation masks ([SpectralVoice]) run inside the existing noise-suppression STFT,
 * so there is one mask and one set of smoothing, and a [TruePeakGuard] follows the noise stage.
 */

/** Per-preset tuning of the podcast chain and its spectral stages. Strengths are 0..1. */
data class PodcastParams(
    val agcTargetDbfs: Double = -20.0, // unused for boost in 1.1.1; kept so older tests and docs still parse
    val agcMaxDb: Double = 0.0,
    val agcMinDb: Double = -36.0,
    val agcRiseSec: Double = 0.6,
    val agcFallSec: Double = 0.18,
    val agcPeakDbfs: Double = VoiceChain.METER_PEAK_CEILING_DBFS,
    val agcPeakRecoverSec: Double = 2.0,
    val agcPeakHoldMs: Double = 150.0,
    val hpHz: Double = 80.0,
    val mudHz: Double = 250.0,
    val mudMaxCutDb: Double = 4.0,
    val mudThreshDb: Double = -6.0,
    val essHz: Double = 6500.0,
    val essMaxCutDb: Double = 6.0,
    val essThreshDb: Double = -12.0,
    val compThreshDbfs: Double = -22.0,
    val compRatio: Double = 3.0,
    val compKneeDb: Double = 10.0,
    val compMakeupDb: Double = 0.0,
    val limCeilingDbfs: Double = VoiceChain.CEILING_DBFS,
    val lookaheadMs: Double = 1.5,
    val limReleaseMs: Double = 60.0,
    val dereverb: Double = 0.0,
    val dialog: Double = 0.0,
)

/** Phone presets (noise mode). PC Studio presets live in the C# PodcastPresets and use the same numbers. */
object PodcastPresets {
    /** Off: lowest latency, no STFT (dereverb and dialog off), 1 ms look-ahead, lighter EQ. */
    val OFF = PodcastParams(mudMaxCutDb = 3.0, essMaxCutDb = 5.0, lookaheadMs = 1.0, limReleaseMs = 50.0)
    val LIGHT = PodcastParams(lookaheadMs = 1.5, dereverb = 0.35, dialog = 0.30)
    val STRONG = PodcastParams(lookaheadMs = 2.0, limReleaseMs = 80.0, dereverb = 0.50, dialog = 0.45)

    fun forNoise(mode: NoiseMode): PodcastParams = when (mode) {
        NoiseMode.OFF -> OFF
        NoiseMode.LIGHT -> LIGHT
        NoiseMode.STRONG -> STRONG
    }
}

/** RBJ cookbook biquad (direct form I) whose coefficients can change mid-stream without resetting state. */
class DspBiquad {
    private var b0 = 1.0; private var b1 = 0.0; private var b2 = 0.0; private var a1 = 0.0; private var a2 = 0.0
    private var x1 = 0.0; private var x2 = 0.0; private var y1 = 0.0; private var y2 = 0.0

    fun process(x: Double): Double {
        var y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2
        if (abs(y) < 1e-25) y = 0.0 // flush denormals: the JVM has no flush-to-zero mode
        x2 = x1; x1 = x; y2 = y1; y1 = y
        return y
    }

    fun reset() { x1 = 0.0; x2 = 0.0; y1 = 0.0; y2 = 0.0 }

    private fun set(nb0: Double, nb1: Double, nb2: Double, a0: Double, na1: Double, na2: Double) {
        b0 = nb0 / a0; b1 = nb1 / a0; b2 = nb2 / a0; a1 = na1 / a0; a2 = na2 / a0
    }

    /** 2nd-order high-pass. */
    fun highPass(sr: Int, f: Double, q: Double) {
        val w = 2 * PI * clampF(f, sr) / sr
        val c = cos(w); val alpha = sin(w) / (2 * q)
        set((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + alpha, -2 * c, 1 - alpha)
    }

    /** Band-pass with 0 dB peak gain (constant peak). x + (g - 1) * bp(x) is a peaking cut of g at [f]. */
    fun bandPass(sr: Int, f: Double, q: Double) {
        val w = 2 * PI * clampF(f, sr) / sr
        val c = cos(w); val alpha = sin(w) / (2 * q)
        set(alpha, 0.0, -alpha, 1 + alpha, -2 * c, 1 - alpha)
    }

    fun peaking(sr: Int, f: Double, gainDb: Double, q: Double) {
        val a = Math.pow(10.0, gainDb / 40.0)
        val w = 2 * PI * clampF(f, sr) / sr
        val c = cos(w); val alpha = sin(w) / (2 * q)
        set(1 + alpha * a, -2 * c, 1 - alpha * a, 1 + alpha / a, -2 * c, 1 - alpha / a)
    }

    private fun clampF(f: Double, sr: Int) = f.coerceIn(10.0, 0.45 * sr)
}

/**
 * Stages 1-4 of the podcast chain, interleaved [channels] (1 or 2), in place on doubles in -1..1.
 * Detectors are linked across channels. Allocation-free after construction. Latency: [latencyFrames].
 */
class PodcastChain(val sampleRate: Int, val channels: Int = 1, initial: PodcastParams = PodcastParams()) {
    init { require(sampleRate in 8000..192000) { "sampleRate" }; require(channels in 1..2) { "channels" } }

    var params: PodcastParams = initial
        set(value) { field = value; configure() }
    var agcEnabled = true
    var dynamicEqEnabled = true
    var compressorEnabled = true
    var limiterEnabled = true
    /** Runtime AGC cap in dB (the host lowers it when the user gain is high). */
    var agcMaxDb: Double = initial.agcMaxDb

    /** Signal delay of the look-ahead limiter in samples (L - 1). */
    val latencyFrames: Int get() = if (limiterEnabled) look - 1 else 0
    val latencyMs: Double get() = latencyFrames * 1000.0 / sampleRate
    val agcGainDb: Double get() = 20 * log10(max(agcG, 1e-9))
    val compGainReductionDb: Double get() = compGr
    val limiterGainDb: Double get() = 20 * log10(max(limOut, 1e-9))
    val gated: Boolean get() = gate
    val floorDbfs: Double get() = 20 * log10(max(floor, 1e-9))

    // gate + AGC
    private var gateMs = 0.0
    private var floor = dbToLin(-54.0)
    private var floorKnown = false
    private var below = 0
    private var gate = true
    private var active = false
    private var hold = 0
    private var agcMs = 0.0
    private var agcG = 1.0
    private var peakEnv = 0.0
    private var peakHold = 0
    private var peakRel = 0.0
    private var peakRecov = 0.0
    private var peakHoldN = 0
    private val gateCoeff = coeff(0.040)
    private val floorDown = coeff(0.30)
    private val floorUp = Math.pow(10.0, 1.5 / 20.0 / sampleRate)
    private val floorUpOpen = Math.pow(10.0, 0.5 / 20.0 / sampleRate)
    private val holdN = (0.30 * sampleRate).toInt()
    private val agcMsCoeff = coeff(0.20)
    private val agcGateRel = coeff(0.25)
    private var agcRise = 0.0
    private var agcFall = 0.0

    // dynamic EQ
    private val hp = Array(channels) { DspBiquad() }
    private val mudBp = Array(channels) { DspBiquad() }
    private val essBp = Array(channels) { DspBiquad() }
    private var essOn = true
    private var broadEnv = 0.0
    private var mudEnv = 0.0
    private var essEnv = 0.0
    private val broadAtk = coeff(0.005); private val broadRel = coeff(0.080)
    private val mudAtk = coeff(0.010); private val mudRel = coeff(0.100)
    private val essAtk = coeff(0.001); private val essRel = coeff(0.050)
    private var mudG = 1.0; private var essG = 1.0
    private var mudGs = 1.0; private var essGs = 1.0
    private val eqSmooth = coeff(0.002)

    // compressor
    private var compEnv = 0.0
    private val compEnvAtk = coeff(0.005); private val compEnvRel = coeff(0.025)
    private var grFast = 0.0; private var grSlow = 0.0; private var compGr = 0.0
    private val grFastAtk = coeffCtl(0.008); private val grFastRel = coeffCtl(0.080)
    private val grSlowAtk = coeffCtl(0.400); private val grSlowRel = coeffCtl(0.900)
    private var compG = 1.0; private var compGs = 1.0
    private val compSmooth = coeff(0.001)
    private var makeup = 1.0
    private var tick = 0

    // limiter
    private val lookMax = (0.005 * sampleRate).toInt() + 2
    private var look = 1
    private val delay = DoubleArray(lookMax * channels)
    private var dPos = 0
    private val dqVal = DoubleArray(lookMax + 1)
    private val dqIdx = LongArray(lookMax + 1)
    private var dqHead = 0; private var dqSize = 0
    private val box = DoubleArray(lookMax)
    private var boxPos = 0; private var boxSum = 0.0; private var boxTick = 0
    private var n = 0L
    private var limHeld = 1.0
    private var limRel = 0.0
    private var limOut = 1.0
    private var ceil = dbToLin(-1.0)

    init { configure(); reset() }

    private fun coeff(sec: Double) = 1 - exp(-1.0 / (sec * sampleRate))
    private fun coeffCtl(sec: Double) = 1 - exp(-CTRL.toDouble() / (sec * sampleRate))

    /** Recomputes coefficients from [params]; filter and envelope state is kept, so changes are click-free. */
    private fun configure() {
        val p = params
        agcRise = coeff(max(p.agcRiseSec, 0.01))
        agcFall = coeff(max(p.agcFallSec, 0.005))
        peakRel = coeff(0.080)
        peakRecov = coeff(max(p.agcPeakRecoverSec, 0.2))
        peakHoldN = (max(p.agcPeakHoldMs, 0.0) / 1000.0 * sampleRate).toInt()
        essOn = sampleRate >= 16000 && p.essMaxCutDb > 0
        for (c in 0 until channels) {
            hp[c].highPass(sampleRate, p.hpHz, 0.7071067811865476)
            mudBp[c].bandPass(sampleRate, p.mudHz, 1.0)
            essBp[c].bandPass(sampleRate, min(p.essHz, 0.4 * sampleRate), 1.4)
        }
        makeup = dbToLin(p.compMakeupDb.coerceIn(0.0, 12.0))
        ceil = dbToLin(p.limCeilingDbfs.coerceIn(-12.0, 0.0))
        limRel = coeff(max(p.limReleaseMs, 5.0) / 1000.0)
        val newLook = (round(p.lookaheadMs.coerceIn(0.0, 3.0) * sampleRate / 1000.0).toInt() + 1).coerceIn(1, lookMax)
        if (newLook != look) { look = newLook; resetLimiter() }
    }

    fun reset() {
        gateMs = 0.0; floor = dbToLin(-54.0); floorKnown = false; below = 0; gate = true; hold = 0
        agcMs = dbToLin(params.agcTargetDbfs).let { it * it }; agcG = 1.0; peakEnv = 0.0; peakHold = 0
        for (c in 0 until channels) { hp[c].reset(); mudBp[c].reset(); essBp[c].reset() }
        broadEnv = 0.0; mudEnv = 0.0; essEnv = 0.0; mudG = 1.0; essG = 1.0; mudGs = 1.0; essGs = 1.0
        compEnv = 0.0; grFast = 0.0; grSlow = 0.0; compGr = 0.0; compG = 1.0; compGs = 1.0; tick = 0
        resetLimiter()
    }

    private fun resetLimiter() {
        delay.fill(0.0); dPos = 0; dqHead = 0; dqSize = 0
        box.fill(1.0); boxPos = 0; boxSum = look.toDouble(); boxTick = 0
        limHeld = 1.0; limOut = 1.0
    }

    /** In place on [frames] interleaved frames starting at [off]. Non-finite input is treated as silence. */
    fun process(x: DoubleArray, frames: Int, off: Int = 0) {
        val p = params
        val minLin = dbToLin(min(p.agcMinDb, 0.0))
        val ch = channels
        for (f in 0 until frames) {
            val base = off + f * ch
            // ---- 0: sanitize, linked mean-square for the gate
            var sq = 0.0
            for (c in 0 until ch) {
                var v = x[base + c]
                if (v.isNaN() || v.isInfinite()) v = 0.0 else if (v > 4.0) v = 4.0 else if (v < -4.0) v = -4.0
                x[base + c] = v
                if (v * v > sq) sq = v * v
            }
            gateStep(sq)
            // ---- 1: 1.1.1 peak-aware protection. The gain never rises above 0 dB (no auto-boost). When a sample
            // peak would meet the yellow-meter margin, the gain drops at once; it holds, then recovers slowly.
            val pk = sqrt(max(sq, 0.0))
            if (pk > peakEnv) peakEnv = pk else peakEnv += (pk - peakEnv) * peakRel
            if (agcEnabled) {
                val peakCeil = dbToLin(p.agcPeakDbfs.coerceIn(-24.0, -3.0))
                val wantPeak = peakCeil / max(max(pk, peakEnv), 1e-8)
                var want = min(1.0, wantPeak).coerceIn(minLin, 1.0)
                val k: Double
                if (want < agcG) {
                    agcG = want // instant downward so a shout cannot paint the meter yellow
                    peakHold = peakHoldN
                    k = 0.0
                } else if (peakHold > 0) {
                    peakHold--
                    k = 0.0 // hold
                } else {
                    k = peakRecov
                    agcG += (want - agcG) * k
                }
                for (c in 0 until ch) x[base + c] *= agcG
            }
            // ---- 2: dynamic EQ
            if (dynamicEqEnabled) {
                var bSq = 0.0; var mSq = 0.0; var eSq = 0.0
                for (c in 0 until ch) {
                    val h = hp[c].process(x[base + c])
                    val bm = mudBp[c].process(h)
                    val be = if (essOn) essBp[c].process(h) else 0.0
                    x[base + c] = h + (mudGs - 1) * bm + (essGs - 1) * be
                    if (h * h > bSq) bSq = h * h
                    if (bm * bm > mSq) mSq = bm * bm
                    if (be * be > eSq) eSq = be * be
                }
                broadEnv += (bSq - broadEnv) * (if (bSq > broadEnv) broadAtk else broadRel)
                mudEnv += (mSq - mudEnv) * (if (mSq > mudEnv) mudAtk else mudRel)
                essEnv += (eSq - essEnv) * (if (eSq > essEnv) essAtk else essRel)
                mudGs += (mudG - mudGs) * eqSmooth
                essGs += (essG - essGs) * eqSmooth
            }
            // ---- 3: compressor detector (linked mean square)
            if (compressorEnabled) {
                var cSq = 0.0
                for (c in 0 until ch) { val v = x[base + c]; if (v * v > cSq) cSq = v * v }
                compEnv += (cSq - compEnv) * (if (cSq > compEnv) compEnvAtk else compEnvRel)
            }
            // control-rate updates (dB math once per CTRL samples)
            if (++tick >= CTRL) { tick = 0; control(p) }
            if (compressorEnabled) {
                compGs += (compG - compGs) * compSmooth
                val g = compGs * makeup
                for (c in 0 until ch) x[base + c] *= g
            }
            // ---- 4: look-ahead brickwall limiter
            if (limiterEnabled) limitFrame(x, base) else {
                for (c in 0 until ch) x[base + c] = x[base + c].coerceIn(-1.0, 1.0)
            }
        }
    }

    private fun gateStep(sq: Double) {
        gateMs += (sq - gateMs) * gateCoeff
        val level = sqrt(max(gateMs, 0.0))
        if (level < floor) {
            floor += (level - floor) * floorDown
            if (!floorKnown && ++below >= sampleRate / 4) floorKnown = true
        } else floor *= if (gate) floorUp else floorUpOpen
        if (floor < 1e-6) floor = 1e-6 else if (floor > 0.3) floor = 0.3
        val open = max(floor * OPEN_OVER_FLOOR, OPEN_MIN)
        val close = max(floor * CLOSE_OVER_FLOOR, CLOSE_MIN)
        active = level > open
        if (active) { gate = false; hold = holdN }
        else if (level < close) { if (hold > 0) hold-- else gate = true }
    }

    private fun control(p: PodcastParams) {
        if (dynamicEqEnabled) {
            val bDb = 10 * log10(broadEnv + 1e-20)
            val mDb = 10 * log10(mudEnv + 1e-20)
            val mudCut = if (mDb > -70) ((mDb - bDb - p.mudThreshDb) * 0.5).coerceIn(0.0, max(p.mudMaxCutDb, 0.0)) else 0.0
            mudG = dbToLin(-mudCut)
            essG = if (essOn) {
                val eDb = 10 * log10(essEnv + 1e-20)
                val cut = if (eDb > -70) ((eDb - bDb - p.essThreshDb) * 0.75).coerceIn(0.0, max(p.essMaxCutDb, 0.0)) else 0.0
                dbToLin(-cut)
            } else 1.0
        } else { mudG = 1.0; essG = 1.0 }
        if (compressorEnabled) {
            val level = 10 * log10(compEnv + 1e-20)
            val target = -gainComputer(level, p.compThreshDbfs, max(p.compRatio, 1.0), max(p.compKneeDb, 0.0)) // GR, dB >= 0
            grFast += (target - grFast) * (if (target > grFast) grFastAtk else grFastRel)
            // The slow envelope only charges under sustained compression, so short peaks recover fast and dense
            // passages recover slowly (program-dependent release, a variable-mu-like feel).
            grSlow += (target - grSlow) * (if (target > grSlow) grSlowAtk else grSlowRel)
            compGr = max(grFast, grSlow)
            compG = dbToLin(-compGr)
        } else { compGr = 0.0; compG = 1.0 }
    }

    private fun limitFrame(x: DoubleArray, base: Int) {
        val ch = channels
        var pk = 0.0
        for (c in 0 until ch) { val a = abs(x[base + c]); if (a > pk) pk = a }
        val r = if (pk > ceil) ceil / pk else 1.0
        // release on the required gain itself: still <= r, so the brickwall guarantee holds
        limHeld = min(r, limHeld + (1 - limHeld) * limRel)
        val g: Double
        if (look <= 1) g = limHeld else {
            // sliding minimum over the last L samples (monotonic deque in fixed arrays)
            val cap = dqVal.size
            while (dqSize > 0 && dqVal[(dqHead + dqSize - 1) % cap] >= limHeld) dqSize--
            val t = (dqHead + dqSize) % cap
            dqVal[t] = limHeld; dqIdx[t] = n; dqSize++
            while (dqIdx[dqHead] <= n - look) { dqHead = (dqHead + 1) % cap; dqSize-- }
            val m = dqVal[dqHead]
            // boxcar of length L over the minimum: reaches the needed gain exactly when the peak leaves the delay
            boxSum += m - box[boxPos]
            box[boxPos] = m
            if (++boxPos >= look) boxPos = 0
            if (++boxTick >= 4096) { boxTick = 0; var s = 0.0; for (i in 0 until look) s += box[i]; boxSum = s }
            g = min(boxSum / look, 1.0)
        }
        n++
        limOut = g
        val d = look - 1
        for (c in 0 until ch) {
            val v: Double
            if (d == 0) v = x[base + c] else {
                val idx = dPos * ch + c
                v = delay[idx]
                delay[idx] = x[base + c]
            }
            // final clamp only catches float rounding; the gain already holds the ceiling
            x[base + c] = (v * g).coerceIn(-ceil, ceil)
        }
        if (d > 0 && ++dPos >= d) dPos = 0
    }

    companion object {
        const val CTRL = 16
        private val OPEN_OVER_FLOOR = dbToLin(9.0)
        private val CLOSE_OVER_FLOOR = dbToLin(5.0)
        private val OPEN_MIN = dbToLin(-62.0)
        private val CLOSE_MIN = dbToLin(-68.0)
        private val FLOOR_BIAS = dbToLin(2.0)

        fun dbToLin(db: Double): Double = Math.pow(10.0, db / 20.0)

        /** Soft-knee static curve: returns the gain change in dB (<= 0) for [levelDb]. */
        fun gainComputer(levelDb: Double, threshDb: Double, ratio: Double, kneeDb: Double): Double {
            val over = levelDb - threshDb
            val slope = 1.0 / ratio - 1.0
            return when {
                kneeDb > 0 && 2 * abs(over) <= kneeDb -> slope * (over + kneeDb / 2) * (over + kneeDb / 2) / (2 * kneeDb)
                over > 0 -> slope * over
                else -> 0.0
            }
        }
    }
}

/**
 * Thin true-peak clip guard after noise suppression: estimates inter-sample peaks with a 4-tap half-sample
 * interpolator, takes gain down at once, releases in 40 ms, and clamps at the ceiling. Delay: 2 samples.
 */
class TruePeakGuard(sampleRate: Int, private val channels: Int = 1, ceilingDbfs: Double = VoiceChain.CEILING_DBFS) {
    val latencyFrames = 2
    private val ceil = PodcastChain.dbToLin(ceilingDbfs)
    private val rel = 1 - exp(-1.0 / (0.040 * sampleRate))
    private val h = DoubleArray(4 * channels) // x[n-3], x[n-2], x[n-1], x[n] per channel
    private val prevMid = DoubleArray(channels)
    private var g = 1.0
    val gainDb: Double get() = 20 * log10(max(g, 1e-9))

    fun reset() { h.fill(0.0); prevMid.fill(0.0); g = 1.0 }

    /** Process one frame (all channels) in place at [base]. */
    fun processFrame(x: DoubleArray, base: Int) {
        var tp = 0.0
        for (c in 0 until channels) {
            var v = x[base + c]
            if (v.isNaN() || v.isInfinite()) v = 0.0
            val o = c * 4
            h[o] = h[o + 1]; h[o + 1] = h[o + 2]; h[o + 2] = h[o + 3]; h[o + 3] = v
            val mid = (-h[o] + 9 * h[o + 1] + 9 * h[o + 2] - h[o + 3]) / 16.0 // between x[n-2] and x[n-1]
            val a = max(abs(h[o + 1]), max(abs(mid), abs(prevMid[c])))
            prevMid[c] = mid
            if (a > tp) tp = a
        }
        val want = if (tp > ceil) ceil / tp else 1.0
        g = if (want < g) want else g + (want - g) * rel
        for (c in 0 until channels) x[base + c] = (h[c * 4 + 1] * g).coerceIn(-ceil, ceil)
    }

    fun process(x: Double): Double {
        scratch[0] = x; processFrame(scratch, 0); return scratch[0]
    }
    private val scratch = DoubleArray(channels)
}

/**
 * Spectral dereverb and dialog-isolation masks for one STFT channel, computed from the host STFT's smoothed power
 * spectrum, magnitudes and noise estimate. Allocation-free after construction.
 *
 * Dereverb: late reverb power is modelled per bin as an exponentially decayed copy of the power spectrum one
 * pre-delay (about 50 ms) earlier, P_late(t) = exp(-2*delta*Td) * P(t - Td), delta = 3 ln 10 / RT60 (RT60 assumed
 * 0.5 s). Gain is Wiener-style, SRR / (1 + SRR) with SRR = max(P - P_late, 0) / P_late, floored by strength.
 *
 * Dialog isolation: a frame VAD (voice-band 100 Hz-4 kHz SNR with a spectral-flatness check, 150 ms hangover) and a
 * per-bin speech-presence value (posterior SNR, local spectral peaks in the voice band are treated as harmonics),
 * weighted by a voice-band prior; non-speech bins go down to the floor, speech bins pass.
 */
class SpectralVoice(private val sampleRate: Int, fftSize: Int, private val hop: Int, rt60Sec: Double = 0.5) {
    val bins = fftSize / 2 + 1
    val tdFrames: Int = max(1, round(0.050 * sampleRate / hop).toInt())
    private val decay: Double
    private val hist = DoubleArray((tdFrames + 1) * bins)
    private var histPos = 0
    private var frames = 0
    private val prior = DoubleArray(bins)
    private val vbLo: Int
    private val vbHi: Int
    private val vadRel: Double
    var vad = 0.0; private set

    init {
        val delta = 3 * ln(10.0) / rt60Sec
        decay = exp(-2 * delta * tdFrames * hop / sampleRate.toDouble())
        val df = sampleRate.toDouble() / fftSize
        for (k in 0 until bins) {
            val f = k * df
            prior[k] = when {
                f < 70 -> 0.3
                f <= 8000 -> 1.0
                f <= 10000 -> 1.0 - 0.5 * (f - 8000) / 2000
                else -> 0.5
            }
        }
        vbLo = max(1, round(100 / df).toInt())
        vbHi = min(bins - 2, max(vbLo + 1, round(4000 / df).toInt()))
        vadRel = exp(-hop / (0.150 * sampleRate))
    }

    fun reset() { hist.fill(0.0); histPos = 0; frames = 0; vad = 0.0 }

    /**
     * Writes the combined dereverb x dialog gain (floored) per bin to [out]. [psd] smoothed power, [mag] magnitude,
     * [noise] noise power estimate. Strengths 0..1; 0 leaves that mask at 1.
     */
    fun compute(psd: DoubleArray, mag: DoubleArray, noise: DoubleArray, dereverb: Double, dialog: Double, out: DoubleArray) {
        val dr = dereverb.coerceIn(0.0, 1.0)
        val dl = dialog.coerceIn(0.0, 1.0)
        val derevFloor = PodcastChain.dbToLin(-12.0 * dr)
        val dialogFloor = PodcastChain.dbToLin(-10.0 * dl)
        // frame VAD
        if (dl > 0) {
            var s = 0.0; var nz = 0.0; var logSum = 0.0; var linSum = 0.0
            for (k in vbLo..vbHi) {
                s += psd[k]; nz += noise[k]
                val m = mag[k] + 1e-12
                logSum += ln(m); linSum += m
            }
            val cnt = (vbHi - vbLo + 1).toDouble()
            val flat = exp(logSum / cnt) / (linSum / cnt)
            val snrDb = if (nz > 0) 10 * log10(s / nz + 1e-12) else 0.0
            var pf = 1.0 / (1.0 + exp(-(snrDb - 5.0) / 1.5))
            if (flat > 0.65) pf *= 0.5 // flat (noise-like) spectra are less likely to be voice
            vad = if (pf > vad) pf else vad * vadRel + pf * (1 - vadRel)
        }
        val past = ((histPos - tdFrames) % (tdFrames + 1) + (tdFrames + 1)) % (tdFrames + 1)
        val haveLate = frames > tdFrames
        for (k in 0 until bins) {
            var g = 1.0
            if (dr > 0 && haveLate) {
                val late = decay * hist[past * bins + k]
                val p = psd[k]
                if (late > 1e-20) {
                    val srr = max(p - late, 0.0) / late
                    g = max(srr / (1 + srr), derevFloor)
                }
            }
            if (dl > 0) {
                val nz = noise[k]
                val post = if (nz > 1e-20) mag[k] * mag[k] / nz else 100.0
                var spp = ((10 * log10(post + 1e-12)) / 9.0).coerceIn(0.0, 1.0)
                if (k in vbLo..vbHi && post > 2.0 && mag[k] >= mag[k - 1] && mag[k] >= mag[k + 1]) spp = max(spp, 0.9)
                val speech = max(spp, vad * prior[k])
                g *= dialogFloor + (1 - dialogFloor) * speech
            }
            out[k] = g
        }
        for (k in 0 until bins) hist[histPos * bins + k] = psd[k]
        histPos = (histPos + 1) % (tdFrames + 1)
        frames++
    }
}

/**
 * Real-time CPU watchdog for the voice chain. Feed the wall time each block took; once the smoothed share of the
 * block's real-time duration stays above [budget] (after a 1 s JIT warm-up), [tripped] latches and the host drops
 * the optional spectral stages (dereverb / dialog) for the rest of the session.
 */
class CpuGuard(private val sampleRate: Int, private val budget: Double = 0.30) {
    var load = 0.0; private set
    var tripped = false; private set
    private var seen = 0L
    private var overFrames = 0L

    fun reset() { load = 0.0; tripped = false; seen = 0; overFrames = 0 }

    fun record(nanos: Long, frames: Int) {
        if (frames <= 0 || tripped) return
        seen += frames
        if (seen < sampleRate) return // JIT / cache warm-up
        val share = nanos / (frames * 1e9 / sampleRate)
        val a = min(1.0, frames / (sampleRate * 0.5))
        load += (share - load) * a
        if (load > budget) { overFrames += frames; if (overFrames >= sampleRate * 3L / 2) tripped = true } else overFrames = 0
    }
}
