"""CordMic Windows receiver CLI.

    python -m cordmic_receiver --list-devices
    python -m cordmic_receiver --device "CABLE Input" --jitter-ms 10
"""
from __future__ import annotations

import argparse
import logging
import signal
import sys
import threading
import time

from . import __version__
from . import protocol as P
from .server import Receiver

log = logging.getLogger("cordmic")


def parse_args(argv=None):
    ap = argparse.ArgumentParser(prog="cordmic_receiver", description="CordMic Windows receiver")
    ap.add_argument("--port", type=int, default=P.DEFAULT_PORT)
    ap.add_argument("--bind", default="0.0.0.0", help="local IP to bind (default all)")
    ap.add_argument("--device", help="output device index or name substring (e.g. 'CABLE Input')")
    ap.add_argument("--list-devices", action="store_true")
    ap.add_argument("--all-apis", action="store_true", help="list devices of all host APIs, not only WASAPI")
    ap.add_argument("--jitter-ms", type=float, default=10.0, help="jitter buffer target (default 10)")
    ap.add_argument("--channels", type=int, default=2, help="output channels; mono is duplicated (default 2)")
    ap.add_argument("--blocksize", type=int, default=240, help="audio callback frames (default 240 = 5 ms)")
    ap.add_argument("--exclusive", action="store_true", help="WASAPI exclusive mode (lowest latency)")
    ap.add_argument("--latency", default="low", help="PortAudio suggested latency: low/high/seconds")
    ap.add_argument("--tray", action="store_true", help="show tray icon (needs pystray + pillow)")
    ap.add_argument("--stats-interval", type=float, default=1.0)
    ap.add_argument("--log", help="also write log/stats to this file")
    ap.add_argument("--no-audio", action="store_true", help="receive + stats only, audio discarded (debug)")
    ap.add_argument("-v", "--verbose", action="store_true")
    return ap.parse_args(argv)


def main(argv=None) -> int:
    a = parse_args(argv)
    handlers = [logging.StreamHandler(sys.stdout)] if sys.stdout is not None else []  # pythonw has no stdout
    if a.log or not handlers:
        a.log = a.log or "cordmic.log"
        handlers.append(logging.FileHandler(a.log, encoding="utf-8"))
    logging.basicConfig(level=logging.DEBUG if a.verbose else logging.INFO,
                        format="%(asctime)s %(levelname)s %(message)s", handlers=handlers)
    log.info("CordMic receiver %s", __version__)

    if a.list_devices:
        from .audio_out import list_devices
        for idx, name, api, sr in list_devices(a.all_apis):
            print(f"[{idx:3d}] {name}  ({api}, default {sr} Hz)")
        return 0

    rx = Receiver(port=a.port, bind=a.bind, jitter_ms=a.jitter_ms)
    rx.start()
    out = None
    if not a.no_audio:
        from .audio_out import AudioOutput, resolve_device
        latency = a.latency
        try:
            latency = float(latency)
        except ValueError:
            pass
        out = AudioOutput(rx.pull, device=resolve_device(a.device), out_channels=a.channels,
                          blocksize=a.blocksize, exclusive=a.exclusive, latency=latency)
        out.start()

    stop = threading.Event()
    if a.no_audio:  # emulate a real-time audio clock so buffer stats stay meaningful
        def null_sink():
            t0, n = time.perf_counter(), 0
            while not stop.is_set():
                n += 1
                d = t0 + n * a.blocksize / 48000 - time.perf_counter()
                if d > 0:
                    time.sleep(d)
                rx.pull(a.blocksize * 2)
        threading.Thread(target=null_sink, daemon=True).start()
    signal.signal(signal.SIGINT, lambda *_: stop.set())

    def stats_loop():
        while not stop.wait(a.stats_interval):
            s = rx.snapshot()
            extra = f" out={out.latency_ms:.1f}ms xruns={out.status_flags}" if out else ""
            est = s.depth_ms + (out.latency_ms if out else 0) + (s.rtt_ms / 2 if s.rtt_ms else 0)
            log.info("%s%s est_pc_side=%.1fms", s.line(), extra, est)

    threading.Thread(target=stats_loop, daemon=True).start()

    from . import tray
    if a.tray and tray.available():
        tray.run_tray(rx, stop)
    else:
        if a.tray:
            log.warning("pystray/pillow not installed; running in console mode")
        while not stop.is_set():
            time.sleep(0.2)
    log.info("stopping")
    if out:
        out.stop()
    rx.stop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
