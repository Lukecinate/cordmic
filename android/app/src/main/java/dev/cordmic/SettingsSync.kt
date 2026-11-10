package dev.cordmic

enum class SyncOrigin { PC, PHONE }

enum class SyncField {
    NOISE, CLARITY, AGC, GAIN, FRONT_FOCUS, FRONT_FOCUS_AR;
}

data class FieldRev(val seq: Int = 0, val origin: SyncOrigin = SyncOrigin.PC) {
    companion object { val None = FieldRev() }
}

/** Last-write-wins sync. Higher sequence wins; equal sequences (echo or conflict) are ignored. */
object SettingsSync {
    const val MAGIC: Byte = 0xC6.toByte()
    const val VERSION: Byte = 1
    const val FIELD_COUNT = 6
    const val SIZE = 10
    const val FLAG_FRONT_FOCUS_AR = 1 shl 0
    const val FLAG_SPEECH_LOW = 1 shl 1
    const val FLAG_PEER_SYNC = 1 shl 2
    const val FLAG_VOICE_CLARITY = 1 shl 3
    /** PING only (0.5.7): the PC wants the diagnostics block in the PONG. Older phones ignore it. */
    const val FLAG_WANT_DIAG = 1 shl 4

    fun nextSeq(local: Int, remote: Int): Int {
        val n = maxOf(local and 0xFF, remote and 0xFF) + 1
        return if (n <= 0 || n > 255) 1 else n
    }

    fun seqAhead(incoming: Int, local: Int): Boolean {
        val a = incoming and 0xFF
        val b = local and 0xFF
        if (a == 0) return false
        if (b == 0) return true
        val d = (a - b) and 0xFF
        return d in 1..127
    }

    fun shouldApply(local: FieldRev, remote: FieldRev, localBusy: Boolean = false): Boolean {
        if (localBusy) return false
        if (remote.seq == 0) return false
        return seqAhead(remote.seq, local.seq)
    }

    fun write(dst: ByteArray, off: Int, flags2: Int, revs: Array<FieldRev>): Int {
        if (off + SIZE > dst.size) return 0
        dst[off] = MAGIC
        dst[off + 1] = VERSION
        dst[off + 2] = flags2.toByte()
        var origins = 0
        for (i in 0 until FIELD_COUNT) {
            val r = if (i < revs.size) revs[i] else FieldRev.None
            dst[off + 4 + i] = (r.seq and 0xFF).toByte()
            if (r.origin == SyncOrigin.PHONE) origins = origins or (1 shl i)
        }
        dst[off + 3] = origins.toByte()
        return SIZE
    }

    fun tryRead(src: ByteArray, off: Int, len: Int = src.size - off): SyncPayload? {
        if (len < SIZE) return null
        if (src[off] != MAGIC || src[off + 1] != VERSION) return null
        val flags2 = src[off + 2].toInt() and 0xFF
        val origins = src[off + 3].toInt() and 0xFF
        val revs = Array(FIELD_COUNT) { i ->
            val seq = src[off + 4 + i].toInt() and 0xFF
            val origin = if (origins and (1 shl i) != 0) SyncOrigin.PHONE else SyncOrigin.PC
            FieldRev(seq, origin)
        }
        return SyncPayload(flags2, revs)
    }
}

data class SyncPayload(val flags2: Int, val revs: Array<FieldRev>) {
    val frontFocusAr: Boolean get() = flags2 and SettingsSync.FLAG_FRONT_FOCUS_AR != 0
    val speechLow: Boolean get() = flags2 and SettingsSync.FLAG_SPEECH_LOW != 0
    val peerSync: Boolean get() = flags2 and SettingsSync.FLAG_PEER_SYNC != 0
    val voiceClarity: Boolean get() = flags2 and SettingsSync.FLAG_VOICE_CLARITY != 0
    val wantDiag: Boolean get() = flags2 and SettingsSync.FLAG_WANT_DIAG != 0
    override fun equals(other: Any?) = other is SyncPayload && flags2 == other.flags2 && revs.contentEquals(other.revs)
    override fun hashCode() = 31 * flags2 + revs.contentHashCode()
}

class SyncClock {
    val revs = Array(SettingsSync.FIELD_COUNT) { FieldRev.None }
    operator fun get(field: SyncField) = revs[field.ordinal]
    fun bump(field: SyncField, origin: SyncOrigin) {
        val cur = revs[field.ordinal]
        revs[field.ordinal] = FieldRev(SettingsSync.nextSeq(cur.seq, cur.seq), origin)
    }
    fun applyRemote(field: SyncField, remote: FieldRev) {
        revs[field.ordinal] = remote
    }
}
