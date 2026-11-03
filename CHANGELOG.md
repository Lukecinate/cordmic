# Changelog

All notable changes to CordMic are documented here. Versions follow [Semantic Versioning](https://semver.org/).

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
