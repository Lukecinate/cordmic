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

    /**
     * 0.5.7 diagnostics block, placed between the name separator and the sync block of a PONG, only when the
     * PING asked for it (FLAG_WANT_DIAG). Bytes: magic 0xC7, floor dBFS (int8), AGC gain in 0.5 dB (int8),
     * flags (bit0 gated, bit1 AGC on, bit2 floor measured), expander in 0.5 dB (int8), check = xor(0..4) ^ 0x5A.
     */
    const val DIAG_MAGIC: Byte = 0xC7.toByte()
    const val DIAG_SIZE = 6

    fun writeDiag(buf: ByteArray, off: Int, d: VoiceDiag): Int {
        if (off + DIAG_SIZE > buf.size) return 0
        buf[off] = DIAG_MAGIC
        buf[off + 1] = Math.round(d.floorDbfs).toInt().coerceIn(-127, 0).toByte()
        buf[off + 2] = Math.round(d.agcGainDb * 2).toInt().coerceIn(-127, 127).toByte()
        var f = 0
        if (d.gated) f = f or 1
        if (d.agc) f = f or 2
        if (d.floorKnown) f = f or 4
        buf[off + 3] = f.toByte()
        buf[off + 4] = Math.round(d.expanderDb * 2).toInt().coerceIn(-127, 0).toByte()
        var x = 0
        for (i in 0 until 5) x = x xor (buf[off + i].toInt() and 0xFF)
        buf[off + 5] = (x xor 0x5A).toByte()
        return DIAG_SIZE
    }

    fun readDiag(buf: ByteArray, off: Int, len: Int): VoiceDiag? {
        if (len < DIAG_SIZE || buf[off] != DIAG_MAGIC) return null
        var x = 0
        for (i in 0 until 5) x = x xor (buf[off + i].toInt() and 0xFF)
        if ((x xor 0x5A).toByte() != buf[off + 5]) return null
        val f = buf[off + 3].toInt()
        return VoiceDiag(buf[off + 1].toDouble(), f and 1 != 0, f and 2 != 0, buf[off + 2] / 2.0, buf[off + 4] / 2.0, f and 4 != 0)
    }

    /** PC → phone. changeSeq 0 means "do not override the phone". */
    fun readPingControl(buf: ByteArray, len: Int): PingControl? {
        if (len < HEADER_SIZE + LINK_SIZE || buf[HEADER_SIZE] != LINK_MAGIC) return null
        val f = buf[HEADER_SIZE + 1].toInt() and 0xFF
        val extended = f and 32 != 0
        val nc = f and 1 != 0
        val stored = (f shr 3) and 3
        val mode = if (extended) NoiseMode.from(stored) else if (nc) NoiseMode.LIGHT else NoiseMode.OFF
        val sync = if (len >= HEADER_SIZE + LINK_SIZE + SettingsSync.SIZE)
            SettingsSync.tryRead(buf, HEADER_SIZE + LINK_SIZE) else null
        return PingControl(
            noiseCancellation = nc,
            agc = f and 2 != 0,
            gainDb = (buf[HEADER_SIZE + 2].toInt() and 0xFF).coerceIn(0, VoiceChain.MAX_GAIN_DB),
            changeSeq = buf[HEADER_SIZE + 3].toInt() and 0xFF,
            extended = extended,
            voiceClarity = f and 4 != 0,
            noiseMode = mode,
            frontFocus = extended && f and 64 != 0,
            frontFocusAr = (extended && f and 128 != 0) || (sync?.frontFocusAr == true),
            sync = sync,
        )
    }

    /** Writes the pong status at [off] and returns how many bytes were used. */
    fun writePongStatus(buf: ByteArray, off: Int, noiseCancellation: Boolean, agc: Boolean, aaudio: Boolean,
                        exclusive: Boolean, lowLatency: Boolean, gainDb: Int, deviceName: String,
                        noiseMode: NoiseMode = NoiseMode.OFF, frontFocus: Boolean = false,
                        dirCode: Int = 0, micCount: Int = 0, frontFocusAr: Boolean = false,
                        speechLow: Boolean = false, sync: SyncClock? = null, voiceClarity: Boolean = true,
                        diag: VoiceDiag? = null): Int {
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
        val withDiag = sync != null && diag != null
        val syncBytes = if (sync != null) 1 + SettingsSync.SIZE + (if (withDiag) DIAG_SIZE else 0) else 0
        val name = deviceName.encodeToByteArray()
        val n = minOf(name.size, buf.size - off - LINK_SIZE - syncBytes)
        if (n > 0) name.copyInto(buf, off + LINK_SIZE, 0, n)
        var used = LINK_SIZE + maxOf(0, n)
        if (sync != null && off + used + 1 + SettingsSync.SIZE <= buf.size) {
            buf[off + used] = 0
            used += 1
            if (withDiag && off + used + DIAG_SIZE + SettingsSync.SIZE <= buf.size) used += writeDiag(buf, off + used, diag!!)
            var flags2 = SettingsSync.FLAG_PEER_SYNC
            if (frontFocusAr) flags2 = flags2 or SettingsSync.FLAG_FRONT_FOCUS_AR
            if (speechLow) flags2 = flags2 or SettingsSync.FLAG_SPEECH_LOW
            if (voiceClarity) flags2 = flags2 or SettingsSync.FLAG_VOICE_CLARITY
            used += SettingsSync.write(buf, off + used, flags2, sync.revs)
        }
        return used
    }

    data class PingControl(val noiseCancellation: Boolean, val agc: Boolean, val gainDb: Int, val changeSeq: Int,
                            val extended: Boolean = false, val voiceClarity: Boolean = true,
                            val noiseMode: NoiseMode = if (noiseCancellation) NoiseMode.LIGHT else NoiseMode.OFF,
                            val frontFocus: Boolean = true, val frontFocusAr: Boolean = false,
                            val sync: SyncPayload? = null)

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
