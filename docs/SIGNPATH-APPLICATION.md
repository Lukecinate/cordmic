# SignPath Foundation application (draft — do not submit)

This is a checklist and draft answers for the maintainer. **Do not submit this
application until you have reviewed the SignPath Foundation terms yourself and
sent it from your own account.** Nobody else should file it on your behalf.

## Before you apply

1. Read the current SignPath Foundation terms and the open-source signing program page.
2. Confirm the repository still meets them (OSI licence, no proprietary payload,
   active maintenance, documented releases, no hacking-tool positioning).
3. Enable multi-factor authentication on GitHub and, after acceptance, on SignPath.
4. Decide the SignPath approver and, when you have one, name a deputy in `RELEASING.md`.

## Draft answers (edit before sending)

- **Project name:** CordMic
- **Repository:** https://github.com/Lukecinate/cordmic
- **Licence:** MIT
- **Project home / docs:** the GitHub repository and `docs/CODE_SIGNING.md`
- **Maintainer contact:** _fill in before submitting_
- **What is signed:** Windows application, installer, and an optional INF-only
  accessory catalog. The Android APK is signed with a separate key held by the
  maintainer and is not submitted to SignPath.
- **Trusted build system:** GitHub Actions, GitHub-hosted runners only, workflow
  `.github/workflows/release.yml`. Signing policy slug to create after acceptance:
  `release-signing`, with **manual approval**.
- **Artifact configurations to create after acceptance:** `inner-binaries`,
  `driver-catalog`, `installer`.
- **Privacy:** CordMic sends nothing except update checks the user enabled.

## After acceptance (you do these; this repo will not)

1. Create the SignPath organisation, enable MFA, link GitHub as the trusted build system.
2. Create project `cordmic` and the policy and artifact configurations above.
3. Store the SignPath API token as a GitHub Actions secret in the protected
   environment `release` (required reviewer: you). Never paste the token into an
   issue, chat, or this file.
4. Send only the K1 and K2 **public** keys and key IDs (see `RELEASING.md`) so they
   can be embedded in the app. Keep the private keys offline.

## Microsoft attestation (open item O1)

Attestation (Partner Center and an EV certificate) is a later decision, only if a
SignPath-signed catalog is refused on a PC with Secure Boot and Memory Integrity on.
**Cost ceiling: _not written_. No spend until the maintainer writes a ceiling.**
