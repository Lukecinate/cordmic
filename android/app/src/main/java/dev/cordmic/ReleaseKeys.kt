package dev.cordmic

/**
 * Production K1/K2 public keys are filled in only after the maintainer sends them.
 * While this list is empty the in-app updater keeps the existing SHA-256SUMS path.
 * Test keys must never be added here.
 */
object ReleaseKeys {
    data class Embedded(val keyId: String, val spkiBase64: String)

    val production: List<Embedded> = emptyList()
    val productionRevoked: List<String> = emptyList()
    val manifestSigningRequired: Boolean get() = production.isNotEmpty()
}
