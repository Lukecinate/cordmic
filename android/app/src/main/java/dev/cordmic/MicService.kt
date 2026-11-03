package dev.cordmic

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
    private var lastText = ""

    private val ticker = object : Runnable {
        override fun run() {
            val s = streamer ?: return
            if (s.status != lastText) {
                lastText = s.status
                getSystemService(NotificationManager::class.java).notify(NOTIF_ID, buildNotification(s.status))
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
            startForeground(NOTIF_ID, buildNotification("Memulai…"), ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE)
        } else {
            startForeground(NOTIF_ID, buildNotification("Memulai…"))
        }
        wakeLock = getSystemService(PowerManager::class.java)
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "CordMic:stream").apply { acquire(12 * 60 * 60 * 1000L) } // max 12 h
        val cfg = Streamer.Config(
            manualHost = intent?.getStringExtra(EXTRA_HOST),
            port = intent?.getIntExtra(EXTRA_PORT, Protocol.DEFAULT_PORT) ?: Protocol.DEFAULT_PORT,
            preset = intent?.getIntExtra(EXTRA_PRESET, 6) ?: 6,
            useAAudio = intent?.getBooleanExtra(EXTRA_AAUDIO, true) ?: true,
        )
        streamer = Streamer(cfg).also { current = it; it.start() }
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
        val ch = NotificationChannel(CHANNEL, "CordMic streaming", NotificationManager.IMPORTANCE_LOW)
        getSystemService(NotificationManager::class.java).createNotificationChannel(ch)
    }

    private fun buildNotification(text: String): Notification {
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val stop = PendingIntent.getService(this, 1, Intent(this, MicService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_IMMUTABLE)
        val b = Notification.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_mic)
            .setContentTitle("CordMic aktif")
            .setContentText(text)
            .setOngoing(true)
            .setContentIntent(open)
            .addAction(Notification.Action.Builder(null, "Stop", stop).build())
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
        private const val CHANNEL = "cordmic"
        private const val NOTIF_ID = 1
        @Volatile var current: Streamer? = null

        fun stop(ctx: Context) = ctx.startService(Intent(ctx, MicService::class.java).setAction(ACTION_STOP))
    }
}
