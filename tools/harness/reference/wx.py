"""E8: whisper protection for RNNoise. Real whisper + regular speech from EARS (CC-BY-NC-4.0, local evaluation
only, never redistributed), 48 kHz fullband. Methods: cm057 Strong, RNNoise raw, gain-floor caps, a fixed blend,
and two speech-presence-gated mix-backs to the cm057 output."""
import numpy as np, soundfile as sf, glob, json, os, subprocess, tempfile, sys
from scipy.signal import butter, sosfilt
from multiprocessing import Pool
from nr import *
from run_nr import make_noise, norm_active, FS, NB, CLEAN
from metrics import evaluate, active_mask
from paths import ROOT
F = 480
SPK = ["p001", "p002", "p003", "p004", "p006", "p007"]

def rnn_vad(x):
    with tempfile.TemporaryDirectory() as d:
        (np.clip(x, -1, 1) * 32767).astype("<i2").tofile(f"{d}/i")
        subprocess.run([f"{ROOT}/ext/rnnoise/rnnoise_vad", f"{d}/i", f"{d}/o", f"{d}/v"], check=True)
        y = np.fromfile(f"{d}/o", "<f4").astype(float) / 32768; v = np.fromfile(f"{d}/v", "<f4").astype(float)
    y = np.concatenate([y[2 * F:], np.zeros(2 * F)])[:len(x)]  # RNNoise total delay = 960 samples (20 ms) in this build
    y = np.concatenate([y, np.zeros(len(x) - len(y))]); v = np.concatenate([v[1:], v[-1:]])
    return y, v

BAND = butter(4, [300, 8000], "band", fs=FS, output="sos")
def frame_e(s):
    b = sosfilt(BAND, s); n = len(b) // F
    return (b[: n * F].reshape(n, F) ** 2).mean(1) + 1e-12

def presence(x, thr_db=9.0):
    """energy detector: floor = running min with +3 dB/s creep; present if > floor + 9 dB; 100 ms hangover"""
    e = frame_e(x); f = e[:30].min(); p = np.zeros(len(e), bool); h = 0; up = 10 ** (0.3 / 100)
    for i, v in enumerate(e):
        f = min(f * up, v) if v < f * 4 else f * up
        if v > f * 10 ** (thr_db / 10): h = 10
        p[i] = h > 0; h -= 1
    return p, e

def gated(x, r, k, vad=None, base_cap=None, thr_db=9.0, lo=6.0, hi=15.0, mmax=1.0):
    p, ex = presence(x, thr_db); er = frame_e(r); n = len(ex)
    supp = 10 * np.log10(ex / er[:n])
    if vad is None: mt = p * np.clip((supp - lo) / (hi - lo), 0, 1) * mmax
    else: mt = p * np.clip(1 - vad[:n] / 0.6, 0, 1) * (supp > 3)
    m = np.zeros(n); s = 0.0
    for i in range(n):
        s = s + 0.5 * (mt[i] - s) if mt[i] > s else 0.9 * s + 0.1 * mt[i]; m[i] = s
    ms = np.interp(np.arange(len(x)), np.arange(n) * F + F / 2, m)
    base = r if base_cap is None else (1 - 10 ** (-base_cap / 20)) * r + 10 ** (-base_cap / 20) * x
    return base + ms * (k - base), float(m[p].mean()) if p.any() else 0.0

def proj_loss(c, y, mask, sos=None):
    if sos is not None: c, y = sosfilt(sos, c), sosfilt(sos, y)
    n = len(mask) * F; cm = c[:n].reshape(-1, F)[mask].ravel(); ym = y[:n].reshape(-1, F)[mask].ravel()
    a = np.dot(ym, cm) / np.dot(cm, cm); return float(-20 * np.log10(max(abs(a), 1e-6)))

HF = butter(4, [3000, 12000], "band", fs=FS, output="sos")
MID = butter(4, [300, 3000], "band", fs=FS, output="sos")
def utts():
    out = []
    for s in SPK:
        for u in ["rainbow_02", "rainbow_04"]:
            out.append((s, u))
    return out

def job(a):
    spk, utt, style, kind, snr = a
    c, sr = sf.read(f"{ROOT}/ext/ears/{spk}/{utt}_{style}.wav"); assert sr == FS
    c = c[: 12 * FS]; c = norm_active(c, -30.0 if style == "whisper" else -26.0)
    rng = np.random.default_rng(hash((spk, utt, kind, snr)) % 2**32)
    nz = make_noise(kind, len(c), rng, CLEAN[:6])
    m = active_mask(c); f = c[: len(m) * F].reshape(-1, F); sl = 10 * np.log10((f[m] ** 2).mean())
    nz *= 10 ** ((sl - snr - 10 * np.log10((nz ** 2).mean())) / 20); x = c + nz
    r, vad = rnn_vad(x); k, _ = stft_process(x, CM057(NB, True), 256, 128, "hann_analysis")
    outs = {"noisy": x, "cm057_strong": k, "rnnoise": r}
    for cap in (12, 15, 20):
        g = 10 ** (-cap / 20); outs[f"rnn_cap{cap}"] = (1 - g) * r + g * x
    outs["blend50_cm057"] = 0.5 * r + 0.5 * k
    outs["gated_cm057"], mg = gated(x, r, k)
    outs["gated_cm057_cap20"], _ = gated(x, r, k, base_cap=20)
    outs["gated_vad_cm057"], mv = gated(x, r, k, vad=vad)
    rows = []
    nm_ = ~m
    for name, y in outs.items():
        n = len(m) * F
        pr = 10 * np.log10((x[:n].reshape(-1, F)[nm_] ** 2).sum() / ((y[:n].reshape(-1, F)[nm_] ** 2).sum() + 1e-20))
        rr = evaluate(c, x, y, dns=True)
        rr.update(method=name, spk=spk, utt=utt, style=style, noise=kind, snr=snr,
                  loss_bb=proj_loss(c, y, m), loss_hf=proj_loss(c, y, m, HF), loss_mid=proj_loss(c, y, m, MID), pause_nr=float(pr),
                  rnn_vad_speech=float(vad[: len(m)][m].mean()))
        rows.append(rr)
    return rows

if __name__ == "__main__":
    jobs = [(s, u, st, k, snr) for (s, u) in utts() for st in ["whisper", "regular"]
            for k in ["pink", "fan", "babble", "keyboard"] for snr in ([10, 20] if st == "whisper" else [5, 20])]
    with Pool(8) as p, open(f"{ROOT}/results/whisper.jsonl", "w") as fo:
        for rows in p.imap_unordered(job, jobs):
            for r in rows: fo.write(json.dumps({k: (float(v) if isinstance(v, np.floating) else v) for k, v in r.items()}) + "\n")
            fo.flush()
    print("done", len(jobs))
