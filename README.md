<p align="center"><img src="docs/assets/logo.png" width="112" height="112" alt="CordMic logo"></p>

# CordMic

**English** | [Bahasa Indonesia](README.id.md)

CordMic turns an Android phone into a low-latency PC microphone over a **USB cable**. It uses Android's built-in
**USB tethering**, so it needs no Wi-Fi, no USB debugging/ADB and no phone-side drivers. Audio flows from the phone
to a Windows tray app, which plays it into a virtual audio cable that Discord, Zoom and games can use as a microphone.

- **Cable only** – the phone shows up as a wired network adapter (RNDIS/NCM); audio never touches Wi-Fi or the internet.
- **Low latency by design** – 48 kHz mono PCM in 5 ms packets, AAudio low-latency capture, a jitter buffer
  floored to the real Windows period, and WASAPI output through IAudioClient3. Designed for roughly **30–45 ms end-to-end** (see [Latency](#latency)).
- **Runs in the background** – Android foreground service with a persistent notification; Windows tray app that
  detects the phone, reconnects automatically and can start with Windows.
- **Plug & play Windows installer** – firewall rule, phone-adapter priority (so games keep using LAN/Wi-Fi) and a
  one-page connect window.
- **English and Indonesian** – Android app, Windows app and installer follow the system language; the Windows tray
  has a *Language / Bahasa* override.

Downloads: see [Releases](https://github.com/Lukecinate/cordmic/releases/latest).

---

## Contents
- [Screenshots](#screenshots)
- [Requirements](#requirements)
- [Install & quick start](#install--quick-start)
- [Using the Windows app](#using-the-windows-app)
- [Using the Android app](#using-the-android-app)
- [Troubleshooting](#troubleshooting)
- [Known limitations](#known-limitations)
- [Architecture & protocol](#architecture--protocol)
- [Latency](#latency)
- [Build from source](#build-from-source)
- [Python receiver (developer tool)](#python-receiver-developer-tool)
- [Upgrading from UsbMic](#upgrading-from-usbmic)
- [Roadmap](#roadmap)
- [License](#license)

## Screenshots

**Android app.** Real renders of the app's layouts, produced by the Robolectric screenshot test
(`./gradlew testDebugUnitTest -PrecordScreenshots`) with sample values; not photos of a physical phone.

| Waiting for USB tethering | Looking for your PC | Streaming to your PC |
|---|---|---|
| ![Waiting](docs/screenshots/android-waiting.png) | ![Searching](docs/screenshots/android-searching.png) | ![Streaming](docs/screenshots/android-streaming.png) |

| Details expanded | Settings (idle) | Microphone unavailable |
|---|---|---|
| ![Details](docs/screenshots/android-details.png) | ![Settings](docs/screenshots/android-settings.png) | ![Error](docs/screenshots/android-error.png) |

| Dark theme | Bahasa Indonesia | Small screen (320 dp) |
|---|---|---|
| ![Dark](docs/screenshots/android-dark.png) | ![Indonesian](docs/screenshots/android-streaming-id.png) | ![Small](docs/screenshots/android-small.png) |

Landscape: [android-landscape.png](docs/screenshots/android-landscape.png).

**Windows app.** The connect window below is a render of the 0.5.0 layout (this environment cannot host WinForms)
using the app's logo, mic icon, Plus Jakarta Sans and colours, with sample values. The older images are design
previews of the 0.4 setup guide.

| Connect window |
|---|
| ![CordMic connect window](docs/screenshots/windows-main.png) |

| Setup guide, streaming (0.4 preview) | Setup guide, waiting (0.4 preview) | Tray menu and tooltip |
|---|---|---|
| ![Windows setup guide, streaming (design preview)](docs/screenshots/windows-wizard-streaming-preview.png) | ![Windows setup guide, waiting (design preview)](docs/screenshots/windows-wizard-waiting-preview.png) | ![Windows tray (design preview)](docs/screenshots/windows-tray-preview.png) |

More: [virtual mic step](docs/screenshots/windows-wizard-cable-preview.png),
[Indonesian](docs/screenshots/windows-wizard-streaming-id-preview.png).

## Requirements

| | |
|---|---|
| PC | Windows 10 or 11, 64-bit (x64) |
| Phone | Android 10 or newer (minSdk 29), with USB tethering |
| Cable | A USB **data** cable (charge-only cables do not work) |
| Virtual mic | [VB-CABLE](https://vb-audio.com/Cable/) (free, installed separately) |

## Install & quick start

From the [latest release](https://github.com/Lukecinate/cordmic/releases/latest) download
`CordMic-Setup-<version>.exe` (Windows) and `CordMic-<version>-release.apk` (Android).

1. **Install on the PC.** Run `CordMic-Setup-<version>.exe` and accept the UAC prompt. The installer is not yet
   code-signed, so SmartScreen may show "Windows protected your PC": click **More info → Run anyway**.
   When CordMic starts, its setup guide walks you through installing **VB-CABLE** (it opens the official download
   page and detects the driver automatically).
2. **Install on the phone and connect.** Copy the APK to the phone and open it (allow "Install unknown apps" for
   your file manager; on Samsung, temporarily turn off Auto Blocker if it is on). Plug in the USB cable, turn on
   **Settings → Connections → Mobile Hotspot and Tethering → USB tethering**, open CordMic and tap **Start**.
   The phone shows **Streaming to your PC** and the tray icon turns teal.
3. **Pick the mic in your app.** Choose **CABLE Output (VB-Audio Virtual Cable)** as the input device, e.g. Discord:
   *User Settings → Voice & Video → Input Device*; Zoom: *Settings → Audio → Microphone*. For games without a mic
   selector, make CABLE Output the default recording device in Windows Sound settings.

What the installer does:
- installs to `C:\Program Files\CordMic` with a Start menu shortcut (optional: start with Windows, desktop shortcut);
- adds the inbound firewall rule **CordMic (UDP 50005)**, limited to the local subnet, for all profiles
  (tethering networks are usually classified as "Public");
- adds the scheduled task `\CordMic\SetTetherMetric` (runs as SYSTEM at boot, logon and on network changes) that
  sets the phone adapter's interface **metric to 9999**, so internet traffic stays on LAN/Wi-Fi and the phone link
  carries only audio.

Uninstall via *Settings → Apps → CordMic → Uninstall*. This removes the app, shortcuts, autostart, firewall rule and
scheduled task. Settings and logs under `%APPDATA%` / `%LOCALAPPDATA%` are kept.

## Using the Windows app

### Tray icon

| Color | Meaning |
|---|---|
| Grey | Waiting for USB tethering, or *Phone found* but Start not tapped yet |
| Teal | Streaming from your phone, audio is flowing |
| Amber | Needs attention: no virtual mic installed, phone connected but not sending audio, or PC internet is routed through the phone |
| Red | Error: UDP port 50005 is in use by another app, or the audio device failed to open (retried automatically) |

Double-click the icon (or use **Open CordMic**) for the window: one round button (disconnected, searching, connected,
error), a status line with the phone and the live PC-side delay, and a settings panel for the output device, latency
preset, noise cancellation, mic gain, start-with-Windows and the log. Hover the icon for the same delay split
(network = ½ USB round trip, the averaged jitter-buffer wait, and the measured Windows output buffer), plus round-trip
time and packet loss over the last 10 seconds. Right-click for the menu:

- **Output device** – *Automatic* (best virtual cable found) or a specific one (VB-CABLE A–D, VAC, VoiceMeeter).
- **Output mode** – *Low latency* (default: shared mode, about one Windows audio period queued), *Stable* (keeps
  the whole Windows buffer full, as before 0.4.2; use it if audio crackles on a busy PC) or *Exclusive* (WASAPI
  exclusive mode, lowest delay; CordMic then owns "CABLE Input" and falls back to Low latency if the device refuses).
- **Jitter buffer** – Low 5 ms / Normal 10 ms (default) / Safe 20 ms. The app raises the target when the Windows
  period cannot sustain it (a 10 ms period floors Low and Normal at 15 ms). Use Safe if audio still crackles.
- **Mic mode** – explains the capture modes (selected in the phone app).
- **Setup guide**, **Fix phone network priority** (shown only when internet goes through the phone; asks for UAC),
  **Open log**, **Start with Windows**, **Language / Bahasa** (Automatic, English, Bahasa Indonesia), **Exit**.

### Settings and logs

| What | Where |
|---|---|
| Settings | `%APPDATA%\CordMic\settings.json` |
| App log (stats every 10 s while connected) | `%LOCALAPPDATA%\CordMic\logs\cordmic.log` |
| Installer / metric task logs | `C:\ProgramData\CordMic\` |

## Using the Android app

The screen is one status card plus one button. The card says in plain words what is happening
(*Ready to start*, *Waiting for USB tethering*, *Looking for your PC*, *Streaming to your PC*,
*Microphone unavailable*) and gives one next step, e.g. an **Open tethering settings** button. While streaming
it shows the live input level, packets sent, send errors and how long ago the PC last replied. Round-trip time is
measured on the PC side and shown in the Windows app.

1. Turn on USB tethering (see below).
2. Check **Settings** (open while idle, collapsed and locked while streaming):
   - **PC IP address** – leave empty to find the PC automatically; **UDP port** `50005`.
   - **Microphone mode**:
     **Voice recognition** (default, minimal processing, usually the lowest latency),
     **Unprocessed** (raw signal without filters), or
     **Voice communication** (noise suppression and echo cancellation; adds latency, useful without a headset).
   - **Low-latency engine (AAudio)** – on by default; turn off only if audio cuts out. CordMic tries exclusive
     low-latency, then shared low-latency, then shared with no performance hint, and sizes the capture buffer to one burst.
   - **Noise cancellation** – on by default. A small on-phone spectral gate (no extra library). It adds **5.3 ms**.
   - **Mic gain** – 0 to +24 dB, default **+12 dB**, with automatic gain and a soft limiter. These two stay editable while streaming.
3. Tap **Start** and grant the Microphone and Notification permissions. A persistent "CordMic" notification
   (with a Stop button) keeps capture running with the screen off or while you use other apps.
   **Details** shows the capture engine, sample rate, buffer sizes and the USB interface.

The app follows the system light/dark theme and language (English or Bahasa Indonesia).
4. To keep One UI from stopping it: **Settings → Apps → CordMic → Battery → Unrestricted**, and make sure CordMic is
   not in the "Sleeping apps" list.

Android 14+ only allows a microphone foreground service to start while the app is visible, so **Start** must be
tapped from the app screen and the service does not auto-start after a reboot.

### USB tethering on Samsung
1. Connect the phone with a USB data cable.
2. **Settings → Connections → Mobile Hotspot and Tethering → USB tethering → On** (the toggle is grayed out until
   a cable is connected). USB debugging is **not** needed.
3. Windows shows a new adapter, usually "Remote NDIS based Internet Sharing Device" or "UsbNcm".

## Troubleshooting

| Symptom | Try |
|---|---|
| Icon stays grey, "Waiting for USB tethering" | Use a **data** cable. Turn *USB tethering* on again (Samsung turns it off on every unplug). Windows *Settings → Network* should list a "Remote NDIS…" / "UsbNcm…" adapter. |
| "Phone found" but not streaming | Open CordMic on the phone and tap *Start*. Check that the firewall rule *CordMic (UDP 50005)* exists (re-run the installer if it is missing). |
| Red, "Port busy" | Another receiver (e.g. the Python `cordmic_receiver`) is still running. Close it; CordMic retries every 5 seconds. |
| Amber, "No virtual mic yet" | Install VB-CABLE (setup guide step 1); restart the PC if it does not appear. |
| Choppy or robotic audio | *Jitter buffer → Safe (20 ms)*; if it persists, *Output mode → Stable*. Check `lost` / `underrun` in the log. Avoid USB hubs; use a port on the PC directly. |
| Game ping rises after plugging in the phone | *Fix phone network priority* in the tray menu (or run `C:\Program Files\CordMic\tools\set-tether-metric.ps1` as administrator). Alternatively, turn off mobile data on the phone. |
| Discord hears nothing | The input device must be **CABLE Output** (not CABLE Input). The level bar in setup guide step 3 should move while you speak. |
| Audio sounds over-processed or delayed | Use *Voice Recognition* on the phone. If you use *Voice Communication*, turn off Discord's noise suppression and echo cancellation to avoid double processing. |

Manual metric fix (PowerShell as administrator):
```powershell
Get-NetAdapter                                                  # find the phone adapter, e.g. "Ethernet 3"
Set-NetIPInterface -InterfaceAlias "Ethernet 3" -InterfaceMetric 9999
Get-NetRoute 0.0.0.0/0                                          # the default route should use LAN/Wi-Fi
```

## Known limitations

- **Unsigned Windows installer.** The installer and `CordMic.exe` are not Authenticode-signed yet, so SmartScreen
  warns on first run (*More info → Run anyway*). Verify downloads with `SHA256SUMS.txt` from the release.
- **USB tethering must be turned on manually.** Android does not let regular apps enable tethering, and Samsung
  turns it off every time the cable is unplugged. Re-enable the toggle after replugging; the PC side reconnects
  automatically.
- **VB-CABLE is required.** Windows only lists microphones that come from an audio driver, and a user-mode app
  cannot create one. CordMic therefore plays into an existing virtual cable (see [Roadmap](#roadmap)).
- **APK signing.** Starting with 0.3.1, the release APK is signed with the project's own release key
  (certificate `CN=CordMic, O=CordMic contributors`, SHA-256
  `2c:da:64:ae:df:4e:ba:6f:9d:4a:fa:06:e6:85:43:04:65:a6:0d:5d:2a:25:ef:99:ee:f3:cc:16:3e:47:1c:8b`). The APK is
  distributed outside Google Play, so Android asks you to allow installs from unknown sources. Version 0.3.0 was
  signed with a debug key; uninstall it before installing 0.3.1 (Android rejects an update signed with a different key).
- **No clock-drift compensation yet.** Over long sessions the jitter buffer corrects phone/PC clock drift by
  dropping packets when it grows past its limit, which can cause a brief click.
- **Round-trip time is PC-side only.** The phone answers the PC's PING but does not time it, so the phone app
  shows the age of the last PC reply instead of a round-trip figure.
- **Android 14+ start rule.** Capture must be started from the visible app; it does not start on boot.

## Architecture & protocol

```
┌──────────────── Android phone ───────────────┐   USB cable     ┌──────────────── Windows PC ──────────────────┐
│ Mic → AAudio (LOW_LATENCY, 48 kHz, mono,     │ (USB tethering, │ CordMic tray app (.NET 8)                    │
│       16-bit, 5 ms frames = 240 samples)     │  RNDIS/NCM =    │  UDP :50005 → jitter buffer (period-aware)   │
│   → noise suppression, gain, limiter         │  network link)  │  → WASAPI (IAudioClient3, own renderer)      │
│   → 20-byte header (seq + timestamp)         │ ──────────────▶ │  → "CABLE Input" (VB-CABLE)                  │
│ Foreground service type=microphone           │                 │  Discord/Zoom/games use "CABLE Output"       │
│ + persistent notification + wake lock        │ ◀── PING/HELLO  │  as their microphone                         │
└──────────────────────────────────────────────┘                 └──────────────────────────────────────────────┘
```

- **Transport.** USB tethering makes the phone appear as a wired network adapter in Windows (built-in RNDIS/NCM
  drivers). No ADB, no Wi-Fi, and no dependency on USB debugging modes.
- **Discovery.** With tethering on, the phone is the gateway/DHCP server (e.g. phone `192.168.42.129`, PC
  `192.168.42.x`). The app finds the tethering interface (`rndis0` / `ncm0` / `usb0`) and broadcasts **DISCOVER** on
  that subnet; the PC replies **HELLO** (the Windows app also sends HELLO proactively when it sees a tethering
  adapter), and the phone then streams unicast to the PC. The PC IP can also be entered manually.
- **Keep-alive and reconnect.** The PC sends **PING** every 500 ms and the phone answers **PONG** (also used to
  measure round-trip time). If the phone hears nothing from the PC for 3 seconds, or the socket fails after an
  unplug, it returns to discovery. If the AAudio stream is disconnected (e.g. an audio route change), it is reopened.
- **Header** (20 bytes, little-endian; identical in `Protocol.kt`, `protocol.py` and `CordMic.Core`):

  | Offset | Size | Field |
  |---|---|---|
  | 0 | 2 | magic `"UM"` |
  | 2 | 1 | version (1) |
  | 3 | 1 | type: 1 AUDIO, 2 DISCOVER, 3 HELLO, 4 PING, 5 PONG, 6 BYE |
  | 4 | 4 | sequence number (uint32, wraps) |
  | 8 | 8 | timestamp in µs (sender's monotonic clock) |
  | 16 | 2 | PCM frame count |
  | 18 | 2 | flags: bits 0–3 codec (0 = PCM s16le), bits 4–7 channels − 1, bits 8–11 sample rate (0 = 48 kHz). Bits 12–14 (0.5.0, ignored by 0.4.2): noise cancellation, AGC, AAudio |

  An AUDIO payload is raw PCM: 240 frames × 2 bytes = 480 bytes per 5 ms (~100 kbit/s).
- **Jitter buffer.** Reorders packets by sequence number. The preset is Low 5 / Normal 10 / Safe 20 ms, then raised
  so it is at least one render pull plus one packet. On a 10 ms shared period that floors Low and Normal at 15 ms
  (3 packets): the callback consumes 10 ms and 5 ms is left, so a late packet does not empty the buffer. Missing
  packets are replaced with silence. Every ~0.5 s a standing backlog above the target is removed by playing two
  packets as one with a crossfade. A hard limit still drops the oldest packets on a large burst.
- **Output.** CordMic's own event-driven WASAPI loop on an MMCSS "Pro Audio" thread (NAudio is not used). *Low latency*
  opens the stream with IAudioClient3 at the engine's minimum shared period and writes at most one period per callback.
- **RTT.** The PC sends PINGs from a second UDP socket on a random port with its own thread; the phone answers to
  the sender, so PONGs never wait behind audio packets.

### Repository layout

```
cordmic/
├── android/                      Gradle project (Kotlin + C++/AAudio)
│   └── app/src/main/
│       ├── AndroidManifest.xml   permissions + foregroundServiceType="microphone"
│       ├── cpp/aaudio_capture.cpp  low-latency AAudio capture (JNI)
│       ├── java/dev/cordmic/     MainActivity, MicService, Streamer, Capture, TetherNet, Protocol
│       └── res/values[-in]/      English and Indonesian strings (+ values-night for the dark theme)
├── docs/                         screenshots, logo (assets/), THIRD_PARTY_NOTICES.md
├── third_party/fonts/            Plus Jakarta Sans + OFL license
├── windows-app/                  Windows app, C# / .NET 8
│   ├── src/CordMic.Core/         protocol, jitter buffer, UDP receiver, settings, string tables
│   ├── src/CordMic.App/          WinForms tray app + own WASAPI (IAudioClient3) + connect window
│   ├── src/CordMic.Harness/      console receiver for loopback testing
│   ├── tests/CordMic.Tests/      xUnit tests (incl. interop with sim_sender.py)
│   ├── installer/                cordmic.nsi + tools/*.ps1 (firewall, adapter metric)
│   └── build.sh                  test → publish win-x64 → installer
└── windows-receiver/             Python reference receiver + phone simulator (sim_sender.py)
```

## Latency

The pipeline is **designed for roughly 30–45 ms end-to-end**. This is a design budget, not a measurement:
AAudio capture ~5–10 ms + 5 ms packet + USB link 1–2 ms + 10 ms jitter buffer + WASAPI/VB-CABLE ~10–20 ms.
Actual latency depends on the phone, the mic mode and the PC audio stack.

**PC-side delay shown by the app.** Network (½ round trip) + jitter buffer (time-averaged) + Windows output (the
measured average fill of CordMic's WASAPI buffer). It leaves out phone capture, the Windows audio engine period,
VB-CABLE's own buffering and the recording app. Before 0.4.2 the output part was the fixed 10 ms setting while
NAudio actually kept a larger buffer full, and the buffer part was a single instant reading; numbers from 0.4.1
and 0.4.2 are therefore not directly comparable.

A 0.4.2 log from a real Windows 11 PC (VB-CABLE, 10 ms shared period, phone not on AAudio) showed a clean link
(loss about 0, jitter under 1 ms, round trip 5–7 ms) and a jitter buffer that still underran about 30 times a
minute on Normal and about 370 times a minute on Low, with several times as many hard overflows. The render
callback consumes one 10 ms period. Normal's 10 ms target and Low's 5 ms target leave nothing after that pull,
so the buffer hits empty, rebuffers, then the hard cap dumps the pile. 0.5.0 asks IAudioClient3 for the shortest
shared period the device will give and refuses a jitter target below one pull plus one packet. A synthetic
10 ms-period run of that failure mode dropped underruns from about 570/min to about 2/min. Noise cancellation
on the phone adds 5.3 ms. For the lowest delay: *Latency → Low*, *Output mode → Low latency* (then try
*Exclusive*), leave noise cancellation on only if you want it, and set both "CABLE Input" and "CABLE Output"
to 48000 Hz in Windows Sound settings so Windows does not resample.

To measure it on your own setup: in OBS add two *Audio Input Capture* sources, (a) "CABLE Output" and (b) another
physical mic (headset/webcam). Record, clap close to **both** mics, open the recording in Audacity and measure the
distance between the two clap peaks. That difference is CordMic's latency minus the reference mic's latency.
Repeat with the Low jitter preset.

## Build from source

### Windows app and installer
Requires the .NET 8 SDK, NSIS 3 (`makensis`) and Python 3 (used by interop tests). Works on Windows (Git Bash/WSL),
Linux or macOS:
```bash
cd windows-app && ./build.sh
```
Runs the tests, publishes `windows-app/publish/CordMic.exe` (win-x64, self-contained, single file) and writes
`dist/CordMic-Setup-<version>.exe`.

### Android app
Requires JDK 17, Android SDK Platform 34 and Build-Tools 34, NDK 26.1.10909125 and CMake 3.22.1
(Android Studio or `sdkmanager`). Built with AGP 8.5.2, Gradle 8.7 and Kotlin 1.9.24.
```bash
cd android
echo "sdk.dir=/path/to/Android/Sdk" > local.properties   # Android Studio creates this automatically
./gradlew testDebugUnitTest lint assembleRelease
```
Release signing is read from a properties file outside the repository
(`storeFile`, `storePassword`, `keyAlias`, `keyPassword`), passed via `CORDMIC_SIGNING_PROPERTIES=/path/file` or
`-PcordmicSigning=/path/file`. Without it, local release builds are signed with the debug key.

### Tests
| Suite | Command | Covers |
|---|---|---|
| Python (24 tests) | `cd windows-receiver && python -m unittest discover -s tests` | protocol, jitter buffer, output callback, UDP loopback (clean and lossy) |
| .NET (70 tests) | `cd windows-app && dotnet test` | protocol golden vectors, jitter buffer and catch-up, render-period floor and allocation-free hot path, voice chain, ping backoff, separate ping socket, adapter/virtual-cable rules, settings and migration, UDP loopback, interop with `sim_sender.py`, localization tables |
| Kotlin (22 tests) | `cd android && ./gradlew testDebugUnitTest` | header byte-compatibility with Python, voice-chain delay/gain/limiter, English/Indonesian string parity, level meter and IP helpers, Robolectric screenshot renders (`-PrecordScreenshots` rewrites `docs/screenshots/android-*.png`) |

## Python receiver (developer tool)

`windows-receiver/` contains the reference receiver used for protocol development and debugging. For everyday use,
install the Windows app instead (both bind UDP 50005, so run only one).

```bat
cd windows-receiver
pip install -r requirements.txt
python -m cordmic_receiver --list-devices
python -m cordmic_receiver --device "CABLE Input" --jitter-ms 10
```

| Option | Default | Meaning |
|---|---|---|
| `--device` | default output | device index or part of its name (e.g. `"CABLE Input"`) |
| `--jitter-ms` | 10 | jitter buffer target; lower to 5 if stats are clean, raise if you see `plc` / `underrun` |
| `--exclusive` | off | WASAPI exclusive mode: lowest output latency, but locks the device |
| `--blocksize` | 240 | callback block size (5 ms) |
| `--channels` | 2 | mono duplicated to stereo (VB-CABLE defaults to stereo) |
| `--tray` | off | tray icon (pystray + pillow) |
| `--log file` | – | write logs and stats to a file |
| `--no-audio` | off | receive and print stats only (network debugging) |

Stats fields: `lost` = sequence gaps, `plc` = slots filled with silence, `late` = packets that arrived after their
turn, `underrun` = buffer empty, `ovf` = packets dropped because the buffer was full, `jitter` = arrival jitter
(RFC 3550), `rtt` = PING/PONG round trip over USB, `est_pc_side` = buffer + output latency + RTT/2 (PC side only).

Without a phone, `python sim_sender.py --host 127.0.0.1 --seconds 10` sends a 440 Hz test tone
(`--loss` and `--jitter-ms` simulate a bad link).

For best results set VB-CABLE to 48 kHz: *Control Panel → Sound → Playback → CABLE Input → Properties → Advanced*
and *Recording → CABLE Output → Advanced*. Do not make CABLE Input your default playback device.

## Upgrading from UsbMic

CordMic was previously called **UsbMic**. The audio protocol is unchanged (`UM` header, UDP 50005).

- **Windows:** run the CordMic installer. It closes `UsbMic.exe` and removes the old firewall rule, scheduled task
  `\UsbMic\SetTetherMetric`, startup entry, shortcuts, `C:\Program Files\UsbMic` and the old uninstall entry.
  Settings in `%APPDATA%\UsbMic` are copied to `%APPDATA%\CordMic` on first start.
- **Android:** the package name changed (`dev.usbmic` → `dev.cordmic`), so CordMic installs as a new app. Grant the
  permissions again and uninstall the old UsbMic app so the two do not compete for the mic.

## Roadmap

1. **Opus** (48 kHz, 2.5/5 ms frames, FEC) as an option; PCM stays the default.
2. **Clock-drift compensation** – insert or drop single samples instead of whole packets.
3. **Native virtual audio driver** ("CordMic Microphone" without VB-CABLE), based on Microsoft's SysVAD samples or
   ACX on Windows 11. Kernel drivers must be signed by Microsoft (EV certificate + attestation signing), which is
   why this is a separate milestone. Windows has no user-mode API for a system-wide virtual microphone.
4. **Code signing** (Authenticode) for the installer and app so SmartScreen does not warn.
5. Better packet-loss concealment.

## License

[MIT](LICENSE) © CordMic contributors. VB-CABLE is a separate product by VB-Audio and is not included.

Design assets: the CordMic logo is an original project asset (generated by `tools/brand/make_logo.py`); the in-app
icons are [Material Icons](https://fonts.google.com/icons) (Round style, Apache 2.0) by Google; the UI font is [Plus Jakarta Sans](https://github.com/tokotype/PlusJakartaSans)
(SIL Open Font License 1.1). Details and the other libraries are listed in
[docs/THIRD_PARTY_NOTICES.md](docs/THIRD_PARTY_NOTICES.md).

## Author

CordMic is developed by [Lukecinate (Rozan Abid)](https://github.com/Lukecinate).
