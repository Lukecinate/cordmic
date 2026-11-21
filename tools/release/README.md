# Release scripts

These scripts cover every **non-key** step of the 0.8.0 release chain. Signing
`release-manifest.json` with K1 or K2 stays a manual `cordmic-keytool sign`
invocation (see `RELEASING.md`).

| Script | Purpose |
|---|---|
| `check-action-pins.sh` | Fail if a workflow `uses:` is not a 40-character SHA |
| `hash-assets.sh` | Write SHA-256 lines for files or a directory |
| `make-manifest.py` | Write an unsigned `release-manifest.json` |
| `verify-gate-h.sh` | Fail if Gate H is missing or still has `- [ ]` |
| `sign-apk.sh` | Sign an unsigned APK; keystore must sit outside the git tree |
| `publish-checks.sh` | Hash, optional manifest signature, optional APK pin, Gate H |

None of these scripts print passphrases, keystore contents, or API tokens.
None recommend disabling Secure Boot, Memory Integrity, or test-signing.
