import unittest

from cordmic_receiver.jitter import JitterBuffer

FPP = 240
NB = FPP * 2


def pkt(n):
    return bytes([n % 256]) * NB


class JitterTest(unittest.TestCase):
    def test_prebuffer_then_in_order(self):
        jb = JitterBuffer(FPP, target_packets=2)
        jb.push(10, pkt(10))
        self.assertEqual(jb.pull(NB), bytes(NB))  # still buffering -> silence
        jb.push(11, pkt(11))
        self.assertEqual(jb.pull(NB), pkt(10))
        self.assertEqual(jb.pull(NB), pkt(11))

    def test_reorder_is_fixed(self):
        jb = JitterBuffer(FPP, target_packets=3)
        for s in (1, 3, 2):
            jb.push(s, pkt(s))
        self.assertEqual([jb.pull(NB) for _ in range(3)], [pkt(1), pkt(2), pkt(3)])

    def test_loss_concealed_with_silence(self):
        jb = JitterBuffer(FPP, target_packets=2)
        for s in (1, 3, 4):
            jb.push(s, pkt(s))
        out = [jb.pull(NB) for _ in range(4)]
        self.assertEqual(out, [pkt(1), bytes(NB), pkt(3), pkt(4)])
        self.assertEqual(jb.stats.concealed, 1)

    def test_late_packet_dropped(self):
        jb = JitterBuffer(FPP, target_packets=1)
        jb.push(1, pkt(1)); jb.push(3, pkt(3))
        jb.pull(NB); jb.pull(NB)  # plays 1, conceals 2
        jb.push(2, pkt(2))
        self.assertEqual(jb.stats.late, 1)
        self.assertEqual(jb.pull(NB), pkt(3))

    def test_underrun_rebuffers(self):
        jb = JitterBuffer(FPP, target_packets=2)
        jb.push(1, pkt(1)); jb.push(2, pkt(2))
        jb.pull(NB); jb.pull(NB)
        self.assertEqual(jb.pull(NB), bytes(NB))
        self.assertEqual(jb.stats.underruns, 1)
        jb.push(3, pkt(3))
        self.assertEqual(jb.pull(NB), bytes(NB))  # waiting for target again
        jb.push(4, pkt(4))
        self.assertEqual(jb.pull(NB), pkt(3))

    def test_overflow_bounds_latency(self):
        jb = JitterBuffer(FPP, target_packets=2, max_packets=6)
        for s in range(100):
            jb.push(s, pkt(s))
        self.assertLessEqual(jb.depth_packets(), 6)
        self.assertGreater(jb.stats.overflow_drops, 0)
        self.assertLessEqual(jb.depth_ms(), 6 * 5.0)

    def test_odd_pull_sizes(self):
        jb = JitterBuffer(FPP, target_packets=1)
        for s in range(4):
            jb.push(s, pkt(s))
        data = b"".join(jb.pull(100) for _ in range(NB * 4 // 100)) + jb.pull(NB * 4 % 100)
        self.assertEqual(data, pkt(0) + pkt(1) + pkt(2) + pkt(3))

    def test_sender_restart(self):
        jb = JitterBuffer(FPP, target_packets=1)
        jb.push(50000, pkt(1)); jb.pull(NB)
        jb.push(0, pkt(2))  # restart with seq 0
        self.assertEqual(jb.pull(NB), pkt(2))

    def test_seq_wraparound(self):
        jb = JitterBuffer(FPP, target_packets=2)
        jb.push(0xFFFFFFFF, pkt(1)); jb.push(0, pkt(2)); jb.push(1, pkt(3))
        self.assertEqual([jb.pull(NB) for _ in range(3)], [pkt(1), pkt(2), pkt(3)])


if __name__ == "__main__":
    unittest.main()
