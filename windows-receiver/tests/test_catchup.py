"""0.4.2 latency work: soft catch-up, resume sizing, averaged depth, separate ping socket."""
import math
import unittest
import socket
import struct
import time

from cordmic_receiver import protocol as P
from cordmic_receiver.jitter import JitterBuffer, crossfade
from cordmic_receiver.server import Receiver, now_us

FPP = 240
NB = FPP * 2


def pkt(n: int) -> bytes:
    return bytes([n % 256]) * NB


def backlogged(catch_up: bool, backlog: int = 8) -> JitterBuffer:
    jb = JitterBuffer(FPP, target_packets=2, max_packets=40)
    jb.catch_up = catch_up
    seq = 1
    for _ in range(backlog):
        jb.push(seq, pkt(seq)); seq += 1
    for _ in range(400):
        jb.push(seq, pkt(seq)); seq += 1
        jb.pull(NB)
    return jb


class CatchUpTest(unittest.TestCase):
    def test_standing_backlog_is_drained(self):
        jb = backlogged(True)
        assert jb.depth_packets() <= jb.target + 1
        assert jb.stats.catch_up_drops >= 5
        assert jb.stats.underruns == 0 and jb.stats.concealed == 0 and jb.stats.overflow_drops == 0

    def test_without_catch_up_backlog_stays(self):
        jb = backlogged(False)
        assert jb.depth_packets() >= 8
        assert jb.stats.catch_up_drops == 0

    def test_small_jitter_is_not_trimmed(self):
        jb = JitterBuffer(FPP, target_packets=2, max_packets=40)
        seq = 1
        jb.push(seq, pkt(seq)); seq += 1
        jb.push(seq, pkt(seq)); seq += 1
        for i in range(400):
            if i % 2 == 0:
                jb.push(seq, pkt(seq)); seq += 1
                jb.push(seq, pkt(seq)); seq += 1
            jb.pull(NB)
        assert jb.stats.catch_up_drops == 0
        assert jb.stats.underruns == 0

    def test_average_depth_after_window(self):
        jb = JitterBuffer(FPP, target_packets=2, max_packets=40)
        jb.window_packets = 50
        assert math.isnan(jb.avg_depth_ms)
        seq = 1
        for _ in range(3):
            jb.push(seq, pkt(seq)); seq += 1
        for _ in range(60):
            jb.push(seq, pkt(seq)); seq += 1
            jb.pull(NB)
        assert 10 <= jb.avg_depth_ms <= 20
        assert not math.isnan(jb.min_residual_ms)

    def test_crossfade_endpoints(self):
        a = struct.pack(f"<{FPP}h", *([10000] * FPP))
        b = struct.pack(f"<{FPP}h", *([-10000] * FPP))
        out = struct.unpack(f"<{FPP}h", crossfade(a, b))
        assert 9800 <= out[0] <= 10000
        assert -10000 <= out[-1] <= -9800
        assert -200 <= out[FPP // 2] <= 200

    def test_resume_waits_for_whole_pull(self):
        jb = JitterBuffer(FPP, target_packets=1, max_packets=40)
        jb.push(1, pkt(1))
        assert jb.pull(2 * NB) == bytes(2 * NB)
        jb.push(2, pkt(2))
        assert jb.pull(2 * NB) == pkt(1) + pkt(2)

    def test_ping_uses_separate_socket(self):
        rx = Receiver(port=0, bind="127.0.0.1", ping_interval=0.05)
        rx.start()
        phone = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        phone.bind(("127.0.0.1", 0))
        phone.settimeout(0.5)
        try:
            phone.sendto(P.encode(P.T_AUDIO, 0, now_us(), bytes(NB), frames=FPP), ("127.0.0.1", rx.port))
            pongs, ping_port = 0, None
            deadline = time.monotonic() + 3
            while pongs < 5 and time.monotonic() < deadline:
                try:
                    data, addr = phone.recvfrom(2048)
                except socket.timeout:
                    continue
                p = P.decode(data)
                if p.type != P.T_PING:
                    continue
                ping_port = addr[1]
                phone.sendto(P.encode(P.T_PONG, p.seq, p.ts_us), addr)
                pongs += 1
            assert pongs >= 5
            assert ping_port != rx.port
            time.sleep(0.1)
            s = rx.snapshot()
            assert s.rtt_ms is not None and 0 <= s.rtt_ms < 100
        finally:
            phone.close()
            rx.stop()

