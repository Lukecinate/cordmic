"""E11c: AGC update gated by RNNoise VAD (speech-specific) in addition to the energy gate. Variant B2:
loudness EMA and gain slew only on frames with gate open AND vad >= VTH (vad smoothed: attack 1, release 0.8/frame
i.e. ~50 ms hangover). Everything else as chain B. Same clips/seeds as E11b."""
from paths import ROOT
import numpy as np, json, collections, math
from multiprocessing import Pool
from vol import *
import vol_trans2 as T2
from wx import rnn_vad
VTH = 0.5
def lufs_agc_v(x, vad, gain_db=6, target=-21.0, ceil_db=-50.0, gmax=30.0, up=6.0, down=12.0, g_init=15.0):
    g = Gate(); L = Limiter(); user = db2(gain_db); F = 480
    xk = kweight(x * user); e_s = None; gdb = g_init; y = np.empty(len(x) // F * F); G = np.empty_like(y); vs = 0.0
    for i, f0 in enumerate(range(0, len(y), F)):
        fr = x[f0:f0 + F] * user
        for s in fr: g.step(s)
        v = vad[min(i, len(vad) - 1)]; vs = v if v > vs else 0.8 * vs + 0.2 * v
        speech = (not g.gated) and vs >= VTH
        if speech:
            e = np.mean(xk[f0:f0 + F] ** 2) + 1e-20; e_s = e if e_s is None else e_s + 0.025 * (e - e_s)
        want = gdb
        if e_s is not None and speech:
            want = target - (-0.691 + 10 * math.log10(e_s))
            if g.known: want = min(want, ceil_db - 20 * math.log10(max(g.floor, 1e-9)))
            want = min(max(want, -12.0), gmax)
        st = (up if want > gdb else down) * F / FS; gnew = gdb + max(-st, min(st, want - gdb))
        ramp = db2(np.linspace(gdb, gnew, F)); gdb = gnew
        for j in range(F): G[f0 + j] = ramp[j] * user; y[f0 + j] = L.step(fr[j] * ramp[j])
    return y, G
def chains2(x):
    k, _ = stft_process(x, CM057(NB, True), 256, 128, 'hann_analysis'); r, v = rnn_vad(x)
    g = gated(x, r, k, thr_db=12, mmax=0.7)[0]
    return {"B_guard+lufs": lufs_agc(g, target=-21, ceil_db=-50, g_init=15), "B2_guard+lufs_vadgate": lufs_agc_v(g, v)}
T2.chains = chains2
def static(a):
    fi, room, dist = a; files = sorted(glob.glob(f'{ROOT}/data/libri/*.flac'))
    kind, ndb = ROOMS[room]; rng = np.random.default_rng(abs(hash(a)) % 2**32)
    c, _ = sf.read(files[fi]); c = resample_poly(c, 3, 1)[:12*FS]; c = c/np.sqrt(np.mean(c[np.abs(c) > 1e-3]**2))*db2(dist)/db2(6)
    nz = noise(kind, len(c), rng, files); nz = nz/np.sqrt(np.mean(nz**2))*db2(ndb); x = c + nz; cref = c*db2(6)
    out = []
    for name, (y, G) in chains2(x).items():
        cl = cref[:len(y)]
        out.append(dict(room=room, dist=dist, method=name, lufs=integrated_lufs(y[-6*FS:]),
            pesq=float(pesq(16000, resample_poly(cl, 1, 3), resample_poly(y, 1, 3), 'wb'))))
    return out
if __name__ == '__main__':
    J = [(i, r, d) for i in range(8) for r in ROOMS for d in (-24.0, -38.0)]
    with Pool(8) as p: TR = sum(p.map(T2.tj, J), []); SR = sum(p.map(static, J), [])
    json.dump(dict(transition=TR, static=SR), open(f'{ROOT}/results/vol_fix.json', 'w'), indent=1)
    L = ['| room | speech | chain | breathing dB | pump std dB | loudest 50 ms in pause | gain change in pause dB (max) | re-onset err dB | speech-2 LUFS | static LUFS | static PESQ |', '|---|---|---|---|---|---|---|---|---|---|---|']
    d = collections.defaultdict(list); s = collections.defaultdict(list)
    for r in TR: d[(r['room'], r['dist'], r['method'])].append(r)
    for r in SR: s[(r['room'], r['dist'], r['method'])].append(r)
    for k in d:
        rs = d[k]; m = lambda q: np.nanmean([r[q] for r in rs]); mx = lambda q: np.nanmax([r[q] for r in rs]); ss = s[k]
        L.append(f"| {k[0]} | {k[1]:.0f} | {k[2]} | {m('breathing_db'):+.1f} | {m('pump_std_db'):.2f} | {m('pause_max50'):.1f} (max {mx('pause_max50'):.1f}) | {m('gain_change_db'):+.1f} ({mx('gain_change_db'):+.1f}) | {m('reonset_err_db'):+.1f} | {m('speech2_lufs'):.1f} | {np.mean([r['lufs'] for r in ss]):.1f} | {np.mean([r['pesq'] for r in ss]):.2f} |")
    open(f'{ROOT}/results/vol_fix.md', 'w').write('\n'.join(L)); print('\n'.join(L))
