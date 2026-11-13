package dev.cordmic

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** 0.6.2: front focus on the AudioRecord path falls back quietly when the phone refuses the direction call. */
class Fixes062Test {
    private class Ops(val dir: () -> Boolean, val field: () -> Unit = {}, val mics: () -> Int = { 3 }) : MicDirectionOps {
        var dirCalls = 0
        override fun setDirectionTowardsUser(): Boolean { dirCalls++; return dir() }
        override fun setFieldDimensionNarrow() = field()
        override fun activeMicrophoneCount() = mics()
    }

    @Test fun refusedDirectionFallsBackToNarrowField() {
        val logs = mutableListOf<String>()
        val code = applyFrontFocusOps(33, true, Ops({ false }), logs::add, FrontFocusReporter())
        assertEquals(3, code)
        assertEquals(1, logs.size)
        assertTrue(logs[0].contains("narrow field"))
        assertTrue(logs.none { it.contains("Exception") })
    }

    @Test fun bothRefusedIsUnsupportedButCaptureContinues() {
        val logs = mutableListOf<String>()
        val code = applyFrontFocusOps(33, true, Ops({ throw IllegalStateException("hal") }, { throw UnsupportedOperationException() }), logs::add, FrontFocusReporter())
        assertEquals(2, code)
        assertTrue(logs.any { it.contains("not supported") })
    }

    @Test fun repeatedOpensLogOnce() {
        val logs = mutableListOf<String>()
        val rep = FrontFocusReporter()
        repeat(20) { applyFrontFocusOps(33, true, Ops({ false }), logs::add, rep) }
        assertEquals(1, logs.size)
        // a different outcome is still reported once
        applyFrontFocusOps(33, true, Ops({ true }), logs::add, rep)
        applyFrontFocusOps(33, true, Ops({ true }), logs::add, rep)
        assertEquals(2, logs.size)
        assertTrue(logs[1].contains("front focus on"))
    }

    @Test fun statsAndSummaryNameTheFallback() {
        assertTrue(EngineStats("AudioRecord", 48000, dirCode = 3).toString().contains("dir=narrow"))
        assertTrue(DeviceTune.formatDetected("Phone", false, false, false, 3, 3, true).contains("front focus (narrow field)"))
    }
}
