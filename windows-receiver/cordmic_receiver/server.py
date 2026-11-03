"""UDP receiver: discovery replies, RTT pings, audio into the jitter buffer, stats."""
from __future__ import annotations

import logging
import socket
import threading
import time
from dataclasses import dataclass

from . import protocol as P
from .jitter import JitterBuffer

log = logging.getLogger("cordmic")


def now_us() -> int:
    return time.perf_counter_ns() // 1000


@dataclass
class StatsSnapshot:
    t: float
    peer: str | None
    received: int
    expected: int
    lost: int
    loss_pct: float
    reordered: int
    late: int
    concealed: int
    underruns: int
    overflow_drops: int
    depth_ms: float
    rtt_ms: float | None
    rtt_min_ms: float | None
    arrival_jitter_ms: float
    pkt_ms: float
    buffer_avg_ms: float = float("nan")
    catch_up_drops: int = 0

    def line(self) -> str:
        rtt = f"{self.rtt_ms:.2f}" if self.rtt_ms is not None else "-"
        rmin = f"{self.rtt_min_ms:.2f}" if self.rtt_min_ms is not None else "-"
        return (f"peer={self.peer or '-'} rx={self.received} lost={self.lost} ({self.loss_pct:.2f}%) "
                f"reord={self.reordered} late={self.late} plc={self.concealed} "
                f"underrun={self.underruns} ovf={self.overflow_drops} "
                f"buf={self.depth_ms:.1f}ms jitter={self.arrival_jitter_ms:.2f}ms "
                f"rtt={rtt}ms (min {rmin}) pkt={self.pkt_ms:.1f}ms "
                f"bufavg={self.buffer_avg_ms:.1f}ms catchup={self.catch_up_drops}")


class Receiver:
    def __init__(self, port: int = P.DEFAULT_PORT, bind: str = "0.0.0.0", jitter_ms: float = 10.0,
                 sample_rate: int = 48000, channels: int = 1, frames_per_packet: int = 240,
                 ping_interval: float = 0.5, peer_timeout: float = 3.0):
        self.port = port
        self.bind = bind
        self.sample_rate = sample_rate
        self.jitter_ms = jitter_ms
        self.channels = channels
        self.ping_interval = ping_interval
        self.peer_timeout = peer_timeout
        self.jb = self._make_jb(frames_per_packet)
        self.sock: socket.socket | None = None
        self.ping_sock: socket.socket | None = None  # RTT pings: own socket so PONGs never wait behind audio
        self.peer: tuple[str, int] | None = None
        self._peer_last = 0.0
        self._stop = threading.Event()
        self._threads: list[threading.Thread] = []
        self._lock = threading.Lock()
        # loss / reorder accounting (RTP style)
        self._first_seq: int | None = None
        self._max_seq: int | None = None
        self._received = 0
        self._reordered = 0
        # arrival jitter (RFC 3550 style, in microseconds)
        self._transit_prev: int | None = None
        self._jitter_us = 0.0
        # RTT
        self._ping_seq = 0
        self._rtt_ms: float | None = None
        self._rtt_min: float | None = None
        self.on_peer_change = None  # callback(peer or None)

    def _make_jb(self, fpp: int) -> JitterBuffer:
        pkt_ms = fpp * 1000.0 / self.sample_rate
        target = max(1, round(self.jitter_ms / pkt_ms))
        self.fpp = fpp
        return JitterBuffer(fpp, self.channels, target_packets=target, sample_rate=self.sample_rate)

    # ------------------------------------------------------------------
    def start(self) -> None:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            s.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 1 << 20)
        except OSError:
            pass
        if hasattr(socket, "SIO_UDP_CONNRESET"):  # Windows: ignore ICMP port-unreachable resets
            try:
                s.ioctl(socket.SIO_UDP_CONNRESET, False)
            except OSError:
                pass
        s.bind((self.bind, self.port))
        s.settimeout(0.2)
        self.port = s.getsockname()[1]
        self.sock = s
        try:
            ps = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            if hasattr(socket, "SIO_UDP_CONNRESET"):
                try:
                    ps.ioctl(socket.SIO_UDP_CONNRESET, False)
                except OSError:
                    pass
            ps.bind((self.bind, 0))
            ps.settimeout(0.05)
            self.ping_sock = ps
        except OSError as e:
            log.warning("ping socket unavailable, using main socket: %s", e)
            self.ping_sock = None
        for fn, name in ((self._rx_loop, "cordmic-rx"), (self._ping_loop, "cordmic-ping")):
            t = threading.Thread(target=fn, name=name, daemon=True)
            t.start()
            self._threads.append(t)
        log.info("listening on udp %s:%d (jitter target %.1f ms = %d pkt)", self.bind, self.port,
                 self.jitter_ms, self.jb.target)

    def stop(self) -> None:
        self._stop.set()
        for t in self._threads:
            t.join(timeout=1)
        if self.sock:
            self.sock.close()
        if self.ping_sock:
            self.ping_sock.close()

    def _send(self, data: bytes, addr) -> None:
        try:
            self.sock.sendto(data, addr)
        except OSError as e:
            log.debug("send failed: %s", e)

    def _set_peer(self, addr) -> None:
        if addr != self.peer:
            log.info("phone connected: %s:%d", *addr)
            self.peer = addr
            if self.on_peer_change:
                self.on_peer_change(addr)
        self._peer_last = time.monotonic()

    # ------------------------------------------------------------------
    def _rx_loop(self) -> None:
        while not self._stop.is_set():
            try:
                data, addr = self.sock.recvfrom(4096)
            except socket.timeout:
                self._check_peer_timeout()
                continue
            except OSError as e:  # e.g. WSAECONNRESET on Windows after ICMP unreachable
                log.debug("recv error: %s", e)
                continue
            arrival = now_us()
            try:
                pkt = P.decode(data)
            except P.ProtocolError as e:
                log.debug("drop malformed from %s: %s", addr, e)
                continue
            if pkt.type == P.T_AUDIO:
                self._set_peer(addr)
                self._on_audio(pkt, arrival)
            elif pkt.type == P.T_DISCOVER:
                log.info("discover from %s:%d", *addr)
                self._send(P.encode(P.T_HELLO, pkt.seq, now_us()), addr)
                self._set_peer(addr)
            elif pkt.type == P.T_PONG:
                self._on_pong(pkt, arrival)
            elif pkt.type == P.T_PING:  # phone may ping us too
                self._send(P.encode(P.T_PONG, pkt.seq, pkt.ts_us), addr)
            elif pkt.type == P.T_BYE:
                log.info("phone said bye")
                self.peer = None
                if self.on_peer_change:
                    self.on_peer_change(None)

    def _on_pong(self, pkt: P.Packet, arrival: int) -> None:
        rtt = (arrival - pkt.ts_us) / 1000.0
        if 0 <= rtt < 5000:
            with self._lock:
                self._rtt_ms = rtt if self._rtt_ms is None else 0.8 * self._rtt_ms + 0.2 * rtt
                self._rtt_min = rtt if self._rtt_min is None else min(self._rtt_min, rtt)

    def _check_peer_timeout(self) -> None:
        if self.peer and time.monotonic() - self._peer_last > self.peer_timeout:
            log.warning("phone %s:%d timed out", *self.peer)
            self.peer = None
            if self.on_peer_change:
                self.on_peer_change(None)

    def _on_audio(self, pkt: P.Packet, arrival_us: int) -> None:
        if pkt.sample_rate != self.sample_rate or pkt.channels != self.channels:
            log.warning("format mismatch: got %d Hz/%d ch, expected %d Hz/%d ch",
                        pkt.sample_rate, pkt.channels, self.sample_rate, self.channels)
            return
        if pkt.frames != self.fpp:
            log.info("packet size changed %d -> %d frames", self.fpp, pkt.frames)
            self.fpp = pkt.frames
            self.jb = self._make_jb(pkt.frames)
        with self._lock:
            self._received += 1
            if self._max_seq is None or P.seq_diff(pkt.seq, self._max_seq) > 1000 or \
                    P.seq_diff(pkt.seq, self._max_seq) < -1000:
                # first packet or sender restart
                self._first_seq = pkt.seq
                self._max_seq = pkt.seq
                self._received = 1
                self._transit_prev = None
            elif P.seq_diff(pkt.seq, self._max_seq) > 0:
                self._max_seq = pkt.seq
            else:
                self._reordered += 1
            transit = arrival_us - pkt.ts_us  # includes unknown clock offset; only deltas matter
            if self._transit_prev is not None:
                d = abs(transit - self._transit_prev)
                self._jitter_us += (d - self._jitter_us) / 16.0
            self._transit_prev = transit
        self.jb.push(pkt.seq, pkt.payload)

    def _ping_loop(self) -> None:
        next_at = time.monotonic() + self.ping_interval
        while not self._stop.is_set():
            ps = self.ping_sock
            if ps is None:
                if self._stop.wait(self.ping_interval):
                    break
            else:
                while not self._stop.is_set() and time.monotonic() < next_at:
                    try:
                        data, _ = ps.recvfrom(2048)
                    except socket.timeout:
                        continue
                    except OSError:
                        if self._stop.is_set():
                            return
                        continue
                    arrival = now_us()
                    try:
                        pkt = P.decode(data)
                    except P.ProtocolError:
                        continue
                    if pkt.type == P.T_PONG:
                        self._on_pong(pkt, arrival)
            next_at = time.monotonic() + self.ping_interval
            if self._stop.is_set():
                break
            peer = self.peer
            if peer:
                self._ping_seq += 1
                ping = P.encode(P.T_PING, self._ping_seq, now_us())
                if ps is not None:
                    try:
                        ps.sendto(ping, peer)
                    except OSError as e:
                        log.debug("ping send failed: %s", e)
                else:
                    self._send(ping, peer)

    # ------------------------------------------------------------------
    def snapshot(self) -> StatsSnapshot:
        with self._lock:
            if self._first_seq is None:
                expected = 0
            else:
                expected = P.seq_diff(self._max_seq, self._first_seq) + 1
            lost = max(0, expected - self._received)
            js = self.jb.stats
            return StatsSnapshot(
                t=time.time(), peer=f"{self.peer[0]}:{self.peer[1]}" if self.peer else None,
                received=self._received, expected=expected, lost=lost,
                loss_pct=(100.0 * lost / expected) if expected else 0.0,
                reordered=self._reordered, late=js.late, concealed=js.concealed,
                underruns=js.underruns, overflow_drops=js.overflow_drops,
                depth_ms=self.jb.depth_ms(self.sample_rate), rtt_ms=self._rtt_ms,
                rtt_min_ms=self._rtt_min, arrival_jitter_ms=self._jitter_us / 1000.0,
                pkt_ms=self.fpp * 1000.0 / self.sample_rate,
                buffer_avg_ms=self.jb.avg_depth_ms, catch_up_drops=js.catch_up_drops)

    def pull(self, nbytes: int) -> bytes:
        return self.jb.pull(nbytes)
