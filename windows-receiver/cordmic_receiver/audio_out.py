"""Audio output through PortAudio (python-sounddevice), WASAPI preferred on Windows."""
from __future__ import annotations

import logging
from array import array

log = logging.getLogger("cordmic")

try:
    import sounddevice as sd
except Exception:  # pragma: no cover - only on machines without PortAudio
    sd = None


def require_sd():
    if sd is None:
        raise SystemExit("python-sounddevice is not installed: pip install -r requirements.txt")
    return sd


def wasapi_index():
    for i, api in enumerate(require_sd().query_hostapis()):
        if "WASAPI" in api["name"]:
            return i
    return None


def list_devices(all_apis: bool = False) -> list[tuple[int, str, str, int]]:
    sd_ = require_sd()
    apis = sd_.query_hostapis()
    wi = wasapi_index()
    out = []
    for idx, d in enumerate(sd_.query_devices()):
        if d["max_output_channels"] <= 0:
            continue
        if not all_apis and wi is not None and d["hostapi"] != wi:
            continue
        out.append((idx, d["name"], apis[d["hostapi"]]["name"], int(d["default_samplerate"])))
    return out


def resolve_device(spec: str | None):
    """Device index, or case-insensitive substring of its name (WASAPI first)."""
    if spec is None or spec == "":
        return None
    if spec.isdigit():
        return int(spec)
    matches = [d for d in list_devices() if spec.lower() in d[1].lower()] or \
              [d for d in list_devices(all_apis=True) if spec.lower() in d[1].lower()]
    if not matches:
        raise SystemExit(f"No output device matches '{spec}'. Use --list-devices.")
    log.info("output device: [%d] %s (%s)", matches[0][0], matches[0][1], matches[0][2])
    return matches[0][0]


class AudioOutput:
    """Pulls mono s16 PCM from `source(nbytes)` and plays it, duplicating to N channels."""

    def __init__(self, source, device=None, sample_rate: int = 48000, out_channels: int = 2,
                 blocksize: int = 240, exclusive: bool = False, latency="low"):
        self.source = source
        self.out_channels = out_channels
        self.callback_count = 0
        self.status_flags = 0
        sd_ = require_sd()
        extra = None
        dev_api = None
        dev_idx = device if device is not None else sd_.default.device[1]
        try:
            dev_api = sd_.query_hostapis(sd_.query_devices(dev_idx)["hostapi"])["name"]
        except Exception:
            pass
        if dev_api and "WASAPI" in dev_api:
            try:
                extra = sd_.WasapiSettings(exclusive=exclusive, auto_convert=not exclusive)
            except TypeError:  # older sounddevice without auto_convert
                extra = sd_.WasapiSettings(exclusive=exclusive)
        self.stream = sd_.RawOutputStream(
            samplerate=sample_rate, blocksize=blocksize, device=device, channels=out_channels,
            dtype="int16", latency=latency, callback=self._cb, extra_settings=extra)
        log.info("audio out: %s, %d Hz, %d ch, block %d, output latency %.1f ms%s",
                 dev_api or "?", sample_rate, out_channels, blocksize, self.stream.latency * 1000,
                 " (WASAPI exclusive)" if exclusive else "")

    def _cb(self, outdata, frames, time_info, status):
        if status:
            self.status_flags += 1
        mono = self.source(frames * 2)
        if self.out_channels == 1:
            outdata[:] = mono
        else:
            src = array("h")
            src.frombytes(mono)
            dst = array("h", bytes(frames * 2 * self.out_channels))
            for c in range(self.out_channels):
                dst[c::self.out_channels] = src
            outdata[:] = dst.tobytes()
        self.callback_count += 1

    def start(self):
        self.stream.start()

    def stop(self):
        self.stream.stop()
        self.stream.close()

    @property
    def latency_ms(self) -> float:
        return self.stream.latency * 1000
