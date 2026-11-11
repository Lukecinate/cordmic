package dev.cordmic

import android.annotation.SuppressLint
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MicrophoneDirection
import android.os.Build
import android.util.Log

/** Mono 16-bit microphone source. read() blocks until `frames` frames are written at dst[off]. */
interface CaptureEngine {
    val name: String
    fun open(): Boolean
    fun read(dst: ByteArray, off: Int, frames: Int): Int
    fun info(): String = stats().toString()
    /** Values reported by the audio stack. Fields the engine does not report are null. Call from the capture thread. */
    fun stats(): EngineStats
    /** Audio-effect session id, or 0 if the engine does not expose one. */
    fun sessionId(): Int = 0
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
    val dirCode: Int? = null,
    val micCount: Int? = null,
) {
    override fun toString() = buildString {
        append("$engine ${sampleRate}Hz")
        framesPerBurst?.let { append(" burst=$it") }
        lowLatency?.let { append(if (it) " LOW_LATENCY" else " NORMAL") }
        exclusive?.let { append(if (it) " EXCLUSIVE" else " SHARED") }
        bufferFrames?.let { append(" buf=$it") }
        xruns?.let { append(" xrun=$it") }
        dirCode?.let { append(when (it) { 1 -> " dir=front"; 2 -> " dir=unsupported"; else -> " dir=off" }) }
        micCount?.let { if (it > 0) append(" mics=$it") }
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
        // Exclusive low-latency, then shared low-latency, then shared with no performance hint.
        val tries = listOf(true to PERF_LOW_LATENCY, false to PERF_LOW_LATENCY, false to PERF_NONE)
        for ((ex, perf) in tries) {
            val r = nativeOpen(rate, 1, preset, ex, perf)
            if (r == 0) { exclusive = ex; return true }
        }
        Log.w(TAG, "AAudio open failed")
        return false
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
        val i = nativeInfo() // [rate, burst, perfMode, sharingMode, bufferSize, xruns]
        return EngineStats(name, i[0], framesPerBurst = i[1], lowLatency = i[2] == PERF_LOW_LATENCY,
            exclusive = i[3] == SHARING_EXCLUSIVE, bufferFrames = i[4], xruns = i[5])
    }

    override fun close() = nativeClose()

    companion object {
        private const val TAG = "CordMic"
        private const val PERF_NONE = 10 // AAUDIO_PERFORMANCE_MODE_NONE
        private const val PERF_LOW_LATENCY = 12 // AAUDIO_PERFORMANCE_MODE_LOW_LATENCY
        private const val SHARING_EXCLUSIVE = 0 // AAUDIO_SHARING_MODE_EXCLUSIVE
        init { System.loadLibrary("cordmic_native") }
        @JvmStatic external fun nativeOpen(rate: Int, ch: Int, preset: Int, exclusive: Boolean, performanceMode: Int): Int
        @JvmStatic external fun nativeRead(dst: ByteArray, off: Int, frames: Int, timeoutNs: Long): Int
        @JvmStatic external fun nativeInfo(): IntArray
        @JvmStatic external fun nativeClose()
        @JvmStatic external fun nativeSessionId(): Int
    }

    override fun sessionId(): Int = nativeSessionId()
}

/** Fallback: Java AudioRecord with a small buffer. Higher latency than AAudio on most devices. */
class AudioRecordCapture(private val rate: Int, private val source: Int, private val frontFocus: Boolean = true) : CaptureEngine {
    override val name = "AudioRecord"
    private var rec: AudioRecord? = null
    var dirCode: Int = 0; private set
    var capturedMics: Int = 0; private set

    @SuppressLint("MissingPermission") // checked by the Activity before the service starts
    override fun open(): Boolean {
        val min = AudioRecord.getMinBufferSize(rate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
        if (min <= 0) return false
        val r = build(min * 2) ?: return false
        if (r.state != AudioRecord.STATE_INITIALIZED) { r.release(); return false }
        r.startRecording()
        dirCode = applyFrontFocus(r, frontFocus)
        capturedMics = try {
            if (Build.VERSION.SDK_INT >= 28) r.activeMicrophones.size else 0
        } catch (_: Throwable) { 0 }
        rec = r
        return true
    }

    /** API 30+: privacy-sensitive capture (the Assistant hotword or another app cannot silence or take it). */
    @SuppressLint("MissingPermission")
    private fun build(bytes: Int): AudioRecord? = try {
        if (Build.VERSION.SDK_INT >= 30) {
            AudioRecord.Builder()
                .setAudioSource(source)
                .setAudioFormat(AudioFormat.Builder().setSampleRate(rate)
                    .setChannelMask(AudioFormat.CHANNEL_IN_MONO).setEncoding(AudioFormat.ENCODING_PCM_16BIT).build())
                .setBufferSizeInBytes(bytes)
                .setPrivacySensitive(true)
                .build()
        } else AudioRecord(source, rate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, bytes)
    } catch (t: Throwable) {
        Log.w("CordMic", "AudioRecord: $t"); null
    }

    override fun read(dst: ByteArray, off: Int, frames: Int): Int {
        val r = rec ?: return -1
        val n = r.read(dst, off, frames * 2, AudioRecord.READ_BLOCKING)
        return if (n < 0) n else n / 2
    }

    override fun stats() = EngineStats(name, rate, bufferFrames = rec?.bufferSizeInFrames, dirCode = dirCode,
        micCount = capturedMics.takeIf { it > 0 } ?: DeviceProbe.profile.micCount.takeIf { it > 0 })
    override fun sessionId(): Int = rec?.audioSessionId ?: 0

    override fun close() { rec?.run { stop(); release() }; rec = null }
}


/** Apply the Android 10+ front-facing mic preference. Never throws; returns the dir code (0 off, 1 front, 2 unsupported). */
internal fun applyFrontFocus(rec: AudioRecord, want: Boolean): Int =
    applyFrontFocusOps(Build.VERSION.SDK_INT, want, object : MicDirectionOps {
        override fun setDirectionTowardsUser() = rec.setPreferredMicrophoneDirection(MicrophoneDirection.MIC_DIRECTION_TOWARDS_USER)
        override fun setFieldDimensionNarrow() { rec.setPreferredMicrophoneFieldDimension(1.0f) }
        override fun activeMicrophoneCount() = rec.activeMicrophones.size
    }) { Log.i("CordMic", it) }
