"""Simulated phone: sends a sine tone using the CordMic protocol (for testing without a phone).

    python sim_sender.py --host 127.0.0.1 --seconds 10 --loss 0.01 --jitter-ms 2
"""
from __future__ import annotations

import argparse
import math
import random
import socket
import struct
import threading
import time

from cordmic_receiver import protocol as P


def tone(frames, start, freq=440.0, rate=48000, amp=0.3):
    return struct.pack(f"<{frames}h", *(int(amp * 32767 * math.sin(2 * math.pi * freq * (start + i) / rate))
                                        for i in range(frames)))


def run(host="127.0.0.1", port=P.DEFAULT_PORT, seconds=5.0, frames=240, loss=0.0, jitter_ms=0.0,
        reorder=0.0, discover=True, seed=1, quiet=False):
    rnd = random.Random(seed)
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.settimeout(0.05)
    flags = P.make_flags(channels=1, sample_rate=48000)
    stop = threading.Event()
    pongs = [0]

    def ctl():
        while not stop.is_set():
            try:
                data, addr = s.recvfrom(2048)
            except (socket.timeout, OSError):
                continue
            try:
                pkt = P.decode(data)
            except P.ProtocolError:
                continue
            if pkt.type == P.T_PING:
                s.sendto(P.encode(P.T_PONG, pkt.seq, pkt.ts_us), addr)
                pongs[0] += 1
            elif pkt.type == P.T_HELLO and not quiet:
                print("HELLO from", addr)

    th = threading.Thread(target=ctl, daemon=True)
    th.start()
    if discover:
        s.sendto(P.encode(P.T_DISCOVER, 0, 0), (host, port))
    period = frames / 48000
    n = int(seconds / period)
    t0 = time.perf_counter()
    held = None
    sent = dropped = 0
    for seq in range(n):
        target = t0 + seq * period
        delay = target - time.perf_counter()
        if delay > 0:
            time.sleep(delay)
        pkt = P.encode(P.T_AUDIO, seq, time.perf_counter_ns() // 1000, tone(frames, seq * frames),
                       frames, flags)
        if rnd.random() < loss:
            dropped += 1
            continue
        if jitter_ms:
            time.sleep(rnd.uniform(0, jitter_ms) / 1000)
        if reorder and held is None and rnd.random() < reorder:
            held = pkt
            continue
        s.sendto(pkt, (host, port))
        sent += 1
        if held is not None:
            s.sendto(held, (host, port))
            sent += 1
            held = None
    s.sendto(P.encode(P.T_BYE, 0, 0), (host, port))
    time.sleep(0.1)
    stop.set()
    th.join(1)
    s.close()
    return {"packets": n, "sent": sent, "dropped": dropped, "pongs": pongs[0]}


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=P.DEFAULT_PORT)
    ap.add_argument("--seconds", type=float, default=10)
    ap.add_argument("--frames", type=int, default=240)
    ap.add_argument("--loss", type=float, default=0.0)
    ap.add_argument("--jitter-ms", type=float, default=0.0)
    ap.add_argument("--reorder", type=float, default=0.0)
    print(run(**{k.replace("-", "_"): v for k, v in vars(ap.parse_args()).items()}))
