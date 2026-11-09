package dev.cordmic

import android.annotation.SuppressLint
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.PowerManager

/** Foreground service (type=microphone) that keeps streaming with the screen off / app in background. */
class MicService : Service() {
    private var streamer: Streamer? = null
    private var wakeLock: PowerManager.WakeLock? = null
    private val handler = Handler(Looper.getMainLooper())
    private var lastKey = ""

    private val ticker = object : Runnable {
        override fun run() {
            val s = streamer ?: return
            val target = s.targetAddress?.let { "${it.address.hostAddress}:${it.port}" }
            val key = "${s.state}|$target"
            if (key != lastKey) {
                lastKey = key
                getSystemService(NotificationManager::class.java).notify(NOTIF_ID, buildNotification(s.state, target))
            }
            handler.postDelayed(this, 1000)
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            shutdown(); return START_NOT_STICKY
        }
        if (streamer != null) return START_STICKY
        createChannel()
        // Android 14 (API 34) requires the type; mic FGS must be started while the app is visible.
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            startForeground(NOTIF_ID, buildNotification(Streamer.State.STARTING, null), ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE)
        } else {
            startForeground(NOTIF_ID, buildNotification(Streamer.State.STARTING, null))
        }
        wakeLock = getSystemService(PowerManager::class.java)
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "CordMic:stream").apply { acquire(12 * 60 * 60 * 1000L) } // max 12 h
        val cfg = Streamer.Config(
            manualHost = intent?.getStringExtra(EXTRA_HOST),
            port = intent?.getIntExtra(EXTRA_PORT, Protocol.DEFAULT_PORT) ?: Protocol.DEFAULT_PORT,
            preset = intent?.getIntExtra(EXTRA_PRESET, 6) ?: 6,
            useAAudio = intent?.getBooleanExtra(EXTRA_AAUDIO, true) ?: true,
            noiseCancellation = intent?.getBooleanExtra(EXTRA_NC, true) ?: true,
            gainDb = intent?.getIntExtra(EXTRA_GAIN, VoiceChain.DEFAULT_GAIN_DB) ?: VoiceChain.DEFAULT_GAIN_DB,
            noiseMode = NoiseMode.from(intent?.getIntExtra(EXTRA_NC_MODE, -1) ?: -1).let {
                if ((intent?.getIntExtra(EXTRA_NC_MODE, -1) ?: -1) < 0)
                    if (intent?.getBooleanExtra(EXTRA_NC, true) != false) NoiseMode.LIGHT else NoiseMode.OFF
                else it
            },
            voiceClarity = intent?.getBooleanExtra(EXTRA_CLARITY, true) ?: true,
            autoVolume = intent?.getBooleanExtra(EXTRA_AGC, true) ?: true,
            frontFocus = intent?.getBooleanExtra(EXTRA_FRONT_FOCUS, true) ?: true,
            frontFocusAr = intent?.getBooleanExtra(EXTRA_FRONT_FOCUS_AR, false) ?: false,
            autoOptimize = intent?.getBooleanExtra(EXTRA_AUTO_OPTIMIZE, true) ?: true,
            userSetGain = intent?.getBooleanExtra(EXTRA_USER_SET_GAIN, false) ?: false,
            upgradeGainNudge = intent?.getBooleanExtra(EXTRA_UPGRADE_GAIN_NUDGE, false) ?: false,
        )
        streamer = Streamer(applicationContext, cfg).also { current = it; it.start() }
        handler.post(ticker)
        // Not sticky: Android 14 forbids restarting a microphone FGS from the background.
        return START_NOT_STICKY
    }

    private fun shutdown() {
        handler.removeCallbacks(ticker)
        streamer?.stop(); streamer = null; current = null
        wakeLock?.let { if (it.isHeld) it.release() }; wakeLock = null
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    override fun onDestroy() {
        if (streamer != null) shutdown()
        super.onDestroy()
    }

    private fun createChannel() {
        val ch = NotificationChannel(CHANNEL, getString(R.string.notif_channel), NotificationManager.IMPORTANCE_LOW)
        getSystemService(NotificationManager::class.java).createNotificationChannel(ch)
    }

    private fun buildNotification(state: Streamer.State, target: String?): Notification {
        val phase = Phase.of(state)
        val text = when (state) {
            Streamer.State.WAITING_TETHER -> getString(R.string.notif_waiting_text)
            Streamer.State.SEARCHING -> getString(R.string.notif_searching_text)
            Streamer.State.STREAMING -> getString(R.string.notif_streaming_text, target ?: getString(R.string.value_none))
            Streamer.State.MIC_FAILED -> getString(R.string.notif_mic_failed_text)
            else -> getString(R.string.state_starting_msg)
        }
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val stop = PendingIntent.getService(this, 1, Intent(this, MicService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_IMMUTABLE)
        val b = Notification.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(getString(UiCopy.title(phase)))
            .setContentText(text)
            .setOngoing(true)
            .setContentIntent(open)
            .addAction(Notification.Action.Builder(null, getString(R.string.stop), stop).build())
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            b.setForegroundServiceBehavior(Notification.FOREGROUND_SERVICE_IMMEDIATE)
        }
        return b.build()
    }

    companion object {
        const val ACTION_STOP = "dev.cordmic.STOP"
        const val EXTRA_HOST = "host"
        const val EXTRA_PORT = "port"
        const val EXTRA_PRESET = "preset"
        const val EXTRA_AAUDIO = "aaudio"
        const val EXTRA_NC = "nc"
        const val EXTRA_GAIN = "gain"
        const val EXTRA_NC_MODE = "ncMode"
        const val EXTRA_CLARITY = "clarity"
        const val EXTRA_AGC = "agc"
        const val EXTRA_FRONT_FOCUS = "frontFocus"
        const val EXTRA_FRONT_FOCUS_AR = "frontFocusAr"
        const val EXTRA_AUTO_OPTIMIZE = "autoOptimize"
        const val EXTRA_USER_SET_GAIN = "userSetGain"
        const val EXTRA_UPGRADE_GAIN_NUDGE = "upgradeGainNudge"
        private const val CHANNEL = "cordmic"
        private const val NOTIF_ID = 1
        // Streamer holds only the application context, which cannot leak an Activity.
        @SuppressLint("StaticFieldLeak")
        @Volatile var current: Streamer? = null

        fun stop(ctx: Context) = ctx.startService(Intent(ctx, MicService::class.java).setAction(ACTION_STOP))
    }
}
