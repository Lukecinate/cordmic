package dev.cordmic.usb

/** Outbound path for a complete wire-v1 packet (already has the 20-byte UM header). */
interface TransportSink {
    /** Enqueue or send. Returns false if the link is gone or the queue dropped the frame. */
    fun send(packet: ByteArray, off: Int, len: Int, createdAtElapsedMs: Long): Boolean
    fun sendBye()
    fun close()
    val isOpen: Boolean
}
