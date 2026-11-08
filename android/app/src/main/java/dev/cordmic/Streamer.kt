package dev.cordmic

import android.content.Context
import android.os.Build
import android.os.Process
import android.os.SystemClock
import android.util.Log
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.SocketTimeoutException
import java.util.concurrent.atomic.AtomicInteger

/** Captures 5 ms frames and sends them over UDP to the PC on the USB-tethering link. */
class Streamer(context: Context, private val cfg: Config) {
    // Only the application context is kept.
    private val ctx: Context = context.applicationContext

    data class Config(
        val manualHost: String?,   // null/blank = auto discovery on the tethering subnet
        val port: Int = Protocol.DEFAULT_PORT,
        val preset: Int = 6,       // 6 VOICE_RECOGNITION, 7 VOICE_COMMUNICATION, 9 UNPROCESSED
        val useAAudio: Boolean = true,
        val sampleRate: Int = 48000,
        val frames: Int = 240,     // 5 ms @ 48 kHz
        val noiseCancellation: Boolean = true,
        val gainDb: Int = VoiceChain.DEFAULT_GAIN_DB,
        val noiseMode: NoiseMode = if (noiseCancellation) NoiseMode.LIGHT else NoiseMode.OFF,
        val voiceClarity: Boolean = true,
        val autoVolume: Boolean = true,
        val frontFocus: Boolean = true,
        val autoOptimize: Boolean = true,
        val userSetGain: Boolean = false,
    )

    @Volatile private var running = false
    @Volatile private var sock: DatagramSocket? = null
    @Volatile private var target: InetSocketAddress? = null
    @Volatile private var lastHeard = 0L
    /** Connection state shown by the app and the notification. */
    enum class State { STARTING, MIC_FAILED, WAITING_TETHER, SEARCHING, STREAMING, STOPPED }

    @Volatile var state: State = State.STARTING; private set
    /** PC address while [State.STREAMING]. */
    @Volatile var targetAddress: InetSocketAddress? = null; private set
    /** Name of the open capture engine ("AAudio" / "AudioRecord"), empty before the mic opens. */
    @Volatile var engineName: String = ""; private set
    /** Refreshed about once per second from the capture thread. */
    @Volatile var engineStats: EngineStats? = null; private set
    val engineInfo: String get() = engineStats?.toString().orEmpty()
    /** elapsedRealtime of the last HELLO/PING from the PC, 0 = never. */
    val lastHeardAt: Long get() = lastHeard
    private val peak = AtomicInteger(0)
    private val voice = VoiceChain(cfg.sampleRate)
    /** Live voice-chain controls. The PC can push a new value on PING; the phone UI writes these too. */
    @Volatile var noiseMode: NoiseMode = cfg.noiseMode
    var noiseCancellation: Boolean
        get() = noiseMode != NoiseMode.OFF
        set(value) { noiseMode = if (value) (if (noiseMode == NoiseMode.OFF) NoiseMode.LIGHT else noiseMode) else NoiseMode.OFF }
    @Volatile var voiceClarity: Boolean = cfg.voiceClarity
    @Volatile var agc: Boolean = cfg.autoVolume
    @Volatile var gainDb: Int = cfg.gainDb.coerceIn(0, VoiceChain.MAX_GAIN_DB)
    @Volatile var frontFocus: Boolean = cfg.frontFocus
    @Volatile var dirCode: Int = if (cfg.frontFocus) 2 else 0
        private set
    @Volatile private var ctrlSeq = -1
    @Volatile private var reopenEngine = false
    val detectedSummary: String get() = DeviceProbe.summary
    private val platformNs = PlatformNs()
    val noiseLatencyMs: Double get() = voice.noiseLatencyMs
    @Volatile var sent = 0L; private set
    @Volatile var sendErrors = 0L; private set
    private var audioThread: Thread? = null
    private var ctlThread: Thread? = null

    fun start() {
        if (running) return
        running = true
        DeviceProbe.ensureStarted(ctx)
        DeviceProbe.refresh(ctx, tryOpen = false, reason = "session")
        DeviceProbe.markCaptureActive(true)
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
        state = State.STOPPED; targetAddress = null
        DeviceProbe.markCaptureActive(false)
    }

    /** Highest absolute PCM16 sample (0..32768) captured since the previous call. */
    fun takePeak(): Int = peak.getAndSet(0)

    fun applyFrontFocus(on: Boolean) {
        if (frontFocus == on) return
        frontFocus = on
        reopenEngine = true
    }

    private fun openEngine(): CaptureEngine? {
        if (cfg.useAAudio) {
            val e = AAudioCapture(cfg.sampleRate, cfg.preset)
            if (e.open()) {
                dirCode = if (frontFocus) 2 else 0
                return e
            }
        }
        val e = AudioRecordCapture(cfg.sampleRate, cfg.preset, frontFocus)
        if (!e.open()) return null
        dirCode = e.dirCode
        return e
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
        var flags = Protocol.flags(1, 0)
        var engine: CaptureEngine? = null
        var seq = 0
        var lastDiscover = 0L
        var lastSockTry = 0L
        var lastStats = 0L
        var measureFrames = 0
        var measureSumSq = 0.0
        var speechFrames = 0
        var speechSumSq = 0.0
        var measured = false
        val measureNeed = cfg.sampleRate * 2
        while (running) {
            if (reopenEngine && engine != null) {
                Log.i(TAG, "reopening capture for updated mic direction")
                engine.close(); engine = null; reopenEngine = false
            }
            if (engine == null) {
                engine = openEngine()
                if (engine == null) { state = State.MIC_FAILED; SystemClock.sleep(1000); continue }
                engineName = engine.name
                engineStats = engine.stats().copy(dirCode = dirCode, micCount = DeviceProbe.profile.micCount)
                DeviceProbe.noteEngine(engineStats!!, directionWorks = dirCode == 1)
                Log.i(TAG, engineInfo + " " + DeviceProbe.capabilityLog())
            }
            val n = engine.read(buf, Protocol.HEADER_SIZE, cfg.frames)
            if (n <= 0) { // error / disconnected (e.g. route change): reopen
                Log.w(TAG, "read returned $n, reopening ${engine.name}")
                engine.close(); engine = null; continue
            }
            if (!measured) {
                val (sumSq, peak) = DeviceProbe.measureBlock(buf, Protocol.HEADER_SIZE, n)
                measureSumSq += sumSq
                measureFrames += n
                if (peak > 1000) { speechSumSq += sumSq; speechFrames += n }
                if (measureFrames >= measureNeed) {
                    measured = true
                    val floor = DeviceProbe.rmsDbfs(measureSumSq, measureFrames)
                    val speech = if (speechFrames > cfg.sampleRate / 5)
                        DeviceProbe.rmsDbfs(speechSumSq, speechFrames) else null
                    DeviceProbe.noteLevels(floor, speech)
                    if (cfg.autoOptimize && !cfg.userSetGain && ctrlSeq < 0) {
                        val g = DeviceTune.suggestedGain(speech, floor)
                        gainDb = g
                        Log.i(TAG, "auto gain -> +${g}dB from floor=${"%.1f".format(floor)} speech=${speech?.let { "%.1f".format(it) } ?: "-"}")
                    }
                }
            }
            val wantNs = noiseMode != NoiseMode.OFF
            platformNs.apply(engine.sessionId(), wantNs)
            // Light prefers the platform suppressor (no extra delay). Strong always runs the OLA STFT.
            voice.mode = when {
                noiseMode == NoiseMode.STRONG -> NoiseMode.STRONG
                noiseMode == NoiseMode.LIGHT && platformNs.active -> NoiseMode.OFF
                else -> noiseMode
            }
            voice.voiceClarity = voiceClarity
            voice.agc = agc
            voice.gainDb = gainDb
            voice.process(buf, Protocol.HEADER_SIZE, n)
            // Level meter after gain, so the bar matches what the PC receives.
            val p = pcm16Peak(buf, Protocol.HEADER_SIZE, n)
            peak.accumulateAndGet(p) { a, b -> if (a > b) a else b }
            val now = SystemClock.elapsedRealtime()
            if (now - lastStats > 1000) { lastStats = now; engineStats = engine.stats() }
            val s = sock ?: if (now - lastSockTry > 1000) { lastSockTry = now; ensureSocket() } else null
            if (s == null) { state = State.WAITING_TETHER; targetAddress = null; continue }
            val auto = cfg.manualHost.isNullOrBlank()
            if (auto && target != null && now - lastHeard > 3000) { target = null; Log.i(TAG, "PC timeout, rediscover") }
            val t = target
            if (t == null) {
                state = State.SEARCHING; targetAddress = null
                if (now - lastDiscover > 500) { lastDiscover = now; sendDiscover(s) }
                continue
            }
            flags = Protocol.flags(1, 0, noiseCancellation, agc = agc, aaudio = engine.name == "AAudio")
            Protocol.writeHeader(buf, Protocol.T_AUDIO, seq++, nowUs(), n, flags)
            pkt.setData(buf, 0, Protocol.HEADER_SIZE + n * 2)
            pkt.socketAddress = t
            try {
                s.send(pkt); sent++
                targetAddress = t; state = State.STREAMING
            } catch (e: Exception) { // cable unplugged / tethering off: rebuild socket
                sendErrors++
                Log.w(TAG, "send: $e")
                s.close(); sock = null
                if (auto) target = null
            }
        }
        platformNs.release()
        engine?.close()
    }

    /** Handles HELLO (discovery reply) and PING (RTT) from the PC. */
    private fun controlLoop() {
        // PING -> PONG latency is part of the RTT the PC shows; at default priority this thread could wait
        // behind other apps while the phone is busy. Audio priority keeps the reply prompt (it only sleeps in receive()).
        Process.setThreadPriority(Process.THREAD_PRIORITY_AUDIO)
        val rb = ByteArray(256)
        val rp = DatagramPacket(rb, rb.size)
        val tb = ByteArray(Protocol.HEADER_SIZE + 64)
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
                        Protocol.readPingControl(rb, rp.length)?.let { c ->
                            if (c.changeSeq != 0 && c.changeSeq != ctrlSeq) {
                                ctrlSeq = c.changeSeq
                                noiseMode = c.noiseMode
                                if (c.extended) {
                                    voiceClarity = c.voiceClarity
                                    agc = c.agc
                                    applyFrontFocus(c.frontFocus)
                                }
                                gainDb = c.gainDb
                            }
                        }
                        val st = engineStats
                        val n = Protocol.writePongStatus(
                            tb, Protocol.HEADER_SIZE,
                            noiseCancellation, agc = agc,
                            aaudio = engineName == "AAudio",
                            exclusive = st?.exclusive == true,
                            lowLatency = st?.lowLatency == true,
                            gainDb, Build.MODEL, noiseMode,
                            frontFocus = frontFocus,
                            dirCode = dirCode,
                            micCount = DeviceProbe.profile.micCount,
                        )
                        Protocol.writeHeader(tb, Protocol.T_PONG, h.seq, h.tsUs, 0, 0)
                        s.send(DatagramPacket(tb, Protocol.HEADER_SIZE + n, rp.socketAddress))
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
