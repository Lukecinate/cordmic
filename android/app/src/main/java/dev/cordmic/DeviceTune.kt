package dev.cordmic

import kotlin.math.roundToInt

/** What a probe (or a unit test) observed about the capture hardware. */
data class DeviceProfile(
    val apiLevel: Int,
    val manufacturer: String = "",
    val model: String = "",
    val micCount: Int = 0,
    val hasFrontMic: Boolean = false,
    val directionApi: Boolean = false,
    val directionWorks: Boolean = false,
    val aaudioAvailable: Boolean = false,
    val aaudioExclusive: Boolean = false,
    val aaudioLowLatency: Boolean = false,
    val hasPlatformNs: Boolean = false,
    val hasPlatformAgc: Boolean = false,
    val hasPlatformAec: Boolean = false,
    val nativeSampleRate: Int = 48000,
    val framesPerBurst: Int = 0,
    val stereoCapture: Boolean = false,
    val noiseFloorDbfs: Double? = null,
    val speechLevelDbfs: Double? = null,
)

data class CaptureAdvice(
    val useAAudio: Boolean,
    val preferExclusive: Boolean,
    val inputPreset: Int,
    val frontFocus: Boolean,
    val noiseMode: NoiseMode,
    val usePlatformNs: Boolean,
    val suggestedGainDb: Int,
    val summary: String,
)

/**
 * Picks safe, low-latency defaults for a wide range of Android phones.
 * Values the user has already chosen are applied afterwards with [withOverrides].
 */
object DeviceTune {
    const val PRESET_VOICE_RECOGNITION = 6
    const val PRESET_UNPROCESSED = 9
    const val PRESET_VOICE_COMMUNICATION = 7

    fun advise(p: DeviceProfile): CaptureAdvice {
        val useAAudio = p.aaudioAvailable
        val exclusive = useAAudio && p.aaudioExclusive
        val preset = PRESET_VOICE_RECOGNITION
        val frontFocus = true
        val nc = NoiseMode.LIGHT
        val gain = suggestedGain(p.speechLevelDbfs, p.noiseFloorDbfs)
        val dirCode = if (p.directionWorks) 1 else 2
        val summary = formatDetected(
            p.model, useAAudio, exclusive, p.aaudioLowLatency, p.micCount, dirCode, frontFocus,
        )
        return CaptureAdvice(useAAudio, exclusive, preset, frontFocus, nc, p.hasPlatformNs, gain, summary)
    }

    fun suggestedGain(speechDbfs: Double?, floorDbfs: Double?): Int {
        if (speechDbfs != null && speechDbfs > -90)
            return (-24.0 - speechDbfs).roundToInt().coerceIn(0, 18)
        if (floorDbfs != null) {
            if (floorDbfs > -38) return 0
            if (floorDbfs > -46) return 3
        }
        return VoiceChain.DEFAULT_GAIN_DB
    }

    fun withOverrides(
        advice: CaptureAdvice,
        useAAudio: Boolean? = null,
        frontFocus: Boolean? = null,
        noiseMode: NoiseMode? = null,
        gainDb: Int? = null,
        inputPreset: Int? = null,
    ): CaptureAdvice {
        var preset = inputPreset ?: advice.inputPreset
        if (inputPreset == null && preset == PRESET_VOICE_COMMUNICATION)
            preset = PRESET_VOICE_RECOGNITION
        return advice.copy(
            useAAudio = useAAudio ?: advice.useAAudio,
            frontFocus = frontFocus ?: advice.frontFocus,
            noiseMode = noiseMode ?: advice.noiseMode,
            suggestedGainDb = gainDb ?: advice.suggestedGainDb,
            inputPreset = preset,
        )
    }

    fun formatDetected(
        model: String?,
        aaudio: Boolean,
        exclusive: Boolean,
        lowLatency: Boolean,
        mics: Int,
        dirCode: Int,
        frontFocus: Boolean,
    ): String {
        val path = when {
            !aaudio -> "AudioRecord"
            exclusive -> "AAudio exclusive"
            lowLatency -> "AAudio low-latency"
            else -> "AAudio"
        }
        val dir = when {
            dirCode == 1 -> "front focus"
            frontFocus -> "front focus unsupported"
            else -> "omni"
        }
        val mic = if (mics > 0) ", $mics mic${if (mics == 1) "" else "s"}" else ""
        val who = model?.trim().orEmpty().let { if (it.isEmpty()) "" else "$it · " }
        return "Detected: $who$path$mic, $dir"
    }
}
