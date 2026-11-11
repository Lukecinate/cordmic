import numpy as np, soundfile as sf, glob, json, os, sys, subprocess, tempfile, time
from scipy.signal import resample_poly, lfilter, butter
from multiprocessing import Pool
from nr import *
from metrics import evaluate

FS = 48000
from paths import ROOT
rng0 = np.random.default_rng(7)

def load_clean():
    files = sorted(glob.glob(f"{ROOT}/data/libri/*.flac"))
    return files

def norm_active(x, target_db):
    from metrics import active_mask
    m = active_mask(x); f = x[: len(m) * 480].reshape(-1, 480)
    lvl = 10 * np.log10((f[m] ** 2).mean())
    return x * 10 ** ((target_db - lvl) / 20)

def make_noise(kind, n, rng, babble_src=None):
    if kind == "white":
        return rng.standard_normal(n)
    if kind == "pink":
        X = np.fft.rfft(rng.standard_normal(n)); f = np.fft.rfftfreq(n, 1 / FS); f[0] = f[1]
        return np.fft.irfft(X / np.sqrt(f), n)
    if kind == "fan":  # PC fan / AC: low-passed broadband + 100 Hz mains harmonics + blade tone wobble
        b, a = butter(2, 800 / (FS / 2)); x = lfilter(b, a, rng.standard_normal(n)) * 3 + 0.3 * rng.standard_normal(n)
        t = np.arange(n) / FS
        for h in range(1, 6): x += 0.15 / h * np.sin(2 * np.pi * 100 * h * t + rng.random() * 6)
        x += 0.1 * np.sin(2 * np.pi * 240 * t + 2 * np.sin(2 * np.pi * 0.3 * t))
        return x
    if kind == "babble":
        x = np.zeros(n)
        for s in babble_src:
            y, _ = sf.read(s); y = resample_poly(y, 3, 1)
            y = np.resize(y, n); y = np.roll(y, rng.integers(n)); x += y / (np.std(y) + 1e-9)
        return x
    if kind == "keyboard":  # sparse clicks: decaying noise bursts, ~7 per second
        x = 0.02 * rng.standard_normal(n)
        pos = np.cumsum(rng.exponential(FS / 7, size=int(n / FS * 10) + 10)).astype(int)
        for p in pos[pos < n - 2000]:
            L = 1500; env = np.exp(-np.arange(L) / 150)
            x[p: p + L] += rng.standard_normal(L) * env * (0.5 + rng.random())
        b, a = butter(2, [800 / (FS / 2), 9000 / (FS / 2)], "band")
        return lfilter(b, a, x)
    raise ValueError(kind)

def rnnoise(x):
    with tempfile.TemporaryDirectory() as d:
        pcm = (np.clip(x, -1, 1) * 32767).astype("<i2"); pcm.tofile(f"{d}/in.raw")
        subprocess.run([f"{ROOT}/ext/rnnoise/rnnoise_demo", f"{d}/in.raw", f"{d}/out.raw"], check=True,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        y = np.fromfile(f"{d}/out.raw", dtype="<i2").astype(float) / 32768
    y = np.concatenate([y, np.zeros(max(0, len(x) - len(y)))])[: len(x)]
    # align (RNNoise delays by one 480-sample frame)
    best = max(range(0, 1500, 10), key=lambda L: np.dot(y[L:L + 96000], x[:96000]))
    return np.concatenate([y[best:], np.zeros(best)])

NB = 129
def methods2(noise48):
    sc = lambda nb=NB, nfft=256, hop=128: MCRA2Scaled(nb, nfft=nfft, hop=hop)
    return {
        "cm057_strong": dict(fn=lambda: CM057(NB, True)),
        "cm057_light": dict(fn=lambda: CM057(NB, False)),
        "cm057_strong_dd099": dict(fn=lambda: CM057(NB, True, dd=0.99)),
        "cm057gain_mcra2s": dict(fn=lambda: CM057(NB, True, noise_est=sc())),
        "cm057gain_mcra2s_dd099": dict(fn=lambda: CM057(NB, True, noise_est=sc(), dd=0.99)),
        "logmmse_mcra2s_a995": dict(fn=lambda: Classic(NB, sc(), "logmmse", alpha=0.995)),
        "logmmse_mcra2s_a995_smooth": dict(fn=lambda: Classic(NB, sc(), "logmmse", alpha=0.995, smooth=True)),
        "cand_512_mcra2s_logmmse_smooth": dict(fn=lambda: Classic(257, sc(257, 512, 256), "logmmse", alpha=0.99, smooth=True), n=512, hop=256, window="sqrt_hann"),
        "cand_512_cm057gain_mcra2s": dict(fn=lambda: CM057(257, True, hf_bin=32, noise_est=sc(257, 512, 256), dd=0.97), n=512, hop=256, window="sqrt_hann"),
    }

def methods(noise48):
    fpsn = FS / 128
    def oracle_frames(n_fft=256, hop=128):
        # true noise PSD per frame (smoothed) for an upper bound
        _, G = stft_process(noise48, lambda X: np.abs(X) ** 2, n_fft, hop)
        P = np.copy(G); S = np.zeros_like(P); s = P[0]
        for i in range(len(P)): s = 0.9 * s + 0.1 * P[i]; S[i] = s
        return S
    return {
        "cm056_strong": dict(fn=lambda: CM056(NB, True)),
        "cm057_light": dict(fn=lambda: CM057(NB, False)),
        "cm057_strong": dict(fn=lambda: CM057(NB, True)),
        "cm057_strong_sqrthann": dict(fn=lambda: CM057(NB, True), window="sqrt_hann"),
        "cm057gain_mcra2": dict(fn=lambda: CM057(NB, True, noise_est=MCRA2(NB))),
        "logmmse_cm057est": dict(fn=lambda: CM057(NB, True, gain="logmmse")),
        "logmmse_mcra2": dict(fn=lambda: Classic(NB, MCRA2(NB), "logmmse")),
        "logmmse_minstat": dict(fn=lambda: Classic(NB, MinStat(NB, fpsn), "logmmse")),
        "omlsa_mcra2": dict(fn=lambda: Classic(NB, MCRA2(NB), "logmmse", omlsa=True)),
        "omlsa_mcra2_smooth": dict(fn=lambda: Classic(NB, MCRA2(NB), "logmmse", omlsa=True, smooth=True)),
        "logmmse_mcra2_512": dict(fn=lambda: Classic(257, MCRA2(257, nfft=512), "logmmse"), n=512, hop=256, window="sqrt_hann"),
        "logmmse_oracle": dict(fn=lambda: Classic(NB, Oracle(oracle_frames()), "logmmse")),
        "wiener_mcra2": dict(fn=lambda: Classic(NB, MCRA2(NB), "wiener")),
        "specsub_mcra2": dict(fn=lambda: Classic(NB, MCRA2(NB), "specsub")),
    }

def job(args):
    ci, cfile, kind, snr, seed = args
    rng = np.random.default_rng(seed)
    c, _ = sf.read(cfile); c = resample_poly(c, 3, 1)[: 12 * FS]
    c = norm_active(c, -26.0)
    others = [f for f in CLEAN if f != cfile][:6]
    nz = make_noise(kind, len(c), rng, others)
    from metrics import active_mask
    m = active_mask(c); f = c[: len(m) * 480].reshape(-1, 480); sl = 10 * np.log10((f[m] ** 2).mean())
    nz = nz * 10 ** ((sl - snr - 10 * np.log10((nz ** 2).mean())) / 20)
    x = c + nz
    rows = []
    def add(name, y, sh=None, nh=None, t=0.0):
        r = evaluate(c, x, y, sh, nh, nz); r.update(method=name, clip=ci, noise=kind, snr=snr, sec=t); rows.append(r)
    add("noisy", x)
    for name, spec in (methods2 if os.environ.get("SET") == "2" else methods)(nz).items():
        n, hop, win = spec.get("n", 256), spec.get("hop", 128), spec.get("window", "hann_analysis")
        t0 = time.time(); y, G = stft_process(x, spec["fn"](), n, hop, win); t = time.time() - t0
        sh = apply_gains(c, G, n, hop, win); nh = apply_gains(nz, G, n, hop, win)
        add(name, y, sh, nh, t)
    t0 = time.time(); y = rnnoise(x); t = time.time() - t0
    add("rnnoise", y, t=t)
    add("rnnoise_lim15", 0.822 * y + 0.178 * x)
    if os.environ.get("SET") == "2":
        add("rnnoise_lim20", 0.9 * y + 0.1 * x)
        add("rnnoise_lim25", 0.944 * y + 0.056 * x)  # crude -15 dB attenuation limit by dry mix
    return rows

CLEAN = load_clean()
if __name__ == "__main__":
    nclip = int(sys.argv[1]) if len(sys.argv) > 1 else 6
    kinds = sys.argv[2].split(",") if len(sys.argv) > 2 else ["white", "pink", "fan", "babble", "keyboard"]
    snrs = [float(s) for s in sys.argv[3].split(",")] if len(sys.argv) > 3 else [0, 5, 10, 20]
    out = sys.argv[4] if len(sys.argv) > 4 else f"{ROOT}/results/nr_results.jsonl"
    jobs = [(i, CLEAN[i], k, s, 1000 * i + int(s) + sum(map(ord, k))) for i in range(nclip) for k in kinds for s in snrs]
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with Pool(8) as p, open(out, "w") as fo:
        for rows in p.imap_unordered(job, jobs):
            for r in rows: fo.write(json.dumps({k: (float(v) if isinstance(v, (np.floating,)) else v) for k, v in r.items()}) + "\n")
            fo.flush()
    print("done", len(jobs))
