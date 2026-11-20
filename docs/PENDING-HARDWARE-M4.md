# PENDING-HARDWARE: milestone M4 (WinUSB engine)

The handshake, pipe policies and device-watch seams are unit-tested with fakes.
The following still need the user's PC and phone.

1. 30-minute USB accessory stream (once G-M1 or Experimental has a working route).
2. Unplug and replug 20 times during a stream; no handle leak, no stale instance id, stream resumes or falls back cleanly.
3. Confirm WinUSB timeouts (500 ms read, 200 ms write), AUTO_CLEAR_STALL on, RAW_IO off, in a real Device Manager / API trace if available.
4. `CM_Register_Notification` plus the 3 s SetupAPI rescan when armed and not streaming.

Do not enable USB debugging for CordMic. Do not turn off Secure Boot or Memory Integrity.
