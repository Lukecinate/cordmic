package dev.cordmic

import android.content.Context
import android.media.AudioDeviceCallback
import android.media.AudioDeviceInfo
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioRecord
import android.media.MicrophoneInfo
import android.media.audiofx.AcousticEchoCanceler
import android.media.audiofx.AutomaticGainControl
import android.media.audiofx.NoiseSuppressor
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.util.Log
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.math.log10
import kotlin.math.max

/**
 * Background device capability probe. Runs at start-up, when the audio route
 * changes, and again when a capture session starts. No polling loop: the
 * [AudioDeviceCallback] plus an explicit refresh cover those moments.
 *
 * Every query is wrapped. A failure leaves the matching field at a safe default
 * and never throws into capture. Results stay on the device; only a short
 * capability summary (plus [android.os.Build.MODEL]) is sent to the PC over
 * the existing link.
 */
object DeviceProbe {
    private const val TAG = "CordMic"

    @Volatile var profile: DeviceProfile = DeviceProfile(apiLevel = Build.VERSION.SDK_INT)
        private set

    @Volatile var summary: String = DeviceTune.formatDetected(
        Build.MODEL, aaudio = true, exclusive = false, lowLatency = false,
        mics = 0, dirCode = 2, frontFocus = true,
    )
        private set

    private val started = AtomicBoolean(false)
    private val probing = AtomicBoolean(false)
    private val captureActive = AtomicBoolean(false)
    private var appCtx: Context? = null
    private var audioManager: AudioManager? = null

    private val callback = object : AudioDeviceCallback() {
        override fun onAudioDevicesAdded(addedDevices: Array<out AudioDeviceInfo>?) {
            refresh(appCtx ?: return, tryOpen = false, reason = "route-add")
        }
        override fun onAudioDevicesRemoved(removedDevices: Array<out AudioDeviceInfo>?) {
            refresh(appCtx ?: return, tryOpen = false, reason = "route-remove")
        }
    }

    fun ensureStarted(context: Context) {
        val app = context.applicationContext
        appCtx = app
        if (started.compareAndSet(false, true)) {
            try {
                audioManager = app.getSystemService(Context.AUDIO_SERVICE) as? AudioManager
                audioManager?.registerAudioDeviceCallback(callback, Handler(Looper.getMainLooper()))
            } catch (t: Throwable) {
                Log.w(TAG, "probe callback: $t")
            }
        }
        refresh(app, tryOpen = false, reason = "start")
    }

    fun markCaptureActive(active: Boolean) {
        captureActive.set(active)
    }

    fun refresh(context: Context, tryOpen: Boolean, reason: String = "manual") {
        if (!probing.compareAndSet(false, true)) return
        try {
            profile = probe(context.applicationContext, tryOpen && !captureActive.get())
            summary = DeviceTune.advise(profile).summary
            Log.i(TAG, "probe ($reason): ${capabilityLog()}")
        } catch (t: Throwable) {
            Log.w(TAG, "probe ($reason) failed: $t")
        } finally {
            probing.set(false)
        }
    }

    fun noteEngine(stats: EngineStats, directionWorks: Boolean) {
        val extra = liveMicCount()
        val cur = profile
        profile = cur.copy(
            aaudioAvailable = cur.aaudioAvailable || stats.engine == "AAudio",
            aaudioExclusive = stats.exclusive ?: cur.aaudioExclusive,
            aaudioLowLatency = stats.lowLatency ?: cur.aaudioLowLatency,
            nativeSampleRate = if (stats.sampleRate > 0) stats.sampleRate else cur.nativeSampleRate,
            framesPerBurst = stats.framesPerBurst ?: cur.framesPerBurst,
            directionWorks = directionWorks,
            micCount = maxOf(stats.micCount ?: 0, extra, cur.micCount),
        )
        summary = DeviceTune.formatDetected(
            profile.model, profile.aaudioAvailable && stats.engine == "AAudio",
            profile.aaudioExclusive, profile.aaudioLowLatency, profile.micCount,
            if (directionWorks) 1 else 2, frontFocus = true,
        )
    }

    fun noteLevels(floorDbfs: Double?, speechDbfs: Double?) {
        profile = profile.copy(noiseFloorDbfs = floorDbfs, speechLevelDbfs = speechDbfs)
    }

    /** Short local / PC-log line. Model name only; no serial, account or address. */
    fun capabilityLog(): String {
        val p = profile
        return buildString {
            append(summary)
            append(" api=${p.apiLevel}")
            append(" ns=${yn(p.hasPlatformNs)} agc=${yn(p.hasPlatformAgc)} aec=${yn(p.hasPlatformAec)}")
            append(" dir=${if (p.directionWorks) "front" else if (p.directionApi) "api" else "no"}")
            append(" rate=${p.nativeSampleRate} burst=${p.framesPerBurst}")
            append(" stereo=${yn(p.stereoCapture)}")
            p.noiseFloorDbfs?.let { append(" floor=${"%.1f".format(it)}") }
            p.speechLevelDbfs?.let { append(" speech=${"%.1f".format(it)}") }
        }
    }

    fun measureBlock(buf: ByteArray, off: Int, frames: Int): Pair<Double, Int> {
        var sumSq = 0.0
        var peak = 0
        var i = off
        val end = off + frames * 2
        while (i + 1 < end) {
            val v = (buf[i].toInt() and 0xFF) or (buf[i + 1].toInt() shl 8)
            val s = if (v >= 0x8000) v - 0x10000 else v
            sumSq += s.toDouble() * s
            val a = if (s < 0) -s else s
            if (a > peak) peak = a
            i += 2
        }
        return sumSq to peak
    }

    fun rmsDbfs(sumSq: Double, frames: Int): Double {
        if (frames <= 0) return -120.0
        val mean = max(sumSq / frames / (32768.0 * 32768.0), 1e-12)
        return 10.0 * log10(mean)
    }

    private fun probe(context: Context, tryOpen: Boolean): DeviceProfile {
        val api = Build.VERSION.SDK_INT
        val model = safe { Build.MODEL }.orEmpty()
        val manufacturer = safe { Build.MANUFACTURER }.orEmpty()
        val am = audioManager ?: safe { context.getSystemService(Context.AUDIO_SERVICE) as? AudioManager }

        val nativeRate = safe {
            am?.getProperty(AudioManager.PROPERTY_OUTPUT_SAMPLE_RATE)?.toIntOrNull()
        } ?: 48000
        val burst = safe {
            am?.getProperty(AudioManager.PROPERTY_OUTPUT_FRAMES_PER_BUFFER)?.toIntOrNull()
        } ?: 0

        var micCount = 0
        var hasFront = false
        if (api >= 28) {
            safe {
                val list = am?.microphones
                if (!list.isNullOrEmpty()) {
                    micCount = maxOf(micCount, list.size)
                    hasFront = list.any { looksFront(it) }
                }
            }
        }
        safe {
            val inputs = am?.getDevices(AudioManager.GET_DEVICES_INPUTS) ?: return@safe
            val onDevice = inputs.count { isOnDeviceMic(it) }
            val builtin = inputs.count { it.type == AudioDeviceInfo.TYPE_BUILTIN_MIC }
            micCount = maxOf(micCount, onDevice, builtin)
            if (onDevice > 0 || builtin > 0) hasFront = true
        }

        val hasNs = safe { NoiseSuppressor.isAvailable() } == true
        val hasAgc = safe { AutomaticGainControl.isAvailable() } == true
        val hasAec = safe { AcousticEchoCanceler.isAvailable() } == true
        val directionApi = api >= 29

        var stereo = false
        safe {
            val min = AudioRecord.getMinBufferSize(
                nativeRate.coerceAtLeast(8000),
                AudioFormat.CHANNEL_IN_STEREO,
                AudioFormat.ENCODING_PCM_16BIT,
            )
            stereo = min > 0
        }

        var aaudio = api >= 26
        var exclusive = false
        var lowLatency = false
        var openedBurst = burst
        if (tryOpen && aaudio) {
            safe {
                val e = AAudioCapture(nativeRate.coerceAtLeast(8000), DeviceTune.PRESET_VOICE_RECOGNITION)
                if (e.open()) {
                    val st = e.stats()
                    exclusive = st.exclusive == true
                    lowLatency = st.lowLatency == true
                    if (st.framesPerBurst != null) openedBurst = st.framesPerBurst
                    aaudio = true
                    e.close()
                }
            }
        }

        return DeviceProfile(
            apiLevel = api,
            manufacturer = manufacturer,
            model = model,
            micCount = micCount,
            hasFrontMic = hasFront,
            directionApi = directionApi,
            directionWorks = false,
            aaudioAvailable = aaudio,
            aaudioExclusive = exclusive,
            aaudioLowLatency = lowLatency,
            hasPlatformNs = hasNs,
            hasPlatformAgc = hasAgc,
            hasPlatformAec = hasAec,
            nativeSampleRate = nativeRate.coerceAtLeast(8000),
            framesPerBurst = openedBurst,
            stereoCapture = stereo,
            noiseFloorDbfs = profile.noiseFloorDbfs,
            speechLevelDbfs = profile.speechLevelDbfs,
        )
    }

    private fun liveMicCount(): Int {
        val am = audioManager ?: return 0
        val api = Build.VERSION.SDK_INT
        var n = 0
        if (api >= 28) safe { am.microphones?.size }?.let { n = maxOf(n, it) }
        safe {
            val inputs = am.getDevices(AudioManager.GET_DEVICES_INPUTS)
            n = maxOf(n, inputs.count { isOnDeviceMic(it) }, inputs.count { it.type == AudioDeviceInfo.TYPE_BUILTIN_MIC })
        }
        return n
    }

    private fun isOnDeviceMic(info: AudioDeviceInfo): Boolean {
        if (!info.isSource) return false
        return when (info.type) {
            AudioDeviceInfo.TYPE_BUILTIN_MIC -> true
            AudioDeviceInfo.TYPE_TELEPHONY,
            AudioDeviceInfo.TYPE_REMOTE_SUBMIX,
            AudioDeviceInfo.TYPE_FM_TUNER,
            AudioDeviceInfo.TYPE_WIRED_HEADSET,
            AudioDeviceInfo.TYPE_USB_DEVICE,
            AudioDeviceInfo.TYPE_USB_HEADSET,
            AudioDeviceInfo.TYPE_USB_ACCESSORY,
            AudioDeviceInfo.TYPE_BLUETOOTH_SCO,
            AudioDeviceInfo.TYPE_BLE_HEADSET,
            AudioDeviceInfo.TYPE_HDMI,
            AudioDeviceInfo.TYPE_LINE_ANALOG,
            AudioDeviceInfo.TYPE_LINE_DIGITAL -> false
            else -> {
                val name = info.productName?.toString().orEmpty()
                val addr = if (Build.VERSION.SDK_INT >= 28) info.address.orEmpty() else ""
                name.contains("mic", ignoreCase = true) ||
                    addr.contains("bottom", ignoreCase = true) ||
                    addr.contains("top", ignoreCase = true) ||
                    addr.contains("back", ignoreCase = true) ||
                    addr.contains("front", ignoreCase = true)
            }
        }
    }

    private fun looksFront(info: MicrophoneInfo): Boolean = try {
        val loc = info.location
        loc == MicrophoneInfo.LOCATION_MAINBODY || loc == MicrophoneInfo.LOCATION_MAINBODY_MOVABLE
    } catch (_: Throwable) {
        true
    }

    private fun yn(v: Boolean) = if (v) "1" else "0"

    private inline fun <T> safe(block: () -> T): T? = try {
        block()
    } catch (t: Throwable) {
        Log.w(TAG, "probe field: $t")
        null
    }
}
