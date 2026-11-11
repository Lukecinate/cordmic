package dev.cordmic

import android.content.Context
import android.media.AudioManager
import android.media.AudioRecordingConfiguration
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.util.Log

/**
 * 0.6.0 capture robustness helpers (spec §5). The decision logic is plain Kotlin so it can be unit-tested without a
 * device; the Android calls are thin and guarded.
 */

/** Microphone-direction calls, abstracted so a throwing or empty implementation can be tested (AND-3). */
internal interface MicDirectionOps {
    /** setPreferredMicrophoneDirection(TOWARDS_USER); returns the platform's result. */
    fun setDirectionTowardsUser(): Boolean
    /** setPreferredMicrophoneFieldDimension(1.0f). */
    fun setFieldDimensionNarrow()
    /** getActiveMicrophones().size; an empty list means "unknown". */
    fun activeMicrophoneCount(): Int
}

/**
 * Apply the Android 10+ front-facing mic preference through [ops]. Never throws: every call is wrapped, an exception
 * or an empty microphone list is treated as "unknown" and capture continues. Returns 0 off, 1 front, 2 unsupported.
 */
internal fun applyFrontFocusOps(sdk: Int, want: Boolean, ops: MicDirectionOps, log: (String) -> Unit = {}): Int {
    if (!want) return 0
    if (sdk < 29) return 2
    val ok = try { ops.setDirectionTowardsUser() } catch (t: Throwable) { log("mic direction: $t"); return 2 }
    try { ops.setFieldDimensionNarrow() } catch (t: Throwable) { log("mic field: $t") }
    try {
        val n = ops.activeMicrophoneCount()
        log(if (n > 0) "activeMicrophones=$n" else "activeMicrophones unknown")
    } catch (t: Throwable) { log("activeMicrophones: $t") }
    return if (ok) 1 else 2
}

/** What the voice chain runs for one block, given the user's settings and whether the PC asked for Studio. */
internal data class VoicePlan(val mode: NoiseMode, val expander: NoiseMode, val agc: Boolean, val platformNs: Boolean)

/**
 * With Studio (FLAG_STUDIO) the phone sends raw capture: no platform NS, no STFT suppressor, no gate/expander, no AGC,
 * so noise reduction is not stacked. User gain, voice clarity EQ and the limiter stay. The user's stored settings are
 * not changed and are still reported in PONG; they apply again as soon as the PC stops asking.
 */
internal fun voicePlan(noiseMode: NoiseMode, agc: Boolean, studio: Boolean, platformNsActive: Boolean): VoicePlan {
    if (studio) return VoicePlan(NoiseMode.OFF, NoiseMode.OFF, agc = false, platformNs = false)
    val mode = when {
        noiseMode == NoiseMode.STRONG -> NoiseMode.STRONG
        noiseMode == NoiseMode.LIGHT && platformNsActive -> NoiseMode.OFF
        else -> noiseMode
    }
    return VoicePlan(mode, noiseMode, agc, platformNs = noiseMode == NoiseMode.LIGHT)
}

/**
 * Hidden-NS check (spec §5.5, logged only): if the room tone falls by more than 8 dB within 2 s after the start while
 * no speech is present, the device probably applies its own noise suppression to VOICE_RECOGNITION. Feed one level
 * per block (dBFS) with a speech flag; blocks of 20 ms are assumed.
 */
internal class NsSuspectDetector(private val blockMs: Int = 20) {
    private var t = 0
    private var startSum = 0.0
    private var startN = 0
    private val recent = ArrayDeque<Double>()
    var suspected = false; private set
    var done = false; private set

    fun feed(levelDbfs: Double, speech: Boolean) {
        if (done) return
        t += blockMs
        if (t > 2000) { done = true; return }
        if (speech) { recent.clear(); return }
        if (t <= 200) { startSum += levelDbfs; startN++; return }
        recent.addLast(levelDbfs)
        if (recent.size > 200 / blockMs) recent.removeFirst()
        if (startN > 0 && recent.size == 200 / blockMs && recent.average() < startSum / startN - 8.0) {
            suspected = true; done = true
        }
    }
}

/**
 * Watches Android's recording configurations (API 29+) and reports when the system silences this app's capture
 * because another app took the microphone (AudioRecordingConfiguration.isClientSilenced). Register before
 * startRecording(). For a non-system app the list only contains its own recordings.
 */
class SilenceWatch(context: Context) {
    private val am = context.getSystemService(Context.AUDIO_SERVICE) as? AudioManager
    @Volatile var silenced: Boolean = false; private set
    private var cb: AudioManager.AudioRecordingCallback? = null

    fun start() {
        val m = am ?: return
        if (Build.VERSION.SDK_INT < 29) return
        val c = object : AudioManager.AudioRecordingCallback() {
            override fun onRecordingConfigChanged(configs: MutableList<AudioRecordingConfiguration>?) = update(configs)
        }
        try {
            m.registerAudioRecordingCallback(c, Handler(Looper.getMainLooper()))
            cb = c
            update(m.activeRecordingConfigurations)
        } catch (t: Throwable) { Log.w("CordMic", "recording callback: $t") }
    }

    private fun update(configs: List<AudioRecordingConfiguration>?) {
        if (Build.VERSION.SDK_INT < 29) return
        val now = try { configs.orEmpty().any { it.isClientSilenced } } catch (t: Throwable) { false }
        if (now != silenced) Log.i("CordMic", if (now) "capture silenced by another app" else "capture no longer silenced")
        silenced = now
    }

    fun stop() {
        val m = am ?: return
        cb?.let { try { m.unregisterAudioRecordingCallback(it) } catch (_: Throwable) {} }
        cb = null
        silenced = false
    }
}
