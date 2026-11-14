package dev.cordmic

import kotlin.math.log10
import kotlin.math.roundToInt

/** What the main screen shows. Built from the running [Streamer] (or from fixtures in screenshot tests). */
data class UiState(
    val phase: Phase,
    /** "192.168.42.10:50005" while streaming. */
    val target: String? = null,
    val engine: String? = null,
    /** Peak level in dBFS since the previous frame, null = no live audio. */
    val levelDb: Float? = null,
    val sent: Long = 0,
    val sendErrors: Long = 0,
    /** Milliseconds since the PC last sent HELLO/PING, null = not heard yet. */
    val lastReplyMs: Long? = null,
    /** "rndis0 · 192.168.42.129/24", null = USB tethering off. */
    val usb: String? = null,
    val stats: EngineStats? = null,
    /** 0.6.0: Android silenced the capture because another app uses the microphone. */
    val micSilenced: Boolean = false,
    /** 0.7.2: USB port state from the USB_STATE broadcast (charging only vs no cable). */
    val link: UsbLink = UsbLink.UNKNOWN,
) {
    val running: Boolean get() = phase.running
}

enum class Phase(val running: Boolean, val tone: Tone) {
    IDLE(false, Tone.NEUTRAL),
    PERMISSION_DENIED(false, Tone.ERROR),
    STARTING(true, Tone.NEUTRAL),
    WAITING_TETHER(true, Tone.WARNING),
    SEARCHING(true, Tone.WARNING),
    STREAMING(true, Tone.OK),
    MIC_FAILED(true, Tone.ERROR);

    companion object {
        fun of(s: Streamer.State): Phase = when (s) {
            Streamer.State.STARTING -> STARTING
            Streamer.State.MIC_FAILED -> MIC_FAILED
            Streamer.State.WAITING_TETHER -> WAITING_TETHER
            Streamer.State.SEARCHING -> SEARCHING
            Streamer.State.STREAMING -> STREAMING
            Streamer.State.STOPPED -> IDLE
        }
    }
}

enum class Tone { NEUTRAL, OK, WARNING, ERROR }

/** Next step offered under the status message. */
enum class HintAction { NONE, OPEN_TETHERING, OPEN_APP_SETTINGS }

object UiCopy {
    fun title(p: Phase): Int = when (p) {
        Phase.IDLE -> R.string.state_idle_title
        Phase.PERMISSION_DENIED -> R.string.state_permission_title
        Phase.STARTING -> R.string.state_starting_title
        Phase.WAITING_TETHER -> R.string.state_waiting_title
        Phase.SEARCHING -> R.string.state_searching_title
        Phase.STREAMING -> R.string.state_streaming_title
        Phase.MIC_FAILED -> R.string.state_mic_failed_title
    }

    /** Message resource; STREAMING takes (target, engine) arguments. */
    fun message(ui: UiState): Int = when (ui.phase) {
        Phase.IDLE -> if (ui.usb != null) R.string.state_idle_msg_tether_on else tetherMessage(ui.link, R.string.state_idle_msg_tether_off)
        Phase.PERMISSION_DENIED -> R.string.state_permission_msg
        Phase.STARTING -> R.string.state_starting_msg
        Phase.WAITING_TETHER -> tetherMessage(ui.link, R.string.state_waiting_msg)
        Phase.SEARCHING -> R.string.state_searching_msg
        Phase.STREAMING -> R.string.state_streaming_msg
        Phase.MIC_FAILED -> R.string.state_mic_failed_msg
    }

    /** 0.7.2: say why tethering is not up, when the USB broadcast tells us. */
    fun tetherMessage(link: UsbLink, fallback: Int): Int = when (link) {
        UsbLink.CHARGING_ONLY -> R.string.state_msg_charging_only
        UsbLink.NO_CABLE -> R.string.state_msg_no_cable
        UsbLink.TETHER_STARTING -> R.string.state_msg_tether_starting
        else -> fallback
    }

    fun hint(ui: UiState): HintAction = when {
        ui.phase == Phase.WAITING_TETHER -> HintAction.OPEN_TETHERING
        ui.phase == Phase.IDLE && ui.usb == null -> HintAction.OPEN_TETHERING
        ui.phase == Phase.PERMISSION_DENIED -> HintAction.OPEN_APP_SETTINGS
        else -> HintAction.NONE
    }

    fun icon(p: Phase): Int = when (p) {
        Phase.WAITING_TETHER -> R.drawable.ic_usb
        Phase.SEARCHING -> R.drawable.ic_search
        Phase.MIC_FAILED, Phase.PERMISSION_DENIED -> R.drawable.ic_error_outline
        else -> R.drawable.ic_mic
    }
}

object Level {
    const val FLOOR_DB = -60f

    /** PCM16 peak (0..32768) to dBFS, clamped to [FLOOR_DB, 0]. 0 -> FLOOR_DB. */
    fun peakToDb(peak: Int): Float {
        if (peak <= 0) return FLOOR_DB
        val db = 20f * log10(peak / 32768f)
        return db.coerceIn(FLOOR_DB, 0f)
    }

    /** Whole dB for display; null when at or below the floor (shown as "Silent"). */
    fun displayDb(db: Float): Int? = if (db <= FLOOR_DB) null else db.roundToInt()
}
