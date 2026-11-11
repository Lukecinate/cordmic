"""E10: held-out validation of WhisperGuard thresholds. Talkers p008-p014,p016 (EARS; tuning used p001-p007),
utterances rainbow_03/05 (tuning used 02/04), REAL noise from DEMAND (CC-BY-4.0; tuning used synthetic
pink/fan/babble/keyboard). Parameters frozen before this run (DELTA_REPORT.md)."""
import numpy as np, soundfile as sf, json
from multiprocessing import Pool
from wx import *
SPK = ["p008", "p009", "p010", "p016", "p011", "p012", "p013", "p014"]
NOISES = ["STRAFFIC", "SCAFE", "OOFFICE", "DKITCHEN", "TBUS", "PSTATION"]
def demand(env, n, rng):
    d, _ = sf.read(f"{ROOT}/ext/demand/{env}/ch01.wav"); s = rng.integers(0, len(d) - n); return d[s:s + n].astype(float)
def job(a):
    spk, utt, style, env, snr = a
    c, sr = sf.read(f"{ROOT}/ext/ears/{spk}/{utt}_{style}.wav"); c = c[:12*FS]; c = norm_active(c, -30.0 if style == "whisper" else -26.0)
    rng = np.random.default_rng(abs(hash(a)) % 2**32); nz = demand(env, len(c), rng)
    m = active_mask(c); f = c[:len(m)*F].reshape(-1, F); sl = 10*np.log10((f[m]**2).mean())
    nz *= 10**((sl - snr - 10*np.log10((nz**2).mean()))/20); x = c + nz
    r, vad = rnn_vad(x); k, _ = stft_process(x, CM057(NB, True), 256, 128, "hann_analysis")
    outs = {"noisy": x, "cm057_strong": k, "rnn_cap20": 0.9*r + 0.1*x,
            "guard_balanced": gated(x, r, k, thr_db=12, mmax=0.7)[0], "guard_whisper": gated(x, r, k)[0]}
    rows = []
    for name, y in outs.items():
        n = len(m)*F; pr = 10*np.log10((x[:n].reshape(-1, F)[~m]**2).sum()/((y[:n].reshape(-1, F)[~m]**2).sum()+1e-20))
        rr = evaluate(c, x, y, dns=True); rr.update(method=name, spk=spk, style=style, snr=snr, noise=env,
            loss_mid=proj_loss(c, y, m, MID), loss_hf=proj_loss(c, y, m, HF), pause_nr=float(pr)); rows.append(rr)
    return rows
if __name__ == "__main__":
    jobs = [(s, u, st, e, snr) for s in SPK for u in ["rainbow_03", "rainbow_05"] for st in ["whisper", "regular"] for e in NOISES
            for snr in ([10, 20] if st == "whisper" else [5, 20])]
    with Pool(8) as p, open(f"{ROOT}/results/heldout.jsonl", "w") as fo:
        for rows in p.imap_unordered(job, jobs):
            for r in rows: fo.write(json.dumps({k: (float(v) if isinstance(v, np.floating) else v) for k, v in r.items()}) + "\n")
    print("done", len(jobs))
