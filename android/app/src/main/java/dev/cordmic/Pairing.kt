package dev.cordmic

import java.security.MessageDigest
import javax.crypto.Mac
import javax.crypto.SecretKeyFactory
import javax.crypto.spec.PBEKeySpec
import javax.crypto.spec.SecretKeySpec

/**
 * Optional pairing code (0.6.1), same derivation as the PC (windows-app/src/CordMic.Core/Pairing.cs):
 * key = PBKDF2-HMAC-SHA256(code, "CordMic-pair-v1", 20000 iterations, 32 bytes);
 * proof = nonce ‖ first 16 bytes of HMAC-SHA256(key, "CordMic-pair-proof-v1" ‖ nonce).
 * The PC sends a fresh 16-byte nonce (T_PAIR_CHALLENGE); the phone answers with T_PAIR_PROOF.
 */
object Pairing {
    const val CODE_LENGTH = 6
    const val NONCE_SIZE = 16
    const val MAC_SIZE = 16
    const val PROOF_SIZE = NONCE_SIZE + MAC_SIZE
    const val ITERATIONS = 20_000
    private val SALT = "CordMic-pair-v1".toByteArray(Charsets.US_ASCII)
    private val LABEL = "CordMic-pair-proof-v1".toByteArray(Charsets.US_ASCII)

    fun isValidCode(code: String?): Boolean = code != null && code.length == CODE_LENGTH && code.all { it in '0'..'9' }

    /** Accepts "123 456" or "123456"; returns the 6 digits or null. */
    fun normalize(input: String?): String? = input?.filter { !it.isWhitespace() }?.takeIf { isValidCode(it) }

    fun deriveKey(code: String): ByteArray {
        require(isValidCode(code)) { "pairing code must be 6 digits" }
        val spec = PBEKeySpec(code.toCharArray(), SALT, ITERATIONS, 256)
        return try {
            SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256").generateSecret(spec).encoded
        } finally { spec.clearPassword() }
    }

    fun proof(key: ByteArray, nonce: ByteArray): ByteArray {
        require(nonce.size == NONCE_SIZE) { "bad nonce" }
        val mac = Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(key, "HmacSHA256")) }
        mac.update(LABEL)
        val full = mac.doFinal(nonce)
        return nonce + full.copyOf(MAC_SIZE)
    }

    /** PC-side check, used by tests to keep both implementations equal. */
    fun verify(key: ByteArray, nonce: ByteArray, proof: ByteArray): Boolean =
        proof.size >= PROOF_SIZE && MessageDigest.isEqual(proof.copyOf(PROOF_SIZE), proof(key, nonce))
}
