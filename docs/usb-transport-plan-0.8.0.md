# Plan: a USB transport without tethering (CordMic 0.8.0)

Status: proposal, written for 0.7.2 (2026-11-14). Nothing here ships in 0.7.2.

## Why

CordMic 0.7.x carries audio over USB tethering (RNDIS/NCM). That has three costs:

1. The user has to turn on USB tethering by hand, and many phones turn it off on every unplug or when locked.
2. Some carriers and One UI versions block or hide tethering.
3. Windows sees the phone as an internet connection. 0.7.2 stops the PC from routing internet or DNS through it
   (ignore default routes, remove `0.0.0.0/0` and `::/0`, DNS to `127.0.0.1`, metric 9999), so mobile data is not used,
   but the adapter still exists.

A transport that does not need tethering would remove all three.

## Options considered

| Option | User steps | Windows driver | Licence | Risk |
|---|---|---|---|---|
| **A. Android Open Accessory (AOA 2.0)** | Plug in, accept "Open CordMic for this accessory?" once | WinUSB bound to the accessory IDs `18D1:2D00` / `18D1:2D01` | Own code (MIT); WinUSB is part of Windows | Medium: driver binding, device re-enumeration |
| B. `adb reverse tcp` with bundled adb | Enable developer options and USB debugging, accept the RSA prompt | Google USB driver or vendor driver (Samsung) | adb is Apache-2.0 (redistributable with notice) | High for users: USB debugging is a security and simplicity regression |
| C. Keep tethering (0.7.2) | Turn on USB tethering | Built in (RNDIS/NCM) | — | Low; data use solved in 0.7.2 |

Decision for 0.7.2: **C**, with the mobile-data protection. Decision for 0.8.0: implement **A** as an opt-in transport,
keep **C** as the default until A has been tested on real Samsung, Pixel and Xiaomi phones and Windows 10/11.
**B** is rejected: asking every user to enable USB debugging conflicts with the project's simplicity and security
priorities. No GPL code (libusb is LGPL and is not needed; WinUSB is used directly).

## Design for option A

### Windows side (CordMic.App)

1. **Find the phone.** Enumerate present USB devices with SetupAPI (already done read-only in 0.7.2 `UsbProbe`).
   A phone in normal mode shows its vendor ID (Samsung `04E8`, Google `18D1`, ...).
2. **Switch it to accessory mode.** The AOA handshake is a set of vendor control transfers to the phone's *normal*
   USB device, which Windows binds to its MTP/composite drivers. User mode can only send them through a driver such
   as WinUSB. Two ways, to be decided by the spike in milestone 1:
   - if the phone exposes an interface Windows already binds to WinUSB, send the transfers there (no extra driver);
   - otherwise an opt-in, one-time "Use CordMic USB mode" step in the elevated setup binds WinUSB to the phone's
     interface with a signed INF (`pnputil /add-driver`), like Zadig does. A second INF binds WinUSB to the accessory
     IDs `USB\VID_18D1&PID_2D00` / `PID_2D01`. This driver binding is the main risk of option A.
   Handshake: `GET_PROTOCOL (51)` → expect 1 or 2; `SEND_STRING (52)` manufacturer "CordMic", model "CordMic Mic",
   description, version, URI `https://github.com/Lukecinate/cordmic`, serial; `START (53)`. The phone re-enumerates
   as `18D1:2D00` (or `2D01` with ADB).
3. **Stream.** Open the accessory with WinUSB, read the bulk IN endpoint on a dedicated thread with overlapped I/O,
   and feed the same packet parser as UDP (wire version 1 framing is unchanged; each USB transfer carries one or more
   CordMic packets with a 2-byte length prefix). Replies (pings, settings) go out on bulk OUT.
4. **Selection.** `Transport = Auto | Tethering | Accessory` in settings. Auto prefers Accessory when the device is
   present and falls back to UDP over tethering.

### Android side

1. Add `<uses-feature android:name="android.hardware.usb.accessory" android:required="false"/>` and an
   `accessory_filter.xml` matching manufacturer "CordMic" and model "CordMic Mic"; `MicService` handles
   `UsbManager.ACTION_USB_ACCESSORY_ATTACHED`.
2. Open the accessory with `UsbManager.openAccessory()` (user approval dialog once, "Always" option), wrap the file
   descriptor in input/output streams, and send the existing packets with the length prefix.
3. No new permissions beyond the accessory approval; no USB debugging; tethering stays available.

### Tests

- Core: framing round-trip (length prefix, split and merged transfers, oversize rejection) and the transport
  selection state machine (Auto prefers Accessory, falls back on detach).
- Android: Robolectric test for the accessory filter and `ACTION_USB_ACCESSORY_ATTACHED` handling; framing vectors
  shared with C# as golden files.
- Manual matrix before making Accessory the default: Samsung (One UI 6/7), Pixel, Xiaomi; Windows 10 22H2 and 11 24H2;
  sleep/resume, unplug/replug, hub vs direct port; latency compared with tethering (target: no worse than +2 ms).

### Risks and mitigations

| Risk | Mitigation |
|---|---|
| WinUSB must be bound to a phone interface (driver replacement) | Opt-in step with clear text and an "undo" that removes the INF with `pnputil /delete-driver`; never automatic |
| INF signing | Attestation-signed via the Microsoft Hardware Dev Center, or a catalog signed with the project certificate; without signing the step is not offered |
| Phones that refuse AOA (work profiles, Auto Blocker) | Fallback to tethering stays the default path |
| Accessory mode disables MTP file transfer while active | Documented; ends when CordMic stops |

### Milestones

1. Spike (Windows only): can user mode send `GET_PROTOCOL` to a Samsung phone in MTP mode? Decide INF vs no INF.
2. Android accessory receiver + framing, behind a developer toggle.
3. Windows accessory reader + Auto selection; opt-in in Settings.
4. Real-device matrix; make Auto prefer Accessory only after it passes.
