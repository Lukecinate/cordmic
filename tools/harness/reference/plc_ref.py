"""Reference pitch-synchronous packet-loss concealment, written as scalar loops so it ports 1:1 to C#.
Original code (idea: waveform substitution / pitch repetition, cf. ITU-T G.711 Appendix I). MIT.

Contract (48 kHz mono float, packet = P samples, default P = 240 = 5 ms):
  plc = PitchPlc(P)
  out = plc.good(pkt)   # every received packet, in order; returns P samples (may crossfade start)
  out = plc.lost()      # for every missing packet or jitter-buffer underrun; returns P samples
Latency: 0 (history-only). State: history ring H=1440 (30 ms), period buffer, phase, run counter.
"""
import math

class PitchPlc:
    H = 1440            # history length (30 ms)
    SEG = 240           # correlation window (5 ms)
    LMIN, LMAX = 120, 960   # pitch lag range 2.5..20 ms (50..400 Hz)
    XF = 96             # resume crossfade (2 ms), raised cosine
    VOICED = 0.6        # normalised-correlation threshold
    FULL_RUN = 2        # packets at full level, then -25 %/packet (silent after 6 = 30 ms)

    def __init__(self, P=240):
        self.P = P
        self.hist = [0.0] * self.H      # oldest .. newest (linear buffer, shift on push; ring in C#)
        self.filled = 0                 # valid samples in hist
        self.run = 0
        self.per = None; self.L = 0; self.ph = 0; self.voiced = False
        self.tail = None                # XF samples continuing the synthetic signal

    # ---- helpers -------------------------------------------------------------------------
    def _push(self, s):
        P = len(s); self.hist = self.hist[P:] + list(s); self.filled = min(self.H, self.filled + P)

    def _pitch(self):
        h = self.hist; n = self.H; seg0 = n - self.SEG
        best_c, best_L = -1.0, self.LMIN * 4
        ee = 0.0
        for k in range(self.SEG): ee += h[seg0 + k] * h[seg0 + k]
        for L in range(self.LMIN, self.LMAX + 1, 2):         # coarse, step 2
            if self.SEG + L > self.filled: break
            xy = 0.0; yy = 0.0; r0 = seg0 - L
            for k in range(self.SEG):
                a = h[r0 + k]; xy += h[seg0 + k] * a; yy += a * a
            c = xy / math.sqrt(ee * yy + 1e-12)
            if c > best_c: best_c, best_L = c, L
        for L in (best_L - 1, best_L + 1):                    # refine +-1
            if L < self.LMIN or L > self.LMAX or self.SEG + L > self.filled: continue
            xy = 0.0; yy = 0.0; r0 = seg0 - L
            for k in range(self.SEG):
                a = h[r0 + k]; xy += h[seg0 + k] * a; yy += a * a
            c = xy / math.sqrt(ee * yy + 1e-12)
            if c > best_c: best_c, best_L = c, L
        if ee < 1e-10: best_c = 0.0                              # digital silence
        return best_L, best_c

    # ---- API -----------------------------------------------------------------------------
    def good(self, pkt):
        out = list(pkt)
        if self.run > 0:
            if self.tail is not None:                          # crossfade synthetic -> real
                for k in range(self.XF):
                    w = 0.5 - 0.5 * math.cos(math.pi * k / self.XF)
                    out[k] = self.tail[k] * (1.0 - w) + out[k] * w
            else:                                              # concealed with silence: fade in
                for k in range(self.XF):
                    out[k] *= (k + 1) / (self.XF + 1)
        self.run = 0; self.tail = None; self.per = None      # next loss run re-estimates pitch
        self._push(out)
        return out

    def _gain(self, run):
        if run <= 0: return 1.0
        g = 1.0 if run <= self.FULL_RUN else max(0.0, 1.0 - 0.25 * (run - self.FULL_RUN))
        if not self.voiced: g *= 0.5 ** run
        return g

    def lost(self):
        P = self.P; self.run += 1
        if self.filled < 2 * self.SEG:                         # not enough history (stream start)
            self.tail = None; z = [0.0] * P; self._push(z); return z
        if self.per is None:          # first concealed packet of this run with enough history
            L, c = self._pitch()
            self.L = L; self.voiced = c > self.VOICED
            self.per = self.hist[self.H - L:]; self.ph = 0; self.run = 1
        g0 = self._gain(self.run - 1) if self.run > 1 else 1.0; g1 = self._gain(self.run)
        out = [0.0] * P; tail = [0.0] * self.XF
        for k in range(P + self.XF):
            g = g0 + (g1 - g0) * min(k, P) / P         # per-sample linear gain ramp (no steps)
            v = self.per[(self.ph + k) % self.L] * g
            if k < P: out[k] = v
            else: tail[k - P] = v
        self.ph = (self.ph + P) % self.L
        self.tail = tail if g1 > 0 else None
        self._push(out)              # concealed audio becomes history (keeps phase continuity)
        return out
