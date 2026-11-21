# PENDING-HARDWARE — M7 accessory binding

The helper verbs, TOCTOU signature check, and refusal path are implemented and
unit-tested with fakes. This build does not ship a SignPath-signed `.cat`, so
**Set up USB connection** refuses to install (and never suggests turning off
Secure Boot, Memory Integrity, or Driver Signature Enforcement).

Record the following on real PCs before Gate H is ticked:

1. Windows 10 and Windows 11, Secure Boot on, Memory Integrity on: with a
   SignPath-signed catalog, install and undo ten times. After undo, no leftover
   `oem*.inf` for this package (`pnputil /enum-drivers`).
2. The same PCs: unsigned package is refused and the message does not tell you
   to weaken platform security.
3. UAC declined: CordMic keeps running; nothing is installed.

Do not disable signature enforcement, test-signing, Secure Boot, or Memory Integrity.
