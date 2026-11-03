"""Loopback test: simulated phone (sim_sender) -> UDP 127.0.0.1 -> Receiver -> real-time pull."""
import os
import sys
import threading
import time
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import sim_sender  # noqa: E402
from cordmic_receiver.server import Receiver  # noqa: E402


class FakeAudioClock:
    """Pulls 5 ms blocks at real-time rate, like a WASAPI callback would."""

    def __init__(self, rx, block=240):
        self.rx, self.block = rx, block
        self.stop = threading.Event()
        self.depths, self.nonsilent = [], 0
        self.t = threading.Thread(target=self.run, daemon=True)

    def run(self):
        t0, n = time.perf_counter(), 0
        while not self.stop.is_set():
            n += 1
            d = t0 + n * self.block / 48000 - time.perf_counter()
            if d > 0:
                time.sleep(d)
            data = self.rx.pull(self.block * 2)
            if any(data):
                self.nonsilent += 1
            self.depths.append(self.rx.jb.depth_ms())


class LoopbackTest(unittest.TestCase):
    def _run(self, **kw):
        rx = Receiver(port=0, bind="127.0.0.1", jitter_ms=10, ping_interval=0.1)
        rx.start()
        clk = FakeAudioClock(rx)
        clk.t.start()
        try:
            res = sim_sender.run(host="127.0.0.1", port=rx.port, quiet=True, **kw)
            time.sleep(0.1)
        finally:
            clk.stop.set(); clk.t.join(1); rx.stop()
        return rx.snapshot(), res, clk

    def test_clean_link(self):
        snap, res, clk = self._run(seconds=3)
        print("\nclean:", snap.line(), res)
        self.assertEqual(snap.lost, 0)
        self.assertGreater(res["pongs"], 5)
        self.assertIsNotNone(snap.rtt_ms)
        self.assertGreater(clk.nonsilent, 0.9 * res["packets"])
        self.assertLess(max(clk.depths), 40)

    def test_lossy_jittery_link(self):
        snap, res, clk = self._run(seconds=3, loss=0.02, jitter_ms=3, reorder=0.02, seed=7)
        print("\nlossy:", snap.line(), res)
        # receiver loss accounting must match what the simulator dropped (within the tail)
        self.assertAlmostEqual(snap.lost, res["dropped"], delta=2)
        self.assertGreater(snap.reordered, 0)
        self.assertLess(max(clk.depths), 40)


if __name__ == "__main__":
    unittest.main()
