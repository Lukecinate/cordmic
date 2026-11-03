package dev.cordmic

import android.content.Context
import android.os.Process
import android.os.SystemClock
import android.util.Log
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.SocketTimeoutException

/** Captures 5 ms frames and sends them over UDP to the PC on the USB-tethering link. */
class Streamer(context: Context, private val cfg: Config) {
    // Only the application context is kept (used for localized status strings).
    private val ctx: Context = context.applicationContext

    data class Config(
        val manualHost: String?,   // null/blank = auto discovery on the tethering subnet
        val port: Int = Protocol.DEFAULT_PORT,
        val preset: Int = 6,       // 6 VOICE_RECOGNITION, 7 VOICE_COMMUNICATION, 9 UNPROCESSED
        val useAAudio: Boolean = true,
        val sampleRate: Int = 48000,
        val frames: Int = 240,     // 5 ms @ 48 kHz
    )

    @Volatile private var running = false
    @Volatile private var sock: DatagramSocket? = null
    @Volatile private var target: InetSocketAddress? = null
    @Volatile private var lastHeard = 0L
    @Volatile var status: String = ctx.getString(R.string.s_idle); private set
    @Volatile var engineInfo: String = ""; private set
    @Volatile var sent = 0L; private set
    @Volatile var sendErrors = 0L; private set
    private var audioThread: Thread? = null
    private var ctlThread: Thread? = null

    fun start() {
        if (running) return
        running = true
        cfg.manualHost?.takeIf { it.isNotBlank() }?.let {
            target = InetSocketAddress(InetAddress.getByName(it.trim()), cfg.port) // numeric IP: no DNS
        }
        audioThread = Thread(::audioLoop, "cordmic-audio").apply { start() }
        ctlThread = Thread(::controlLoop, "cordmic-ctl").apply { isDaemon = true; start() }
    }

    fun stop() {
        running = false
        audioThread?.join(1000)
        val s = sock; val t = target
        if (s != null && t != null) try {
            val b = ByteArray(Protocol.HEADER_SIZE)
            Protocol.writeHeader(b, Protocol.T_BYE, 0, 0, 0, 0)
            s.send(DatagramPacket(b, b.size, t))
        } catch (_: Exception) {}
        sock?.close(); sock = null
        ctlThread?.join(1000)
        status = ctx.getString(R.string.s_stopped)
    }

    private fun openEngine(): CaptureEngine? {
        if (cfg.useAAudio) {
            val e = AAudioCapture(cfg.sampleRate, cfg.preset)
            if (e.open()) return e
        }
        val e = AudioRecordCapture(cfg.sampleRate, cfg.preset)
        return if (e.open()) e else null
    }

    private fun ensureSocket(): DatagramSocket? {
        sock?.let { if (!it.isClosed) return it }
        val iface = TetherNet.find()
        if (iface == null && cfg.manualHost.isNullOrBlank()) return null
        return try {
            // Prefer the well-known port on the tether interface: the PC app (0.2+) then also finds us by
            // sending HELLO straight to <gateway>:port, even if Windows drops our broadcast DISCOVER.
            // Fall back to an ephemeral port if it is taken (e.g. a second instance).
            val s = if (iface != null) bindOrNull(InetSocketAddress(iface.local, cfg.port))
                ?: bindOrNull(InetSocketAddress(iface.local, 0))!!
            else DatagramSocket()
            s.broadcast = true
            s.trafficClass = 0xB8 // DSCP EF (expedited forwarding)
            s.soTimeout = 500
            s.sendBufferSize = 64 * 1024
            Log.i(TAG, "socket on ${iface?.name} ${s.localSocketAddress}")
            sock = s
            s
        } catch (e: Exception) {
            Log.w(TAG, "socket: $e"); null
        }
    }

    private fun bindOrNull(addr: InetSocketAddress): DatagramSocket? = try {
        DatagramSocket(null).apply { reuseAddress = true; bind(addr) }
    } catch (e: Exception) {
        Log.w(TAG, "bind $addr: $e"); null
    }

    private fun sendDiscover(s: DatagramSocket) {
        val b = ByteArray(Protocol.HEADER_SIZE)
        Protocol.writeHeader(b, Protocol.T_DISCOVER, 0, nowUs(), 0, 0)
        val iface = TetherNet.find()
        val dests = listOfNotNull(iface?.broadcast, InetAddress.getByName("255.255.255.255"))
        for (d in dests) try { s.send(DatagramPacket(b, b.size, d, cfg.port)) } catch (_: Exception) {}
    }

    private fun audioLoop() {
        Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_AUDIO)
        val buf = ByteArray(Protocol.HEADER_SIZE + cfg.frames * 2)
        val pkt = DatagramPacket(buf, buf.size)
        val flags = Protocol.flags(1, 0)
        var engine: CaptureEngine? = null
        var seq = 0
        var lastDiscover = 0L
        var lastSockTry = 0L
        while (running) {
            if (engine == null) {
                engine = openEngine()
                if (engine == null) { status = ctx.getString(R.string.s_mic_failed); SystemClock.sleep(1000); continue }
                engineInfo = engine.info()
                Log.i(TAG, engineInfo)
            }
            val n = engine.read(buf, Protocol.HEADER_SIZE, cfg.frames)
            if (n <= 0) { // error / disconnected (e.g. route change): reopen
                Log.w(TAG, "read returned $n, reopening ${engine.name}")
                engine.close(); engine = null; continue
            }
            val now = SystemClock.elapsedRealtime()
            val s = sock ?: if (now - lastSockTry > 1000) { lastSockTry = now; ensureSocket() } else null
            if (s == null) { status = ctx.getString(R.string.s_wait_tether, TetherNet.describe(ctx.getString(R.string.iface_none))); continue }
            val auto = cfg.manualHost.isNullOrBlank()
            if (auto && target != null && now - lastHeard > 3000) { target = null; Log.i(TAG, "PC timeout, rediscover") }
            val t = target
            if (t == null) {
                status = ctx.getString(R.string.s_searching, TetherNet.describe(ctx.getString(R.string.iface_none)))
                if (now - lastDiscover > 500) { lastDiscover = now; sendDiscover(s) }
                continue
            }
            Protocol.writeHeader(buf, Protocol.T_AUDIO, seq++, nowUs(), n, flags)
            pkt.setData(buf, 0, Protocol.HEADER_SIZE + n * 2)
            pkt.socketAddress = t
            try {
                s.send(pkt); sent++
                status = ctx.getString(R.string.s_streaming, t.address.hostAddress, t.port, engine.name)
            } catch (e: Exception) { // cable unplugged / tethering off: rebuild socket
                sendErrors++
                Log.w(TAG, "send: $e")
                s.close(); sock = null
                if (auto) target = null
            }
        }
        engine?.close()
    }

    /** Handles HELLO (discovery reply) and PING (RTT) from the PC. */
    private fun controlLoop() {
        val rb = ByteArray(256)
        val rp = DatagramPacket(rb, rb.size)
        val tb = ByteArray(Protocol.HEADER_SIZE)
        while (running) {
            val s = sock
            if (s == null || s.isClosed) { SystemClock.sleep(100); continue }
            try {
                rp.setData(rb, 0, rb.size)
                s.receive(rp)
                val h = Protocol.parse(rb, rp.length) ?: continue
                when (h.type) {
                    Protocol.T_HELLO -> {
                        lastHeard = SystemClock.elapsedRealtime()
                        if (cfg.manualHost.isNullOrBlank()) {
                            val from = InetSocketAddress(rp.address, cfg.port)
                            if (target != from) Log.i(TAG, "PC found at $from")
                            target = from
                        }
                    }
                    Protocol.T_PING -> {
                        lastHeard = SystemClock.elapsedRealtime()
                        Protocol.writeHeader(tb, Protocol.T_PONG, h.seq, h.tsUs, 0, 0)
                        s.send(DatagramPacket(tb, tb.size, rp.socketAddress))
                    }
                }
            } catch (_: SocketTimeoutException) {
            } catch (e: Exception) {
                if (running) SystemClock.sleep(50)
            }
        }
    }

    private fun nowUs() = SystemClock.elapsedRealtimeNanos() / 1000

    companion object { const val TAG = "CordMic" }
}
