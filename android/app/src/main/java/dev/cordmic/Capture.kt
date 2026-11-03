package dev.cordmic

import android.annotation.SuppressLint
import android.media.AudioFormat
import android.media.AudioRecord
import android.util.Log

/** Mono 16-bit microphone source. read() blocks until `frames` frames are written at dst[off]. */
interface CaptureEngine {
    val name: String
    fun open(): Boolean
    fun read(dst: ByteArray, off: Int, frames: Int): Int
    fun info(): String = stats().toString()
    /** Values reported by the audio stack. Fields the engine does not report are null. Call from the capture thread. */
    fun stats(): EngineStats
    fun close()
}

/** Diagnostics of an open capture stream, as reported by AAudio / AudioRecord (never estimated). */
data class EngineStats(
    val engine: String,
    val sampleRate: Int,
    val framesPerBurst: Int? = null,
    val lowLatency: Boolean? = null,
    val exclusive: Boolean? = null,
    val bufferFrames: Int? = null,
    val xruns: Int? = null,
) {
    override fun toString() = buildString {
        append("$engine ${sampleRate}Hz")
        framesPerBurst?.let { append(" burst=$it") }
        lowLatency?.let { append(if (it) " LOW_LATENCY" else " NORMAL") }
        exclusive?.let { append(if (it) " EXCLUSIVE" else " SHARED") }
        bufferFrames?.let { append(" buf=$it") }
        xruns?.let { append(" xrun=$it") }
    }
}

/** Peak of little-endian PCM16 samples in [off, off + frames*2), 0..32768. */
fun pcm16Peak(buf: ByteArray, off: Int, frames: Int): Int {
    var peak = 0
    var i = off
    val end = off + frames * 2
    while (i + 1 < end) {
        val v = (buf[i].toInt() and 0xFF) or (buf[i + 1].toInt() shl 8)
        val a = if (v < 0) -v else v
        if (a > peak) peak = a
        i += 2
    }
    return peak
}

/** AAudio (native) low-latency input: PERFORMANCE_MODE_LOW_LATENCY, exclusive if possible. */
class AAudioCapture(private val rate: Int, private val preset: Int) : CaptureEngine {
    override val name = "AAudio"
    private var exclusive = true

    override fun open(): Boolean {
        var r = nativeOpen(rate, 1, preset, true)
        if (r != 0) { exclusive = false; r = nativeOpen(rate, 1, preset, false) }
        if (r != 0) Log.w(TAG, "AAudio open failed: $r")
        return r == 0
    }

    override fun read(dst: ByteArray, off: Int, frames: Int): Int {
        var got = 0
        while (got < frames) {
            val n = nativeRead(dst, off + got * 2, frames - got, 200_000_000L)
            if (n < 0) return n
            if (n == 0) return got // timeout
            got += n
        }
        return got
    }

    override fun stats(): EngineStats {
        val i = nativeInfo() // [rate, burst, perfMode, sharingMode, capacity, xruns]
        return EngineStats(name, i[0], framesPerBurst = i[1], lowLatency = i[2] == PERF_LOW_LATENCY,
            exclusive = i[3] == SHARING_EXCLUSIVE, bufferFrames = i[4], xruns = i[5])
    }

    override fun close() = nativeClose()

    companion object {
        private const val TAG = "CordMic"
        private const val PERF_LOW_LATENCY = 12 // AAUDIO_PERFORMANCE_MODE_LOW_LATENCY
        private const val SHARING_EXCLUSIVE = 0 // AAUDIO_SHARING_MODE_EXCLUSIVE
        init { System.loadLibrary("cordmic_native") }
        @JvmStatic external fun nativeOpen(rate: Int, ch: Int, preset: Int, exclusive: Boolean): Int
        @JvmStatic external fun nativeRead(dst: ByteArray, off: Int, frames: Int, timeoutNs: Long): Int
        @JvmStatic external fun nativeInfo(): IntArray
        @JvmStatic external fun nativeClose()
    }
}

/** Fallback: Java AudioRecord with a small buffer. Higher latency than AAudio on most devices. */
class AudioRecordCapture(private val rate: Int, private val source: Int) : CaptureEngine {
    override val name = "AudioRecord"
    private var rec: AudioRecord? = null

    @SuppressLint("MissingPermission") // checked by the Activity before the service starts
    override fun open(): Boolean {
        val min = AudioRecord.getMinBufferSize(rate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
        if (min <= 0) return false
        val r = AudioRecord(source, rate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, min * 2)
        if (r.state != AudioRecord.STATE_INITIALIZED) { r.release(); return false }
        r.startRecording()
        rec = r
        return true
    }

    override fun read(dst: ByteArray, off: Int, frames: Int): Int {
        val r = rec ?: return -1
        val n = r.read(dst, off, frames * 2, AudioRecord.READ_BLOCKING)
        return if (n < 0) n else n / 2
    }

    override fun stats() = EngineStats(name, rate, bufferFrames = rec?.bufferSizeInFrames)

    override fun close() { rec?.run { stop(); release() }; rec = null }
}
