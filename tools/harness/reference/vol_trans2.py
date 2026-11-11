"""E11b: transition metrics with an audibility floor. Levels below -70 dBFS (inaudible after a -21 LUFS target)
are clamped so ratios between digital-silence levels do not count as breathing; the pause window ends 200 ms
before speech 2 to exclude onset leakage. Also reports the loudest 50 ms in the pause."""
from paths import ROOT
import numpy as np, json, collections
from multiprocessing import Pool
from vol import *
FLOOR = -70.0
def tj(a):
    fi, room, dist = a; files = sorted(glob.glob(f'{ROOT}/data/libri/*.flac')); kind, ndb = ROOMS[room]
    rng = np.random.default_rng(abs(hash(a)) % 2**32)
    c, _ = sf.read(files[fi]); c = resample_poly(c, 3, 1); c = c/np.sqrt(np.mean(c[np.abs(c) > 1e-3]**2))*db2(dist)/db2(6)
    cc = np.concatenate([c[:4*FS], np.zeros(6*FS), c[4*FS:8*FS]])
    nz = noise(kind, len(cc), rng, files); nz = nz/np.sqrt(np.mean(nz**2))*db2(ndb); x = cc + nz
    rows = []
    for name, (y, G) in chains(x).items():
        lv = lambda a0, a1: max(FLOOR, 10*np.log10(np.mean(y[int(a0*FS):int(a1*FS)]**2)+1e-20))
        seg = y[int(4.5*FS):int(9.8*FS)]; l50 = np.maximum(FLOOR, 10*np.log10((seg[:len(seg)//2400*2400].reshape(-1, 2400)**2).mean(1)+1e-20))
        gd = 20*np.log10(G+1e-12)
        on = 10*np.log10(np.mean(y[10*FS:int(10.3*FS)]**2)+1e-20) - 10*np.log10(np.mean(y[int(11*FS):14*FS]**2)+1e-20)
        on_c = 10*np.log10(np.mean(cc[10*FS:int(10.3*FS)]**2)+1e-20) - 10*np.log10(np.mean(cc[int(11*FS):14*FS]**2)+1e-20)
        rows.append(dict(room=room, dist=dist, method=name, breathing_db=lv(8.8, 9.8) - lv(4.8, 5.8), pump_std_db=float(np.std(l50)),
            pause_max50=float(l50.max()), gain_change_db=float(gd[int(9.8*FS)] - gd[int(4.5*FS)]), reonset_err_db=float(on - on_c),
            speech2_lufs=integrated_lufs(y[int(10*FS):14*FS])))
    return rows
if __name__ == '__main__':
    T = [(i, r, d) for i in range(8) for r in ROOMS for d in (-24.0, -38.0)]
    with Pool(8) as p: TR = sum(p.map(tj, T), [])
    json.dump(TR, open(f'{ROOT}/results/vol_trans2.json', 'w'), indent=1)
    d = collections.defaultdict(list)
    for r in TR: d[(r['room'], r['dist'], r['method'])].append(r)
    L = [f'Levels clamped at {FLOOR} dBFS. n=8 clips per row.', '',
         '| room | speech | chain | breathing dB | pump std dB | loudest 50 ms in pause dBFS | gain change in pause dB | re-onset error dB | speech-2 LUFS |', '|---|---|---|---|---|---|---|---|---|']
    for k in d:
        rs = d[k]; m = lambda q: np.nanmean([r[q] for r in rs]); mx = lambda q: np.nanmax([r[q] for r in rs])
        L.append(f"| {k[0]} | {k[1]:.0f} | {k[2]} | {m('breathing_db'):+.1f} | {m('pump_std_db'):.2f} | {m('pause_max50'):.1f} (max {mx('pause_max50'):.1f}) | {m('gain_change_db'):+.1f} (max {mx('gain_change_db'):+.1f}) | {m('reonset_err_db'):+.1f} | {m('speech2_lufs'):.1f} |")
    open(f'{ROOT}/results/vol_trans2.md', 'w').write('\n'.join(L)); print('\n'.join(L))
