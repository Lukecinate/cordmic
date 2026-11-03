"""CordMic wire protocol (shared with the Android app, see README.md section 1).

Every UDP datagram starts with a 20-byte little-endian header:

    offset size field
    0      2    magic      b"UM"
    2      1    version    1
    3      1    type       1=AUDIO 2=DISCOVER 3=HELLO 4=PING 5=PONG 6=BYE
    4      4    seq        uint32, wraps
    8      8    ts_us      uint64 microseconds (sender monotonic clock)
    16     2    frames     uint16, number of PCM frames in payload (AUDIO)
    18     2    flags      uint16, bits 0-3 codec (0 = PCM s16le), bits 4-7 channels-1,
                           bits 8-11 sample-rate code (0=48000, 1=44100, 2=16000)

AUDIO payload: raw PCM s16le, `frames * channels` samples.
PING/PONG: PONG echoes seq and ts_us of the PING unchanged (RTT measurement).
"""
from __future__ import annotations

import struct
from dataclasses import dataclass

MAGIC = b"UM"
VERSION = 1
HEADER = struct.Struct("<2sBBIQHH")
HEADER_SIZE = HEADER.size  # 20

T_AUDIO, T_DISCOVER, T_HELLO, T_PING, T_PONG, T_BYE = 1, 2, 3, 4, 5, 6
TYPE_NAMES = {1: "AUDIO", 2: "DISCOVER", 3: "HELLO", 4: "PING", 5: "PONG", 6: "BYE"}

CODEC_PCM16 = 0
RATE_CODES = {0: 48000, 1: 44100, 2: 16000}
RATE_TO_CODE = {v: k for k, v in RATE_CODES.items()}

DEFAULT_PORT = 50005
SEQ_MOD = 1 << 32


class ProtocolError(ValueError):
    pass


@dataclass(frozen=True)
class Packet:
    type: int
    seq: int
    ts_us: int
    frames: int
    flags: int
    payload: bytes

    @property
    def codec(self) -> int:
        return self.flags & 0x0F

    @property
    def channels(self) -> int:
        return ((self.flags >> 4) & 0x0F) + 1

    @property
    def sample_rate(self) -> int:
        return RATE_CODES.get((self.flags >> 8) & 0x0F, 48000)


def make_flags(codec: int = CODEC_PCM16, channels: int = 1, sample_rate: int = 48000) -> int:
    if not 1 <= channels <= 16:
        raise ValueError("channels out of range")
    return (codec & 0x0F) | ((channels - 1) << 4) | (RATE_TO_CODE[sample_rate] << 8)


def encode(ptype: int, seq: int, ts_us: int, payload: bytes = b"", frames: int = 0,
           flags: int = 0) -> bytes:
    return HEADER.pack(MAGIC, VERSION, ptype, seq % SEQ_MOD, ts_us & 0xFFFFFFFFFFFFFFFF,
                       frames, flags) + payload


def decode(data: bytes) -> Packet:
    if len(data) < HEADER_SIZE:
        raise ProtocolError("short packet")
    magic, ver, ptype, seq, ts_us, frames, flags = HEADER.unpack_from(data, 0)
    if magic != MAGIC:
        raise ProtocolError("bad magic")
    if ver != VERSION:
        raise ProtocolError(f"unsupported version {ver}")
    payload = bytes(data[HEADER_SIZE:])
    if ptype == T_AUDIO:
        ch = ((flags >> 4) & 0x0F) + 1
        if (flags & 0x0F) == CODEC_PCM16 and len(payload) != frames * ch * 2:
            raise ProtocolError("payload length does not match frame count")
    return Packet(ptype, seq, ts_us, frames, flags, payload)


def seq_diff(a: int, b: int) -> int:
    """Signed distance a - b on the 32-bit wrapping sequence space."""
    d = (a - b) % SEQ_MOD
    return d - SEQ_MOD if d >= SEQ_MOD // 2 else d
