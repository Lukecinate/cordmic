package dev.cordmic

/**
 * 1.1.1 optional trailing block: signed mic gain in 0.1 dB units.
 * Magic 0xC9, version 1, int16 little-endian tenths (−240..+240).
 * Older peers ignore it (see the C# GainExt comment for placement rules).
 * The legacy gain byte stays 0–24 (negative sent as 0).
 */
object GainExt {
    const val MAGIC: Byte = 0xC9.toByte()
    const val VERSION: Byte = 1
    const val SIZE = 4

    fun write(buf: ByteArray, off: Int, gainDb: Double): Int {
        if (off + SIZE > buf.size) return 0
        buf[off] = MAGIC
        buf[off + 1] = VERSION
        val tenths = (kotlin.math.round(VoiceChain.clampGain(gainDb) * 10.0).toInt()).coerceIn(-240, 240)
        buf[off + 2] = (tenths and 0xFF).toByte()
        buf[off + 3] = ((tenths shr 8) and 0xFF).toByte()
        return SIZE
    }

    fun tryRead(buf: ByteArray, off: Int, len: Int = buf.size - off): Double? {
        if (len < SIZE) return null
        if (buf[off] != MAGIC || buf[off + 1] != VERSION) return null
        val tenths = (buf[off + 2].toInt() and 0xFF) or (buf[off + 3].toInt() shl 8)
        val signed = tenths.toShort().toInt()
        if (signed < -240 || signed > 240) return null
        return VoiceChain.clampGain(signed / 10.0)
    }
}
