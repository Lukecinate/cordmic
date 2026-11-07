package dev.cordmic

import android.media.audiofx.NoiseSuppressor
import android.util.Log

/** Thin wrapper around the platform [NoiseSuppressor] so Light mode can use it when the session exists. */
class PlatformNs {
    private var ns: NoiseSuppressor? = null
    @Volatile var active: Boolean = false
        private set

    fun apply(sessionId: Int, want: Boolean) {
        if (!want) {
            release()
            return
        }
        if (active && ns != null) return
        if (sessionId <= 0 || !NoiseSuppressor.isAvailable()) {
            release()
            return
        }
        try {
            val created = NoiseSuppressor.create(sessionId)
            if (created == null) {
                release()
                return
            }
            created.enabled = true
            ns = created
            active = true
        } catch (e: Exception) {
            Log.w(TAG, "NoiseSuppressor unavailable: $e")
            release()
        }
    }

    fun release() {
        try { ns?.release() } catch (_: Exception) {}
        ns = null
        active = false
    }

    companion object { private const val TAG = "CordMic" }
}
