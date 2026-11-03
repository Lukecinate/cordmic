package dev.cordmic

import java.nio.ByteBuffer
import java.nio.ByteOrder

/** Wire protocol shared with windows-receiver/cordmic_receiver/protocol.py (20-byte LE header). */
object Protocol {
    const val HEADER_SIZE = 20
    const val VERSION: Byte = 1
    const val T_AUDIO: Byte = 1
    const val T_DISCOVER: Byte = 2
    const val T_HELLO: Byte = 3
    const val T_PING: Byte = 4
    const val T_PONG: Byte = 5
    const val T_BYE: Byte = 6
    const val DEFAULT_PORT = 50005

    /** codec PCM16 (0), channels-1 in bits 4-7, rate code (0 = 48 kHz) in bits 8-11. */
    fun flags(channels: Int = 1, rateCode: Int = 0): Int = ((channels - 1) shl 4) or (rateCode shl 8)

    fun writeHeader(buf: ByteArray, type: Byte, seq: Int, tsUs: Long, frames: Int, flags: Int) {
        val bb = ByteBuffer.wrap(buf, 0, HEADER_SIZE).order(ByteOrder.LITTLE_ENDIAN)
        bb.put('U'.code.toByte()).put('M'.code.toByte()).put(VERSION).put(type)
        bb.putInt(seq).putLong(tsUs).putShort(frames.toShort()).putShort(flags.toShort())
    }

    class Header(val type: Byte, val seq: Int, val tsUs: Long)

    fun parse(buf: ByteArray, len: Int): Header? {
        if (len < HEADER_SIZE || buf[0] != 'U'.code.toByte() || buf[1] != 'M'.code.toByte() || buf[2] != VERSION) return null
        val bb = ByteBuffer.wrap(buf, 0, len).order(ByteOrder.LITTLE_ENDIAN)
        return Header(buf[3], bb.getInt(4), bb.getLong(8))
    }
}
