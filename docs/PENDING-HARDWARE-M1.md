# PENDING-HARDWARE: milestone M1 (feasibility spike)

Status: **not decided**. Code and simulated tests for the probe tools are in this branch.
Nothing below has been run on the user's phone or PC in this work. Auto still uses USB
tethering only until gate **G-M1** is recorded here.

G-M1 will decide: (a) which routes enter Auto in 0.8.0 (route 1, 1b, 2b, or tethering
plus the engine seam only); (b) whether Microsoft attestation (O1) is needed. Do not
treat a simulated run as a pass.

The tools never install a driver unless you later choose step 4 with a **trusted**
catalog. They never ask you to turn off Secure Boot, Memory Integrity, or driver
signature enforcement, and they never enable USB debugging for CordMic.

Mask IP addresses and Windows user names before sending any file. Serial numbers in
reports are salted hashes only. Do not send voice recordings.

## Step 1 — Read-only USB probe (U1, catalog)

1. On the PC, build `tools/usbprobe` (`dotnet publish -c Release -r win-x64 --self-contained`).
2. Plug the phone in. Set USB mode to **Charging only**, run `cordmic-usbprobe --scan`, save the report.
3. Repeat for **File transfer** and **USB tethering**.
4. Send the three report files.

## Step 2 — Auto Blocker (U4, note only)

On the phone: Settings › Security and privacy › Auto Blocker. Note on or off.
Do not change it for CordMic.

## Step 3 — Optional AOA test (U2, U6)

Only if USB debugging is already on for some other reason (do not turn it on for
CordMic): run `cordmic-usbprobe --aoa-test`. The phone may show "Open CordMic for this
accessory?" when a test APK is installed. Note whether an accessory device (`18D1:2D00`
or `2D01`) appeared and whether it already has a driver. Unplug, turn tethering back
on, and confirm tethering still works (U6).

## Step 4 — Route 2b catalog install test (U3)

Prefer a Windows 11 VM with USB passthrough first, then the user's PC.

1. Run `tools/m1/record-windows-security.ps1`. Leave Secure Boot and Memory Integrity as they are.
2. Run `tools/m1/catalog-install-test.ps1`. If there is no trusted `.cat`, the script
   **stops** (unsigned packages are never installed).
3. When a SignPath-signed catalog exists: install only through the helper, record
   whether Windows shows a prompt, the publisher name, and whether Device Manager shows
   "CordMic Accessory" under Universal Serial Bus devices.
4. Run Remove USB setup. Confirm the `oemNN.inf` is gone (`pnputil /enum-drivers`).
5. If Windows refuses the catalog, record the exact error, Windows build, and the
   Secure Boot / Memory Integrity state. Do not work around a refusal.

## Step 5 — Memory Integrity

Windows Security › Device security › Core isolation: note the state. Do not turn it off.

## Step 6 — USB MIDI test (U5, gates M-1…M-7)

1. Install a MIDI probe APK (not the production app). Choose **MIDI** in the USB notification.
2. On the PC run `cordmic-midiprobe --run all` (about 25 minutes: 10 min at the Opus
   rate, 10 min at the raw-PCM rate, plus replug prompts).
3. Repeat with the screen locked, and once after sleep/resume.
4. Send the report. All of M-1…M-7 must pass for a "go" on route 1b. A ` --sim` run
   on this machine is not a go.

## Step 7 — M0 field checks (if not already done)

On a 0.8.0-beta.1 (or later) install: confirm the orb no longer stops listening on a
double click and that Settings shows "Phone data: blocked". Send `tether-metric.log`
from `%ProgramData%\CordMic\` with IPs and user names masked, plus the PowerShell
commands listed in the 0.8.0 plan section 18, step 7.

## Recording the G-M1 decision

After steps 1–6, fill this in (leave as pending until then):

- Date / Windows build / phone model:
- U1 MS OS descriptor on accessory 2D00/2D01:
- U2 GET_PROTOCOL (request 51) via an existing WinUSB handle:
- U3 SignPath catalog on Win10/Win11 with Secure Boot and Memory Integrity:
- U4 Auto Blocker vs AOA:
- U5 MIDI gates M-1…M-7:
- U6 Tethering/MTP after leaving accessory mode:
- G-M1(a) routes in Auto:
- G-M1(b) attestation (O1) needed? (only if U3 was a refusal)

Until this section is filled, `UsbEngineGate.HardwareGateGm1Passed` stays false and
Auto uses tethering. Testers may opt in locally with Settings
`EnableExperimentalUsbRoutes` or the file `%ProgramData%\CordMic\enable-experimental-usb`.
