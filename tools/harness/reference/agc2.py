"""E9: distant-speech loudness. LUFS-targeted speech-gated AGC with the noise ceiling measured AFTER noise
reduction, vs the 0.5.7 AGC, on far speech (-38 dBFS after +6 dB user gain) in a noisy room."""
from paths import ROOT
import numpy as np, soundfile as sf, glob, json, math, sys
from scipy.signal import resample_poly
from multiprocessing import Pool
from pystoi import stoi
from pesq import pesq
import importlib
src = open('agc.py').read().split('import sys')[0]; exec(src)          # Gate, Limiter, cordmic_agc, kweight
from nr import stft_process, CM057
from run_nr import make_noise
from wx import rnn_vad, gated
NB = 129

def integrated_lufs(x):
    y = kweight(x); B = int(0.4 * FS); H = int(0.1 * FS)
    z = np.array([np.mean(y[i:i + B] ** 2) for i in range(0, len(y) - B, H)]) + 1e-20
    l = -0.691 + 10 * np.log10(z); z1 = z[l > -70]
    if len(z1) == 0: return -70.0
    rel = -0.691 + 10 * np.log10(z1.mean()) - 10; z2 = z[l > rel]
    return float(-0.691 + 10 * np.log10(z2.mean()))

def lufs_agc(x, gain_db=6, target=-20.0, ceil_db=-55.0, gmax=30.0, up=6.0, down=12.0, g_init=0.0):
    """10 ms frames. Speech loudness = K-weighted energy averaged over gate-open frames (tau ~0.4 s of speech).
    Gain target = target - loudness, capped so post-NR noise floor * gain <= ceil_db. Slew limited; gain
    frozen while the gate is closed (no pumping)."""
    g = Gate(); L = Limiter(); user = db2(gain_db); F = 480
    xk = kweight(x * user); e_s = None; gdb = g_init; y = np.empty(len(x) // F * F); G = np.empty_like(y)
    for f0 in range(0, len(y), F):
        fr = x[f0:f0 + F] * user
        for s in fr: g.step(s)
        if not g.gated:
            e = np.mean(xk[f0:f0 + F] ** 2) + 1e-20
            e_s = e if e_s is None else e_s + 0.025 * (e - e_s)
        want = gdb
        if e_s is not None and not g.gated:
            want = target - (-0.691 + 10 * math.log10(e_s))
            if g.known: want = min(want, ceil_db - 20 * math.log10(max(g.floor, 1e-9)))
            want = min(max(want, -12.0), gmax)
        st = (up if want > gdb else down) * F / FS
        gnew = gdb + max(-st, min(st, want - gdb))
        ramp = db2(np.linspace(gdb, gnew, F)); gdb = gnew
        for j in range(F):
            G[f0 + j] = ramp[j] * user; y[f0 + j] = L.step(fr[j] * ramp[j])
    return y, G

def job(a):
    fi, noise_db, kind = a
    files = sorted(glob.glob(f'{ROOT}/data/libri/*.flac'))
    c, _ = sf.read(files[fi]); c = resample_poly(c, 3, 1)[: 12 * FS]
    c = c / np.sqrt(np.mean(c[np.abs(c) > 1e-3] ** 2)) * db2(-38) / db2(6)
    rng = np.random.default_rng(fi * 7 + int(-noise_db))
    nz = make_noise(kind, len(c), rng, files[:6]); nz = nz / np.sqrt(np.mean(nz ** 2)) * db2(noise_db)
    x = c + nz
    k, _ = stft_process(x, CM057(NB, True), 256, 128, 'hann_analysis')
    r, vad = rnn_vad(x); r20 = 0.9 * r + 0.1 * x
    rg, _ = gated(x, r, k)
    chains = {
        '057_noNR+agc057': (x, cordmic_agc), '057strong+agc057': (k, cordmic_agc),
        '057strong+lufs': (k, lufs_agc), 'rnn_cap20+agc057': (r20, cordmic_agc),
        'rnn_cap20+lufs': (r20, lufs_agc), 'rnn_gated+lufs': (rg, lufs_agc)}
    rows = []
    cref = c * db2(6)
    for name, (inp, fn) in chains.items():
        y, G = fn(inp); n = len(y); cl = cref[:n]
        ce = frame_db(cl); act = ce > ce.max() - 30
        yf = frame_db(y); gdb = 20 * np.log10(G[: n // 960 * 960].reshape(-1, 960).mean(1) + 1e-12)
        # residual noise in long pauses (> 300 ms after speech) -> what the listener hears between words
        pause = np.zeros_like(act)
        for i in range(len(act)):
            if not act[max(0, i - 15): i + 1].any(): pause[i] = True
        rows.append(dict(method=name, clip=fi, noise_db=noise_db, kind=kind,
            lufs_int=integrated_lufs(y), lufs_err=abs(integrated_lufs(y) + 20.0),
            pause_noise_dbfs=float(np.mean(yf[pause])) if pause.any() else float('nan'),
            gain_db_speech=float(np.mean(gdb[act])), gain_std_speech=float(np.std(gdb[act][len(gdb[act]) // 3:])),
            pesq=float(pesq(16000, resample_poly(cl, 1, 3), resample_poly(y, 1, 3), 'wb')),
            estoi=float(stoi(resample_poly(cl, 1, 3), resample_poly(y, 1, 3), 16000, extended=True))))
    return rows

if __name__ == '__main__':
    jobs = [(i, nd, k) for i in range(8) for nd in (-62.0, -74.0) for k in ('pink', 'fan')]
    with Pool(8) as p: R = sum(p.map(job, jobs), [])
    json.dump(R, open(f'{ROOT}/results/agc2.json', 'w'), indent=1)
    import collections; d = collections.defaultdict(list)
    for r in R: d[(r['noise_db'], r['method'])].append(r)
    lines = ['| room noise (pre-gain) | chain | integrated LUFS (target -20) | pause noise dBFS | speech gain dB | gain std dB | PESQ-WB | ESTOI |', '|---|---|---|---|---|---|---|---|']
    for k in sorted(d):
        rs = d[k]; m = lambda q: np.nanmean([r[q] for r in rs])
        lines.append(f"| {k[0]:.0f} dBFS | {k[1]} | {m('lufs_int'):.1f} | {m('pause_noise_dbfs'):.1f} | {m('gain_db_speech'):.1f} | {m('gain_std_speech'):.2f} | {m('pesq'):.2f} | {m('estoi'):.3f} |")
    open(f'{ROOT}/results/agc2_table.md', 'w').write('\n'.join(lines)); print('\n'.join(lines))
