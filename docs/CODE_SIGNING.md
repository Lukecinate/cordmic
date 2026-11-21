# Code signing policy

This is the public policy for how CordMic release binaries are signed. It applies to
the Windows installer, the Windows application, the optional accessory catalog, and
(separately) the Android APK.

CordMic is MIT-licensed, actively maintained, and signed only for binaries built from
this repository by a trusted build system. There is no telemetry, and the apps send
nothing except update checks the user has enabled.

## Who signs what

| Artifact | Signer | How |
|---|---|---|
| `CordMic.exe`, update helper | SignPath Foundation, project `cordmic`, policy `release-signing` (manual approval) | GitHub Actions uploads the unsigned PE; SignPath signs after a maintainer approves |
| Optional accessory `.cat` | Same SignPath project, artifact configuration `driver-catalog` | INF-only; no `.sys` of ours |
| Windows installer | Same SignPath project, artifact configuration `installer` | Built from already-signed inner binaries, then signed |
| Android APK | The existing CordMic upload keystore, held by the maintainer | Signed on a private machine after CI produces the unsigned APK; not uploaded to SignPath |
| `release-manifest.json` | Offline ECDSA P-256 keys K1 (primary) and K2 (backup) | Signed by the maintainer with `cordmic-keytool`. This step stays manual. |

The SignPath certificate subject we expect on Windows binaries is **SignPath Foundation**.
Authenticode signatures are RFC 3161 timestamped so they remain valid after the certificate expires.

## Roles

- **Maintainer / SignPath approver:** reviews the workflow run, approves the signing request, holds K1.
- **Deputy (SignPath approver backup and K2 holder):** _not yet named_. See `RELEASING.md`.
- **GitHub Actions:** builds on GitHub-hosted runners only. No self-hosted runners, no `pull_request_target`.

## What the updater checks

Until the maintainer embeds the K1 and K2 public keys, the in-app updater keeps the
existing SHA-256SUMS path (HTTPS download, size limit, hash on a locked file handle).
Once those public keys are embedded, every automatic update must also present a
`release-manifest.json` signed by a non-revoked embedded key, with a version newer
than the running app and a matching hash. Windows also checks Authenticode
(`WinVerifyTrust`) when a SignPath-signed installer is in use.

## What we do not do

- We never ask anyone to disable Secure Boot, Memory Integrity, Driver Signature
  Enforcement, or to turn on test-signing.
- We never bypass those features in scripts or documentation.
- Private keys, keystore passwords, and SignPath tokens are never committed, pasted
  into chat, or printed in CI logs.
- We do not bundle VB-CABLE or any third-party installer inside a signed CordMic package.

## Privacy

CordMic sends nothing except the update checks the user enabled (a request to GitHub
Releases over HTTPS). Diagnostics that can be copied from Settings have IP addresses
and Windows user names removed, and never include voice recordings.

## Applying for SignPath

The application is a maintainer action. A draft checklist lives in
`docs/SIGNPATH-APPLICATION.md`. This project does not submit that application on its own.
