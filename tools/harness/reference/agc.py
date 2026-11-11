"""E3: Auto volume. CordMic 0.5.7 gated AGC (40 ms RMS, 25 ms down / 220 ms up, cap so floor <= -52 dBFS)
vs a slow speech-level AGC in the style of WebRTC AGC2 adaptive digital (speech-frame level estimate,
gain slew <= 6 dB/s, same noise ceiling) followed by the same -1 dBFS limiter. NR off, clarity EQ off,
so only the level stage is compared. Metrics: output speech level spread (per 3 s window, K-weighted
LUFS), syllable dynamics kept (P95-P5 of 20 ms speech frame levels, out minus in), within-speech
gain modulation, and pause-noise level right after speech (pumping)."""
from paths import ROOT
import numpy as np, soundfile as sf, glob, json, math
from scipy.signal import resample_poly, lfilter
from pesq import pesq
FS = 48000
db2 = lambda d: 10 ** (d / 20)

def kweight(x):
    b1 = [1.53512485958697, -2.69169618940638, 1.19839281085285]; a1 = [1, -1.69065929318241, 0.73248077421585]
    b2 = [1.0, -2.0, 1.0]; a2 = [1, -1.99004745483398, 0.99007225036621]
    return lfilter(b2, a2, lfilter(b1, a1, x))

def lufs_blocks(x, win=3.0):
    y = kweight(x); n = int(win * FS); out = []
    for i in range(0, len(y) - n, n // 2):
        z = np.mean(y[i:i + n] ** 2); out.append(-0.691 + 10 * np.log10(z + 1e-20))
    return np.array(out)

class Gate:
    """CordMic 0.5.7 GateStep (pre-gain floor tracking + gate with 200 ms hold)."""
    def __init__(self):
        self.rms = 0; self.floor = db2(-54); self.gated = True; self.hold = 0; self.tick = 0; self.below = 0; self.known = False
        self.rc = 1 - math.exp(-1 / (0.04 * FS)); self.fd = 1 - math.exp(-1 / (0.3 * FS))
        self.fu = 10 ** (1.5 / 20 / FS); self.fuo = 10 ** (0.5 / 20 / FS); self.holdN = int(0.2 * FS)
        self.set_gates(-54)
    def set_gates(self, fdb):
        o = min(max(fdb + 10, -55), -28); c = min(max(fdb + 4, -65), -38)
        if c > o - 3: c = o - 3
        self.go, self.gc = db2(o), db2(c)
    def step(self, x):
        self.rms += (x * x - self.rms) * self.rc; lv = math.sqrt(max(self.rms, 0))
        if lv < self.floor:
            self.floor += (lv - self.floor) * self.fd
            if not self.known:
                self.below += 1
                if self.below >= FS // 4: self.known = True
        else: self.floor *= self.fu if self.gated else self.fuo
        self.floor = min(max(self.floor, 1e-5), 0.1)
        self.tick += 1
        if self.tick >= 64: self.tick = 0; self.set_gates(20 * math.log10(self.floor))
        if lv > self.go: self.gated = False; self.hold = self.holdN
        elif lv < self.gc:
            if self.hold > 0: self.hold -= 1
            else: self.gated = True

class Limiter:
    def __init__(self):
        self.peak = 0; self.lim = 1; self.pr = 1 - math.exp(-1 / (0.03 * FS)); self.lr = 1 - math.exp(-1 / (0.05 * FS)); self.t = db2(-1)
    def step(self, y):
        a = abs(y); self.peak = a if a > self.peak else self.peak + (a - self.peak) * self.pr
        gr = self.t / self.peak if self.peak > self.t else 1.0
        self.lim = gr if gr < self.lim else self.lim + (gr - self.lim) * self.lr
        return max(-0.99, min(0.99, y * self.lim))

def cordmic_agc(x, gain_db=6):
    g = Gate(); L = Limiter(); user = db2(gain_db); maxagc = db2(min(24, 24 - gain_db)); tgt = db2(-18); nc = db2(-52)
    rc = 1 - math.exp(-1 / (0.04 * FS)); rise = 1 - math.exp(-1 / (0.22 * FS)); fall = 1 - math.exp(-1 / (0.025 * FS))
    grel = 1 - math.exp(-1 / (0.08 * FS)); rms = 0; agc = 1.0
    y = np.empty_like(x); G = np.empty_like(x)
    for i, xi in enumerate(x):
        g.step(xi); v = xi * user
        rms += (v * v - rms) * rc; lv = math.sqrt(max(rms, 0))
        cap = min(max(nc / max(g.floor * user, 1e-9), 1.0), maxagc) if g.known else maxagc
        want = 1.0 if g.gated else min(max(tgt / max(lv, 1e-5), 0.25), cap)
        c = grel if g.gated else (rise if want > agc else fall)
        agc += (want - agc) * c
        G[i] = agc * user; y[i] = L.step(v * agc)
    return y, G

def slow_agc(x, gain_db=6, slew_db_s=6.0, tgt_db=-18.0):
    """Speech-level AGC: 10 ms frames; speech level = exponentially averaged dB RMS of gate-open frames
    (~0.6 s of speech); target gain approached at <= slew dB/s; same noise ceiling and limiter."""
    g = Gate(); L = Limiter(); user = db2(gain_db); nc = 20 * math.log10(db2(-52))
    F = 480; lvl = None; gdb = 0.0; y = np.empty_like(x); G = np.empty_like(x)
    step = slew_db_s * F / FS
    for f0 in range(0, len(x) - F + 1, F):
        fr = x[f0:f0 + F]
        for s in fr: g.step(s)
        e = 10 * math.log10(np.mean((fr * user) ** 2) + 1e-12)
        if not g.gated:
            lvl = e if lvl is None else (lvl + (e - lvl) * 0.05 if e > lvl - 20 else lvl)
        want = 0.0
        if lvl is not None:
            want = tgt_db - lvl
            if g.known: want = min(want, nc - 20 * math.log10(max(g.floor * user, 1e-9)))
            want = min(max(want, -12), 24 - gain_db)
        gnew = gdb + max(-step, min(step, want - gdb))
        ramp = db2(np.linspace(gdb, gnew, F)) * user; gdb = gnew
        for j in range(F):
            G[f0 + j] = ramp[j]; y[f0 + j] = L.step(fr[j] * ramp[j])
    n = len(x) // F * F
    return y[:n], G[:n]

def frame_db(x, F=960):
    f = x[: len(x) // F * F].reshape(-1, F); return 10 * np.log10((f ** 2).mean(1) + 1e-12)

def analyse(clean, x, y, G):
    n = min(len(x), len(y)); x, y, G, clean = x[:n], y[:n], G[:n], clean[:n]
    ce = frame_db(clean); act = ce > ce.max() - 30
    din = frame_db(x)[act]; dout = frame_db(y)[act]
    dyn_in = np.percentile(din, 95) - np.percentile(din, 5); dyn_out = np.percentile(dout, 95) - np.percentile(dout, 5)
    gdb = 20 * np.log10(G[: len(G) // 960 * 960].reshape(-1, 960).mean(1) + 1e-12)
    # pause frames shortly after speech (first 300 ms of a pause)
    after = np.zeros_like(act)
    for i in range(1, len(act)):
        if not act[i] and act[max(0, i - 15):i].any(): after[i] = True
    lu = lufs_blocks(y)
    return dict(dyn_loss_db=float(dyn_in - dyn_out), gain_std_speech_db=float(gdb[act].std()),
                pause_noise_dbfs=float(np.mean(frame_db(y)[after])), lufs_mean=float(lu.mean()),
                pesq=float(pesq(16000, resample_poly(clean, 1, 3), resample_poly(y, 1, 3), "wb")))

import sys
NOISE_DB = float(sys.argv[1]) if len(sys.argv) > 1 else -74.0
files = sorted(glob.glob(f"{ROOT}/data/libri/*.flac"))
rng = np.random.default_rng(5); res = []
for f in files:
    c, _ = sf.read(f); c = resample_poly(c, 3, 1)[: 12 * FS]
    c = c / np.sqrt(np.mean(c[np.abs(c) > 1e-3] ** 2))
    for scen, prof in (("near_-24", np.full(len(c), -24.0)), ("far_-38", np.full(len(c), -38.0)),
                       ("step_-24_to_-36", np.where(np.arange(len(c)) < 6 * FS, -24.0, -36.0))):
        cl = c * db2(prof) / db2(6)          # pre-gain level so that +6 dB user gain gives the scenario level
        noise = rng.standard_normal(len(c)) * db2(NOISE_DB)
        x = cl + noise
        for name, fn in (("cordmic057", cordmic_agc), ("slow_6dBps", slow_agc)):
            y, G = fn(x)
            r = analyse(cl * db2(6), x * db2(6), y, G); r.update(clip=f.split("/")[-1][:2], scen=scen, method=name)
            res.append(r); print(r, flush=True)
json.dump(res, open(f"{ROOT}/results/agc_noise{int(NOISE_DB)}.json", "w"), indent=1)
