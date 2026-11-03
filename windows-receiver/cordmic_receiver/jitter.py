"""Minimal jitter buffer for fixed-size PCM packets.

Design goals: lowest possible latency with bounded depth.
* Packets are stored by sequence number; playout pulls in order.
* Playback starts (and restarts after an underrun) once `target` packets are buffered.
* A missing packet while later packets are already waiting is treated as lost:
  silence is played for it (packet-loss concealment = silence in this prototype).
* If depth exceeds `max_depth`, the oldest packets are dropped down to `target`
  so latency can never creep up (clock drift / bursts).
Thread-safe: push() from the network thread, pull() from the audio callback.
"""
from __future__ import annotations

import threading
from dataclasses import dataclass, field

from .protocol import SEQ_MOD, seq_diff


@dataclass
class JitterStats:
    pushed: int = 0
    played: int = 0
    concealed: int = 0      # lost packets replaced by silence at playout time
    late: int = 0           # arrived after its playout slot
    duplicates: int = 0
    overflow_drops: int = 0
    underruns: int = 0
    resets: int = 0


class JitterBuffer:
    def __init__(self, frames_per_packet: int, channels: int = 1, target_packets: int = 2,
                 max_packets: int | None = None):
        if target_packets < 1:
            raise ValueError("target_packets must be >= 1")
        self.fpp = frames_per_packet
        self.channels = channels
        self.bytes_per_packet = frames_per_packet * channels * 2
        self.target = target_packets
        self.max_depth = max_packets if max_packets is not None else max(target_packets * 3, target_packets + 4)
        self._buf: dict[int, bytes] = {}
        self._next: int | None = None
        self._buffering = True
        self._leftover = b""
        self._lock = threading.Lock()
        self.stats = JitterStats()
        self._silence = bytes(self.bytes_per_packet)

    # ----- producer side -------------------------------------------------
    def push(self, seq: int, payload: bytes) -> None:
        if len(payload) != self.bytes_per_packet:
            # frame size changed on the sender: reconfigure
            with self._lock:
                self.fpp = len(payload) // (2 * self.channels)
                self.bytes_per_packet = len(payload)
                self._silence = bytes(self.bytes_per_packet)
                self._reset_locked()
        with self._lock:
            self.stats.pushed += 1
            if self._next is None:
                self._next = seq
            d = seq_diff(seq, self._next)
            if d < 0:
                if d < -1000:  # sender restarted with a new sequence space
                    self._reset_locked()
                    self._next = seq
                else:
                    self.stats.late += 1
                    return
            elif d > 5000:  # huge jump forward: sender restarted
                self._reset_locked()
                self._next = seq
            if seq in self._buf:
                self.stats.duplicates += 1
                return
            self._buf[seq] = payload
            self._trim_locked()

    def _reset_locked(self) -> None:
        self._buf.clear()
        self._next = None
        self._buffering = True
        self._leftover = b""
        self.stats.resets += 1

    def _span_locked(self) -> int:
        """Packets between next playout slot and newest packet (inclusive)."""
        if not self._buf or self._next is None:
            return 0
        newest = max(self._buf, key=lambda s: seq_diff(s, self._next))
        return seq_diff(newest, self._next) + 1

    def _trim_locked(self) -> None:
        span = self._span_locked()
        if span > self.max_depth:
            drop = span - self.target
            for _ in range(drop):
                if self._buf.pop(self._next, None) is not None:
                    self.stats.overflow_drops += 1
                else:
                    self.stats.overflow_drops += 1  # skipped slot (was missing anyway)
                self._next = (self._next + 1) % SEQ_MOD

    # ----- consumer side -------------------------------------------------
    def _pop_packet_locked(self) -> bytes | None:
        if self._next is None:
            return None
        if self._buffering:
            if self._span_locked() >= self.target:
                self._buffering = False
            else:
                return None
        pkt = self._buf.pop(self._next, None)
        if pkt is not None:
            self._next = (self._next + 1) % SEQ_MOD
            self.stats.played += 1
            return pkt
        if self._buf:  # a gap but newer data waiting -> declare lost, conceal
            self._next = (self._next + 1) % SEQ_MOD
            self.stats.concealed += 1
            return self._silence
        # nothing at all: underrun -> rebuffer up to target before resuming
        self.stats.underruns += 1
        self._buffering = True
        return None

    def pull(self, nbytes: int) -> bytes:
        """Return exactly nbytes of PCM (silence-padded). Safe for audio callbacks."""
        out = bytearray()
        with self._lock:
            if self._leftover:
                take = self._leftover[:nbytes]
                out += take
                self._leftover = self._leftover[len(take):]
            while len(out) < nbytes:
                pkt = self._pop_packet_locked()
                if pkt is None:
                    break
                need = nbytes - len(out)
                out += pkt[:need]
                if len(pkt) > need:
                    self._leftover = pkt[need:]
        if len(out) < nbytes:
            out += bytes(nbytes - len(out))
        return bytes(out)

    # ----- introspection --------------------------------------------------
    def depth_packets(self) -> int:
        with self._lock:
            return len(self._buf)

    def depth_ms(self, sample_rate: int = 48000) -> float:
        with self._lock:
            frames = len(self._buf) * self.fpp + len(self._leftover) // (2 * self.channels)
        return frames * 1000.0 / sample_rate
