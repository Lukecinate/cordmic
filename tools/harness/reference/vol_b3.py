"""E11e: B3 = B2 (VAD-gated update) + minimum-statistics floor for the ceiling and the speech gate.
Floor = min of 50 ms block levels over the last 2.0 s (Martin-style minimum tracking, no bias correction),
smoothed with 0.5 s release. Speech frame = level > floor + 8 dB AND smoothed VAD >= 0.5.
Ceiling: gain <= ceil_db - floor. This replaces the 0.5.7 creeping floor, which follows a continuous whisper up."""
from paths import ROOT
import numpy as np, json, collections, math
from multiprocessing import Pool
from vol import *
import vol_trans2 as T2, vol_fix as VF
from wx import rnn_vad, norm_active
from heldout import SPK
def lufs_agc_ms(x, vad, gain_db=6, target=-21.0, ceil_db=-50.0, gmax=30.0, up=6.0, down=12.0, g_init=15.0, win_s=2.0, open_db=8.0, vth=0.5):
    L = Limiter(); user = db2(gain_db); F = 480; xu = x * user
    xk = kweight(xu); e_s = None; gdb = g_init; n = len(x) // F; y = np.empty(n * F); G = np.empty_like(y); vs = 0.0
    hist = collections.deque(maxlen=int(win_s * 100)); fl = None
    for i in range(n):
        f0 = i * F; fr = xu[f0:f0 + F]
        lvl = 10 * math.log10(np.mean(fr ** 2) + 1e-20); hist.append(lvl)
        if i % 5 == 4:
            b = np.array(hist); b = b[:len(b) // 5 * 5].reshape(-1, 5).mean(1) if len(b) >= 5 else b
            m = float(b.min()); fl = m if fl is None or m < fl else fl + 0.1 * (m - fl)
        v = vad[min(i, len(vad) - 1)]; vs = v if v > vs else 0.8 * vs + 0.2 * v
        speech = fl is not None and lvl > fl + open_db and vs >= vth
        if speech:
            e = np.mean(xk[f0:f0 + F] ** 2) + 1e-20; e_s = e if e_s is None else e_s + 0.025 * (e - e_s)
        want = gdb
        if e_s is not None and speech:
            want = target - (-0.691 + 10 * math.log10(e_s)); want = min(want, ceil_db - max(fl, -100.0)); want = min(max(want, -12.0), gmax)
        st = (up if want > gdb else down) * F / FS; gnew = gdb + max(-st, min(st, want - gdb))
        ramp = db2(np.linspace(gdb, gnew, F)); gdb = gnew
        for j in range(F): G[f0 + j] = ramp[j] * user; y[f0 + j] = L.step(fr[j] * ramp[j])
    return y, G
def chains3(x):
    k, _ = stft_process(x, CM057(NB, True), 256, 128, 'hann_analysis'); r, v = rnn_vad(x)
    g = gated(x, r, k, thr_db=12, mmax=0.7)[0]
    return {"B2_vadgate": VF.lufs_agc_v(g, v), "B3_vadgate+minstat": lufs_agc_ms(g, v)}
T2.chains = chains3; VF.chains2 = chains3
def wj(a):
    spk, utt, room, lvl = a; kind, ndb = ROOMS[room]; rng = np.random.default_rng(abs(hash(a)) % 2**32)
    c, _ = sf.read(f"{ROOT}/ext/ears/{spk}/{utt}_whisper.wav"); c = norm_active(c[:12*FS], lvl - 6)
    files = sorted(glob.glob(f'{ROOT}/data/libri/*.flac'))
    nz = noise(kind, len(c), rng, files); nz = nz/np.sqrt(np.mean(nz**2))*db2(ndb); x = c + nz
    return [dict(spk=spk, room=room, lvl=lvl, method=nm, lufs=integrated_lufs(y), lufs_last6=integrated_lufs(y[-6*FS:]), gend=float(20*np.log10(G[-1]))) for nm, (y, G) in chains3(x).items()]
def static(a):
    return VF.static(a)
if __name__ == '__main__':
    J = [(i, r, d) for i in range(8) for r in ROOMS for d in (-24.0, -38.0)]
    W = [(s, u, r, l) for s in SPK if s != "p014" for u in ["rainbow_03", "rainbow_05"] for r in ["quiet", "fan", "babble_cafe"] for l in (-30.0, -38.0)]
    with Pool(8) as p: TR = sum(p.map(T2.tj, J), []); SR = sum(p.map(static, J), []); WR = sum(p.map(wj, W), [])
    json.dump(dict(transition=TR, static=SR, whisper=WR), open(f'{ROOT}/results/vol_b3.json', 'w'), indent=1)
    out = ['| room | speech | chain | breathing dB | pump std dB | loudest 50 ms in pause (max) | gain change in pause dB (max) | re-onset err dB | speech-2 LUFS | static LUFS | static PESQ |', '|---|---|---|---|---|---|---|---|---|---|---|']
    d = collections.defaultdict(list); s = collections.defaultdict(list)
    for r in TR: d[(r['room'], r['dist'], r['method'])].append(r)
    for r in SR: s[(r['room'], r['dist'], r['method'])].append(r)
    for k in d:
        rs = d[k]; m = lambda q: np.nanmean([r[q] for r in rs]); mx = lambda q: np.nanmax([r[q] for r in rs]); ss = s[k]
        out.append(f"| {k[0]} | {k[1]:.0f} | {k[2]} | {m('breathing_db'):+.1f} | {m('pump_std_db'):.2f} | {m('pause_max50'):.1f} ({mx('pause_max50'):.1f}) | {m('gain_change_db'):+.1f} ({mx('gain_change_db'):+.1f}) | {m('reonset_err_db'):+.1f} | {m('speech2_lufs'):.1f} | {np.mean([r['lufs'] for r in ss]):.1f} | {np.mean([r['pesq'] for r in ss]):.2f} |")
    out += ['', '## Whisper (EARS held-out, 14 clips per row), integrated LUFS of the whole output', '', '| room | whisper level dBFS | chain | LUFS mean | min | max | end gain dB |', '|---|---|---|---|---|---|---|']
    for room in ["quiet", "fan", "babble_cafe"]:
        for l in (-30.0, -38.0):
            for nm in ["B2_vadgate", "B3_vadgate+minstat"]:
                rs = [r for r in WR if r['room'] == room and r['lvl'] == l and r['method'] == nm]; q = [r['lufs'] for r in rs]
                out.append(f"| {room} | {l:.0f} | {nm} | {np.mean(q):.1f} | {min(q):.1f} | {max(q):.1f} | {np.mean([r['gend'] for r in rs]):.1f} |")
    open(f'{ROOT}/results/vol_b3.md', 'w').write('\n'.join(out)); print('\n'.join(out))
