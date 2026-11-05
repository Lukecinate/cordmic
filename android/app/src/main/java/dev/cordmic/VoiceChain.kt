package dev.cordmic

import kotlin.math.PI
import kotlin.math.cos
import kotlin.math.exp
import kotlin.math.max
import kotlin.math.round
import kotlin.math.sin
import kotlin.math.sqrt
import kotlin.math.tanh

/**
 * Same chain as CordMic.Core.VoiceChain: high-pass, optional noise suppression, user gain, AGC, soft limiter.
 * Noise suppression delays audio by exactly [NOISE_DELAY_FRAMES] samples (5.33 ms at 48 kHz). Nothing else adds samples.
 * Total boost is capped at +24 dB.
 */
class VoiceChain(private val sampleRate: Int = 48000) {
    var noiseCancellation: Boolean = true
    var agc: Boolean = true
    var highPass: Boolean = true
    var gainDb: Int = DEFAULT_GAIN_DB
        set(value) { field = value.coerceIn(0, MAX_GAIN_DB) }

    val delayFrames: Int get() = if (noiseCancellation) NOISE_DELAY_FRAMES else 0
    val noiseLatencyMs: Double get() = delayFrames * 1000.0 / sampleRate

    private val b0: Double
    private val b1: Double
    private val b2: Double
    private val a1: Double
    private val a2: Double
    private var x1 = 0.0; private var x2 = 0.0; private var y1 = 0.0; private var y2 = 0.0
    private val envRelease: Double
    private val agcAttack: Double
    private val agcRelease: Double
    private val blockIn = DoubleArray(NOISE_FFT)
    private val blockOut = DoubleArray(NOISE_FFT)
    private val re = DoubleArray(NOISE_FFT)
    private val im = DoubleArray(NOISE_FFT)
    private val mag = DoubleArray(NOISE_FFT / 2 + 1)
    private val noise = DoubleArray(NOISE_FFT / 2 + 1)
    private val binGain = DoubleArray(NOISE_FFT / 2 + 1)
    private val tail = DoubleArray(FADE)
    private var fill = 0
    private var emit = 0
    private var haveBlock = false
    private var env = 0.0
    private var agcGain = 1.0
    private val shorts = ShortArray(4096)

    init {
        val w0 = 2 * PI * 80.0 / sampleRate
        val c = cos(w0)
        val alpha = sin(w0) / (2 * 0.7071067811865476)
        val a0 = 1 + alpha
        b0 = ((1 + c) / 2) / a0
        b1 = (-(1 + c)) / a0
        b2 = b0
        a1 = (-2 * c) / a0
        a2 = (1 - alpha) / a0
        envRelease = 1 - exp(-1.0 / (0.20 * sampleRate))
        agcAttack = 1 - exp(-1.0 / (0.008 * sampleRate))
        agcRelease = 1 - exp(-1.0 / (0.18 * sampleRate))
    }

    fun reset() {
        x1 = 0.0; x2 = 0.0; y1 = 0.0; y2 = 0.0
        fill = 0; emit = 0; haveBlock = false; env = 0.0; agcGain = 1.0
        blockIn.fill(0.0); blockOut.fill(0.0); noise.fill(0.0); tail.fill(0.0)
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

    fun process(samples: ShortArray, n: Int = samples.size) {
        val user = dbToLin(gainDb.toDouble())
        val maxAgc = dbToLin((MAX_GAIN_DB - gainDb).toDouble())
        val target = dbToLin(TARGET_DBFS)
        for (i in 0 until n) {
            var x = samples[i] / 32768.0
            if (highPass) x = highPassStep(x)
            x = if (noiseCancellation) noiseStep(x) else { fill = 0; emit = 0; haveBlock = false; x }
            var y = x * user
            if (agc) {
                val ay = kotlin.math.abs(y)
                env = if (ay > env) ay else env + (ay - env) * envRelease
                val want = (target / max(env, 1e-5)).coerceIn(1.0, maxAgc)
                agcGain += (want - agcGain) * (if (want > agcGain) agcAttack else agcRelease)
                y *= agcGain
            }
            y = softLimit(y)
            var s = round(y * 32768.0).toInt()
            if (s > 32767) s = 32767 else if (s < -32768) s = -32768
            samples[i] = s.toShort()
        }
    }

    private fun highPassStep(x: Double): Double {
        val y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2
        x2 = x1; x1 = x; y2 = y1; y1 = y
        return y
    }

    private fun noiseStep(x: Double): Double {
        val y = if (haveBlock) blockOut[emit] else 0.0
        blockIn[fill] = x
        fill++; emit++
        if (fill == NOISE_FFT) {
            suppressBlock()
            fill = 0; emit = 0; haveBlock = true
        }
        return y
    }

    private fun suppressBlock() {
        re.fill(0.0); im.fill(0.0)
        for (i in 0 until NOISE_FFT) re[i] = blockIn[i]
        fft(re, im, false)
        var maxBin = 0.0
        var sumBin = 0.0
        for (k in 0..NOISE_FFT / 2) {
            val m = sqrt(re[k] * re[k] + im[k] * im[k])
            mag[k] = m
            if (k > 0) { if (m > maxBin) maxBin = m; sumBin += m }
        }
        val mean = sumBin / (NOISE_FFT / 2)
        val noiseLike = maxBin < mean * 12.0 + 1e-12
        for (k in 0..NOISE_FFT / 2) {
            val m = mag[k]
            var nse = noise[k]
            nse = if (noiseLike) 0.75 * nse + 0.25 * m
            else if (m < nse) 0.50 * nse + 0.50 * m
            else 0.998 * nse + 0.002 * m
            noise[k] = nse
            var g = 1.0 - 1.5 * nse / (m + 1e-12)
            if (g < GAIN_FLOOR) g = GAIN_FLOOR else if (g > 1) g = 1.0
            binGain[k] = g
        }
        for (k in 0 until NOISE_FFT) {
            val bin = if (k <= NOISE_FFT / 2) k else NOISE_FFT - k
            re[k] *= binGain[bin]
            im[k] *= binGain[bin]
        }
        fft(re, im, true)
        for (i in 0 until FADE) {
            val w = (i + 1) / (FADE + 1.0)
            blockOut[i] = tail[i] * (1 - w) + re[i] * w
        }
        for (i in FADE until NOISE_FFT) blockOut[i] = re[i]
        for (i in 0 until FADE) tail[i] = blockOut[NOISE_FFT - FADE + i]
    }

    companion object {
        const val NOISE_FFT = 256
        const val NOISE_DELAY_FRAMES = 256
        const val MAX_GAIN_DB = 24
        const val DEFAULT_GAIN_DB = 12
        const val TARGET_DBFS = -14.0
        private const val FADE = 16
        private const val GAIN_FLOOR = 0.20

        fun dbToLin(db: Double) = Math.pow(10.0, db / 20.0)

        fun softLimit(x: Double): Double {
            val a = kotlin.math.abs(x)
            val knee = 0.70
            val ceiling = 0.94
            if (a <= knee) return x
            val over = (a - knee) / (1.0 - knee)
            val y = knee + (ceiling - knee) * tanh(over)
            return Math.copySign(y, x)
        }

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
            if (invert) for (k in 0 until n) { re[k] /= n; im[k] /= n }
        }
    }
}
