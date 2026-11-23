# Research notes for CordMic 1.0.0

Looked up 23 November 2026 (Asia/Jakarta). These notes informed the 1.0.0
stable release. They are not a product comparison page and they do not
reproduce anyone else's source. Competitor apps listed here include GPL
projects (AndroidMic, QuicMic); CordMic does not copy their code or
decompile their packages.

## What a credible major release needs

For a first supported stable after a long 0.7.2 / 0.8 beta line, the bar that
was applied here:

- A version number that does not over-claim. 1.0.0 is the supported tethering
  product. 0.8.0 stable is not used, because that number would imply USB
  accessory (AOA) hardware gates had passed. They have not.
- Honest limitations in the README, changelog, and release notes: USB
  experimental, off by default, untested on hardware; Auto stays tethering;
  Gate H / G-M1 stays unset; Windows builds unsigned while SignPath is not
  configured.
- A migration path: settings from 0.7.2 and 0.8.0 betas still load; the
  in-app updater follows `/releases/latest` and offers this stable to those
  users; prereleases stay ignored.
- Security work on the shipped bits: patched runtime in the self-contained
  exe, installer toolchain, dependency audit, CI scanning, a look at the
  network parser, updater, installer, and diagnostics.
- A real fix for a user-visible bug (Settings page scrolling), with tests.
- Checksums on release assets; existing tags never moved; CI green before
  the new tag.

Items that still need a person, and are not claimed done: Authenticode
signing (SignPath organisation and secrets), hardware evidence for Gate H,
Dependabot alerts/security updates at the GitHub organisation/repo level,
keystore backup, and company / managed-PC deployment.

## Competitor phone-as-mic apps (public)

Looked at public feature lists, store pages, and recent user-visible releases
or complaints. Used only as context for what people already struggle with.

| Product | What is public | Recent public notes (2026) |
|---|---|---|
| WO Mic | Phone as PC mic, Wi-Fi and USB, with a Windows driver | Android 17 / Wi-Fi fix noted around 11 November 2026 |
| Phone As Mic | Simple phone-as-mic utility | Store listing updated 9 August 2026 |
| AudioRelay | Low-latency audio over the network, not USB-tethering-only | Driver and connection friction show up in public threads |
| DroidCam / iVCam | Camera + mic over USB or Wi-Fi, often with a desktop driver | USB driver / Secure Boot / signing friction is a recurring complaint |

Recurring public complaints that match pain CordMic already tries to avoid or
already documents:

- **Drivers and Secure Boot.** Several competitors need a signed kernel
  driver. CordMic's supported path is USB tethering (RNDIS/NCM) plus a
  virtual audio cable; it does not ship a phone-side USB driver and does not
  bundle VB-CABLE.
- **Reconnects and tethering that drops when the cable moves.** CordMic
  already reconnects on the PC side after tethering is turned back on, and
  0.7.2 added tethering diagnosis. No new protocol was copied from anyone
  else for 1.0.0.
- **Latency drift and “it sounds late after a while.”** CordMic already has
  a resampling drift controller and a render-period-aware jitter target.
  1.0.0 does not change that behaviour.

Nothing from AndroidMic, QuicMic, or other GPL or closed binaries was
incorporated. Useful 1.0.0 work that came out of this pass is original:
the Settings wheel-scroll fix, the NSIS 3.13 rebuild, the .NET 8.0.34
runtime pin, HTTPS-only updater downloads, CodeQL, and secret scan.

## .NET / NuGet advisories

Microsoft's 10 November 2026 Patch Tuesday includes .NET 8 runtime **8.0.34**
/ SDK **8.0.428**. A large part of that bulletin is ASP.NET Core and Visual
Studio. CordMic's Windows app is a self-contained WinForms executable, so
the shipped host must still be rebuilt against 8.0.34. CI is pinned to SDK
8.0.428 and the app sets `RuntimeFrameworkVersion` 8.0.34.

`dotnet list package --vulnerable --include-transitive` on the solution,
with current NuGet sources, reported no vulnerable packages. The only
outdated test-only package bumped in this release is coverlet.collector
10.1.0 → 10.1.1. CVE-2026-118747 (`System.Formats.Cbor`) is not a CordMic
dependency.

## Gradle / Android

Direct APK dependencies remain androidx.core-ktx 1.13.1, appcompat 1.7.0,
constraintlayout 2.1.4, and Material 1.12.0. OSV Scanner 2.7.1, run against
the tree on 23 November 2026, reported 70 findings / 18 packages, all from
`android/gradle/verification-metadata.xml`. Those versions are pulled by
the **build** toolchain (Android Gradle Plugin 8.5.2 and Kotlin 1.9.24):
Guava, Netty, protobuf, commons-io, Bouncy Castle, jose4j, jdom2,
commons-compress, httpclient, and `org.jetbrains.kotlin:kotlin-gradle-plugin`.
The OSV “fixed version” for the Kotlin plugin was listed as 2.4.20-Beta1,
which is not acceptable in a stable. They are not packaged in the APK.

A large AGP/Kotlin jump to clear those transitives is left for a later
release, after a dedicated compatibility pass. The allowlist is unchanged
except for pins already on main after 0.8.0-beta.3 (junit-bom modules).

## NSIS

NSIS 3.11, previously pinned, is in the CVE-2026-42171 range (3.06.1–3.11):
when an installer runs as SYSTEM and `my_GetTempFileName` fails, a Low-IL
temp directory may be used. 3.12 fixed it; 3.13 (27 September 2026) also
fixes an nsExec crash. Official zip SHA-256
`ba63dffc4410ee89193e1cb5a41989991bd77c61068da17e3156d136b7b0b3d8`
(2,363,925 bytes), verified 23 November 2026. Typical user installs do not
run as SYSTEM; rebuilding still puts the fixed exehead in the installer.

## GitHub Actions

Existing workflows already pin marketplace actions to 40-character SHAs.
This release adds `codeql.yml` using `github/codeql-action` v4.39.4 commit
`1bd86f2fdb4b86187fa4059f16f31a3202acc9ea` (v3 is deprecated from December
2026). Dependabot's SignPath action PR (2.3 → 3.0) is a major bump: the
default `connector-url` changes from `https://githubactions.connectors.signpath.io`
to `https://pipelineconnector.connectors.signpath.io/GitHubActions/GitHubCom`,
and 3.2.0 is already current. It is left unmerged; signing jobs cannot be
exercised without organisation and secrets.

## Parser, updater, installer, diagnostics

Reviewed in this pass (no GPL, no decompile):

- **Network parser** (`FrameDecoder` / `FrameCodec`): length cap, CRC32C,
  one-byte resync on a bad header, existing chaos and fuzz tests on
  split/capped feeds. No change required for 1.0.0. An extra fuzz of
  `AppUpdate.ParseLatestJson` was added.
- **Updater:** already ignores prereleases, refuses downgrades, verifies
  SHA-256, caps size, and sanitises `html_url` to GitHub. 1.0.0 also
  refuses non-HTTPS (or user-info) asset and SHA256SUMS URLs before any
  download. Settings from 0.7.2 and 0.8.0 betas still load;
  experimental USB flags default off.
- **Installer:** rebuilt with NSIS 3.13. No new driver, no key material.
- **Diagnostics:** `SanitizedDiagnostics` already masks identifiers and
  records the gate as `PENDING-HARDWARE`. Gate constant stays false.

## Folded into 1.0.0

Scroll fix, NSIS 3.13, .NET 8.0.34, HTTPS download gate, CodeQL, secret
scan, documentation of USB status, updater offer tests for 1.0.0. Competitor
features (kernel drivers, Wi-Fi paths, camera) were not added.
