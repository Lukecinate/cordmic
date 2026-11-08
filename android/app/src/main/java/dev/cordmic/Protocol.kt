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

    /**
     * Bits 0–11 match 0.4.2 (codec, channels-1, rate). Bits 12–14 were added in 0.5.0 and stay 0 on older
     * senders: noise cancellation, AGC, capture path is AAudio. A 0.4.2 receiver ignores them.
     */
    fun flags(channels: Int = 1, rateCode: Int = 0, noiseCancellation: Boolean = false, agc: Boolean = false, aaudio: Boolean = false): Int {
        var v = ((channels - 1) shl 4) or (rateCode shl 8)
        if (noiseCancellation) v = v or (1 shl 12)
        if (agc) v = v or (1 shl 13)
        if (aaudio) v = v or (1 shl 14)
        return v
    }

    const val LINK_MAGIC: Byte = 0xC5.toByte()
    const val LINK_SIZE = 4

    /** PC → phone. changeSeq 0 means "do not override the phone". */
    fun readPingControl(buf: ByteArray, len: Int): PingControl? {
        if (len < HEADER_SIZE + LINK_SIZE || buf[HEADER_SIZE] != LINK_MAGIC) return null
        val f = buf[HEADER_SIZE + 1].toInt() and 0xFF
        val extended = f and 32 != 0
        val nc = f and 1 != 0
        val stored = (f shr 3) and 3
        val mode = if (extended) NoiseMode.from(stored) else if (nc) NoiseMode.LIGHT else NoiseMode.OFF
        return PingControl(
            noiseCancellation = nc,
            agc = f and 2 != 0,
            gainDb = (buf[HEADER_SIZE + 2].toInt() and 0xFF).coerceIn(0, VoiceChain.MAX_GAIN_DB),
            changeSeq = buf[HEADER_SIZE + 3].toInt() and 0xFF,
            extended = extended,
            voiceClarity = f and 4 != 0,
            noiseMode = mode,
            frontFocus = extended && f and 64 != 0,
        )
    }

    /** Writes the pong status at [off] and returns how many bytes were used. */
    fun writePongStatus(buf: ByteArray, off: Int, noiseCancellation: Boolean, agc: Boolean, aaudio: Boolean,
                        exclusive: Boolean, lowLatency: Boolean, gainDb: Int, deviceName: String,
                        noiseMode: NoiseMode = NoiseMode.OFF, frontFocus: Boolean = false,
                        dirCode: Int = 0, micCount: Int = 0): Int {
        buf[off] = LINK_MAGIC
        var f = 0
        if (noiseCancellation) f = f or 1
        if (agc) f = f or 2
        if (aaudio) f = f or 4
        if (exclusive) f = f or 8
        if (lowLatency) f = f or 16
        if (frontFocus) f = f or 32
        f = f or ((noiseMode.code and 3) shl 6)
        buf[off + 1] = f.toByte()
        buf[off + 2] = gainDb.coerceIn(0, VoiceChain.MAX_GAIN_DB).toByte()
        val engine = if (aaudio) 1 else 2
        buf[off + 3] = (engine or ((dirCode and 3) shl 2) or ((micCount.coerceIn(0, 15) and 15) shl 4)).toByte()
        val name = deviceName.encodeToByteArray()
        val n = minOf(name.size, buf.size - off - LINK_SIZE)
        if (n > 0) name.copyInto(buf, off + LINK_SIZE, 0, n)
        return LINK_SIZE + maxOf(0, n)
    }

    data class PingControl(val noiseCancellation: Boolean, val agc: Boolean, val gainDb: Int, val changeSeq: Int,
                            val extended: Boolean = false, val voiceClarity: Boolean = true,
                            val noiseMode: NoiseMode = if (noiseCancellation) NoiseMode.LIGHT else NoiseMode.OFF,
                            val frontFocus: Boolean = true)

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
