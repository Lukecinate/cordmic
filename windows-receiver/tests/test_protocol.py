import unittest

from cordmic_receiver import protocol as P


class ProtocolTest(unittest.TestCase):
    def test_roundtrip(self):
        payload = bytes(480)
        flags = P.make_flags(channels=1, sample_rate=48000)
        data = P.encode(P.T_AUDIO, 123456, 987654321, payload, 240, flags)
        self.assertEqual(len(data), P.HEADER_SIZE + 480)
        pkt = P.decode(data)
        self.assertEqual((pkt.type, pkt.seq, pkt.ts_us, pkt.frames), (P.T_AUDIO, 123456, 987654321, 240))
        self.assertEqual((pkt.channels, pkt.sample_rate, pkt.codec), (1, 48000, 0))

    def test_header_layout_matches_android(self):
        # Must match android/.../Protocol.kt (little endian, 20 bytes)
        data = P.encode(P.T_PING, 0x01020304, 0x1122334455667788, b"", 0, 0)
        self.assertEqual(data.hex(), "554d" "01" "04" "04030201" "8877665544332211" "0000" "0000")

    def test_bad_packets(self):
        with self.assertRaises(P.ProtocolError):
            P.decode(b"xx")
        with self.assertRaises(P.ProtocolError):
            P.decode(b"XX" + bytes(18))
        with self.assertRaises(P.ProtocolError):  # length mismatch
            P.decode(P.encode(P.T_AUDIO, 1, 1, bytes(10), 240, P.make_flags()))

    def test_seq_wrap(self):
        self.assertEqual(P.seq_diff(0, 0xFFFFFFFF), 1)
        self.assertEqual(P.seq_diff(0xFFFFFFFF, 0), -1)
        self.assertEqual(P.seq_diff(10, 5), 5)


if __name__ == "__main__":
    unittest.main()
