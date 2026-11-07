# Security notes

CordMic is a local USB-tethering audio app. This page describes the privileged
helper introduced in 0.5.2 and how to undo what it changes.

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
are logged to `%LOCALAPPDATA%\CordMic\logs\cable-helper.log`.

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
