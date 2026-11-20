package dev.cordmic

import android.content.Context
import java.security.SecureRandom

/**
 * 0.8.0 M0: a random 16-byte install id stored in app preferences. It is not a hardware serial
 * and is sent only when the PC asks (FlagWantPhoneKey). The PC stores a salted hash, never this value.
 */
object InstallId {
    const val SIZE = 16
    private const val PREFS = "cordmic"
    private const val KEY = "install_id"

    fun get(ctx: Context): ByteArray {
        val p = ctx.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val hex = p.getString(KEY, null)
        decode(hex)?.let { return it }
        val id = ByteArray(SIZE)
        SecureRandom().nextBytes(id)
        p.edit().putString(KEY, toHex(id)).apply()
        return id
    }

    fun toHex(id: ByteArray): String {
        val sb = StringBuilder(id.size * 2)
        for (b in id) sb.append("%02x".format(b))
        return sb.toString()
    }

    fun decode(hex: String?): ByteArray? {
        if (hex == null || hex.length != SIZE * 2) return null
        val out = ByteArray(SIZE)
        var i = 0
        while (i < hex.length) {
            val v = hex.substring(i, i + 2).toIntOrNull(16) ?: return null
            out[i / 2] = v.toByte()
            i += 2
        }
        return out
    }
}

/** Optional trailing PONG block (magic 0xC8, version 1, 16-byte id). */
object PhoneIdBlock {
    const val MAGIC: Byte = 0xC8.toByte()
    const val VERSION: Byte = 1
    const val SIZE = 2 + InstallId.SIZE

    fun write(dst: ByteArray, off: Int, id: ByteArray): Int {
        if (off + SIZE > dst.size || id.size != InstallId.SIZE) return 0
        if (id.all { it == 0.toByte() }) return 0
        dst[off] = MAGIC
        dst[off + 1] = VERSION
        id.copyInto(dst, off + 2)
        return SIZE
    }

    fun tryRead(src: ByteArray, off: Int, len: Int = src.size - off): ByteArray? {
        if (len < SIZE) return null
        if (src[off] != MAGIC || src[off + 1] != VERSION) return null
        val id = src.copyOfRange(off + 2, off + SIZE)
        if (id.all { it == 0.toByte() }) return null
        return id
    }
}
