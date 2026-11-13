using System.Runtime.CompilerServices;
namespace CordMic.Core.Dsp;

/// <summary>
/// Pitch-repeat packet-loss concealment (0.6.0). Mono float, one call per packet of <see cref="PacketFrames"/> samples.
/// On a lost packet the last pitch period of the history is repeated with phase continuity, a 2 ms raised-cosine
/// crossfade back into the next good packet, full level for two packets, then −25 % per packet (silent after 30 ms).
/// Unvoiced history (normalised correlation ≤ 0.6) is additionally halved per packet so repeated noise does not buzz.
/// Concealed audio is pushed into the history. Original MIT code after the published description in ITU-T G.711
/// Appendix I; reference implementation and test vectors: tools/harness/reference/plc_ref.py.
/// Allocation-free after construction.
/// </summary>
public sealed class PitchPlc
{
    public const int H = 1440, Seg = 240, LMin = 120, LMax = 960, Xf = 96, FullRun = 2;
    public const double Voiced = 0.6;

    private readonly double[] _hist = new double[H];
    private readonly double[] _per = new double[LMax + 2];
    private readonly double[] _tail = new double[Xf];
    private int _filled, _run, _l, _ph;
    private bool _voiced, _hasPer, _hasTail;

    public int PacketFrames { get; }
    /// <summary>Consecutive concealed packets in the current loss run (0 after a good packet).</summary>
    public int Run => _run;
    /// <summary>Pitch searches performed (for tests / CPU accounting).</summary>
    public long PitchSearches { get; private set; }
    /// <summary>Multiply-accumulate taps spent in pitch searches (deterministic CPU accounting for tests).</summary>
    public long SearchTaps { get; private set; }
    /// <summary>Upper bound of <see cref="SearchTaps"/> for one search: energy, the coarse lag grid, two refinements.</summary>
    public const long MaxTapsPerSearch = Seg + ((LMax - LMin) / 2 + 1 + 2) * (long)Seg;

    public PitchPlc(int packetFrames = 240)
    {
        if (packetFrames <= 0 || packetFrames > H) throw new ArgumentOutOfRangeException(nameof(packetFrames));
        PacketFrames = packetFrames;
    }

    public void Reset()
    {
        Array.Clear(_hist); Array.Clear(_tail);
        _filled = _run = _l = _ph = 0; _voiced = _hasPer = _hasTail = false;
    }

    private void Push(ReadOnlySpan<float> s)
    {
        int p = s.Length;
        Array.Copy(_hist, p, _hist, 0, H - p);
        for (int i = 0; i < p; i++) _hist[H - p + i] = s[i];
        _filled = Math.Min(H, _filled + p);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private double Corr(int l, double ee)
    {
        int seg0 = H - Seg, r0 = seg0 - l;
        SearchTaps += Seg;
        double xy = 0, yy = 0;
        for (int k = 0; k < Seg; k++) { double a = _hist[r0 + k]; xy += _hist[seg0 + k] * a; yy += a * a; }
        return xy / Math.Sqrt(ee * yy + 1e-12);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private (int L, double C) Pitch()
    {
        PitchSearches++;
        SearchTaps += Seg;
        int seg0 = H - Seg;
        double ee = 0;
        for (int k = 0; k < Seg; k++) ee += _hist[seg0 + k] * _hist[seg0 + k];
        double bc = -1; int bl = LMin * 4;
        for (int l = LMin; l <= LMax; l += 2)
        {
            if (Seg + l > _filled) break;
            double c = Corr(l, ee);
            if (c > bc) { bc = c; bl = l; }
        }
        for (int d = -1; d <= 1; d += 2)
        {
            int l = bl + d;
            if (l < LMin || l > LMax || Seg + l > _filled) continue;
            double c = Corr(l, ee);
            if (c > bc) { bc = c; bl = l; }
        }
        if (ee < 1e-10) bc = 0;
        return (bl, bc);
    }

    private double Gain(int r)
    {
        if (r <= 0) return 1;
        double g = r <= FullRun ? 1 : Math.Max(0, 1 - 0.25 * (r - FullRun));
        if (!_voiced) g *= Math.Pow(0.5, r);
        return g;
    }

    /// <summary>A received packet, processed in place (crossfade or fade-in after a loss run).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Good(Span<float> pkt)
    {
        if (pkt.Length != PacketFrames) throw new ArgumentException("packet length", nameof(pkt));
        if (_run > 0)
        {
            int n = Math.Min(Xf, pkt.Length);
            if (_hasTail)
                for (int k = 0; k < n; k++) { double w = 0.5 - 0.5 * Math.Cos(Math.PI * k / Xf); pkt[k] = (float)(_tail[k] * (1 - w) + pkt[k] * w); }
            else
                for (int k = 0; k < n; k++) pkt[k] = (float)(pkt[k] * ((k + 1.0) / (Xf + 1)));
        }
        _run = 0; _hasTail = false; _hasPer = false;
        Push(pkt);
    }

    /// <summary>Writes one concealment packet into <paramref name="dst"/>. Call on loss and on jitter-buffer underrun.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Lost(Span<float> dst)
    {
        if (dst.Length != PacketFrames) throw new ArgumentException("packet length", nameof(dst));
        _run++;
        if (_filled < 2 * Seg) { _hasTail = false; dst.Clear(); Push(dst); return; }
        if (!_hasPer)
        {
            var (l, c) = Pitch();
            _l = l; _voiced = c > Voiced;
            Array.Copy(_hist, H - _l, _per, 0, _l);
            _ph = 0; _run = 1; _hasPer = true;
        }
        double g0 = _run > 1 ? Gain(_run - 1) : 1, g1 = Gain(_run);
        int p = PacketFrames;
        if (g0 == 0 && g1 == 0)
        {
            dst.Clear(); _hasTail = false; _ph = (_ph + p) % _l; Push(dst); return;
        }
        for (int k = 0; k < p + Xf; k++)
        {
            double g = g0 + (g1 - g0) * Math.Min(k, p) / p;
            double v = _per[(_ph + k) % _l] * g;
            if (k < p) dst[k] = (float)v; else _tail[k - p] = v;
        }
        _ph = (_ph + p) % _l;
        _hasTail = g1 > 0;
        Push(dst);
    }
}
