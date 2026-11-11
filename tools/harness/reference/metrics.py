from paths import ROOT
import numpy as np, onnxruntime as ort
from scipy.signal import resample_poly, stft
from pesq import pesq
from pystoi import stoi

_dns = None
def dnsmos(x16):
    """DNSMOS P.835 (Reddy et al. 2022), primary sig/bak/ovr model only, non-personalized polyfit."""
    global _dns
    if _dns is None:
        so = ort.SessionOptions(); so.intra_op_num_threads = 1; so.inter_op_num_threads = 1
        _dns = ort.InferenceSession(f"{ROOT}/ext/sig_bak_ovr.onnx", so, providers=["CPUExecutionProvider"])
    L = int(9.01 * 16000); a = x16.astype(np.float32)
    while len(a) < L: a = np.concatenate([a, a])
    hops = int(np.floor(len(a) / 16000) - 9.01) + 1
    r = []
    for i in range(hops):
        seg = a[i * 16000: i * 16000 + L]
        if len(seg) < L: continue
        r.append(_dns.run(None, {"input_1": seg[None, :]})[0][0])
    s, b, o = np.mean(r, axis=0)
    p_ovr = np.poly1d([-0.06766283, 1.11546468, 0.04602535])
    p_sig = np.poly1d([-0.08397278, 1.22083953, 0.0052439])
    p_bak = np.poly1d([-0.13166888, 1.60915514, -0.39604546])
    return float(p_sig(s)), float(p_bak(b)), float(p_ovr(o))

def to16(x48):
    return resample_poly(x48, 1, 3)

def si_sdr(ref, est):
    ref = ref - ref.mean(); est = est - est.mean()
    a = np.dot(est, ref) / (np.dot(ref, ref) + 1e-12)
    t = a * ref; e = est - t
    return 10 * np.log10((t @ t + 1e-12) / (e @ e + 1e-12))

def active_mask(clean48, frame=480, thr_db=-35):
    f = clean48[: len(clean48) // frame * frame].reshape(-1, frame)
    e = 10 * np.log10((f ** 2).mean(1) + 1e-12)
    return e > e.max() + thr_db

def seg_attn(ref, proc, mask, frame=480):
    n = min(len(ref), len(proc)) // frame * frame
    r = ref[:n].reshape(-1, frame); p = proc[:n].reshape(-1, frame); m = mask[: n // frame]
    return 10 * np.log10((r[m] ** 2).sum() / ((p[m] ** 2).sum() + 1e-20))

def seg_snr(ref, proc, mask, frame=480):
    n = min(len(ref), len(proc)) // frame * frame
    r = ref[:n].reshape(-1, frame); p = proc[:n].reshape(-1, frame); m = mask[: n // frame]
    s = 10 * np.log10((r ** 2).sum(1) / (((r - p) ** 2).sum(1) + 1e-20) + 1e-20)
    return float(np.clip(s[m], -10, 35).mean())

def log_kurtosis_ratio(n_in, n_out, fs=48000):
    """Musical-noise measure (Uemura et al. 2009): log(kurt_out/kurt_in) of the power spectra of the
    noise component before and after processing (shadow-filtered). 0 = no added musical noise."""
    def kurt(x):
        _, _, Z = stft(x, fs, nperseg=1024, noverlap=512)
        P = (np.abs(Z[5:-5]) ** 2).ravel()
        return np.mean(P ** 2) / (np.mean(P) ** 2 + 1e-30)
    return float(np.log(kurt(n_out) / kurt(n_in)))

def evaluate(clean48, noisy48, out48, s_hat=None, n_hat=None, noise48=None, dns=True):
    c16, o16 = to16(clean48), to16(out48)
    r = dict(pesq=pesq(16000, c16, o16, "wb"), stoi=stoi(c16, o16, 16000, extended=True), sisdr=si_sdr(c16, o16))
    if dns:
        r["dns_sig"], r["dns_bak"], r["dns_ovr"] = dnsmos(o16)
    if s_hat is not None:
        mask = active_mask(clean48)
        r["speech_attn_db"] = seg_attn(clean48, s_hat, mask)       # dB lost on speech (shadow)
        r["noise_attn_db"] = 10 * np.log10((noise48 ** 2).sum() / ((n_hat ** 2).sum() + 1e-20))
        r["speech_segsnr"] = seg_snr(clean48, s_hat, mask)         # speech distortion (higher = less)
        r["lkr"] = log_kurtosis_ratio(noise48, n_hat)
    return r
