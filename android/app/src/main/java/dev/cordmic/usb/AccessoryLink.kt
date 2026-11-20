package dev.cordmic.usb

import android.os.SystemClock
import java.io.Closeable
import java.io.InputStream
import java.io.OutputStream
import java.util.ArrayDeque

/**
 * Accessory bulk link: reader and writer over streams from UsbManager.openAccessory (or a test double).
 * Writer queue is 8 frames; audio older than 40 ms is dropped. Keepalive every 250 ms.
 * On any I/O error the streams are closed and [onLinkLost] is run once.
 */
class AccessoryLink(
    private val input: InputStream,
    private val output: OutputStream,
    private val closeable: Closeable,
    private val onLinkLost: (String) -> Unit,
    private val onFrame: (UsbFrameCodec.Frame) -> Unit = {},
    private val nowMs: () -> Long = { SystemClock.elapsedRealtime() },
) : TransportSink {
    companion object {
        const val QUEUE_CAP = 8
        const val AUDIO_TTL_MS = 40L
        const val KEEPALIVE_MS = 250L
        const val JOIN_MS = 1000L
    }

    private data class Queued(val bytes: ByteArray, val audio: Boolean, val createdAt: Long)

    private val gate = Object()
    private val queue = ArrayDeque<Queued>(QUEUE_CAP)
    @Volatile private var open = true
    @Volatile private var lost = false
    @Volatile private var outSeq = 0
    private var droppedOld = 0
    private val decoder = UsbFrameDecoder()
    private val reader = Thread(::readLoop, "cordmic-aoa-read").apply { isDaemon = true }
    private val writer = Thread(::writeLoop, "cordmic-aoa-write").apply { isDaemon = true }

    val decoderStats: UsbFrameDecoder get() = decoder
    val droppedAudio: Int get() = droppedOld
    override val isOpen: Boolean get() = open

    fun start() {
        reader.start()
        writer.start()
    }

    override fun send(packet: ByteArray, off: Int, len: Int, createdAtElapsedMs: Long): Boolean {
        val framed = UsbFrameCodec.encode(UsbFrameCodec.TYPE_AUDIO, nextSeq(), packet, off, len) ?: return false
        return enqueue(Queued(framed, true, createdAtElapsedMs))
    }

    override fun sendBye() {
        val framed = UsbFrameCodec.encode(UsbFrameCodec.TYPE_BYE, nextSeq(), ByteArray(0)) ?: return
        enqueue(Queued(framed, false, nowMs()))
    }

    fun sendControl(payload: ByteArray): Boolean {
        val framed = UsbFrameCodec.encode(UsbFrameCodec.TYPE_CONTROL, nextSeq(), payload) ?: return false
        return enqueue(Queued(framed, false, nowMs()))
    }

    private fun nextSeq(): Int = synchronized(gate) { outSeq++ }

    private fun enqueue(item: Queued): Boolean {
        if (!open) return false
        if (item.audio && nowMs() - item.createdAt > AUDIO_TTL_MS) { droppedOld++; return false }
        synchronized(gate) {
            if (item.audio) {
                val now = nowMs()
                val it = queue.iterator()
                while (it.hasNext()) {
                    val q = it.next()
                    if (q.audio && now - q.createdAt > AUDIO_TTL_MS) { it.remove(); droppedOld++ }
                }
            }
            if (queue.size >= QUEUE_CAP) {
                val firstAudio = queue.firstOrNull { it.audio }
                if (firstAudio != null) { queue.remove(firstAudio); droppedOld++ }
                else if (item.audio) { droppedOld++; return false }
            }
            queue.addLast(item)
            gate.notifyAll()
        }
        return true
    }

    private fun readLoop() {
        val buf = ByteArray(16 * 1024)
        try {
            while (open) {
                val n = try { input.read(buf) } catch (e: Exception) { lose("read"); return }
                if (n < 0) { lose("eof"); return }
                if (n == 0) continue
                decoder.feed(buf, 0, n)
                while (true) {
                    val r = decoder.poll() ?: break
                    if (r.status == UsbFrameCodec.Status.UNSUPPORTED_VERSION) { lose("frame-version"); return }
                    val f = r.frame ?: continue
                    if (r.status == UsbFrameCodec.Status.VALID && decoder.trusted) {
                        if (f.type == UsbFrameCodec.TYPE_BYE) { lose("bye"); return }
                        onFrame(f)
                    }
                }
            }
        } catch (_: Exception) {
            lose("read-exception")
        }
    }

    private fun writeLoop() {
        var lastSend = nowMs()
        try {
            while (open) {
                val item: Queued? = synchronized(gate) {
                    if (queue.isEmpty()) {
                        try { gate.wait(KEEPALIVE_MS) } catch (_: InterruptedException) { }
                    }
                    if (!open) return@synchronized null
                    val now = nowMs()
                    // Drop stale audio just before write.
                    val it = queue.iterator()
                    while (it.hasNext()) {
                        val q = it.next()
                        if (q.audio && now - q.createdAt > AUDIO_TTL_MS) { it.remove(); droppedOld++ }
                    }
                    if (queue.isEmpty()) {
                        if (now - lastSend >= KEEPALIVE_MS) {
                            val ka = UsbFrameCodec.encode(UsbFrameCodec.TYPE_KEEPALIVE, nextSeq(), ByteArray(0))
                            return@synchronized if (ka != null) Queued(ka, false, now) else null
                        }
                        return@synchronized null
                    }
                    queue.removeFirst()
                }
                if (item == null) continue
                try {
                    output.write(item.bytes)
                    output.flush()
                    lastSend = nowMs()
                } catch (_: Exception) {
                    lose("write")
                    return
                }
            }
        } catch (_: Exception) {
            lose("write-exception")
        }
    }

    private fun lose(reason: String) {
        if (lost) return
        lost = true
        open = false
        synchronized(gate) { gate.notifyAll() }
        try { closeable.close() } catch (_: Exception) {}
        onLinkLost(reason)
    }

    override fun close() {
        open = false
        synchronized(gate) { gate.notifyAll() }
        try { closeable.close() } catch (_: Exception) {}
        reader.join(JOIN_MS)
        writer.join(JOIN_MS)
    }
}
