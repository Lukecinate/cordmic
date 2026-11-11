"""CordMic 0.6.0 release gates on the SHIPPING C# code (spec §3, §4, §6).

Runs the C# Studio chain (CordMic.Harness `studio` host) on the held-out NR set (E10) and on the AGC rooms (E11),
next to the Python reference of the 0.5.7 Strong suppressor, and prints the NR-1..8 and AGC-1..6 tables.
Usage (from tools/harness):  PYTHONHASHSEED=0 python cs_gates.py [--host PATH] [--quick]
Data: ./fetch_data.sh first (cache is git-ignored; EARS is CC-BY-NC, local evaluation only; never commit it)."""
import os, sys, json, subprocess, tempfile, argparse, collections
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "reference"))
os.chdir(os.path.join(os.path.dirname(os.path.abspath(__file__)), "reference"))  # agc2.py execs agc.py by relative path
if os.environ.get("PYTHONHASHSEED") != "0":
    sys.exit("set PYTHONHASHSEED=0 (job seeds use hash() of tuples)")
import numpy as np, soundfile as sf
from multiprocessing import Pool
from paths import ROOT
import heldout as H
from wx import active_mask, proj_loss, MID, HF, norm_active, rnn_vad, gated
from nr import stft_process, CM057
from run_nr import NB
from metrics import evaluate
import vol as V, vol_trans2 as T2
from vol_b3 import lufs_agc_ms
from agc2 import integrated_lufs, db2

HOST = None
FS = 48000

def cs_studio(x, preset, autovol=False, start_gain=15.0):
    with tempfile.TemporaryDirectory() as d:
        np.asarray(x, dtype="<f4").tofile(f"{d}/in.f32")
        cmd = [HOST, "studio", "--preset", preset, "--in", f"{d}/in.f32", "--out", f"{d}/out.f32", "--gain", f"{d}/g.f32"]
        if autovol: cmd += ["--autovol", "--start-gain", str(start_gain)]
        subprocess.run(cmd, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        y = np.fromfile(f"{d}/out.f32", dtype="<f4").astype(float)
        g = np.fromfile(f"{d}/g.f32", dtype="<f4").astype(float)
    return y, g

def nr_job(a):
    spk, utt, style, env, snr = a
    c, sr = sf.read(f"{ROOT}/ext/ears/{spk}/{utt}_{style}.wav"); c = c[:12*FS]; c = norm_active(c, -30.0 if style == "whisper" else -26.0)
    rng = np.random.default_rng(abs(hash(a)) % 2**32); nz = H.demand(env, len(c), rng)
    m = active_mask(c); f = c[:len(m)*480].reshape(-1, 480); sl = 10*np.log10((f[m]**2).mean())
    nz *= 10**((sl - snr - 10*np.log10((nz**2).mean()))/20); x = c + nz
    k, _ = stft_process(x, CM057(NB, True), 256, 128, "hann_analysis")
    outs = {"noisy": x, "cm057_strong": k}
    for p in ("balanced", "whisper", "max"):
        outs["cs_" + p] = cs_studio(x, p)[0]
    if REF:
        r, _ = rnn_vad(x); outs["ref_balanced"] = gated(x, r, k, thr_db=12, mmax=0.7)[0]
    rows = []
    for name, y in outs.items():
        rr = evaluate(c, x, y, dns=True)
        rr.update(method=name, spk=spk, utt=utt, style=style, snr=snr, noise=env, loss_mid=proj_loss(c, y, m, MID), loss_hf=proj_loss(c, y, m, HF))
        rows.append({q: (float(v) if isinstance(v, (np.floating, float)) else v) for q, v in rr.items()})
    return rows

def agc_static(a):
    fi, room, dist = a; files = sorted(__import__("glob").glob(f"{ROOT}/data/libri/*.flac"))
    kind, ndb = V.ROOMS[room]; rng = np.random.default_rng(abs(hash(a)) % 2**32)
    from scipy.signal import resample_poly
    from pesq import pesq
    c, _ = sf.read(files[fi]); c = resample_poly(c, 3, 1)[:12*FS]; c = c/np.sqrt(np.mean(c[np.abs(c) > 1e-3]**2))*db2(dist)/db2(6)
    nz = V.noise(kind, len(c), rng, files); nz = nz/np.sqrt(np.mean(nz**2))*db2(ndb); x = c + nz; cref = c*db2(6)
    y, g = cs_studio(x * db2(6), "balanced", autovol=True)
    cl = cref[:len(y)]
    return dict(room=room, dist=dist, lufs=integrated_lufs(y[-6*FS:]), peak=float(20*np.log10(np.abs(y).max()+1e-12)),
                pesq=float(pesq(16000, resample_poly(cl, 1, 3), resample_poly(y, 1, 3), "wb")))

def agc_trans(a):
    fi, room, dist = a; files = sorted(__import__("glob").glob(f"{ROOT}/data/libri/*.flac")); kind, ndb = V.ROOMS[room]
    from scipy.signal import resample_poly
    rng = np.random.default_rng(abs(hash(a)) % 2**32)
    c, _ = sf.read(files[fi]); c = resample_poly(c, 3, 1); c = c/np.sqrt(np.mean(c[np.abs(c) > 1e-3]**2))*db2(dist)/db2(6)
    cc = np.concatenate([c[:4*FS], np.zeros(6*FS), c[4*FS:8*FS]])
    nz = V.noise(kind, len(cc), rng, files); nz = nz/np.sqrt(np.mean(nz**2))*db2(ndb); x = cc + nz
    y, gf = cs_studio(x * db2(6), "balanced", autovol=True)
    # per-frame gain was recorded at processing time; the output is shifted back by the chain delay (2 frames)
    gf = np.concatenate([gf[2:], np.repeat(gf[-1:], 2)]); gd = np.repeat(gf, 480)[:len(y)]
    FLOOR = T2.FLOOR
    lv = lambda a0, a1: max(FLOOR, 10*np.log10(np.mean(y[int(a0*FS):int(a1*FS)]**2)+1e-20))
    seg = y[int(4.5*FS):int(9.8*FS)]; l50 = np.maximum(FLOOR, 10*np.log10((seg[:len(seg)//2400*2400].reshape(-1, 2400)**2).mean(1)+1e-20))
    on = 10*np.log10(np.mean(y[10*FS:int(10.3*FS)]**2)+1e-20) - 10*np.log10(np.mean(y[int(11*FS):14*FS]**2)+1e-20)
    on_c = 10*np.log10(np.mean(cc[10*FS:int(10.3*FS)]**2)+1e-20) - 10*np.log10(np.mean(cc[int(11*FS):14*FS]**2)+1e-20)
    return dict(room=room, dist=dist, breathing_db=lv(8.8, 9.8) - lv(4.8, 5.8), pump_std_db=float(np.std(l50)), pause_max50=float(l50.max()),
                gain_change_db=float(abs(gd[int(9.8*FS)] - gd[int(4.5*FS)])), reonset_err_db=float(on - on_c), peak=float(20*np.log10(np.abs(y).max()+1e-12)))

def init(host, ref):
    global HOST, REF
    HOST = host; REF = ref

def gate(rows, ok, name, value, crit):
    rows.append((name, value, crit, "PASS" if ok else "FAIL"))

if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default=os.environ.get("CORDMIC_STUDIO_HOST", "/tmp/cmhost/CordMic.Harness"))
    ap.add_argument("--quick", action="store_true", help="2 talkers, 2 noises (smoke run, not a gate)")
    ap.add_argument("--ref", action="store_true", help="also run the Python reference WhisperGuard (port check)")
    ap.add_argument("--skip-nr", action="store_true"); ap.add_argument("--skip-agc", action="store_true")
    a = ap.parse_args(); REF = a.ref
    avail = [s for s in H.SPK if os.path.isdir(f"{ROOT}/ext/ears/{s}")]
    if len(avail) < len(H.SPK): print("missing EARS talkers (skipped):", sorted(set(H.SPK) - set(avail)), flush=True)
    spk = avail[:2] if a.quick else avail; noises = H.NOISES[:2] if a.quick else H.NOISES
    out = os.path.join(ROOT, "results"); res = []
    with Pool(8, initializer=init, initargs=(a.host, a.ref)) as p:
        if not a.skip_nr:
            jobs = [(s, u, st, e, snr) for s in spk for u in ["rainbow_03", "rainbow_05"] for st in ["whisper", "regular"] for e in noises
                    for snr in ([10, 20] if st == "whisper" else [5, 20])]
            R = sum(p.map(nr_job, jobs), [])
            json.dump(R, open(f"{out}/cs_nr.json", "w"))
            df = collections.defaultdict(list)
            for r in R: df[(r["method"], r["style"], r["snr"])].append(r)
            M = lambda m, st, snr, q: float(np.mean([r[q] for r in df[(m, st, snr)]]))
            gate(res, M("cs_balanced", "whisper", 20, "loss_mid") <= 3.5, "NR-1 whisper 20 dB Balanced mid loss", f"{M('cs_balanced','whisper',20,'loss_mid'):.2f} dB", "<= 3.5")
            e_b, e_n = M("cs_balanced", "whisper", 20, "stoi"), M("noisy", "whisper", 20, "stoi")
            gate(res, e_b >= 0.93 * e_n, "NR-2 whisper 20 dB Balanced ESTOI", f"{e_b:.3f} / {e_n:.3f} = {e_b/e_n:.3f}x", ">= 0.93x")
            gate(res, M("cs_whisper", "whisper", 20, "loss_mid") <= 1.5, "NR-3 whisper 20 dB Whisper mid loss", f"{M('cs_whisper','whisper',20,'loss_mid'):.2f} dB", "<= 1.5")
            gate(res, M("cs_whisper", "whisper", 10, "loss_mid") <= 2.0, "NR-4 whisper 10 dB Whisper mid loss", f"{M('cs_whisper','whisper',10,'loss_mid'):.2f} dB", "<= 2.0")
            d5 = M("cs_balanced", "regular", 5, "dns_ovr") - M("cm057_strong", "regular", 5, "dns_ovr")
            gate(res, d5 >= 0.3, "NR-5 regular 5 dB OVRL vs 0.5.7 Strong", f"{d5:+.2f}", ">= +0.30")
            d20 = M("cs_balanced", "regular", 20, "dns_ovr") - M("cm057_strong", "regular", 20, "dns_ovr")
            pq, pn = M("cs_balanced", "regular", 20, "pesq"), M("noisy", "regular", 20, "pesq")
            gate(res, d20 >= 0.05 and pq >= pn, "NR-6 regular 20 dB OVRL vs Strong; PESQ vs noisy", f"{d20:+.2f}; {pq:.2f} vs {pn:.2f}", ">= +0.05 and >=")
            d7 = float(np.mean([M("cs_balanced", "regular", s, "dns_ovr") - M("cs_max", "regular", s, "dns_ovr") for s in (5, 20)]))
            gate(res, d7 >= -0.05, "NR-7 regular Balanced vs Max OVRL", f"{d7:+.2f}", ">= -0.05")
            for st in ("whisper", "regular"):
                pairs = collections.defaultdict(dict)
                for r in R:
                    if r["style"] == st and r["method"] in ("cs_balanced", "cm057_strong"): pairs[(r["spk"], r["utt"], r["noise"], r["snr"])][r["method"]] = r["dns_ovr"]
                w = np.mean([v["cs_balanced"] > v["cm057_strong"] for v in pairs.values()])
                gate(res, w >= 0.8, f"NR-8 {st} OVRL win rate vs Strong", f"{100*w:.0f} %", ">= 80 %")
            if REF:
                for st, snr in (("whisper", 20), ("regular", 5)):
                    dl = M("cs_balanced", st, snr, "loss_mid") - M("ref_balanced", st, snr, "loss_mid")
                    do = M("cs_balanced", st, snr, "dns_ovr") - M("ref_balanced", st, snr, "dns_ovr")
                    gate(res, abs(do) <= 0.05 and abs(dl) <= 0.3, f"PORT C# vs Python Balanced ({st} {snr} dB)", f"mid loss {dl:+.2f} dB, OVRL {do:+.3f}", "|OVRL| <= 0.05, |loss| <= 0.3")
        if not a.skip_agc:
            J = [(i, r, d) for i in range(8) for r in V.ROOMS for d in (-24.0, -38.0)]
            S = p.map(agc_static, J); T = p.map(agc_trans, J)
            json.dump(dict(static=S, transition=T), open(f"{out}/cs_agc.json", "w"))
            cell = collections.defaultdict(list); tcell = collections.defaultdict(list)
            for r in S: cell[(r["room"], r["dist"])].append(r)
            for r in T: tcell[(r["room"], r["dist"])].append(r)
            l = [float(np.mean([r["lufs"] for r in v])) for v in cell.values()]
            gate(res, all(-22.5 <= q <= -19.5 for q in l), "AGC-1 static loudness, 14 cells", f"{min(l):.1f} ... {max(l):.1f} LUFS", "-21 +- 1.5")
            gm = [float(np.mean([r["gain_change_db"] for r in v])) for v in tcell.values()]; gx = [max(r["gain_change_db"] for r in v) for v in tcell.values()]
            gate(res, max(gm) <= 1 and max(gx) <= 4, "AGC-2 gain change in a 6 s pause", f"mean <= {max(gm):.2f}, max {max(gx):.2f} dB", "<= 1 / <= 4")
            ro = [float(np.mean([r["reonset_err_db"] for r in v])) for v in tcell.values()]
            gate(res, all(-2 <= q <= 2 for q in ro), "AGC-3 re-onset error", f"{min(ro):+.1f} ... {max(ro):+.1f} dB", "within +-2")
            br = [float(np.mean([r["breathing_db"] for r in tcell[(rm, d)]])) for rm in ("quiet", "fan", "street", "very_noisy_station") for d in (-24.0, -38.0)]
            gate(res, max(br) <= 6, "AGC-4 breathing (steady rooms)", f"<= {max(br):+.1f} dB", "<= +6")
            pk = max([r["peak"] for r in S] + [r["peak"] for r in T])
            gate(res, pk <= -1.0 + 0.05, "AGC-5 peaks", f"{pk:.2f} dBFS", "<= -1.0")
            pq = {k: float(np.mean([r["pesq"] for r in v])) for k, v in cell.items()}
            ref = os.path.join(ROOT, "results", "vol_fix.json")
            if os.path.exists(ref):
                RB = json.load(open(ref))["static"]; rb = collections.defaultdict(list)
                for r in RB:
                    if r["method"] == "B_guard+lufs": rb[(r["room"], r["dist"])].append(r["pesq"])
                dd = [pq[k] - float(np.mean(rb[k])) for k in pq if k in rb]
                gate(res, bool(dd) and min(dd) >= -0.05, "AGC-6 PESQ vs second-pass B", f"worst {min(dd):+.2f}" if dd else "no reference", ">= -0.05")
            else:
                res.append(("AGC-6 PESQ vs second-pass B", "reference results/vol_fix.json missing (run reference/vol_fix.py)", ">= -0.05", "NOT RUN"))
            res.append(("AGC info: PESQ per cell", ", ".join(f"{k[0]}{int(k[1])}:{v:.2f}" for k, v in sorted(pq.items())), "", "INFO"))
    lines = ["| gate | measured | criterion | result |", "|---|---|---|---|"] + [f"| {a_} | {b} | {c} | {d} |" for a_, b, c, d in res]
    open(f"{out}/cs_gates.md", "w").write("\n".join(lines) + "\n"); print("\n".join(lines))
