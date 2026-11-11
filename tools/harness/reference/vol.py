"""E11: auto-volume across rooms + speech->silence->speech transition (pumping/breathing).
Chains: A = 0.5.7 Strong NR + 0.5.7 AGC (phone today); B = WhisperGuard balanced + LUFS AGC (target -21, ceiling -50,
start 15 dB). Speech: LibriSpeech (8 clips) at near -24 / far -38 dBFS after +6 dB user gain.
Rooms (noise RMS pre-gain): quiet (pink -80), fan (synthetic fan -62), babble (DEMAND SCAFE -60), keyboard (synthetic
-62), street (DEMAND STRAFFIC -58), very noisy (DEMAND TBUS -48, PSTATION -48)."""
from paths import ROOT
import numpy as np, json, collections
from multiprocessing import Pool
from agc2 import *
from heldout import demand
ROOMS = {"quiet": ("pink", -80), "fan": ("fan", -62), "babble_cafe": ("SCAFE", -60), "keyboard": ("keyboard", -62),
         "street": ("STRAFFIC", -58), "very_noisy_bus": ("TBUS", -48), "very_noisy_station": ("PSTATION", -48)}
def noise(kind, n, rng, files):
    if kind.isupper(): return demand(kind, n, rng)
    return make_noise(kind, n, rng, files[:6])
def chains(x):
    k, _ = stft_process(x, CM057(NB, True), 256, 128, 'hann_analysis'); r, _ = rnn_vad(x)
    g = gated(x, r, k, thr_db=12, mmax=0.7)[0]
    return {"A_057strong+agc057": cordmic_agc(k), "B_guard+lufs": lufs_agc(g, target=-21, ceil_db=-50, g_init=15)}
def job(a):
    fi, room, dist = a; files = sorted(glob.glob(f'{ROOT}/data/libri/*.flac'))
    kind, ndb = ROOMS[room]; rng = np.random.default_rng(abs(hash(a)) % 2**32)
    c, _ = sf.read(files[fi]); c = resample_poly(c, 3, 1)[:12*FS]; c = c/np.sqrt(np.mean(c[np.abs(c) > 1e-3]**2))*db2(dist)/db2(6)
    if room == "transition":
        pass
    nz = noise(kind, len(c), rng, files); nz = nz/np.sqrt(np.mean(nz**2))*db2(ndb); x = c + nz
    rows = []; cref = c*db2(6)
    for name, (y, G) in chains(x).items():
        n = len(y); cl = cref[:n]; ce = frame_db(cl); act = ce > ce.max() - 30; yf = frame_db(y)
        pause = np.array([not act[max(0, i-15): i+1].any() for i in range(len(act))])
        rows.append(dict(room=room, dist=dist, method=name, lufs=integrated_lufs(y[-6*FS:]),
            pause_noise=float(np.mean(yf[pause])) if pause.any() else float('nan'),
            peak=float(20*np.log10(np.abs(y).max()+1e-9)),
            pesq=float(pesq(16000, resample_poly(cl, 1, 3), resample_poly(y, 1, 3), 'wb')),
            estoi=float(stoi(resample_poly(cl, 1, 3), resample_poly(y, 1, 3), 16000, extended=True))))
    return rows
def trans_job(a):
    """4 s speech, 6 s noise only, 4 s speech. Breathing = rise of residual noise during the pause
    (last 1 s vs first 1 s after hangover); pumping = noise-level modulation (std of 50 ms levels) in the pause;
    re-onset = level of first 300 ms of speech 2 vs its steady level."""
    fi, room, dist = a; files = sorted(glob.glob(f'{ROOT}/data/libri/*.flac')); kind, ndb = ROOMS[room]
    rng = np.random.default_rng(abs(hash(a)) % 2**32)
    c, _ = sf.read(files[fi]); c = resample_poly(c, 3, 1); c = c/np.sqrt(np.mean(c[np.abs(c) > 1e-3]**2))*db2(dist)/db2(6)
    s1, s2 = c[:4*FS], c[4*FS:8*FS]; cc = np.concatenate([s1, np.zeros(6*FS), s2])
    nz = noise(kind, len(cc), rng, files); nz = nz/np.sqrt(np.mean(nz**2))*db2(ndb); x = cc + nz
    rows = []
    for name, (y, G) in chains(x).items():
        lv = lambda a0, a1: 10*np.log10(np.mean(y[int(a0*FS):int(a1*FS)]**2)+1e-20)
        seg = y[int(5.0*FS):int(10*FS)]; l50 = 10*np.log10((seg[:len(seg)//2400*2400].reshape(-1, 2400)**2).mean(1)+1e-20)
        gd = 20*np.log10(G+1e-12)
        on = 10*np.log10(np.mean(y[10*FS:int(10.3*FS)]**2)+1e-20) - 10*np.log10(np.mean(y[int(11*FS):14*FS]**2)+1e-20)
        on_c = 10*np.log10(np.mean(cc[10*FS:int(10.3*FS)]**2)+1e-20) - 10*np.log10(np.mean(cc[int(11*FS):14*FS]**2)+1e-20)
        rows.append(dict(room=room, dist=dist, method=name, breathing_db=float(lv(9, 10) - lv(5, 6)), pump_std_db=float(np.std(l50)),
            gain_change_in_pause_db=float(gd[int(9.95*FS)] - gd[int(4.5*FS)]), reonset_err_db=float(on - on_c), pause_level=float(lv(5, 10))))
    return rows
if __name__ == '__main__':
    J = [(i, r, d) for i in range(8) for r in ROOMS for d in (-24.0, -38.0)]
    T = [(i, r, d) for i in range(4) for r in ROOMS for d in (-24.0, -38.0)]
    with Pool(8) as p: R = sum(p.map(job, J), []); TR = sum(p.map(trans_job, T), [])
    json.dump(dict(rooms=R, transition=TR), open(f'{ROOT}/results/vol.json', 'w'), indent=1)
    d = collections.defaultdict(list)
    for r in R: d[(r['room'], r['dist'], r['method'])].append(r)
    L = ['| room | speech | chain | LUFS (B target -21) | pause noise dBFS | peak dBFS | PESQ | ESTOI |', '|---|---|---|---|---|---|---|---|']
    for k in d:
        rs = d[k]; m = lambda q: np.nanmean([r[q] for r in rs])
        L.append(f"| {k[0]} | {k[1]:.0f} | {k[2]} | {m('lufs'):.1f} | {m('pause_noise'):.1f} | {m('peak'):.1f} | {m('pesq'):.2f} | {m('estoi'):.3f} |")
    d = collections.defaultdict(list)
    for r in TR: d[(r['room'], r['dist'], r['method'])].append(r)
    L += ['', '| room | speech | chain | breathing dB (pause end - start) | pump std dB | gain change in pause dB | re-onset error dB | pause level dBFS |', '|---|---|---|---|---|---|---|---|']
    for k in d:
        rs = d[k]; m = lambda q: np.nanmean([r[q] for r in rs])
        L.append(f"| {k[0]} | {k[1]:.0f} | {k[2]} | {m('breathing_db'):+.1f} | {m('pump_std_db'):.2f} | {m('gain_change_in_pause_db'):+.1f} | {m('reonset_err_db'):+.1f} | {m('pause_level'):.1f} |")
    open(f'{ROOT}/results/vol_tables.md', 'w').write('\n'.join(L)); print('\n'.join(L))
