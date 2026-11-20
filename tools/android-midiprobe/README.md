# Android MIDI probe (M1, not shipped in the store APK)

This folder holds the SysEx pattern used by `cordmic-midiprobe` so a small test APK can be
built later for the hardware run (plan section 4.2 / step 6). It is **not** part of the
production CordMic APK and is not on the Auto route until G-M1 says go.

Message format (development SysEx ID `0x7D`):

```
F0 7D 01  <seq 5×7-bit>  <time-ms 5×7-bit>  <len 2×7-bit>  <7-bit packed payload>  F7
```

Packing is implemented in `CordMic.Core.Usb.MidiProbeCodec` and covered by unit tests.

PENDING-HARDWARE: install a test APK that exposes a `MidiDeviceService` with one output
port, choose **MIDI** in the phone's USB notification, run `cordmic-midiprobe --run all`
on the PC (~25 minutes), then repeat with the screen locked and after sleep/resume.
Send the report. Do not change Auto on the released app based on a simulated `--sim` run.
