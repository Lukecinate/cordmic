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
    fun info(): String
    fun close()
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

    override fun info(): String {
        val i = nativeInfo()
        val perf = if (i[2] == 12) "LOW_LATENCY" else "perf=${i[2]}"
        val share = if (i[3] == 0) "EXCLUSIVE" else "SHARED"
        return "AAudio ${i[0]}Hz burst=${i[1]} $perf $share cap=${i[4]} xrun=${i[5]}"
    }

    override fun close() = nativeClose()

    companion object {
        private const val TAG = "CordMic"
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

    override fun info() = "AudioRecord ${rate}Hz buf=${rec?.bufferSizeInFrames ?: 0}"

    override fun close() { rec?.run { stop(); release() }; rec = null }
}
