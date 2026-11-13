package dev.cordmic

import java.net.InetAddress
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Regression tests for the 0.6.1 audit fixes on the phone side. */
class Fixes061Test {
    private fun hex(b: ByteArray) = b.joinToString("") { "%02x".format(it) }
    private fun ip(s: String) = InetAddress.getByName(s)

    // Same vector as windows-app/tests/CordMic.Tests/Fixes061Tests.cs, so both sides derive identical proofs.
    @Test fun pairingMatchesPcVector() {
        val key = Pairing.deriveKey("123456")
        assertEquals("a3d6f690e4fb5fedc18714c2116777fe4370cabf2740e6e519a32303a8b0945d", hex(key))
        val nonce = ByteArray(16) { it.toByte() }
        val proof = Pairing.proof(key, nonce)
        assertEquals(Pairing.PROOF_SIZE, proof.size)
        assertArrayEquals(nonce, proof.copyOf(16))
        assertEquals("dd42ece9fdf11f5558e3580e75a5fc0e", hex(proof.copyOfRange(16, 32)))
        assertTrue(Pairing.verify(key, nonce, proof))
        assertFalse(Pairing.verify(Pairing.deriveKey("654321"), nonce, proof))
        val tampered = proof.clone().also { it[31] = (it[31].toInt() xor 1).toByte() }
        assertFalse(Pairing.verify(key, nonce, tampered))
    }

    @Test fun pairingCodeNormalization() {
        assertEquals("123456", Pairing.normalize(" 123 456 "))
        assertNull(Pairing.normalize("12345"))
        assertNull(Pairing.normalize("12345a"))
        assertNull(Pairing.normalize(""))
        assertNull(Pairing.normalize(null))
    }

    @Test fun protocolPairingTypesMatchPc() {
        assertEquals(7.toByte(), Protocol.T_PAIR_CHALLENGE)
        assertEquals(8.toByte(), Protocol.T_PAIR_PROOF)
    }

    @Test fun manualModeAcceptsOnlyTheConfiguredPc() {
        val pc = ip("192.168.42.10")
        val other = ip("192.168.42.99")
        for (t in listOf(Protocol.T_HELLO, Protocol.T_PING, Protocol.T_PAIR_CHALLENGE)) {
            assertTrue(ControlFilter.accept(t, pc, pc, pc))
            assertFalse(ControlFilter.accept(t, other, pc, pc))
        }
        assertFalse(ControlFilter.accept(Protocol.T_PING, null, pc, pc))
    }

    @Test fun autoModeTakesPingOnlyFromTheCurrentPc() {
        val pc = ip("192.168.42.129")
        val other = ip("192.168.42.7")
        assertTrue(ControlFilter.accept(Protocol.T_HELLO, other, null, pc)) // HELLO announces a PC
        assertTrue(ControlFilter.accept(Protocol.T_PING, pc, null, pc))
        assertFalse(ControlFilter.accept(Protocol.T_PING, other, null, pc))
        assertFalse(ControlFilter.accept(Protocol.T_PAIR_CHALLENGE, other, null, pc))
        assertTrue(ControlFilter.accept(Protocol.T_PAIR_CHALLENGE, other, null, null))
    }

    @Test fun wakeLockIsRenewedBeforeItExpires() {
        assertTrue(WakeLockPolicy.RENEW_EVERY_MS < WakeLockPolicy.TIMEOUT_MS)
        assertTrue(WakeLockPolicy.shouldRenew(force = true, nowMs = 5, lastMs = 5))
        assertTrue(WakeLockPolicy.shouldRenew(force = false, nowMs = 5, lastMs = 0))
        assertFalse(WakeLockPolicy.shouldRenew(false, 1_000 + WakeLockPolicy.RENEW_EVERY_MS - 1, 1_000))
        assertTrue(WakeLockPolicy.shouldRenew(false, 1_000 + WakeLockPolicy.RENEW_EVERY_MS, 1_000))
        // Simulate 24 h of 1 s ticks: the lock is never allowed to lapse.
        var last = 0L; var heldUntil = 0L
        var t = 1L
        while (t <= 24 * 3600_000L) {
            if (WakeLockPolicy.shouldRenew(last == 0L, t, last)) { last = t; heldUntil = t + WakeLockPolicy.TIMEOUT_MS }
            assertTrue("lapsed at $t", heldUntil > t)
            t += 1000
        }
    }

    @Test fun updateIsStagedUnderAFixedName() {
        assertEquals("update.apk", UpdateInstaller.LOCAL_APK)
    }
}
