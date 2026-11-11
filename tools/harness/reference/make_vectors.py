"""Deterministic test vectors for the C# ports. Signals are defined by formulas (regenerate in C#),
expected outputs stored with 9 significant digits. Tolerance for float32 C#: abs 2e-5."""
import math, json
from plc_ref import PitchPlc
from asrc_ref import CubicResampler, DriftController
FS = 48000; P = 240
def voiced(n): return 0.3*math.sin(2*math.pi*150*n/FS) + 0.1*math.sin(2*math.pi*450*n/FS + 0.5)
def lcg_noise(N, seed=12345):
    x = seed; out = []
    for _ in range(N):
        x = (1664525*x + 1013904223) % 4294967296; out.append((x/4294967296.0 - 0.5)*0.2)
    return out
r9 = lambda v: float('%.9g' % v)
V = {"tolerance_abs": 2e-5, "plc": [], "asrc": {}, "controller": {}}
cases = {
  "voiced_150Hz": [voiced(n) for n in range(40*P)],
  "lcg_noise": lcg_noise(40*P),
  "digital_silence": [0.0]*(40*P),
}
lost = sorted({0, 10, 11, 20} | set(range(25, 33)))   # start-of-stream loss, pair, 8-run (> 6 -> silence)
for name, sig in cases.items():
    plc = PitchPlc(P); out = []
    for p in range(40):
        pkt = sig[p*P:(p+1)*P]
        out += plc.lost() if p in lost else plc.good(pkt)
    V["plc"].append({"name": name, "packet": P, "lost_packets": lost, "output": [r9(v) for v in out],
                     "signal": {"voiced_150Hz": "0.3*sin(2*pi*150*n/48000)+0.1*sin(2*pi*450*n/48000+0.5)",
                                "lcg_noise": "x=(1664525*x+1013904223) mod 2^32, seed 12345; v=(x/2^32-0.5)*0.2",
                                "digital_silence": "0"}[name]})
src = [0.5*math.sin(2*math.pi*1000*n/FS) for n in range(4000)]
for ratio in (1.0, 1.0003, 0.9997):
    it = iter(src); r = CubicResampler(); y = r.process(2000, ratio, lambda: next(it, None))
    V["asrc"][str(ratio)] = {"input": "0.5*sin(2*pi*1000*n/48000), n=0..3999", "n_out": 2000, "output": [r9(v) for v in y]}
c = DriftController(1440); fills = [1540]*500 + [1340]*500 + [1440]*500
V["controller"] = {"target": 1440, "fills": "1540 x500, 1340 x500, 1440 x500", "ratios": [float('%.12g' % c.update(f)) for f in fills]}
json.dump(V, open("test_vectors.json", "w"))
# self-checks
y1 = V["asrc"]["1.0"]["output"]; assert all(abs(y1[k] - src[k+1]) < 1e-9 for k in range(2000)), "ratio 1 must be exact (1-sample offset)"
print("ok", {k: len(v["output"]) for k, v in V["asrc"].items()}, len(V["plc"]), len(V["controller"]["ratios"]))
