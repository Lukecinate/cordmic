package dev.cordmic

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class SettingsSyncTest {
    @Test fun higherSequenceWinsAndEchoIsIgnored() {
        val local = FieldRev(1, SyncOrigin.PC)
        assertTrue(SettingsSync.shouldApply(local, FieldRev(2, SyncOrigin.PHONE)))
        assertFalse(SettingsSync.shouldApply(local, FieldRev(1, SyncOrigin.PC)))
        assertFalse(SettingsSync.shouldApply(local, FieldRev(1, SyncOrigin.PHONE)))
        assertFalse(SettingsSync.shouldApply(local, FieldRev(0, SyncOrigin.PHONE)))
        assertFalse(SettingsSync.shouldApply(FieldRev(3, SyncOrigin.PHONE), FieldRev(2, SyncOrigin.PC)))
    }

    @Test fun busyControlIsNotOverwritten() {
        val local = FieldRev(1, SyncOrigin.PC)
        assertFalse(SettingsSync.shouldApply(local, FieldRev(4, SyncOrigin.PHONE), localBusy = true))
        assertTrue(SettingsSync.shouldApply(local, FieldRev(4, SyncOrigin.PHONE), localBusy = false))
    }

    @Test fun nextSeqSkipsZeroAndWraps() {
        assertEquals(1, SettingsSync.nextSeq(0, 0))
        assertEquals(6, SettingsSync.nextSeq(5, 3))
        assertEquals(1, SettingsSync.nextSeq(255, 255))
    }

    @Test fun payloadRoundTrip() {
        val revs = Array(SettingsSync.FIELD_COUNT) { FieldRev.None }
        revs[SyncField.GAIN.ordinal] = FieldRev(4, SyncOrigin.PHONE)
        val buf = ByteArray(SettingsSync.SIZE)
        val flags = SettingsSync.FLAG_PEER_SYNC or SettingsSync.FLAG_FRONT_FOCUS_AR
        SettingsSync.write(buf, 0, flags, revs)
        val got = SettingsSync.tryRead(buf, 0)!!
        assertTrue(got.peerSync && got.frontFocusAr)
        assertEquals(4, got.revs[SyncField.GAIN.ordinal].seq)
        assertEquals(SyncOrigin.PHONE, got.revs[SyncField.GAIN.ordinal].origin)
    }
}
