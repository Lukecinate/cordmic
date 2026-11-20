package dev.cordmic.usb

/** Streaming decoder with the same resync rules as the PC FrameDecoder. */
class UsbFrameDecoder {
    companion object {
        const val BUFFER_CAP = (UsbFrameCodec.HEADER + UsbFrameCodec.MAX_PAYLOAD) * 2
    }

    private val buf = ByteArray(BUFFER_CAP)
    private var len = 0
    private var goodStreak = 0
    var trusted = false
        private set
    var badHeader = 0L; private set
    var badCrc = 0L; private set
    var resyncs = 0L; private set
    var skipped = 0L; private set
    var unknown = 0L; private set
    var unsupported = 0L; private set
    var valid = 0L; private set

    fun reset() {
        len = 0; goodStreak = 0; trusted = false
    }

    fun feed(data: ByteArray, off: Int, count: Int) {
        var o = off
        var left = count
        while (left > 0) {
            var space = BUFFER_CAP - len
            if (space == 0) {
                System.arraycopy(buf, 1, buf, 0, len - 1)
                len--
                skipped++
                space = 1
            }
            val n = minOf(space, left)
            System.arraycopy(data, o, buf, len, n)
            len += n
            o += n
            left -= n
        }
    }

    fun poll(): UsbFrameCodec.Result? {
        while (len > 0) {
            val r = UsbFrameCodec.tryDecode(buf, 0, len)
            if (r.status == UsbFrameCodec.Status.NEED_MORE) return null
            if (r.consumed <= 0) return null
            if (r.status == UsbFrameCodec.Status.BAD_HEADER || r.status == UsbFrameCodec.Status.BAD_CRC) {
                if (r.status == UsbFrameCodec.Status.BAD_HEADER) badHeader++ else badCrc++
                skip(r.consumed)
                loseTrust()
                continue
            }
            skip(r.consumed)
            if (r.status == UsbFrameCodec.Status.UNSUPPORTED_VERSION) {
                unsupported++
                loseTrust()
                return r
            }
            if (r.status == UsbFrameCodec.Status.UNKNOWN_TYPE) {
                unknown++
                noteGood()
                continue
            }
            valid++
            noteGood()
            return r
        }
        return null
    }

    private fun skip(n: Int) {
        if (n >= len) { len = 0; return }
        System.arraycopy(buf, n, buf, 0, len - n)
        len -= n
        if (n == 1) skipped++
    }

    private fun loseTrust() {
        if (trusted || goodStreak > 0) resyncs++
        goodStreak = 0
        trusted = false
    }

    private fun noteGood() {
        if (goodStreak < 2) goodStreak++
        if (goodStreak >= 2) trusted = true
    }
}
