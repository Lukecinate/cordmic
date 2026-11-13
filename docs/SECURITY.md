# Security notes

CordMic is a local USB-tethering audio app. This page describes the privileged
helper introduced in 0.5.2 and how to undo what it changes, and the network
access the apps perform (current as of 0.6.1).

## What the app changes

The everyday tray app (`CordMic.exe`) runs **as the signed-in user**
(`asInvoker`). It does not require administrator rights and does not write to
`HKLM`.

Setting a Windows audio endpoint's default format (`PKEY_AudioEngine_DeviceFormat`
/ `PKEY_AudioEngine_OEMFormat`) and turning off audio enhancements
(`PKEY_AudioEndpoint_Disable_SysFx`) needs administrator access to that
endpoint's property store under
`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices`. CordMic never
elevates the tray app for this. Instead a **fixed-verb helper mode of the same
executable** does it once:

- The installer (already elevated) runs `CordMic.exe --configure-cable` after
  copying files, so a normal install applies the VB-CABLE settings without an
  extra prompt.
- Settings has an **Optimize VB-CABLE** button. It starts the same executable
  with `Verb=runas` and the argument `--configure-cable` only when the current
  format is not already 16- or 24-bit PCM, 48 kHz, 2 channels (or enhancements
  are still on). If User Account Control is declined, CordMic keeps running and
  shows a one-line hint.

`--restore-cable` writes the backed-up values back. The uninstaller runs it
before deleting `CordMic.exe`. You can also run it by hand (a UAC prompt
appears if you are not already elevated):

```
CordMic.exe --restore-cable
```

The helper changes **only** the stereo endpoints whose friendly name is
`CABLE Input (VB-Audio Virtual Cable)` or `CABLE Output (VB-Audio Virtual Cable)`
**and** whose manufacturer or name identifies VB-Audio. Multi-channel endpoints
such as `CABLE In 16ch` are never written. Device ids are taken from MMDevice
enumeration and must match
`{0.0.0.00000000}.{guid}` (render) or `{0.0.1.00000000}.{guid}` (capture). The
command line cannot supply a path, a device id, or any other argument.

Writes go through `IPropertyStore` (`STGM_READWRITE`) on those endpoints. If
that fails, the helper falls back to the same endpoint's `Properties` registry
key, built from the enumerated id — never from input. No other registry keys
are opened for writing. Exclusive-mode checkboxes are left at the VB-CABLE
default (normally allowed); CordMic tries WASAPI exclusive at runtime and
falls back if the device refuses.

Previous values are stored in `%ProgramData%\CordMic\cable-format-backup.json`
with an ACL that grants full control to Administrators and SYSTEM and read
access to Users. The first successful configure keeps that backup so later
runs cannot overwrite the original with the optimized format. Helper actions
are logged to `%ProgramData%\CordMic\cable-helper.log`.

From 0.6.1 the elevated helper uses `%ProgramData%\CordMic` only if it is a
real folder (not a junction or symbolic link) owned by Administrators; the
installer sets that owner and ACL. A backup or log in a folder that fails the
check is not read or written, and the backup is created with `CreateNew`, so a
pre-planted file is never followed or overwritten.

## What the helper refuses

- Any argument other than exactly `--configure-cable` or `--restore-cable`
  (exit code 1). Extra tokens, paths and device ids are rejected.
- Writing a property whose key is not in the compiled allow-list
  (`DeviceFormat`, `OEMFormat`, `Disable_SysFx`).
- Restoring onto an endpoint that is no longer a live stereo VB-CABLE target,
  or from a backup that fails validation (bad JSON, wrong version, illegal
  key, malformed blob).
- Network access, launching `cmd.exe` / PowerShell, or downloading anything.

Exit codes: `0` success (including "already configured" and "nothing to
restore"), `1` usage, `2` no matching VB-CABLE endpoints (the installer treats
this as fine), `3` I/O or backup failure with no changes, `4` partial write.

## Which phones the PC accepts (0.6.1)

- **Sources.** The receiver handles packets only from `127.0.0.1` and from the
  subnet of the detected USB tethering adapter. Packets from Wi-Fi, LAN or VPN
  hosts are dropped without a reply, and the status line says so if they keep
  arriving. An adapter that CordMic does not recognise as USB tethering is not
  trusted; the status then shows *Phone not on the USB cable*.
- **One phone.** The first phone that streams becomes the peer. Until it stops
  for 3 s or says goodbye, audio, PONG and BYE from any other address are
  ignored.
- **Firewall.** The rule *CordMic (UDP 50005)* is created for the *Private*
  profile and `127.0.0.1`. The SYSTEM task `\CordMic\SetTetherMetric` marks the
  tethering network as *Private* and narrows the rule to its subnet whenever the
  network changes, and on install or upgrade. If Windows refuses to change the
  network category, the rule is still limited to the tethering subnet.
- **Optional pairing code.** *Require a pairing code on the phone* (main window,
  off by default) shows a 6-digit code. The phone enters it once under
  Settings. The PC sends a random single-use challenge; the phone answers with
  an HMAC-SHA256 proof keyed by PBKDF2-SHA256 of the code (20 000 iterations), so
  the code itself never travels. Five wrong answers pause pairing from that
  address for 60 s. With the option off, a phone on the tethering cable
  connects without a code, as before.
- **Phone side.** With a manual PC address the phone accepts control packets
  only from that address; in automatic mode it takes settings (PING) only from
  the PC it is streaming to.

## Downloaded installers (0.6.1)

The update installer and the VB-CABLE setup are downloaded into memory, checked
(SHA-256, and for VB-CABLE the Authenticode signature and signer), and written
to a new folder with a random name whose ACL allows only the current user,
SYSTEM and Administrators. Each file stays open with write and delete sharing
denied from the hash check until the started process has opened it, so the
file cannot be replaced between the check and the launch.

## Network access

Audio, discovery and control traffic stay on the USB tethering link (UDP 50005,
tethering subnet only). Apart from that, CordMic makes only these requests, all over
HTTPS and without an account:

- **Update check** (phone and PC, from 0.5.5): the latest GitHub Release of
  `Lukecinate/cordmic`, when *Check for updates* is used or, if *Auto update* is
  on, at launch and about every six hours. A downloaded asset is installed only
  if its SHA-256 matches the release's `SHA256SUMS` and its version is newer.
  Android additionally requires the APK signing certificate to match the
  installed app.
- **VB-CABLE download** (installer task and Settings button, from 0.5.5):
  VB-Audio's official `VBCABLE_Driver_Pack46.zip` from `download.vb-audio.com`,
  checked against a pinned SHA-256 (and the Authenticode signer where possible)
  before VB-Audio's own setup is started. VB-CABLE binaries are never
  redistributed by CordMic.

There is no telemetry or analytics. The phone's device probe runs locally; only
a short capability summary and the model name are sent to the PC over the
tethering link. From 0.5.7 the phone also reports four voice-chain levels (noise
floor, gate state, Auto volume gain, pause expander gain) to the connected PC
when the PC asks for them; they are written only to the local PC log. From 0.6.0
three more flags travel the same way (capture silenced by another app, Studio
bypass active, device noise suppression suspected). The phone reads Android's
recording configurations only for its own capture. PC Studio noise reduction
runs locally (`rnnoise.dll`) and sends nothing. No audio
or level data is sent anywhere else. The elevated `--configure-cable` / `--restore-cable` helper
performs no network access.

## How to undo

1. Uninstall CordMic (the installer runs `--restore-cable`), or
2. Run `CordMic.exe --restore-cable` from an elevated command prompt, or
3. In Windows Settings → Sound, open **CABLE Input** (the stereo device, not
   16ch) and **CABLE Output** → Properties → Advanced, and pick the format you
   want. Enhancements can be turned back on there as well.

The backup file is not deleted on uninstall so you can inspect what was saved.

## Reporting issues

Please open a GitHub issue at https://github.com/Lukecinate/cordmic/issues
with the relevant lines from `cable-helper.log` (no secrets are written there).
