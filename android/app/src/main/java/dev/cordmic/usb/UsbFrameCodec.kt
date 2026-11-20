package dev.cordmic.usb

/** USB bulk framing, byte-for-byte with CordMic.Core.Transport.FrameCodec. Never throws on bad input. */
object UsbFrameCodec {
    const val HEADER = 14
    const val MAX_PAYLOAD = 4096
    const val VERSION: Byte = 1
    const val MAGIC0: Byte = 0x43 // 'C'
    const val MAGIC1: Byte = 0x4D // 'M'
    const val TYPE_AUDIO: Byte = 1
    const val TYPE_CONTROL: Byte = 2
    const val TYPE_BYE: Byte = 3
    const val TYPE_KEEPALIVE: Byte = 4

    enum class Status { VALID, NEED_MORE, BAD_HEADER, BAD_CRC, UNSUPPORTED_VERSION, UNKNOWN_TYPE }

    data class Frame(val type: Byte, val seq: Int, val payload: ByteArray, val version: Int)
    data class Result(val status: Status, val frame: Frame?, val consumed: Int, val version: Int)

    fun crc32c(a: ByteArray, aOff: Int, aLen: Int, b: ByteArray, bOff: Int, bLen: Int): Int {
        var crc = CRC32C.compute(a, aOff, aLen, 0xFFFF_FFFF.toInt())
        crc = CRC32C.compute(b, bOff, bLen, crc)
        return crc xor 0xFFFF_FFFF.toInt()
    }

    fun encode(type: Byte, seq: Int, payload: ByteArray, off: Int = 0, len: Int = payload.size): ByteArray? {
        if (len > MAX_PAYLOAD || off < 0 || off + len > payload.size) return null
        val out = ByteArray(HEADER + len)
        out[0] = MAGIC0
        out[1] = MAGIC1
        out[2] = VERSION
        out[3] = type
        out[4] = (len and 0xFF).toByte()
        out[5] = ((len shr 8) and 0xFF).toByte()
        writeU32(out, 6, seq)
        val crc = CRC32C.of(out, 0, 10, payload, off, len)
        writeU32(out, 10, crc)
        System.arraycopy(payload, off, out, HEADER, len)
        return out
    }

    fun tryDecode(src: ByteArray, off: Int, length: Int): Result {
        if (length < 2) return Result(Status.NEED_MORE, null, 0, 0)
        if (src[off] != MAGIC0 || src[off + 1] != MAGIC1) return Result(Status.BAD_HEADER, null, 1, 0)
        if (length < HEADER) return Result(Status.NEED_MORE, null, 0, 0)
        val ver = src[off + 2].toInt() and 0xFF
        val typ = src[off + 3]
        val len = (src[off + 4].toInt() and 0xFF) or ((src[off + 5].toInt() and 0xFF) shl 8)
        if (len > MAX_PAYLOAD) return Result(Status.BAD_HEADER, null, 1, ver)
        val total = HEADER + len
        if (length < total) return Result(Status.NEED_MORE, null, 0, ver)
        val seq = readU32(src, off + 6)
        val crc = readU32(src, off + 10)
        val got = CRC32C.of(src, off, 10, src, off + HEADER, len)
        if (got != crc) return Result(Status.BAD_CRC, null, 1, ver)
        val payload = src.copyOfRange(off + HEADER, off + total)
        val frame = Frame(typ, seq, payload, ver)
        if (ver != (VERSION.toInt() and 0xFF)) return Result(Status.UNSUPPORTED_VERSION, frame, total, ver)
        val t = typ.toInt() and 0xFF
        if (t < 1 || t > 4) return Result(Status.UNKNOWN_TYPE, frame, total, ver)
        return Result(Status.VALID, frame, total, ver)
    }

    private fun writeU32(d: ByteArray, o: Int, v: Int) {
        d[o] = (v and 0xFF).toByte()
        d[o + 1] = ((v shr 8) and 0xFF).toByte()
        d[o + 2] = ((v shr 16) and 0xFF).toByte()
        d[o + 3] = ((v shr 24) and 0xFF).toByte()
    }

    private fun readU32(s: ByteArray, o: Int): Int =
        (s[o].toInt() and 0xFF) or
            ((s[o + 1].toInt() and 0xFF) shl 8) or
            ((s[o + 2].toInt() and 0xFF) shl 16) or
            ((s[o + 3].toInt() and 0xFF) shl 24)
}

/** Castagnoli CRC-32C (reflected 0x82F63B78), same table as the PC engine. */
object CRC32C {
    private val table: IntArray = IntArray(256).also { t ->
        val poly = 0x82F63B78.toInt()
        for (i in 0 until 256) {
            var crc = i
            repeat(8) { crc = if ((crc and 1) != 0) (crc ushr 1) xor poly else crc ushr 1 }
            t[i] = crc
        }
    }

    fun compute(data: ByteArray, off: Int, len: Int, init: Int): Int {
        var crc = init
        for (i in off until off + len) {
            val b = data[i].toInt() and 0xFF
            crc = table[(crc xor b) and 0xFF] xor (crc ushr 8)
        }
        return crc
    }

    fun of(a: ByteArray, aOff: Int, aLen: Int, b: ByteArray, bOff: Int, bLen: Int): Int {
        var crc = compute(a, aOff, aLen, 0xFFFF_FFFF.toInt())
        crc = compute(b, bOff, bLen, crc)
        return crc xor 0xFFFF_FFFF.toInt()
    }
}
