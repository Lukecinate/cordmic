# Changelog

All notable changes to CordMic are documented here. Versions follow [Semantic Versioning](https://semver.org/).

## [0.4.0] - 2026-11-03

"Quiet utility" redesign of the Android and Windows apps. The audio protocol, ports and streaming behaviour are
unchanged; 0.4.0 works with 0.3.x on the other side.

### Changed
- **Android app redesigned** around one status card: a plain-language state (Ready to start, Waiting for USB
  tethering, Looking for your PC, Streaming to your PC, Microphone unavailable), one next step, and one primary
  Start/Stop button. While streaming the card shows a live input level meter, packets sent, send errors and the age
  of the last PC reply.
  - Settings (PC IP, UDP port, microphone mode, AAudio engine) move into a card that collapses with a one-line
    summary once streaming, and is locked while streaming.
  - A collapsible **Details** card shows the capture engine, sample rate, buffer sizes and USB interface.
  - Light theme with a dark theme that follows the system; calm neutral palette with one teal accent, amber and red
    reserved for warnings and errors; Plus Jakarta Sans as the UI font.
  - Accessibility: 48 dp minimum touch targets, AA contrast in both themes, TalkBack labels and live-region
    announcements for state changes, edge-to-edge layout with system-bar insets.
- **Windows app redesigned** in the same language: the setup guide uses a status card with the same state wording,
  a live level meter and real PC-side stats (estimated delay, round trip, loss; shown only while connected), numbered
  steps in a card, and one primary action per step. Plus Jakarta Sans is embedded (falls back to Segoe UI).
- Tray menu is flat and uses the same palette; tray states are now grey (waiting), teal (streaming), amber
  (attention) and red (error), with tooltips in the form "CordMic · <state>".
- Microphone mode names in the Windows app now match the phone app (Voice recognition, Unprocessed,
  Voice communication; Indonesian: Pengenalan suara, Tanpa pemrosesan, Komunikasi suara).

### Added
- New app icon (Material Icons Round `mic` on teal) delivered as standard files: Android mipmap PNGs with an
  adaptive icon, a multi-size Windows `.ico` (16/24/32/48/256 px) for the app, installer and four tray states.
- The Android capture engine exposes its peak level and counters to the UI (read-only; no change to capture or
  sending).
- Robolectric screenshot renders of the Android layouts and design previews of the Windows UI in
  `docs/screenshots/`, plus a Screenshots section in both READMEs.
- `docs/THIRD_PARTY_NOTICES.md` with font, icon and library licenses.

### Notes
- The release APK is signed with the same release key as 0.3.1, so it installs as an update.
- The Windows screenshots in the README are design previews rendered from the app's tokens and strings, not
  captures of the running app.

## [0.3.1] - 2026-11-03

### Added
- English and Indonesian user interface:
  - Windows app follows the Windows display language, with a **Language / Bahasa** tray menu
    (Automatic / English / Bahasa Indonesia).
  - Android app ships `values` (English) and `values-in` (Indonesian) string resources.
  - Installer shows English by default and Indonesian on Indonesian systems.
- Tests that keep the English and Indonesian string tables in sync (.NET and Kotlin).
- MIT license, English README (`README.md`) and Indonesian README (`README.id.md`).

### Changed
- Release APK is signed with the project release key instead of the debug key.
  **If you installed the 0.3.0 APK, uninstall it before installing 0.3.1.**
- Installer publisher and app metadata now read "CordMic contributors".
- Numbers in the tray tooltip are formatted independently of the system locale.

### Fixed
- Android: lint warning about a static Context reference (the streamer now keeps only the application context).

## [0.3.0]

### Changed
- Renamed from UsbMic to CordMic (Android package `dev.cordmic`). The installer removes the old UsbMic
  installation and migrates settings from `%APPDATA%\UsbMic`.

## [0.2.0]

### Added
- Windows tray app with setup guide, NSIS installer, firewall rule and phone-adapter metric task.
- Android binds UDP 50005 on the tethering interface so the PC can contact the phone directly.

## [0.1.0]

### Added
- Android capture app (AAudio, foreground service) and Python reference receiver.
