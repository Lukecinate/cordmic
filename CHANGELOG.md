# Changelog

All notable changes to CordMic are documented here. Versions follow [Semantic Versioning](https://semver.org/).

## [0.6.1] - 2026-11-13

Security and robustness release from a concurrency and security audit of 0.6.0. Android `versionCode` is 17 /
`versionName` 0.6.1. Wire version stays 1. With the new pairing code off (the default), 0.6.1 works with 0.6.0 and
0.5.4–0.5.7 on the other side; a phone older than 0.6.1 cannot answer a pairing code, so leave the option off when
one is in use. The release APK is signed with the same key as 0.6.0. The Windows installer is not
Authenticode-signed.

### Security
- **PC accepts only the USB tethering phone.** The receiver handles packets only from `127.0.0.1` and the subnet
  of the detected USB tethering adapter, and locks to the first phone that streams: audio, PONG and BYE from other
  addresses are ignored until that phone stops or says goodbye. Untrusted senders get no reply and are reported
  once in the status line and the log (rate-limited).
- **Narrower firewall rule.** *CordMic (UDP 50005)* is now created for the *Private* profile and `127.0.0.1` only.
  The SYSTEM task marks the tethering network as *Private* and limits the rule to its subnet on every network
  change and on install or upgrade (before: local subnet, all profiles).
- **Optional pairing code** (*Require a pairing code on the phone*, off by default). The PC shows a 6-digit code;
  the phone enters it once under Settings and answers a single-use challenge with an HMAC-SHA256 proof (PBKDF2 key,
  the code never travels). Five wrong answers pause pairing from that address for 60 s.
- **Downloaded installers cannot be swapped before launch.** Update and VB-CABLE setups are verified in memory,
  written to a per-run folder with a random name and a user/SYSTEM/Administrators-only ACL, and kept open with
  write and delete sharing denied, hash checked on the same handle, until the started process has opened them.
  The VB-CABLE setup must also carry a valid Authenticode signature from VB-Audio. The PowerShell VB-CABLE path
  stages in the Administrators-only `%ProgramData%\CordMic` instead of `%TEMP%`.
- **Elevated helper paths.** The cable helper uses `%ProgramData%\CordMic` only when it is a real folder owned by
  Administrators, creates its backup with `CreateNew`, and writes its log there (`cable-helper.log`).
- **Android.** With a manual PC address the phone accepts control packets only from that address; in automatic
  mode settings (PING) are taken only from the PC it streams to.

### Fixed
- **Optimize VB-CABLE no longer freezes the window.** The elevated helper is awaited off the UI thread; the button
  shows progress and turns into *Stop waiting* (the helper itself finishes on its own; it cannot be killed from the
  non-elevated app). Waiting ends after 2 minutes.
- **Auto update asks first.** Automatic checks only download and verify the update and then show *Update ready*;
  installing starts from that notice or the button. Declining UAC keeps the update ready. If a silent install
  fails, the installer starts CordMic again.
- **Android wakelock.** The stream wakelock is renewed every 30 minutes (1 h timeout) while streaming instead of
  lapsing after 12 hours.
- **Android Stop→Start.** A new session waits for the previous capture thread to release the microphone instead of
  briefly reporting *Microphone failed*.
- **Android update flow.** The install-status receiver lives on the application context, so rotating or leaving the
  screen during an update no longer loses the install prompt; a second download cannot start while one runs; the
  APK is staged under a fixed name via a temporary file.
- Studio noise reduction: no native access after dispose (the chain is freed only when the worker has exited);
  jitter-buffer reconfiguration is serialised; audio headers are fully validated (codec, frame count 1–1920,
  format) before use; the receive thread survives unexpected exceptions and logs them.
- Logging from the audio threads goes through a bounded queue (4096 lines, drops counted) written by a background
  thread; repeated per-packet messages are rate-limited.
- Settings are saved through a unique temporary file, flushed and swapped with a `.bak` copy; a corrupt file is kept
  as `.bad-<time>` and the backup is loaded.
- Receiver and renderer stop: sockets are closed before joining threads, and a thread that does not exit in time is
  logged and its native resources are left alone instead of freed under it.

### Tests
- 15 new Windows regression tests (sources, peer lock, pairing vector and replay, malformed audio, concurrent
  reconfiguration, dispose, bounded log, settings recovery, private folders, zip-slip). The two wall-clock CPU tests
  now assert deterministic work counts and zero allocations; timings are printed only. Test packages updated
  (no vulnerable transitive packages remain).
- 7 new Android unit tests (pairing vector shared with the PC, control-packet filter, wakelock schedule, staging name).

## [0.6.0] - 2026-11-11

Packet-loss concealment by pitch repetition, drift correction by resampling, and an optional PC Studio noise
reduction based on RNNoise. Android `versionCode` is 16 / `versionName` 0.6.0. Wire version stays 1: the new PING
bit and diagnostics flags are ignored by 0.5.x peers, so 0.6.0 works with 0.5.7, 0.5.6, 0.5.5 and 0.5.4 on the other
side. With Studio off (the default) the phone-side voice processing is the same as in 0.5.7. The release APK is
signed with the same key as 0.5.7. The Windows installer is not Authenticode-signed.

### Added
- **PC Studio noise reduction** (Windows, opt-in, off by default). Presets *Balanced*, *Whisper priority* and *Max*
  in the main window next to Noise cancellation.
  - RNNoise (BSD-3, native `rnnoise.dll`, pinned build) runs at 48 kHz in parallel with a PC copy of the 0.5.7
    Strong suppressor aligned to RNNoise's 960-sample delay. WhisperGuard mixes the Strong output back in while
    RNNoise removes a clearly present signal, so whispers and soft consonants survive. *Max* is RNNoise only.
  - The PC asks the phone (PING bit 5) to send capture without its noise suppression, gate, Auto volume and pause
    expander. Mic gain, Voice clarity and the limiter stay on. The phone's stored settings are unchanged and apply
    again when Studio is off; the phone confirms the bypass in its diagnostics, and the PC shows a notice when an
    older phone does not.
  - Runs on a worker thread with a 10 ms frame, never in the WASAPI callback, with no per-frame allocation. If
    3 deadlines are missed within 1 s, Studio turns off for the session, a notice appears and the phone's own noise
    cancellation is used again.
  - Added latency is about 30 ms (RNNoise 20 ms plus the 10 ms worker frame); the presets are labelled `+30 ms`.
- **B3 loudness Auto volume** inside Studio (when Auto volume is on): K-weighted loudness toward −21 LUFS with the
  gain frozen in pauses and a −1 dBFS limiter. The gain is saved and used as the starting point next session.
- **Move-closer hint.** *Move the phone closer (about 15 cm) for a cleaner sound* appears when the estimated
  speech-to-noise ratio stays below 15 dB for 10 s and hides above 18 dB for 10 s.
- **Silenced-microphone warning.** The phone registers an Android recording callback before capture starts and
  reports when Android silences its capture because another app took the microphone. Both apps show a warning.
- **Privacy-sensitive capture** on Android 11+ (AudioRecord via `setPrivacySensitive(true)`, AAudio via
  `AAudioStreamBuilder_setPrivacySensitive` looked up at run time). `VOICE_RECOGNITION` stays the default source.
- **Hidden noise suppression check** (logged only): if the room tone falls more than 8 dB within 2 s of the start
  with no speech, the phone flags `devns=1` in the PC log.
- New PC log fields: `drift=`, `uplc=`, `studio=`, `studiocpu=`, `studiogain=`, `snr=`, `silenced=`, `devns=`.
- `tools/harness/`: the regression harness (developer tool, not shipped). Evaluation data is downloaded at test time
  into a git-ignored cache; EARS clips (CC-BY-NC) are for local evaluation only and are never committed.

### Changed
- **Packet-loss and underrun concealment.** Lost packets and buffer underruns repeat the last pitch period with a
  2 ms crossfade, keep full level for 10 ms and then fade (silent after 30 ms of continuous loss). Unvoiced history
  is attenuated faster so repeated noise does not buzz. This replaces the short decay of 0.5.x.
- **Drift correction.** A cubic resampler driven by a PI controller on the buffer fill replaces the crossfaded
  sample slips; `slip=` therefore stays 0 and `drift=` shows the corrected clock difference.
- Android: the microphone-direction calls (`setPreferredMicrophoneDirection`, field dimension,
  `getActiveMicrophones`) are fully guarded; an exception or an empty list counts as "unknown" and capture continues.

### Release gates (measured on the build machine)
- PLC: vectors match the reference within 2.9e-8 (PLC-1), 0 % loss is bit-identical, single-loss error 1.1e-14,
  largest step 0.0131 vs the tone's own 0.0131, worst loss frame 0.12 ms.
- Resampler: vectors within 2.8e-8 (controller 5e-12); closed loop at 0/±100/±300 ppm with 3 ms jitter: THD+N −87 to
  −97 dB, 0 underruns, fill 1435–1443 samples (target 1440), ratio jitter 10–36 ppm; 8 kHz open loop −64.5 dB;
  11 µs per frame.
- Studio noise reduction on the held-out set (C# code, 7 EARS talkers × 6 DEMAND noises): whisper 20 dB Balanced
  mid loss 3.09 dB, ESTOI 0.935× noisy; Whisper preset mid loss 1.29 dB (20 dB) and 1.79 dB (10 dB); regular speech
  DNSMOS OVRL +0.38 over 0.5.7 Strong at 5 dB and +0.12 at 20 dB (PESQ 3.27 vs 2.97 noisy); Balanced vs Max −0.03;
  OVRL win rate over Strong 91 % (whisper) / 92 % (regular). C# matches the Python reference within 0.01 OVRL.
- Auto volume (B3), 14 room × distance cells: −21.1 to −20.5 LUFS, pause gain change ≤ 0.51 dB mean / 3.9 dB max,
  re-onset −0.4 to +0.9 dB, breathing ≤ +4.4 dB, peaks −1.00 dBFS, PESQ within 0.02 of the reference.
- CPU: Studio chain 0.50 ms mean, 1.23 ms p99.9 per 10 ms frame, 0 bytes allocated and 0 gen-0 collections over
  70 s; Light 0.04 ms; watchdog falls back in 0.65 s when the worker stalls.
- Not met: the plan's ≤ 12 ms latency budget for Studio (measured about 30 ms), so Studio stays opt-in. The
  silenced-microphone and privacy-sensitive checks (AND-1, AND-2) need a phone and were not run for this release.

### Known limitations
- Whispers can end up 2–7 LU below the −21 LUFS target in Studio Auto volume.
- With Balanced, bumps such as a bus or keyboard can still be heard in pauses; Max avoids it.
- Not tested on real phones or on Windows hardware for this release; the Windows build was produced and tested
  with the .NET SDK on Linux.

### Tests
- .NET (189): PLC and resampler vectors ported from the research reference, closed-loop drift through the jitter
  buffer (s16 and float), lost-packet and underrun concealment, RNNoise delay, Studio CPU and allocations, worker
  watchdog, WhisperGuard presets, B3 Auto volume, move-closer hint, Strong delay, diagnostics flags (golden bytes
  shared with Kotlin), Studio PING bit. Timing tests run in a serial collection.
- Kotlin (46): Studio bypass plan, diagnostics flags (same golden bytes as C#), PING Studio bit, guarded
  mic-direction calls with throwing and empty fakes, hidden noise suppression detector.

## [0.5.7] - 2026-11-10

Smoother Strong noise reduction and phone voice-chain diagnostics in the PC log. Android `versionCode` is
15 / `versionName` 0.5.7. Wire version stays 1. The diagnostics are requested by the PC and sent by the phone
only on request, so 0.5.7 works with 0.5.6, 0.5.5 and 0.5.4 on the other side; with an older peer the new log
fields show `-`. The release APK is signed with the same key as 0.5.6. The Windows installer is not
Authenticode-signed.

### Fixed
- **Strong noise cancellation sounded static, robotic or watery on louder speech.** The 0.5.6 Strong filter
  used spectral subtraction with a −26 dB floor and fast per-bin changes, so leftover hiss fluttered from
  hop to hop (musical noise), and its noise estimate could follow whispers and s/f/sh sounds and then cut
  them. In a synthetic test (white noise at −50 dBFS with a voiced harmonic signal) about 6 % of per-bin
  gain steps exceeded 8 dB, the RMS step was 4.3 dB and the speech harmonics were cut by 15 dB. With 0.5.7
  the same signal has no steps above 8 dB, an RMS step of 0.7 dB and harmonics at about −6 dB.
  - Each bin now uses a Wiener gain from a decision-directed SNR estimate instead of direct subtraction.
  - The Strong floor is −15 dB (was −26 dB), so a little natural background remains instead of a
    fluttering near-silence. Light stays at −10 dB.
  - Gains are smoothed across neighbouring bins and over time, opening faster than they close.
  - The noise estimate is computed on smoothed power, follows quieter passages within a few tens of
    milliseconds and otherwise rises only about 3 dB/s, so speech, whispers and fricatives do not train it.
  - While the band above 3 kHz is clearly above its noise level, those bins are kept at −4.4 dB or higher.
    A synthetic fricative or whisper burst over −55 dBFS hiss now passes Strong within 1 dB (was −25 dB).
  - The Light filter uses the same estimator and is also steadier.

### Added
- **Phone diagnostics in the PC log.** The stats line now ends with `floor=` (the phone's background level
  before the mic gain, prefixed with `~` until a pause has been measured), `gate=` (`open` / `closed`),
  `agcgain=` (current Auto volume makeup, or `off`) and `exp=` (current pause expander gain). The PC sets a
  new request bit in the PING settings block; a 0.5.7 phone then adds a 6-byte block (magic `0xC7`, with a
  check byte) between the device name and the settings block of its PONG. Phones that do not know the bit
  ignore it, and PCs that do not set it receive the 0.5.6 layout. The values go only to the connected PC.

### Changed
- The PC no longer resets the phone's reported direction, mic count and settings revisions when an audio
  packet updates the noise / AGC flags.
- Android: the noise cancellation help text mentions that Strong keeps some background and protects
  whispers and s/f sounds.

### Tests
- .NET (149): Strong gain flutter (RMS per-hop step, share of steps above 8 dB, per-bin spread, harmonic
  gain) at two noise levels, Light flutter, Strong noise-only floor, fricative and whisper retention with
  no noise-estimate drift afterwards, diagnostics block golden bytes, PONG with and without diagnostics,
  empty-name and unmeasured-floor cases, request bit compatibility, stats line fields.
- Kotlin (39): the same flutter, floor, fricative and whisper checks on the Android chain, diagnostics golden
  bytes (identical to C#), PONG layout with and without the request, PING request flag, and the chain's
  reported floor, gate, AGC and expander state.

## [0.5.6] - 2026-11-10

Less hiss and less static. The phone's gate and noise floor no longer depend on the mic gain, the floor
is tracked for the whole session, pauses between words are lowered, and the PC jitter buffer no longer
produces a hard step when it runs dry or trims a burst. Android `versionCode` is 14 / `versionName` 0.5.6.
Wire version stays 1 and the protocol is unchanged, so 0.5.6 works with 0.5.5 and 0.5.4 on the other
side (the phone fixes apply with any PC version, the PC fixes with any phone version). The release APK is
signed with the same key as 0.5.5. The Windows installer is not Authenticode-signed.

### Fixed
- **Hiss was boosted when the mic gain was raised.** The Auto volume gate compared the level after the
  user gain with a floor measured before it. At +10 dB or more, steady room noise or mic hiss crossed the
  open threshold and AGC lifted it by up to +18 dB. The gate and the floor now both use the signal before
  the user gain.
- **The noise floor was measured only once.** 0.5.5 took the RMS of the first two seconds after Start,
  which included any speech in that window and never followed later changes (a fan, a different room).
  The floor is now tracked continuously: it follows quiet passages down within about 0.3 s and rises at
  most 1.5 dB/s in pauses (0.5 dB/s while the gate is open, so sustained speech does not lift it). The gate
  keeps the previous offsets (open about +10 dB, close about +4 dB above the floor) and now holds open for
  200 ms so word endings are not clipped.
- **AGC makeup lifted the noise floor along with quiet speech.** Makeup gain is now limited so the tracked
  floor stays at or below −52 dBFS after gain. The fixed cap still applies until a pause has been observed.
- **Clicks when the PC jitter buffer ran dry.** An underrun ended on a hard step to silence and playback
  resumed on another hard step. The buffer now fades from the last sample to silence over 2 ms and ramps
  back in over 32 samples.
- **Clicks when a burst was trimmed.** After an overflow drop the next packet was faded in from zero,
  which is itself a step from the previous sample. It now ramps from the last played sample.
- **Zipper noise on live gain changes.** A new Mic gain value now ramps over about 20 ms instead of
  applying in one sample.

### Added
- **Pause expander.** While the gate is closed the output is lowered by about 6 dB with Light noise
  cancellation and about 12 dB with Strong (Off leaves pauses unchanged). It opens in about 5 ms and
  releases over about 150 ms. It follows the selected noise mode even when Light uses the phone's own
  suppressor, so the default setup also benefits.
- **Adaptive jitter target on the PC.** When two underruns occur within 10 seconds, the target (and the
  overflow limits with it) is raised by one packet (5 ms), up to three packets above the preset. After
  60 seconds without an underrun it steps back down by one packet. The `jtarget` field in the stats line
  shows the target in use.

### Changed
- The Noise cancellation help text on the phone mentions the pause attenuation.
- New tests: pre-gain gating with raised gain, expander depth per mode, gain ramp, AGC with a tracked floor,
  underrun fade and resume, overflow splice continuity, and the adaptive target (.NET 138 tests, Kotlin 32).

## [0.5.5] - 2026-11-09

Front focus that can actually take effect, an AGC gate that follows the measured noise floor,
quieter overflow at a 2.7 ms render period, in-app updates, live two-way settings, and an
optional official VB-CABLE download. Android `versionCode` is 13 / `versionName` 0.5.5.
Wire version stays 1; 0.5.5 works with 0.5.4 peers (extra sync bytes are ignored by older
phones and are only appended to PONG after a 0.5.5 PC advertises them). The release APK is
signed with the same key as 0.5.4. The Windows installer is not Authenticode-signed.

### Added
- **Front focus (higher latency).** AAudio has no microphone-direction API, so an explicit
  option switches capture to AudioRecord and applies `setPreferredMicrophoneDirection`. The
  first PING now carries a non-zero control sequence so desktop defaults are applied on
  connect. The Detected line reports the phone’s real direction (front / unsupported / off).
  Samsung phones that returned zero mics from `getMicrophones` also count built-in input
  devices and `AudioRecord.getActiveMicrophones` after capture starts.
- **In-app updates** on the phone and the desktop: Check for updates, plus an optional Auto
  update toggle (off by default). CordMic reads the latest GitHub Release for
  `Lukecinate/cordmic` over HTTPS (unauthenticated, at most at launch and about every six
  hours, no telemetry), verifies the asset against a `SHA256SUMS` file, and never downgrades.
  Android also requires the APK signing certificate to match the installed app before a
  `PackageInstaller` session (you still tap Install once; “install unknown apps” may be
  requested). Windows downloads the setup executable, verifies its hash, and runs it with
  `/S`; UAC or SmartScreen may still appear.
- **Install VB-CABLE** without visiting the website. The Windows installer has an optional
  task (checked by default when no CABLE device is found) and Settings has a matching button
  when no cable is detected. Both download
  `https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack46.zip` over HTTPS, verify
  the pinned SHA-256, check the Authenticode signer when possible, and launch VB-Audio’s own
  `VBCABLE_Setup_x64.exe`. The official package documents no silent switch, so that setup UI
  is shown. On failure the official page is opened. VB-CABLE binaries are never redistributed.
  A reboot may be required afterward.
- A speech-level warning when talk stays below about −40 dBFS after gain.

### Changed
- Auto volume’s gate follows the measured pause floor (about +10 dB to open) instead of a
  fixed −42 dBFS. Makeup gain can reach +24 dB when speech is far below −18 dBFS; pauses
  still release toward unity so hiss is not held up. After an upgrade from 0.5.3/0.5.4, a
  one-shot gain nudge is allowed when speech remains that quiet.
- Light and Strong noise cancellation are distinct at normal speech level: Light still prefers
  the platform suppressor (or a milder STFT); Strong uses only the deeper STFT.
- At a single-packet WASAPI period (about 2.7 ms) the jitter target is three packets (15 ms)
  and the drift-lock cap still starts at ±150 ppm, rising toward ±800 ppm when a standing,
  still-growing error of up to about 25 ms is pegged.
- Settings that both sides can change are applied live, with a per-setting sequence and origin
  so the latest edit wins and echoes are ignored. A focused, dragged, or open control is not
  overwritten. Capture-path changes reopen the engine with a short gap.

### Fixed
- Desktop Front focus could stay checked while the phone reported the direction as unsupported,
  because the first PING used control sequence 0 (ignored by the phone).

## [0.5.4] - 2026-11-08

Front-facing capture by default, per-phone auto-tuning, a quieter AGC that actually reaches its target,
and a drift lock that can follow a larger clock offset without overflowing a short WASAPI period.
Android `versionCode` is 12 / `versionName` 0.5.4. Wire version stays 1; 0.5.4 works with 0.5.3–0.4.x
on the other side (older phones ignore the new front-focus bit). The release APK is signed with the
same key as 0.5.3. The Windows installer is not Authenticode-signed.

### Added
- **Front focus** (default on). On the AudioRecord path (Android 10+) CordMic calls
  `setPreferredMicrophoneDirection(TOWARDS_USER)` after the recorder starts. AAudio has no direction
  API, so flagship phones keep the exclusive low-latency path and report the setting as unsupported
  rather than falling back to a slower engine. The preference is a no-op on devices that cannot apply it.
- **Background device detection.** At start-up, when a headset/USB/Bluetooth mic is plugged in or
  removed (`AudioDeviceCallback`), and again when a session starts, the phone probes API level, model,
  built-in mics, AAudio low-latency/exclusive, platform NS/AGC/AEC, the direction API, input presets,
  native rate and burst, stereo capture, and the first seconds of input level. There is no polling loop.
  The probe stays on the device (no network or telemetry). Only a short capability summary plus the
  model name is sent to the PC over the existing link.
- **Optimize automatically** (default on) and a **Re-detect** action on the phone. Auto mode picks
  AAudio exclusive when it is granted, otherwise shared AAudio, otherwise AudioRecord; uses the Voice
  recognition preset (not Voice communication, which would stack a second NS/AEC/AGC); turns Front
  focus on; uses Light noise cancellation (the platform suppressor when it is available, otherwise
  CordMic’s own Light STFT); and sets a starting gain from the measured level. A value the user has
  already chosen is never overwritten (the same rule as the PC output mode). Existing 0.5.3 settings
  are treated as user-chosen after upgrade.
- A read-only **Detected:** line on the phone and in Windows Settings (from the PONG summary), for
  example `Detected: SM-S721B · AAudio exclusive, 3 mics, front focus`.

### Changed
- Auto volume now follows an RMS envelope (~40 ms) instead of the instantaneous peak, with a slightly
  faster rise, so typical speech settles near −18 dBFS. The silence gate (~−50 dBFS) and −1 dBFS
  limiter are unchanged.
- The drift-lock cap starts at ±150 ppm and can rise toward ±800 ppm when a standing clock error
  lasts more than about half a second. The jitter buffer’s hard cap is the target plus five render
  periods (35 ms at a 2.7 ms callback) so a short exclusive period has burst room.
- Extended PING bit 6 is front focus. PONG flag bit 5 reports the requested setting; byte 3 still
  encodes the engine in the low two bits and now also carries a direction code and the mic count.
  Older readers continue to see AAudio from flags bit 2.

### Fixed
- Windows Settings controls no longer snap back to a stale phone echo while you are moving them
  (sliders apply on release with a short debounce; dropdowns are not rebuilt when the device list
  is unchanged).

## [0.5.3] - 2026-11-08

Microphone quality: less static, less mud, and a conservative output level that games and Discord
can accept without the mic slider near maximum. Android `versionCode` is 11 / `versionName` 0.5.3.
Wire version stays 1; 0.5.3 works with 0.5.2, 0.5.1, 0.5.0 and 0.4.x on the other side (older phones
keep the previous on/off noise-cancellation bit and ignore the new options). The release APK is signed
with the same key as 0.5.2. The Windows installer is not Authenticode-signed.

### Fixed
- **Drift lock no longer hunts.** The 0.5.2 controller skipped or inserted a raw sample whenever the
  standing depth sat outside a one-period band, at up to one slip per packet (~100/s on a 2.7 ms
  period). Field logs showed about 5,000–6,000 slips/min and frequent underruns — periodic clicks
  that settings could not remove. 0.5.3 uses a slow PI controller with a 3 ms (or 0.75-period)
  deadband, capped at ±150 ppm, and crossfades each skipped or inserted sample over 32 frames.
- **Spectral noise suppression no longer uses a rectangular, non-overlapping FFT.** The previous
  256-point gate (5.33 ms, 16-sample edge fade only) produced musical noise. Light and Strong now
  use a Hann window, 50 % overlap (hop 128, 2.67 ms), temporally smoothed gains, and a floor of
  −14 dB (Light) or −20 dB (Strong) instead of muting bins.
- **AGC no longer boosts silence.** The previous detector never went below unity gain and had no
  noise gate, so hiss rose by up to +24 dB. Games that use an open-mic threshold then keyed on
  that hiss. Auto volume now targets −18 dBFS, caps AGC at +12 dB, allows downward gain when the
  user gain is already hot, and holds unity during silence.
- **Hot peaks are limited at −1 dBFS without tanh.** The previous cubic-knee + tanh limiter sat at
  about −0.5 dBFS and added distortion on loud speech.
- **PLC and overflow splices fade** instead of jumping to digital silence or dumping a packet edge.

### Changed
- Default mic gain on new installs and after **Reset audio settings** is +6 dB (a saved +12 is left
  alone until you reset). Speech sits near −18 dBFS; peaks stay at or below −1 dBFS.
- Voice clarity (default on): 120 Hz high-pass, a gentle low-shelf cut around 220 Hz (−5 dB), and a slight
  presence lift around 3 kHz.
- Noise cancellation is Off / Light / Strong (default Light). Light uses Android’s platform
  `NoiseSuppressor` when the capture session exposes one; otherwise it uses the light STFT.
  Strong always runs the STFT (and the platform suppressor as well, when available).

### Added
- Settings: noise cancellation Off / Light / Strong, Voice clarity, Auto volume, and Reset audio
  settings (Windows and Android).
- Extended ping flags (bit 5 = extended, bit 2 = voice clarity, bits 3–4 = mode) so a 0.5.2 phone
  still sees the original NC/AGC bits.

### Notes for Discord, Zoom and games
CordMic’s output is meant to be used with the other app’s mic volume around 50–60 %, not 95 %.
In Valorant: Input Device = CABLE Output, Mic Volume about 50–60 %. If you use Automatic open-mic
instead of Push-to-Talk, raise the sensitivity threshold; a value near 90 will open on remaining
hiss. Discord Input Volume similarly around 50–60 %.

Offline quality checks (1 kHz tones and jitter-buffer sine playback) are summarized in the GitHub
release notes.

## [0.5.2] - 2026-11-07

Shorter, more stable Windows playback on VB-CABLE, and the app applies the
useful VB-CABLE device format itself instead of asking for a manual Sound
control-panel change. Android `versionCode` is 10 / `versionName` 0.5.2. Wire
version stays 1; 0.5.2 works with 0.5.1, 0.5.0 and 0.4.x on the other side.
The release APK is signed with the same key as 0.5.1. The Windows installer
is not Authenticode-signed.

### Fixed
- **Stereo CABLE Input is preferred over "CABLE In 16ch".** Automatic output
  picking ranked any "VB-Audio Virtual Cable" device equally, so the 16-channel
  endpoint often won by enumeration order. The engine then had to convert 2
  channels into 16, which blocked the short shared period.
- **IAudioClient3 can keep the advertised minimum period.** Shared-mode init
  first opens the device mix format without `AUTOCONVERTPCM` (that flag is not
  compatible with `InitializeSharedAudioStream`). HRESULT values are logged
  when a period request fails, then the app falls back safely to converted
  PCM and to `IAudioClient`.
- **Jitter-buffer floor and overflow no longer fight the render callback.**
  The target is at least two render periods (20 ms at a 10 ms period, 10 ms
  at a 2.7 ms period). Overflow trims to a high-water mark one period above
  the target instead of dumping back to the bare target. A sample-level drift
  lock skips or inserts one frame per pull instead of dropping whole packets.

### Added
- **Output mode → Auto** (default on new installs) tries the short shared
  period and uses Exclusive only when that would be shorter. An explicit
  Low latency, Stable or Exclusive setting is left unchanged.
- **Optimize VB-CABLE.** The installer runs `CordMic.exe --configure-cable`
  (already elevated) to set stereo CABLE Input and CABLE Output to 16-bit
  48 kHz and to disable enhancements. Settings offers the same action via a
  one-time UAC prompt if the current format differs. `--restore-cable` is
  used by the uninstaller. Design notes: [docs/SECURITY.md](docs/SECURITY.md).
- A one-line hint when Windows keeps a long shared period even though the
  device advertised a shorter one, and when UAC for the helper is declined.

### Changed
- Render device lists are written to the log only when they change.
- The Windows jitter buffer no longer uses packet-drop catch-up in production.

### Catatan rilis (Bahasa Indonesia)
- CABLE Input stereo dipilih daripada "CABLE In 16ch", dan IAudioClient3
  memakai periode minimum perangkat (tanpa `AUTOCONVERTPCM`) bila berhasil.
- Target jitter buffer minimal dua periode render; luapan dipotong ke tanda
  air tinggi, bukan dikosongkan ke target. Koreksi drift memakai satu sampel
  per callback, bukan membuang paket utuh.
- Mode output *Otomatis* (default instal baru) memilih periode stabil
  terpendek. Pilihan eksplisit tidak diganti diam-diam.
- Instalasi mengatur format VB-CABLE stereo 48 kHz dan mematikan enhancement.
  Tombol *Optimalkan VB-CABLE* di Pengaturan meminta UAC sekali bila format
  masih berbeda. `CordMic.exe --restore-cable` mengembalikan nilai semula
  (juga dijalankan saat uninstall). Lihat [docs/SECURITY.md](docs/SECURITY.md).

## [0.5.1] - 2026-11-06

The settings control no longer overlaps the header logo, and the 0.5.0 field-log notes
are written in a neutral maintainer voice. Android `versionCode` is 9. Wire version stays 1;
0.5.1 works with 0.5.0 and 0.4.x on the other side. The release APK is signed with the same
key as 0.5.0, so it installs as an update.

### Fixed
- **Settings overlapped the header logo.** The connect window created the Settings label at
  the default top-left origin and never laid it out against the logo and title, so the two
  drew on top of each other. Settings is now a gear button on the right of the header row
  (Material Icons Round `settings`, with a tooltip and accessible name), in a table layout
  that stays clear of the logo and title at 100%, 125% and 150% DPI.

### Changed
- The 0.5.0 changelog and release notes describe the 0.4.2 field logs in a neutral
  maintainer voice, without referring to a particular machine or quoting a complaint.

### Catatan rilis (Bahasa Indonesia)
- Tombol Pengaturan di jendela desktop menimpa logo CM. Kini berupa tombol roda gigi di
  sisi kanan baris judul, sejajar dengan logo dan nama aplikasi, dan tidak bertumpuk pada
  100%, 125%, dan 150% DPI.
- Catatan rilis 0.5.0 ditulis ulang dalam gaya maintainer yang netral.

## [0.5.0] - 2026-11-06

Lower, steadier PC playback, a louder voice with a noise gate, and a one-page desktop window. Wire version stays 1.
0.4.2 peers still connect: the new flag bits and the 4-byte PING/PONG payload are ignored when they are absent.
Android `versionCode` is 8.

### Root cause (0.4.2 field logs)

Field logs from 0.4.2 on Windows 11 with VB-CABLE in *Low latency* shared mode showed
**period 10.0 ms, buffer 30.0 ms**. Packets are 5 ms. The Normal jitter target is 10 ms
(2 packets) and Low is 5 ms (1 packet). Each WASAPI callback consumes about one period.
A target that is not larger than that pull leaves the buffer empty, so a fraction of a
millisecond of arrival skew underruns the callback. Playback then rebuffers, packets pile
up, and the hard cap dumps them back to the bare target. The same logs show the resulting
oscillation: about 30 underruns/min and about 100 overflows/min on Normal, and about 370
underruns/min and about 1260 overflows/min after switching to Low. The link itself was
clean (loss about 0–0.01%, jitter under 1 ms, round trip about 5–7 ms). Perceived PC delay
of 20–40 ms is mostly the 10 ms period plus a jitter buffer that was averaging about 15 ms
because it kept refilling. Capture levels sat between −36 and −70 dBFS, which is quiet for
a voice stream. One `ping send failed` line was WSAENOBUFS.

### Fixed
- **Jitter target follows the render period.** The effective target is at least one full pull plus one packet. A 10 ms
  period floors Low and Normal at 15 ms (3 packets). Safe (20 ms) is already above that floor.
- **The callback no longer pulls a variable chunk.** Low-latency mode writes at most one period per event and keeps
  about 2 ms already queued, instead of writing `period + 3 ms − padding` (which grew past the target whenever the
  thread woke late).
- **IAudioClient3.** Shared mode asks for the engine's minimum period and falls back to `IAudioClient.Initialize` if
  the device refuses.
- **WSAENOBUFS on the ping socket.** The send backs off from 250 ms to 4 s and logs once per episode, then once when
  it recovers.
- **AAudio details reported HAL capacity.** After shrinking the capture buffer to one burst, the details line now
  shows that actual buffer size rather than the larger capacity the HAL reserved.
- Status text no longer tells you to open a setup-guide wizard (that page is gone). It points at Settings for the
  VB-CABLE download link.
- **Android VoiceChain did not compile.** Kotlin rejects `DoubleArray[i] /= n` when `n` is an `Int` (the inverse FFT
  scale). The phone app and its unit tests were never built in the 0.5.0 PR; the scale is now a plain assignment.

### Added
- **Noise cancellation** (default on), on the phone, before the packet is sent. A 256-point spectral gate. Algorithmic
  delay is **256 samples = 5.33 ms** at 48 kHz. Gain, AGC and the limiter add no samples. Toggle on the phone and in
  the PC settings panel; the PC pushes a change on the next ping (change counter 0 does not override the phone).
- **Mic gain 0 to +24 dB**, default **+12 dB**, plus an AGC that aims at about −14 dBFS using whatever headroom is
  left under the +24 dB cap, and a soft limiter that stays under about −0.5 dBFS. A −40 dBFS sine measures about
  −18 dBFS after the default chain (about +22 dB).
- **Stats line** now appends `period`, `jtarget`, `urate`, `orate`, `nc`, `agc`, `gain`, `phone` and, when the phone
  sends it, `dev`.
- **Desktop window.** One page: a round connect button (disconnected / searching / connected / error) with a short
  colour ease, a status line (phone name or IP, live delay), and a settings panel (output device, output mode,
  latency, noise cancellation, gain). The tray icon and menu stay.
- AAudio tries exclusive low-latency, then shared low-latency, then shared with no performance hint, and sets the
  capture buffer to one burst. Release builds shrink with R8.

### Changed
- NAudio is gone. Device enumeration, notifications and render are CordMic's own WASAPI interop. There is no
  WasapiOut fallback.
- The jitter hot path keeps packets in a fixed ring. After construction, push and pull do not allocate. A 4,000
  packet run allocated under 1 KB, against about 2 MB for the old per-packet arrays.
- Voice chain over 20 s of audio used about 7.4 ms of CPU per 1 s of audio (about 0.7% of one core) in the C# reference.
- A synthetic 3-minute run of the 10 ms-period failure (late wakes, ±0.4 ms arrival jitter) went from about
  **568 underruns/min** with the old target and variable pull to about **2.3/min** with the floored target and a
  fixed one-period pull.

### Catatan rilis (Bahasa Indonesia)
- **Penyebab (log lapangan 0.4.2):** pada Windows 11 dengan VB-CABLE dalam mode shared *Latensi rendah*,
  periode render 10 ms memakan target jitter 10 ms (Normal) atau 5 ms (Rendah), jadi buffer kosong lalu
  dibuang keras. Tautan bersih (kehilangan sekitar 0–0,01%, jitter di bawah 1 ms, round trip sekitar 5–7 ms).
  Level tangkapan −36 sampai −70 dBFS, terlalu pelan untuk aliran suara. Satu peringatan ping adalah WSAENOBUFS.
- Target jitter sekarang minimal satu tarikan render ditambah satu paket (periode 10 ms → lantai 15 ms). Callback
  hanya menulis satu periode. IAudioClient3 meminta periode shared terpendek yang perangkat izinkan.
- Peredam bising (default nyala) menambah **5,3 ms**. Gain mic default **+12 dB** (0…+24) plus AGC dan pembatas.
- Jendela PC satu halaman: satu tombol bulat seperti aplikasi VPN, satu baris status, panel pengaturan (termasuk mode output). Ikon tray tetap.
- Detail AAudio menampilkan ukuran buffer sebenarnya (satu burst), bukan kapasitas HAL. Teks status tidak lagi
  mengarah ke wizard panduan yang sudah dihapus.
- NAudio dihapus. Jalur panas jitter tidak mengalokasi. R8 mengecilkan build rilis Android. Protokol tetap versi 1.

## [0.4.2] - 2026-11-03

Lower and steadier PC-side delay, and an honest breakdown of it. A user measured 25–45 ms PC-side delay with
0.4.1, with the round trip rising from under 10 ms to under 20 ms whenever the delay was around 30 ms or more. This
release fixes the causes found in the receiver code; the effect on real hardware has not been measured yet. No
protocol change: 0.4.2 works with 0.4.x and 0.3.x on the other side.

### Fixed
- **Jitter-buffer backlog could float between the target and 3× the target.** A burst or a slightly fast phone clock
  left extra packets queued until the hard limit (30 ms on Normal) trimmed them. The buffer now checks the smallest
  amount left after each read over ~0.5 s windows and removes a standing excess by playing two consecutive packets
  as one with a 5 ms crossfade. The hard limit remains as a backstop.
- **The Windows output buffer was always full.** NAudio's WasapiOut tops up the whole endpoint buffer on every
  event, so audio waited roughly a full buffer while the tooltip counted a fixed 10 ms. CordMic now has its own
  event-driven WASAPI render loop on an MMCSS "Pro Audio" thread that keeps about one device period plus 3 ms
  queued (*Low latency* mode).
- **Repeated underruns after resuming.** Playback resumed with only the target queued while Windows reads 10 ms at
  a time, so on Low the first read could run dry again. It now waits for enough audio to cover one whole read.
- **RTT pings shared the audio socket and thread on the PC.** PONGs queued behind audio packets in the same receive
  loop. Pings now go out from a separate UDP socket on a random port with its own thread; the phone already replies
  to the sender, so no protocol change is needed. On Android the thread that answers pings now runs at audio
  priority instead of the default priority.

### Added
- Tray menu **Output mode**: *Low latency* (default), *Stable* (whole Windows buffer kept full, the pre-0.4.2
  behaviour) and *Exclusive* (WASAPI exclusive mode; falls back to Low latency with a notice if the device refuses).
  The choice is saved in settings. If CordMic's renderer cannot open the device at all, the old NAudio path is used.
- PC-side delay breakdown in the tooltip and the setup guide: network (½ round trip), jitter buffer (time-averaged
  wait) and Windows output (measured average buffer fill). The log line adds `bufavg`, `bufmin` and `catchup`.
- Garbage collector runs in sustained low-latency mode to avoid blocking collections on the audio threads.
- Tests: catch-up, crossfade, resume sizing, averaged depth and the separate ping socket (C# and Python).

### Changed
- The PC-side delay number is defined differently (see above) and is not directly comparable with 0.4.1. It still
  excludes phone capture, the Windows engine period, VB-CABLE's own buffering and the recording app.
- Python reference receiver: same catch-up and separate ping socket; `__version__` is now 0.4.2 (was 0.3.1).

### What to test
- *Jitter buffer → Low* with *Output mode → Low latency*, then *Exclusive*; compare with a clap test (README,
  Latency). Use *Stable* or *Safe* if audio crackles.
- Set "CABLE Input" and "CABLE Output" to 48000 Hz in Windows Sound settings so Windows does not resample.

### Catatan rilis (Bahasa Indonesia)
- **Antrean jitter buffer bisa naik sampai 3× target.** Kini kelebihan yang menetap dibuang perlahan: dua paket
  berurutan diputar sebagai satu dengan crossfade 5 ms. Batas keras tetap ada.
- **Buffer output Windows selalu penuh.** CordMic kini punya loop render WASAPI sendiri (event, thread MMCSS
  "Pro Audio") yang hanya mengantrekan sekitar satu periode perangkat + 3 ms (mode *Latensi rendah*).
- **Underrun berulang setelah lanjut** diperbaiki: playback menunggu cukup audio untuk satu kali baca penuh.
- **Ping RTT berbagi socket dan thread dengan audio di PC.** Ping kini memakai socket UDP terpisah dengan thread
  sendiri; di Android thread yang membalas ping berjalan dengan prioritas audio. Tidak ada perubahan protokol.
- Menu tray baru **Mode output**: *Latensi rendah* (default), *Stabil* (perilaku lama) dan *Eksklusif*.
- Rincian jeda sisi PC di tooltip dan panduan penyiapan: jaringan, jitter buffer, output Windows. Angkanya
  didefinisikan ulang sehingga tidak bisa dibandingkan langsung dengan 0.4.1; efek di perangkat nyata belum diukur.
- Yang perlu dicoba: *Jitter buffer → Rendah* + *Mode output → Latensi rendah*, lalu *Eksklusif*; atur CABLE
  Input/Output ke 48000 Hz. Pakai *Stabil* atau *Aman* bila suara putus-putus.

## [0.4.1] - 2026-11-03

New CordMic logo across both apps, the installer and the docs. No change to audio, protocol, ports or settings;
0.4.1 works with 0.4.0 and 0.3.x on the other side.

### Changed
- **New logo:** a "CM" monogram with a plug-shaped notch in the M, white on a teal (`#007A7F`) rounded tile. It
  replaces the generic microphone icon on the Android launcher (adaptive icon), the Windows app, installer and tray
  icons, and the README.
- The app accent colour is aligned to the logo teal (`#0B6B5D` → `#007A7F`; dark theme `#5EC6CA`). Contrast stays
  at WCAG AA for text and controls in both themes.
- Small sizes are tuned for legibility: at 16 and 24 px (tray, taskbar, notification) the plug notch is left out
  and the strokes are slightly heavier.
- Tray icons keep their state colours (grey = waiting, teal = streaming, amber = needs attention, red = error) on
  the same rounded tile with the white monogram.

### Added
- The logo in the Android app bar and in the Windows setup guide header; installer header and welcome-page
  bitmaps.
- Android notification icon in the CordMic monogram (white on transparent).
- `docs/assets/`: logo (512 px), transparent marks, a 512 px store icon and a 1280×640 social preview image.
- `tools/brand/make_logo.py`, which regenerates every logo file (PNG, ICO, BMP) from one geometric definition.

### Removed
- `docs/cordmic-icon.png` (replaced by `docs/assets/logo.png`).

### Catatan rilis (Bahasa Indonesia)
- **Logo baru:** monogram "CM" dengan takik berbentuk colokan pada huruf M, putih di atas kotak teal (`#007A7F`).
  Dipakai di ikon peluncur Android, ikon aplikasi, installer dan tray Windows, serta README.
- Warna aksen aplikasi disamakan dengan teal logo (`#007A7F`; tema gelap `#5EC6CA`), kontras tetap memenuhi WCAG AA.
- Pada ukuran 16 dan 24 px takik colokan dihilangkan dan garis sedikit dipertebal agar tetap terbaca.
- Logo tampil di app bar Android, header panduan penyiapan Windows, serta header dan halaman sambutan installer.
- Ikon notifikasi Android memakai monogram CordMic; aset logo dan gambar pratinjau sosial ada di `docs/assets/`.
- Tidak ada perubahan pada audio, protokol, atau pengaturan.

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
