"""Tests the output callback's mono->N-channel duplication without PortAudio."""
import struct
import unittest

from cordmic_receiver.audio_out import AudioOutput


class CallbackTest(unittest.TestCase):
    def _out(self, ch, src):
        o = AudioOutput.__new__(AudioOutput)  # skip stream creation (no audio device needed)
        o.source, o.out_channels, o.callback_count, o.status_flags = src, ch, 0, 0
        return o

    def test_stereo_duplication(self):
        mono = struct.pack("<4h", 1, -2, 3, 32767)
        o = self._out(2, lambda n: mono[:n])
        buf = bytearray(4 * 2 * 2)
        o._cb(memoryview(buf), 4, None, None)
        self.assertEqual(struct.unpack("<8h", buf), (1, 1, -2, -2, 3, 3, 32767, 32767))

    def test_mono_passthrough(self):
        mono = struct.pack("<3h", 5, 6, 7)
        o = self._out(1, lambda n: mono[:n])
        buf = bytearray(6)
        o._cb(memoryview(buf), 3, None, None)
        self.assertEqual(bytes(buf), mono)


if __name__ == "__main__":
    unittest.main()
