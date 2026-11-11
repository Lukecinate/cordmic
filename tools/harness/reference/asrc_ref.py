"""Reference drift-compensating resampler for the PC jitter buffer: 4-point cubic Lagrange (Farrow form)
fractional interpolation + PI control of the buffer fill. Scalar loops, portable 1:1 to C#. Original, MIT.

Model: the consumer (WASAPI/VB-CABLE render callback) pulls n_out samples. The resampler reads input
samples from the jitter FIFO at an average rate of `ratio` input samples per output sample.
ratio > 1 drains the FIFO faster (phone clock fast / FIFO growing); ratio < 1 drains slower.
"""

class CubicResampler:
    def __init__(self):
        self.x = [0.0, 0.0, 0.0, 0.0]   # x[-1], x[0], x[1], x[2] around the read position
        self.mu = 0.0                   # fractional position in [0,1) between x[0] and x[1]
        self.primed = False

    def process(self, n_out, ratio, read_sample):
        """read_sample() -> next input float (caller supplies; returns None on underrun -> caller
        conceals). Returns list of n_out outputs. ratio is clamped by the controller."""
        out = []
        if not self.primed:
            for k in range(4):
                s = read_sample(); self.x[k] = 0.0 if s is None else s
            self.primed = True
        for _ in range(n_out):
            m = self.mu; xm1, x0, x1, x2 = self.x
            # Farrow / Horner form of 3rd-order Lagrange through (-1,0,1,2), exact at mu=0 (returns x0)
            c0 = x0
            c1 = x1 - xm1 / 3.0 - x0 / 2.0 - x2 / 6.0
            c2 = (xm1 + x1) / 2.0 - x0
            c3 = (x2 - xm1) / 6.0 + (x0 - x1) / 2.0
            out.append(((c3 * m + c2) * m + c1) * m + c0)
            self.mu += ratio
            while self.mu >= 1.0:                  # advance one input sample
                self.mu -= 1.0
                s = read_sample(); s = self.x[3] if s is None else s   # hold on underrun
                self.x = [self.x[1], self.x[2], self.x[3], s]
        return out

class DriftController:
    """PI loop on FIFO fill (in samples), output = ratio. Fill is smoothed (EMA, tau ~1 s).
    Tuned in test_asrc.py (3 ms arrival jitter, 240-sample packets, 10 ms render calls):
    tau 2 s, kp 5e-7, ki 1e-9 -> ratio jitter 16-77 ppm rms (wow < 0.01 %), THD+N <= -95 dB at 0..300 ppm,
    fill held at target +-140 samples. Larger gains (kp 2e-6) give ~200 ppm ratio jitter.
    Hard resync (outside this class): if |fill - target| > 960 samples, skip/pad once with a crossfade."""
    def __init__(self, target, fs=48000, calls_per_s=100):
        self.target = float(target); self.fill_f = float(target); self.integ = 0.0
        self.a = 1.0 / (2.0 * calls_per_s)   # EMA coefficient per call (tau = 2 s)
        self.kp = 5.0e-7                      # ratio per sample of error
        self.ki = 1.0e-9                      # ratio per sample-call of accumulated error
        self.lim = 1.0e-3
    def update(self, fill):
        self.fill_f += self.a * (fill - self.fill_f)
        e = self.fill_f - self.target
        self.integ += e
        imax = self.lim / self.ki; self.integ = max(-imax, min(imax, self.integ))   # anti-windup
        r = 1.0 + self.kp * e + self.ki * self.integ
        return max(1.0 - self.lim, min(1.0 + self.lim, r))
