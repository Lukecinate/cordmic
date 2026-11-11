"""Offline re-implementations of noise-reduction gain rules for comparison (research only, not CordMic code).
All operate on a 48 kHz float signal through a common STFT/OLA engine and return (output, gains)."""
import numpy as np
from scipy.special import exp1, i0e, i1e

EPS = 1e-12

def stft_process(x, gain_fn, n=256, hop=128, window="hann_analysis"):
    """window: 'hann_analysis' = CordMic (periodic Hann analysis, rectangular synthesis);
    'sqrt_hann' = sqrt-Hann analysis and synthesis (WOLA)."""
    w = 0.5 - 0.5 * np.cos(2 * np.pi * np.arange(n) / n)
    if window == "hann_analysis":
        wa, ws = w, np.ones(n)
    else:
        wa = ws = np.sqrt(w)
    pad = np.concatenate([np.zeros(n - hop), x, np.zeros(n)])
    nf = (len(pad) - n) // hop + 1
    y = np.zeros(len(pad))
    G = np.zeros((nf, n // 2 + 1))
    for m in range(nf):
        seg = pad[m * hop: m * hop + n] * wa
        X = np.fft.rfft(seg)
        g = gain_fn(X)
        G[m] = g
        y[m * hop: m * hop + n] += np.fft.irfft(X * g, n) * ws
    out = y[n - hop: n - hop + len(x)]
    return out, G

def apply_gains(x, G, n=256, hop=128, window="hann_analysis"):
    """Shadow filtering: apply precomputed gains to another signal (clean or noise component)."""
    it = iter(G)
    return stft_process(x, lambda X: next(it), n, hop, window)[0]

# ---------------------------------------------------------------- CordMic 0.5.6 (spectral subtraction)
class CM056:
    def __init__(self, nb, strong=True):
        self.noise = np.zeros(nb); self.g = np.ones(nb)
        self.floor = 0.05 if strong else 0.32
        self.over = 1.85 if strong else 1.0
        self.smooth = 0.38 if strong else 0.70
    def __call__(self, X):
        m = np.abs(X); half = len(m) - 1
        noise_like = m[1:].max() < (m[1:].sum() / half) * 12 + 1e-12
        n = self.noise
        if noise_like: n = 0.8 * n + 0.2 * m
        else: n = np.where(m < n, 0.7 * n + 0.3 * m, n)
        self.noise = n
        g = np.clip(1 - self.over * n / (m + EPS), self.floor, 1)
        self.g = self.smooth * self.g + (1 - self.smooth) * g
        return self.g.copy()

# ---------------------------------------------------------------- CordMic 0.5.7 (DD Wiener + smoothing + HF protect)
class CM057:
    def __init__(self, nb, strong=True, hf_bin=16, noise_est=None, gain="wiener", dd=None, floor=None):
        self.nb = nb; self.strong = strong
        self.psd = np.zeros(nb); self.noise = np.zeros(nb); self.prev = np.zeros(nb)
        self.g = np.ones(nb); self.hf = hf_bin
        self.floor = floor if floor is not None else (0.18 if strong else 0.32)
        self.over = 1.6 if strong else 1.0
        self.dd = dd if dd is not None else (0.94 if strong else 0.92)
        self.rise = 10 ** (3 / 10 / 375)
        self.noise_est = noise_est  # optional external estimator object with update(P) -> noise psd
        self.gain = gain
    def __call__(self, X):
        p = np.abs(X) ** 2; mag2 = p
        self.psd = np.where(self.psd <= 0, p, 0.6 * self.psd + 0.4 * p)
        s = self.psd
        if self.noise_est is not None:
            n = self.noise_est.update(p)
        else:
            m = np.sqrt(p); half = self.nb - 1
            noise_like = m[1:].max() < (m[1:].sum() / half) * 12 + 1e-12
            noiseE = self.noise.sum(); frameE = s.sum()
            near = noiseE <= 0 or frameE < 2.5 * noiseE
            n = self.noise
            boot = n <= 0
            n = np.where(boot, s if noise_like else 0.0,
                np.where(s < n, 0.8 * n + 0.2 * s,
                    (0.95 * n + 0.05 * s) if (noise_like and near) else n * self.rise))
            self.noise = n
        nn = np.maximum(self.over * n, 1e-20)
        post = mag2 / nn
        prio = self.dd * self.prev / nn + (1 - self.dd) * np.maximum(post - 1, 0)
        if self.gain == "wiener":
            graw = prio / (1 + prio)
        else:
            graw = gain_rule(self.gain, prio, post)
        graw = np.clip(graw, self.floor, 1)
        hfS = s[self.hf:].sum(); hfN = n[self.hf:].sum()
        g = np.convolve(np.pad(graw, 1, mode="edge"), [0.25, 0.5, 0.25], "valid")
        if hfN > 0 and hfS > 2 * hfN:
            g[self.hf:] = np.maximum(g[self.hf:], 0.6)
        a = np.where(g > self.g, 0.45, 0.82)
        self.g = a * self.g + (1 - a) * g
        self.prev = (self.g ** 2) * mag2
        return self.g.copy()

# ---------------------------------------------------------------- classic gain rules
def gain_rule(kind, xi, gamma):
    xi = np.maximum(xi, 1e-6); gamma = np.maximum(gamma, 1e-6)
    v = xi / (1 + xi) * gamma
    if kind == "wiener":
        return xi / (1 + xi)
    if kind == "logmmse":  # Ephraim & Malah 1985
        return xi / (1 + xi) * np.exp(0.5 * exp1(np.maximum(v, 1e-8)))
    if kind == "mmse_stsa":  # Ephraim & Malah 1984, with scaled Bessel functions for stability
        vv = np.minimum(v, 500)
        return (np.sqrt(np.pi * vv) / (2 * gamma)) * ((1 + vv) * i0e(vv / 2) + vv * i1e(vv / 2))
    if kind == "specsub":  # Boll 1979 power subtraction
        return np.sqrt(np.maximum(1 - 1 / gamma, 0))
    raise ValueError(kind)

# ---------------------------------------------------------------- noise PSD estimators
class MinStat:
    """Simplified minimum statistics (Martin 2001): fixed-alpha smoothing, minimum over D frames
    in U sub-windows, bias compensation. Window ~1.5 s."""
    def __init__(self, nb, frames_per_s, win_s=1.5, alpha=0.85, U=8):
        self.alpha = alpha; self.P = None
        D = int(win_s * frames_per_s); self.V = max(1, D // U); self.U = U
        self.sub = np.full(nb, np.inf); self.mins = []
        self.cnt = 0; self.n = None
        # bias for chi2 minimum ~ 1.5 for D~150 at alpha 0.85 (Martin 2001 Table/approximation)
        self.bias = 1.5
    def update(self, p):
        self.P = p.copy() if self.P is None else self.alpha * self.P + (1 - self.alpha) * p
        self.sub = np.minimum(self.sub, self.P); self.cnt += 1
        if self.cnt % self.V == 0:
            self.mins.append(self.sub.copy()); self.mins = self.mins[-self.U:]
            self.sub = np.full_like(self.sub, np.inf)
        allm = self.mins + [self.sub]
        pmin = np.minimum.reduce(allm)
        pmin = np.where(np.isfinite(pmin), pmin, self.P)
        self.n = self.bias * pmin
        return self.n

class MCRA2:
    """MCRA-2 style (Rangachari & Loizou 2006): continuous minimum tracking, frequency-dependent
    speech-presence thresholds, speech-presence-controlled recursive averaging."""
    def __init__(self, nb, fs=48000, nfft=256):
        f = np.arange(nb) * fs / nfft
        self.delta = np.where(f < 1000, 2.0, np.where(f < 3000, 2.0, 5.0))
        self.P = None; self.Pmin = None; self.Pprev = None; self.n = None; self.p = np.zeros(nb)
        self.eta, self.gam, self.beta = 0.7, 0.998, 0.96
        self.ad, self.ap = 0.85, 0.2
    def update(self, p):
        if self.P is None:
            self.P = p.copy(); self.Pmin = p.copy(); self.Pprev = p.copy(); self.n = p.copy(); return self.n
        self.P = self.eta * self.P + (1 - self.eta) * p
        up = self.Pmin < self.P
        self.Pmin = np.where(up, self.gam * self.Pmin + (1 - self.gam) / (1 - self.beta) * (self.P - self.beta * self.Pprev), self.P)
        self.Pprev = self.P.copy()
        I = (self.P / np.maximum(self.Pmin, EPS)) > self.delta
        self.p = self.ap * self.p + (1 - self.ap) * I
        a = self.ad + (1 - self.ad) * self.p
        self.n = a * self.n + (1 - a) * p
        return self.n

class Oracle:
    def __init__(self, noise_psd_frames):
        self.it = iter(noise_psd_frames)
    def update(self, p):
        return next(self.it)

class Classic:
    """DD a-priori SNR (alpha=0.98, xi_min) + chosen gain rule + floor; optional OM-LSA
    speech-presence weighting G = G_H1^p * Gmin^(1-p) (Cohen & Berdugo 2001)."""
    def __init__(self, nb, est, rule="logmmse", alpha=0.98, xi_min_db=-25, floor_db=-15, omlsa=False, q=0.5,
                 smooth=False):
        self.est = est; self.rule = rule; self.alpha = alpha
        self.ximin = 10 ** (xi_min_db / 10); self.gmin = 10 ** (floor_db / 20)
        self.prev = None; self.omlsa = omlsa; self.q = q; self.smooth = smooth; self.g = np.ones(nb)
    def __call__(self, X):
        p = np.abs(X) ** 2
        n = np.maximum(self.est.update(p), 1e-20)
        gamma = p / n
        if self.prev is None: self.prev = np.ones_like(p) * self.ximin * n
        xi = np.maximum(self.alpha * self.prev / n + (1 - self.alpha) * np.maximum(gamma - 1, 0), self.ximin)
        gh1 = gain_rule(self.rule, xi, gamma)
        if self.omlsa:
            v = xi / (1 + xi) * gamma
            ratio = (self.q / (1 - self.q)) * (1 + xi) * np.exp(-np.minimum(v, 80))
            pres = 1 / (1 + ratio)
            g = gh1 ** pres * self.gmin ** (1 - pres)
        else:
            g = np.maximum(gh1, self.gmin)
        g = np.clip(g, self.gmin, 1)
        if self.smooth:
            a = np.where(g > self.g, 0.45, 0.82); self.g = a * self.g + (1 - a) * g; g = self.g.copy()
        self.prev = (gh1 ** 2) * p  # DD uses the unfloored H1 estimate
        return g

class MCRA2Scaled(MCRA2):
    """MCRA-2 with its time constants re-derived for a 2.67 ms hop (reference: 10 ms hop):
    c' = c ** (hop / hop_ref)."""
    def __init__(self, nb, fs=48000, nfft=256, hop=128, hop_ref_s=0.010):
        super().__init__(nb, fs, nfft)
        r = (hop / fs) / hop_ref_s
        self.eta, self.gam, self.beta = self.eta ** r, self.gam ** r, self.beta ** r
        self.ad, self.ap = self.ad ** r, self.ap ** r
