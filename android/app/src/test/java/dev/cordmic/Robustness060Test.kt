package dev.cordmic

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** 0.6.0: Studio bypass, diag flags, mic-direction guards and the hidden-NS check. */
class Robustness060Test {
    @Test fun diagFlagBitsRoundTripAndStayCompatible() {
        val d = VoiceDiag(-62.0, true, true, 8.5, -6.0, true, silenced = true, studioBypass = true, nsSuspected = true)
        val buf = ByteArray(Protocol.DIAG_SIZE)
        Protocol.writeDiag(buf, 0, d)
        // Same golden bytes as the C# Protocol060Tests.
        org.junit.Assert.assertArrayEquals(byteArrayOf(0xC7.toByte(), 0xC2.toByte(), 0x11, 0x3F, 0xF4.toByte(), 0x85.toByte()), buf)
        assertEquals(d, Protocol.readDiag(buf, 0, buf.size))
        // A 0.5.7 block (flags 0x07) reads with the new fields off.
        val old = byteArrayOf(0xC7.toByte(), 0xC2.toByte(), 0x11, 0x07, 0xF4.toByte(), 0xBD.toByte())
        val r = Protocol.readDiag(old, 0, old.size)!!
        assertFalse(r.silenced); assertFalse(r.studioBypass); assertFalse(r.nsSuspected)
    }

    @Test fun studioFlagIsBit5() {
        assertEquals(32, SettingsSync.FLAG_STUDIO)
        assertTrue(SyncPayload(SettingsSync.FLAG_STUDIO, emptyArray()).studio)
        assertFalse(SyncPayload(SettingsSync.FLAG_WANT_DIAG, emptyArray()).studio)
    }

    @Test fun studioBypassesPhoneProcessing() {
        for (m in NoiseMode.values()) for (agc in listOf(false, true)) for (ns in listOf(false, true)) {
            val p = voicePlan(m, agc, studio = true, platformNsActive = ns)
            assertEquals(VoicePlan(NoiseMode.OFF, NoiseMode.OFF, false, false), p)
        }
    }

    @Test fun withoutStudioPlanMatches057() {
        assertEquals(VoicePlan(NoiseMode.STRONG, NoiseMode.STRONG, true, false), voicePlan(NoiseMode.STRONG, true, false, false))
        assertEquals(VoicePlan(NoiseMode.OFF, NoiseMode.LIGHT, false, true), voicePlan(NoiseMode.LIGHT, false, false, true))
        assertEquals(VoicePlan(NoiseMode.LIGHT, NoiseMode.LIGHT, false, true), voicePlan(NoiseMode.LIGHT, false, false, false))
        assertEquals(VoicePlan(NoiseMode.OFF, NoiseMode.OFF, true, false), voicePlan(NoiseMode.OFF, true, false, false))
    }

    private class FakeOps(val dir: () -> Boolean, val field: () -> Unit = {}, val mics: () -> Int = { 2 }) : MicDirectionOps {
        override fun setDirectionTowardsUser() = dir()
        override fun setFieldDimensionNarrow() = field()
        override fun activeMicrophoneCount() = mics()
    }

    @Test fun micDirectionGuardsNeverThrow() {
        assertEquals(1, applyFrontFocusOps(30, true, FakeOps({ true })))
        assertEquals(0, applyFrontFocusOps(30, false, FakeOps({ throw IllegalStateException() })))
        assertEquals(2, applyFrontFocusOps(30, true, FakeOps({ throw IllegalStateException("hal") })))
        assertEquals(1, applyFrontFocusOps(30, true, FakeOps({ true }, { throw UnsupportedOperationException() }, { throw RuntimeException() })))
        val logs = mutableListOf<String>()
        assertEquals(1, applyFrontFocusOps(30, true, FakeOps({ true }, mics = { 0 }), logs::add))
        assertTrue(logs.any { it.contains("unknown") })
        assertEquals(2, applyFrontFocusOps(28, true, FakeOps({ true })))
    }

    @Test fun nsSuspectedOnSharpRoomToneDrop() {
        val d = NsSuspectDetector(20)
        repeat(10) { d.feed(-50.0, false) }      // first 200 ms
        repeat(20) { d.feed(-62.0, false) }      // 12 dB lower, no speech
        assertTrue(d.suspected)
    }

    @Test fun nsNotSuspectedOnSteadyNoiseOrSpeech() {
        val steady = NsSuspectDetector(20)
        repeat(150) { steady.feed(-50.0 + (it % 3), false) }
        assertFalse(steady.suspected); assertTrue(steady.done)
        val speech = NsSuspectDetector(20)
        repeat(10) { speech.feed(-50.0, false) }
        repeat(90) { speech.feed(if (it % 5 == 0) -20.0 else -62.0, it % 5 == 0) } // speech resets the window
        assertFalse(speech.suspected)
    }
}
