package dev.cordmic

import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.cos
import kotlin.math.exp
import kotlin.math.max
import kotlin.math.min
import kotlin.math.round
import kotlin.math.sin
import kotlin.math.sqrt

enum class NoiseMode(val code: Int) {
    OFF(0), LIGHT(1), STRONG(2);
    companion object {
        fun from(code: Int): NoiseMode = when (code) {
            2 -> STRONG
            1 -> LIGHT
            else -> OFF
        }
    }
}

/**
 * Same chain as CordMic.Core.VoiceChain: voice-clarity EQ, optional Hann-OLA noise suppression,
 * smoothed user gain, gated AGC toward −18 dBFS, a pause expander, and a peak limiter at −1 dBFS.
 * The gate and noise floor are measured before the user gain and the floor is tracked continuously,
 * so a higher gain cannot open the gate on hiss; AGC makeup keeps the floor at or below −52 dBFS.
 * STFT delay is [NOISE_DELAY_FRAMES] samples (2.67 ms at 48 kHz) when suppression is on.
 */
class VoiceChain(private val sampleRate: Int = 48000) {
    var mode: NoiseMode = NoiseMode.LIGHT
    var noiseCancellation: Boolean
        get() = mode != NoiseMode.OFF
        set(value) { mode = if (value) (if (mode == NoiseMode.OFF) NoiseMode.LIGHT else mode) else NoiseMode.OFF }
    var agc: Boolean = true
    /** Pause expander depth; set from the user's noise mode even when the platform suppressor replaces the STFT. */
    var expander: NoiseMode = NoiseMode.LIGHT
    /** Tracked pause floor before user gain, dBFS. */
    val noiseFloorDbfs: Double get() = 20 * kotlin.math.log10(max(floor, 1e-6))
    var voiceClarity: Boolean = true
    var highPass: Boolean
        get() = voiceClarity
        set(value) { voiceClarity = value }
    var gainDb: Int = DEFAULT_GAIN_DB
        set(value) { field = value.coerceIn(0, MAX_GAIN_DB) }

    val delayFrames: Int get() = if (mode == NoiseMode.OFF) 0 else NOISE_DELAY_FRAMES
    val noiseLatencyMs: Double get() = delayFrames * 1000.0 / sampleRate

    private val hp: Biquad
    private val shelf: Biquad
    private val presence: Biquad
    private val rmsCoeff: Double
    private val agcRise: Double
    private val agcFall: Double
    private val agcGateRel: Double
    private val peakRel: Double
    private val limRel: Double
    private val floorDown: Double
    private val floorUp: Double
    private val floorUpOpen: Double
    private val gainSmooth: Double
    private val expOpen: Double
    private val expClose: Double
    private val holdFrames: Int
    private var rmsPre = 0.0
    private var floor = dbToLin(INITIAL_FLOOR_DBFS)
    private var userGain = Double.NaN
    private var expGain = 1.0
    private var hold = 0
    private var gateTick = 0
    private var belowFloor = 0
    private var floorKnown = false
    private val blockIn = DoubleArray(NOISE_FFT)
    private val hopOut = DoubleArray(NOISE_HOP)
    private val ola = DoubleArray(NOISE_HOP)
    private val hann = DoubleArray(NOISE_FFT)
    private val re = DoubleArray(NOISE_FFT)
    private val im = DoubleArray(NOISE_FFT)
    private val mag = DoubleArray(NOISE_FFT / 2 + 1)
    private val noise = DoubleArray(NOISE_FFT / 2 + 1)
    private val binGain = DoubleArray(NOISE_FFT / 2 + 1) { 1.0 }
    private val psd = DoubleArray(NOISE_FFT / 2 + 1)
    private val prevClean = DoubleArray(NOISE_FFT / 2 + 1)
    private val rawGain = DoubleArray(NOISE_FFT / 2 + 1)
    private var fill = NOISE_HOP
    private var emit = 0
    private var haveHop = false
    private var rms = 0.0
    private var agcGain = 1.0
    private var peak = 0.0
    private var lim = 1.0
    private var gated = true
    private var gateOpen = dbToLin(DEFAULT_GATE_OPEN_DBFS)
    private var gateClose = dbToLin(DEFAULT_GATE_CLOSE_DBFS)
    private val shorts = ShortArray(4096)

    init {
        hp = Biquad.highPass(sampleRate, 120.0, 0.7071067811865476)
        shelf = Biquad.lowShelf(sampleRate, 220.0, -5.0)
        presence = Biquad.peaking(sampleRate, 3000.0, 2.5, 1.0)
        for (i in 0 until NOISE_FFT) hann[i] = 0.5 - 0.5 * cos(2 * PI * i / NOISE_FFT)
        rmsCoeff = 1 - exp(-1.0 / (0.040 * sampleRate))
        agcRise = 1 - exp(-1.0 / (0.22 * sampleRate))
        agcFall = 1 - exp(-1.0 / (0.025 * sampleRate))
        agcGateRel = 1 - exp(-1.0 / (0.080 * sampleRate))
        peakRel = 1 - exp(-1.0 / (0.030 * sampleRate))
        limRel = 1 - exp(-1.0 / (0.050 * sampleRate))
        floorDown = 1 - exp(-1.0 / (0.30 * sampleRate))
        floorUp = Math.pow(10.0, FLOOR_RISE_DB_PER_SEC / 20.0 / sampleRate)
        floorUpOpen = Math.pow(10.0, FLOOR_RISE_OPEN_DB_PER_SEC / 20.0 / sampleRate)
        gainSmooth = 1 - exp(-1.0 / (0.020 * sampleRate))
        expOpen = 1 - exp(-1.0 / (0.005 * sampleRate))
        expClose = 1 - exp(-1.0 / (0.150 * sampleRate))
        holdFrames = (GATE_HOLD_SEC * sampleRate).toInt()
    }

    fun reset() {
        hp.reset(); shelf.reset(); presence.reset()
        fill = NOISE_HOP; emit = 0; haveHop = false
        rms = 0.0; agcGain = 1.0; peak = 0.0; lim = 1.0; gated = true
        rmsPre = 0.0; floor = dbToLin(INITIAL_FLOOR_DBFS); userGain = Double.NaN; expGain = 1.0
        hold = 0; gateTick = 0; belowFloor = 0; floorKnown = false
        gatesFromFloor(INITIAL_FLOOR_DBFS).let { gateOpen = it.first; gateClose = it.second }
        blockIn.fill(0.0); hopOut.fill(0.0); ola.fill(0.0); noise.fill(0.0)
        psd.fill(0.0); prevClean.fill(0.0)
        binGain.fill(1.0)
    }

    fun setNoiseFloorDbfs(floorDbfs: Double?) {
        val (o, c) = gatesFromFloor(floorDbfs)
        gateOpen = o
        gateClose = c
        if (floorDbfs != null && !floorDbfs.isNaN() && floorDbfs >= -85 && floorDbfs <= -15) {
            floor = dbToLin(floorDbfs); floorKnown = true
        }
    }

    /** In-place on little-endian PCM16 starting at [off], [frames] frames. */
    fun process(buf: ByteArray, off: Int, frames: Int) {
        val n = frames.coerceAtMost(shorts.size)
        var i = 0
        var p = off
        while (i < n) {
            val v = (buf[p].toInt() and 0xFF) or (buf[p + 1].toInt() shl 8)
            shorts[i] = v.toShort()
            i++; p += 2
        }
        process(shorts, n)
        i = 0; p = off
        while (i < n) {
            val v = shorts[i].toInt()
            buf[p] = (v and 0xFF).toByte()
            buf[p + 1] = (v shr 8 and 0xFF).toByte()
            i++; p += 2
        }
    }

    /** Last chain state for the PC log (pre-gain floor, gate, AGC and expander gain). Local only; sent only when the PC asks. */
    @Volatile var diagnostics: VoiceDiag = VoiceDiag(INITIAL_FLOOR_DBFS, true, false, 0.0, 0.0, false)
        private set

    fun process(samples: ShortArray, n: Int = samples.size) {
        val user = dbToLin(gainDb.toDouble())
        if (userGain.isNaN()) userGain = user
        val maxAgc = dbToLin(min(MAX_AGC_DB, (MAX_GAIN_DB - gainDb).toDouble()))
        val target = dbToLin(TARGET_DBFS)
        val thresh = dbToLin(CEILING_DBFS)
        val noiseCeil = dbToLin(NOISE_CEILING_DBFS)
        val pause = when (expander) {
            NoiseMode.STRONG -> dbToLin(STRONG_PAUSE_DB)
            NoiseMode.LIGHT -> dbToLin(LIGHT_PAUSE_DB)
            else -> 1.0
        }
        val stft = mode != NoiseMode.OFF
        for (i in 0 until n) {
            var x = samples[i] / 32768.0
            if (voiceClarity) {
                x = hp.process(x)
                x = shelf.process(x)
                x = presence.process(x)
            }
            x = if (stft) noiseStep(x) else { fill = NOISE_HOP; emit = 0; haveHop = false; x }
            gateStep(x)
            userGain += (user - userGain) * gainSmooth
            var y = x * userGain
            if (agc) {
                rms += (y * y - rms) * rmsCoeff
                val level = sqrt(max(rms, 0.0))
                // Do not lift the pause floor above the noise ceiling; until a pause is seen only the fixed cap applies.
                val cap = if (floorKnown) (noiseCeil / max(floor * userGain, 1e-9)).coerceIn(1.0, maxAgc) else maxAgc
                val want = if (gated) 1.0 else (target / max(level, 1e-5)).coerceIn(0.25, cap)
                val coeff = if (gated) agcGateRel else if (want > agcGain) agcRise else agcFall
                agcGain += (want - agcGain) * coeff
                y *= agcGain
            }
            val expWant = if (gated) pause else 1.0
            expGain += (expWant - expGain) * (if (expWant > expGain) expOpen else expClose)
            y *= expGain
            y = limitStep(y, thresh)
            var s = round(y * 32768.0).toInt()
            if (s > 32767) s = 32767 else if (s < -32768) s = -32768
            samples[i] = s.toShort()
        }
        diagnostics = VoiceDiag(
            floorDbfs = 20 * kotlin.math.log10(max(floor, 1e-9)),
            gated = gated,
            agc = agc,
            agcGainDb = if (agc) 20 * kotlin.math.log10(max(agcGain, 1e-9)) else 0.0,
            expanderDb = 20 * kotlin.math.log10(max(expGain, 1e-9)),
            floorKnown = floorKnown,
        )
    }

    /** Pre-gain envelope, continuous floor tracking, and the gate with a short hold. */
    private fun gateStep(x: Double) {
        rmsPre += (x * x - rmsPre) * rmsCoeff
        val level = sqrt(max(rmsPre, 0.0))
        if (level < floor) {
            floor += (level - floor) * floorDown
            if (!floorKnown && ++belowFloor >= sampleRate / 4) floorKnown = true
        } else floor *= if (gated) floorUp else floorUpOpen
        floor = floor.coerceIn(1e-5, 0.1)
        if (++gateTick >= GATE_UPDATE_FRAMES) {
            gateTick = 0
            val (o, c) = gatesFromFloor(20 * kotlin.math.log10(floor))
            gateOpen = o; gateClose = c
        }
        if (level > gateOpen) { gated = false; hold = holdFrames }
        else if (level < gateClose) { if (hold > 0) hold-- else gated = true }
    }

    private fun limitStep(y0: Double, thresh: Double): Double {
        var y = y0
        val ay = abs(y)
        peak = if (ay > peak) ay else peak + (ay - peak) * peakRel
        val gr = if (peak > thresh) thresh / peak else 1.0
        // Lookahead-less: take gain reduction immediately so a shout cannot sneak past −1 dBFS.
        if (gr < lim) lim = gr else lim += (gr - lim) * limRel
        y *= lim
        if (y > 0.99) y = 0.99
        if (y < -0.99) y = -0.99
        return y
    }

    private fun noiseStep(x: Double): Double {
        val y = if (haveHop) hopOut[emit] else 0.0
        blockIn[fill] = x
        fill++
        emit++
        if (fill == NOISE_FFT) {
            suppressHop()
            blockIn.copyInto(blockIn, 0, NOISE_HOP, NOISE_FFT)
            fill = NOISE_HOP
            emit = 0
            haveHop = true
        }
        return y
    }

    private fun suppressHop() {
        for (i in 0 until NOISE_FFT) {
            re[i] = blockIn[i] * hann[i]
            im[i] = 0.0
        }
        fft(re, im, false)
        val half = NOISE_FFT / 2
        var maxBin = 0.0
        var sumBin = 0.0
        var frameE = 0.0
        var noiseE = 0.0
        for (k in 0..half) {
            val p = re[k] * re[k] + im[k] * im[k]
            val m = sqrt(p)
            mag[k] = m
            if (k > 0) {
                if (m > maxBin) maxBin = m
                sumBin += m
            }
            psd[k] = if (psd[k] <= 0) p else 0.6 * psd[k] + 0.4 * p
            frameE += psd[k]
            noiseE += noise[k]
        }
        val mean = sumBin / half
        val noiseLike = maxBin < mean * 12.0 + 1e-12
        // Only a frame close to the current noise level may pull the estimate up quickly.
        val nearFloor = noiseE <= 0 || frameE < 2.5 * noiseE
        val strong = mode == NoiseMode.STRONG
        val floorG = if (strong) STRONG_FLOOR else LIGHT_FLOOR
        val over = if (strong) STRONG_OVER_SUB else 1.0
        val dd = if (strong) 0.94 else 0.92
        var hfS = 0.0
        var hfN = 0.0
        for (k in 0..half) {
            val sp = psd[k]
            var n = noise[k]
            n = when {
                n <= 0 -> if (noiseLike) sp else 0.0 // bootstrap only from a noise-like frame
                sp < n -> 0.8 * n + 0.2 * sp
                noiseLike && nearFloor -> 0.95 * n + 0.05 * sp
                else -> n * NOISE_RISE_PER_HOP
            }
            noise[k] = n
            if (k >= HF_BIN) { hfS += sp; hfN += n }
            val nn = max(over * n, 1e-20)
            val post = mag[k] * mag[k] / nn
            // Decision-directed a-priori SNR, then a Wiener gain.
            val prio = dd * prevClean[k] / nn + (1 - dd) * max(post - 1, 0.0)
            val g = prio / (1 + prio)
            rawGain[k] = g.coerceIn(floorG, 1.0)
        }
        // Whispers and s/f/sh sounds: keep the band above 3 kHz while it is clearly above its noise.
        val hfActive = hfN > 0 && hfS > HF_PROTECT_RATIO * hfN
        for (k in 0..half) {
            val l = rawGain[if (k > 0) k - 1 else k]
            val c = rawGain[k]
            val r = rawGain[if (k < half) k + 1 else k]
            var g = 0.25 * l + 0.5 * c + 0.25 * r
            if (hfActive && k >= HF_BIN && g < HF_PROTECT_FLOOR) g = HF_PROTECT_FLOOR
            val a = if (g > binGain[k]) GAIN_RISE else GAIN_FALL
            binGain[k] = a * binGain[k] + (1 - a) * g
            val clean = binGain[k] * mag[k]
            prevClean[k] = clean * clean
        }
        for (k in 0 until NOISE_FFT) {
            val bin = if (k <= half) k else NOISE_FFT - k
            re[k] *= binGain[bin]
            im[k] *= binGain[bin]
        }
        fft(re, im, true)
        for (i in 0 until NOISE_HOP) {
            hopOut[i] = ola[i] + re[i]
            ola[i] = re[i + NOISE_HOP]
        }
    }

    /** Current per-bin suppression gains (tests and diagnostics). */
    fun copyBinGains(dst: DoubleArray) = binGain.copyInto(dst, 0, 0, minOf(dst.size, binGain.size))

    private class Biquad(
        private val b0: Double, private val b1: Double, private val b2: Double,
        private val a1: Double, private val a2: Double,
    ) {
        private var x1 = 0.0; private var x2 = 0.0; private var y1 = 0.0; private var y2 = 0.0
        fun process(x: Double): Double {
            val y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2
            x2 = x1; x1 = x; y2 = y1; y1 = y
            return y
        }
        fun reset() { x1 = 0.0; x2 = 0.0; y1 = 0.0; y2 = 0.0 }

        companion object {
            fun highPass(sr: Int, freq: Double, q: Double): Biquad {
                val w0 = 2 * PI * freq / sr
                val c = cos(w0)
                val alpha = sin(w0) / (2 * q)
                val a0 = 1 + alpha
                return Biquad(((1 + c) / 2) / a0, (-(1 + c)) / a0, ((1 + c) / 2) / a0, (-2 * c) / a0, (1 - alpha) / a0)
            }
            fun lowShelf(sr: Int, freq: Double, gainDb: Double): Biquad {
                val a = Math.pow(10.0, gainDb / 40.0)
                val w0 = 2 * PI * freq / sr
                val c = cos(w0)
                val s = sin(w0)
                val alpha = s / 2 * sqrt(2.0)
                val two = 2 * sqrt(a) * alpha
                val a0 = (a + 1) + (a - 1) * c + two
                return Biquad(
                    a * ((a + 1) - (a - 1) * c + two) / a0,
                    2 * a * ((a - 1) - (a + 1) * c) / a0,
                    a * ((a + 1) - (a - 1) * c - two) / a0,
                    -2 * ((a - 1) + (a + 1) * c) / a0,
                    ((a + 1) + (a - 1) * c - two) / a0,
                )
            }
            fun peaking(sr: Int, freq: Double, gainDb: Double, q: Double): Biquad {
                val a = Math.pow(10.0, gainDb / 40.0)
                val w0 = 2 * PI * freq / sr
                val c = cos(w0)
                val alpha = sin(w0) / (2 * q)
                val a0 = 1 + alpha / a
                return Biquad((1 + alpha * a) / a0, (-2 * c) / a0, (1 - alpha * a) / a0, (-2 * c) / a0, (1 - alpha / a) / a0)
            }
        }
    }

    companion object {
        const val NOISE_FFT = 256
        const val NOISE_HOP = 128
        const val NOISE_DELAY_FRAMES = NOISE_HOP
        const val MAX_GAIN_DB = 24
        const val DEFAULT_GAIN_DB = 6
        const val TARGET_DBFS = -18.0
        const val MAX_AGC_DB = 24.0
        const val CEILING_DBFS = -1.0
        private const val LIGHT_FLOOR = 0.32
        private const val STRONG_FLOOR = 0.18 // −15 dB: a little natural background
        private const val STRONG_OVER_SUB = 1.6
        private const val HF_BIN = 16 // 3 kHz
        private const val HF_PROTECT_RATIO = 2.0
        private const val HF_PROTECT_FLOOR = 0.6
        private const val GAIN_RISE = 0.45
        private const val GAIN_FALL = 0.82
        private val NOISE_RISE_PER_HOP = Math.pow(10.0, 3.0 / 10 / 375.0)
        const val DEFAULT_GATE_OPEN_DBFS = -42.0
        const val DEFAULT_GATE_CLOSE_DBFS = -50.0
        const val SPEECH_LOW_DBFS = -40.0
        const val NOISE_CEILING_DBFS = -52.0
        const val INITIAL_FLOOR_DBFS = -54.0
        const val LIGHT_PAUSE_DB = -6.0
        const val STRONG_PAUSE_DB = -12.0
        private const val FLOOR_RISE_DB_PER_SEC = 1.5
        private const val FLOOR_RISE_OPEN_DB_PER_SEC = 0.5
        private const val GATE_HOLD_SEC = 0.20
        private const val GATE_UPDATE_FRAMES = 64

        fun gatesFromFloor(floorDbfs: Double?): Pair<Double, Double> {
            if (floorDbfs == null || floorDbfs.isNaN() || floorDbfs < -85 || floorDbfs > -15)
                return dbToLin(DEFAULT_GATE_OPEN_DBFS) to dbToLin(DEFAULT_GATE_CLOSE_DBFS)
            var openDb = (floorDbfs + 10.0).coerceIn(-55.0, -28.0)
            var closeDb = (floorDbfs + 4.0).coerceIn(-65.0, -38.0)
            if (closeDb > openDb - 4) closeDb = openDb - 6
            return dbToLin(openDb) to dbToLin(closeDb)
        }

        fun dbToLin(db: Double): Double = Math.pow(10.0, db / 20.0)

        fun fft(re: DoubleArray, im: DoubleArray, invert: Boolean) {
            val n = re.size
            var i = 1
            var j = 0
            while (i < n) {
                var bit = n shr 1
                while (j and bit != 0) { j = j xor bit; bit = bit shr 1 }
                j = j xor bit
                if (i < j) {
                    val tr = re[i]; re[i] = re[j]; re[j] = tr
                    val ti = im[i]; im[i] = im[j]; im[j] = ti
                }
                i++
            }
            var len = 2
            while (len <= n) {
                val ang = 2 * PI / len * (if (invert) -1 else 1)
                val wlenRe = cos(ang)
                val wlenIm = sin(ang)
                var base = 0
                while (base < n) {
                    var wr = 1.0
                    var wi = 0.0
                    val half = len shr 1
                    for (k in 0 until half) {
                        val u = base + k
                        val v = u + half
                        val vr = re[v] * wr - im[v] * wi
                        val vi = re[v] * wi + im[v] * wr
                        re[v] = re[u] - vr
                        im[v] = im[u] - vi
                        re[u] += vr
                        im[u] += vi
                        val nwr = wr * wlenRe - wi * wlenIm
                        wi = wr * wlenIm + wi * wlenRe
                        wr = nwr
                    }
                    base += len
                }
                len = len shl 1
            }
            if (invert) for (k in 0 until n) { re[k] = re[k] / n; im[k] = im[k] / n }
        }
    }
}

/** Phone voice-chain state shown in the PC log. */
data class VoiceDiag(
    val floorDbfs: Double,
    val gated: Boolean,
    val agc: Boolean,
    val agcGainDb: Double,
    val expanderDb: Double,
    val floorKnown: Boolean,
)
