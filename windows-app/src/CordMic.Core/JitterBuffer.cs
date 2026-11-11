using CordMic.Core.Dsp;

namespace CordMic.Core;

public sealed class JitterStats
{
    public long Pushed, Played, Concealed, Late, Duplicates, OverflowDrops, Underruns, Resets;
    /// <summary>Packets removed by the soft catch-up (drift / backlog control), each spliced with a crossfade.</summary>
    public long CatchUpDrops;
    /// <summary>Single-sample skip/insert adjustments from the drift lock.</summary>
    public long SampleSlips;
    /// <summary>Packets synthesised by pitch-repeat concealment during an underrun (0.6.0).</summary>
    public long UnderrunConcealed;
    /// <summary>Hard resyncs of the drift resampler (fill more than 20 ms above target; packets dropped once).</summary>
    public long ResampleResyncs;
}

/// <summary>
/// Push from the network thread, Pull from the audio callback. After construction the hot path
/// does not allocate: packets live in a fixed ring indexed by sequence, and pull copies out of it.
///
/// Latency control:
/// * Playback (re)starts once max(target, packets needed by the current pull) are buffered.
/// * The hard cap (<see cref="MaxDepth"/>) trims down to <see cref="HighWater"/> (target plus one
///   period), not all the way back to the target, so a burst does not dump the buffer empty.
/// * A sample-level drift lock (<see cref="DriftLock"/>) skips or inserts at most one frame per
///   pull when the standing depth stays outside a one-period band around the target. Packet-drop
///   catch-up (<see cref="CatchUp"/>) remains available but is off in production.
/// The target itself is at least two render pulls; see <see cref="JitterPolicy"/>.
/// </summary>
public sealed class JitterBuffer : IFloatSource
{
    private readonly object _lock = new();
    // 0.6.0: pitch-repeat concealment and the cubic drift resampler (mono only; off by default for the old tests).
    private PitchPlc? _plc;
    private float[] _plcF = Array.Empty<float>();
    private bool _plcPlayed;
    private readonly CubicResampler _rs = new();
    private readonly DriftController _dc = new(0, DriftController.KpTuned);
    private float[] _rsIn = Array.Empty<float>();
    private byte[] _rsRaw = Array.Empty<byte>();
    private float[] _rsOut = Array.Empty<float>();
    private int _rsPos, _rsLen;
    private byte[] _pool = Array.Empty<byte>();
    private uint[] _seq = Array.Empty<uint>();
    private byte[] _occ = Array.Empty<byte>();
    private int _cap, _mask, _count;
    private uint _newest;
    private uint? _next;
    private bool _buffering = true;
    private byte[] _silence = Array.Empty<byte>();
    private byte[] _out = Array.Empty<byte>();
    private byte[] _keep = Array.Empty<byte>();
    private int _keepLen, _keepOff;

    // catch-up window state (all in frames)
    private long _winFrames;
    private long _winMinResidual = long.MaxValue;
    private double _winDepthArea;
    private int _pendingDrop;
    private double _ppm;
    private double _integ;
    private double _slipAcc;
    private double _offTargetSec;
    private double _depthAtPeg = double.NaN;
    private double _avgDepthMs = double.NaN, _minResidualMs = double.NaN;
    private short[] _lastFrame = Array.Empty<short>();
    private byte[] _extra = Array.Empty<byte>();
    private bool _fadeIn;
    private const int SlipCrossfade = 32;
    private const double BasePpm = 150;
    private const double MaxPpm = 800;
    /// <summary>Length of the fade to silence written when the buffer runs dry (2 ms at 48 kHz).</summary>
    private const int UnderrunFadeFrames = 96;
    private short[] _tailFrom = Array.Empty<short>();
    private int _tailPos = UnderrunFadeFrames;
    // Adaptive target state, in frames of playout.
    private int _extraPackets;
    private long _clockFrames;
    private long _lastUnderrunAt = long.MinValue / 2;
    private long _lastRaiseAt = long.MinValue / 2;
    private long _calmSince;

    public int FramesPerPacket { get; private set; }
    public int Channels { get; }
    public int SampleRate { get; }
    public int BytesPerPacket { get; private set; }
    /// <summary>Target configured at construction (before any adaptive raise).</summary>
    public int BaseTarget { get; }
    /// <summary>Target in use: <see cref="BaseTarget"/> plus the adaptive extra packets.</summary>
    public int Target => BaseTarget + _extraPackets;
    public int MaxDepth => _baseMax + _extraPackets;
    /// <summary>Overflow trim target (usually one period above <see cref="Target"/>).</summary>
    public int HighWater => _baseHigh + _extraPackets;
    private readonly int _baseMax, _baseHigh;
    /// <summary>
    /// Raise the target by one packet when two underruns occur within <see cref="AdaptWindowSec"/>,
    /// up to <see cref="MaxExtraPackets"/>; lower it again after <see cref="CalmSec"/> without one.
    /// </summary>
    public bool AdaptiveTarget { get; set; }
    public const int MaxExtraPackets = 3;
    public const double AdaptWindowSec = 10;
    public const double CalmSec = 60;
    /// <summary>Packets currently added to the target by the adaptive control.</summary>
    public int ExtraPackets { get { lock (_lock) return _extraPackets; } }
    /// <summary>Packet-drop catch-up. Off in production; kept for tests and emergency use.</summary>
    public bool CatchUp { get; set; }
    /// <summary>Apply a slow, crossfaded sample-level correction when mean depth drifts outside a deadband.</summary>
    public bool DriftLock { get; set; }
    /// <summary>
    /// 0.6.0: conceal lost packets and underruns by pitch repetition (<see cref="PitchPlc"/>) instead of a 3 ms decay
    /// and a fade to silence. Mono only; ignored for stereo.
    /// </summary>
    public bool PitchConcealment { get; set; }
    /// <summary>
    /// 0.6.0: correct clock drift with the cubic resampler and <see cref="DriftController"/> instead of sample slips.
    /// Mono only. When on, <see cref="DriftLock"/> should be off.
    /// </summary>
    public bool Resample { get; set; }
    private bool PlcOn => PitchConcealment && Channels == 1;
    private bool RsOn => Resample && Channels == 1;
    /// <summary>WASAPI period used for hysteresis; 0 if unknown.</summary>
    public double RenderPeriodMs { get; set; }
    /// <summary>Length of one catch-up decision window, in packets of playout.</summary>
    public int WindowPackets { get; set; } = 100;
    public JitterStats Stats { get; } = new();

    public JitterBuffer(int framesPerPacket, int channels = 1, int targetPackets = 2, int? maxPackets = null, int sampleRate = 48000, int? highWaterPackets = null)
    {
        if (targetPackets < 1) throw new ArgumentOutOfRangeException(nameof(targetPackets));
        FramesPerPacket = framesPerPacket;
        Channels = channels;
        SampleRate = sampleRate;
        BytesPerPacket = framesPerPacket * channels * 2;
        BaseTarget = targetPackets;
        _baseMax = maxPackets ?? Math.Max(targetPackets * 3, targetPackets + 4);
        _baseHigh = highWaterPackets ?? targetPackets;
        if (_baseHigh < BaseTarget) throw new ArgumentOutOfRangeException(nameof(highWaterPackets));
        Alloc(BytesPerPacket);
    }

    private void Alloc(int bytes)
    {
        BytesPerPacket = bytes;
        // Span can briefly be MaxDepth+1 between insert and trim; the ring must be larger than that
        // so two live sequence numbers never share a slot.
        _cap = NextPow2(Math.Max(8, _baseMax + MaxExtraPackets + 2));
        _mask = _cap - 1;
        _pool = new byte[_cap * bytes];
        _seq = new uint[_cap];
        _occ = new byte[_cap];
        _silence = new byte[bytes];
        _out = new byte[bytes];
        _keep = new byte[bytes];
        _lastFrame = new short[Channels];
        _tailFrom = new short[Channels];
        _tailPos = UnderrunFadeFrames;
        _extra = new byte[2 * Channels];
        _count = 0;
        int fpp = bytes / (2 * Channels);
        _plc = Channels == 1 && fpp > 0 && fpp <= PitchPlc.H ? new PitchPlc(fpp) : null;
        _plcF = new float[Math.Max(1, fpp)];
        _plcPlayed = false;
        _rsIn = new float[Math.Max(1, fpp)];
        _rsRaw = new byte[bytes];
        _rsPos = _rsLen = 0;
        _rs.Reset();
        _dc.Reset();
    }

    private static int NextPow2(int n)
    {
        int p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    public void Push(uint seq, byte[] payload) => Push(seq, payload.AsSpan());

    public void Push(uint seq, ReadOnlySpan<byte> payload)
    {
        lock (_lock)
        {
            if (payload.Length != BytesPerPacket)
            {
                FramesPerPacket = payload.Length / (2 * Channels);
                Alloc(payload.Length);
                ResetLocked();
            }
            Stats.Pushed++;
            _next ??= seq;
            long d = Protocol.SeqDiff(seq, _next.Value);
            if (d < 0)
            {
                if (d < -1000) { ResetLocked(); _next = seq; }
                else { Stats.Late++; return; }
            }
            else if (d > 5000) { ResetLocked(); _next = seq; }
            int slot = (int)(seq & _mask);
            if (_occ[slot] != 0 && _seq[slot] == seq) { Stats.Duplicates++; return; }
            if (_occ[slot] != 0)
            {
                // Stale occupant outside the live window (capacity exceeds the depth).
                _occ[slot] = 0;
                _count--;
            }
            payload.CopyTo(_pool.AsSpan(slot * BytesPerPacket, BytesPerPacket));
            _seq[slot] = seq;
            _occ[slot] = 1;
            _count++;
            if (_count == 1) _newest = seq;
            else if (Protocol.SeqDiff(seq, _newest) > 0) _newest = seq;
            TrimLocked();
        }
    }

    private void ResetLocked()
    {
        Array.Clear(_occ);
        _count = 0;
        _next = null;
        _buffering = true;
        _keepLen = 0; _keepOff = 0;
        _pendingDrop = 0;
        _ppm = 0; _integ = 0; _slipAcc = 0; _offTargetSec = 0; _depthAtPeg = double.NaN;
        _fadeIn = false;
        Array.Clear(_lastFrame);
        _tailPos = UnderrunFadeFrames;
        _plc?.Reset();
        _plcPlayed = false;
        _rs.Reset(); _dc.Reset(); _rsPos = _rsLen = 0;
        ResetWindowLocked();
        Stats.Resets++;
    }

    private void ResetWindowLocked()
    {
        _winFrames = 0; _winMinResidual = long.MaxValue; _winDepthArea = 0;
    }

    private int SpanLocked()
    {
        if (_count == 0 || _next is null) return 0;
        return (int)Protocol.SeqDiff(_newest, _next.Value) + 1;
    }

    private void ClearSlot(uint seq)
    {
        int slot = (int)(seq & _mask);
        if (_occ[slot] == 0 || _seq[slot] != seq) return;
        _occ[slot] = 0;
        _count--;
        if (_count > 0 && seq == _newest) RecomputeNewest();
    }

    private void RecomputeNewest()
    {
        long best = long.MinValue;
        uint bestSeq = _next ?? 0;
        for (int i = 0; i < _cap; i++)
        {
            if (_occ[i] == 0) continue;
            long d = Protocol.SeqDiff(_seq[i], _next!.Value);
            if (d > best) { best = d; bestSeq = _seq[i]; }
        }
        _newest = bestSeq;
    }

    private bool TrySlot(uint seq, out int offset)
    {
        int slot = (int)(seq & _mask);
        if (_occ[slot] != 0 && _seq[slot] == seq) { offset = slot * BytesPerPacket; return true; }
        offset = 0;
        return false;
    }

    private void TrimLocked()
    {
        int span = SpanLocked();
        if (span <= MaxDepth) return;
        int cap = Math.Max(Target, HighWater);
        int drop = span - cap;
        for (int i = 0; i < drop; i++)
        {
            ClearSlot(_next!.Value);
            Stats.OverflowDrops++;
            _next = unchecked(_next.Value + 1);
            _fadeIn = true;
        }
        _pendingDrop = 0;
    }

    /// <summary>Copies the next packet into <paramref name="dst"/> (length <see cref="BytesPerPacket"/>). False = underrun / still buffering.</summary>
    private bool CopyPacketLocked(Span<byte> dst, int neededPackets)
    {
        if (_next is null) return false;
        if (_buffering)
        {
            if (SpanLocked() >= Math.Max(Target, neededPackets)) _buffering = false;
            else return false;
        }
        uint cur = _next.Value;
        if (TrySlot(cur, out int off))
        {
            uint nxt = unchecked(cur + 1);
            if (_pendingDrop > 0 && TrySlot(nxt, out int offB))
            {
                CrossfadeInto(_pool.AsSpan(off, BytesPerPacket), _pool.AsSpan(offB, BytesPerPacket), dst, Channels);
                ClearSlot(cur); ClearSlot(nxt);
                _next = unchecked(cur + 2);
                _pendingDrop--;
                Stats.CatchUpDrops++;
                Stats.Played++;
                return true;
            }
            _pool.AsSpan(off, BytesPerPacket).CopyTo(dst);
            ClearSlot(cur);
            _next = nxt;
            if (PlcOn && _plc is { } plc)
            {
                if (plc.Run > 0) _fadeIn = false; // the concealment crossfades into this packet itself
                FadeInPacket(dst);
                ToFloat(dst, _plcF);
                plc.Good(_plcF);
                FromFloat(_plcF, dst);
                _plcPlayed = true;
            }
            else FadeInPacket(dst);
            RememberLast(dst);
            Stats.Played++;
            return true;
        }
        if (_count > 0)
        {
            _next = unchecked(cur + 1);
            Stats.Concealed++;
            if (PlcOn && _plc is { } plc)
            {
                plc.Lost(_plcF);
                FromFloat(_plcF, dst);
                _fadeIn = false;
                RememberLast(dst);
            }
            else WriteConcealment(dst);
            return true;
        }
        Stats.Underruns++;
        _buffering = true;
        _pendingDrop = 0;
        StartUnderrunFadeLocked();
        NoteUnderrunLocked();
        return false;
    }

    /// <summary>Linear crossfade of two equally long s16 interleaved packets into dst.</summary>
    public static void CrossfadeInto(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> dst, int channels)
    {
        int frames = a.Length / (2 * channels);
        for (int f = 0; f < frames; f++)
        {
            double w = frames > 1 ? (double)f / (frames - 1) : 1.0;
            for (int c = 0; c < channels; c++)
            {
                int i = (f * channels + c) * 2;
                int sa = (short)(a[i] | (a[i + 1] << 8));
                int sb = (short)(b[i] | (b[i + 1] << 8));
                int v = (int)Math.Round(sa * (1 - w) + sb * w);
                v = Math.Clamp(v, short.MinValue, short.MaxValue);
                dst[i] = (byte)v; dst[i + 1] = (byte)(v >> 8);
            }
        }
    }

    private long QueuedFramesLocked() =>
        (long)_count * FramesPerPacket + (_keepLen > _keepOff ? (_keepLen - _keepOff) / (2 * Channels) : 0);

    /// <summary>Fills dst completely with PCM (silence-padded). Does not allocate (after the first pull of a given size).</summary>
    public void Pull(Span<byte> dst)
    {
        lock (_lock)
        {
            if (RsOn) PullResampledLocked(dst);
            else PullCoreLocked(dst);
        }
    }

    /// <summary>Resampler input: the next sample of the jitter-buffer stream (always available: concealment or silence fills gaps).</summary>
    float? IFloatSource.Next()
    {
        if (_rsPos >= _rsLen)
        {
            PullCoreLocked(_rsRaw);
            ToFloat(_rsRaw, _rsIn);
            _rsLen = _rsIn.Length; _rsPos = 0;
        }
        return _rsIn[_rsPos++];
    }

    private double FillFramesLocked() => QueuedFramesLocked() + (_rsLen - _rsPos) + _rs.HeldSamples;

    private void PullResampledLocked(Span<byte> dst)
    {
        int frames = dst.Length / 2;
        if (frames == 0) return;
        if (_rsOut.Length < frames) _rsOut = new float[frames];
        double before = FillFramesLocked();
        double target = Target * (double)FramesPerPacket;
        if (ResyncIfFarAboveLocked(before, target)) before = FillFramesLocked();
        _rs.Process(_rsOut.AsSpan(0, frames), _dc.Ratio, this);
        FromFloat(_rsOut.AsSpan(0, frames), dst);
        double after = FillFramesLocked();
        if (ResyncIfFarAboveLocked(after, target)) { after = FillFramesLocked(); before = after + frames; }
        _dc.Target = target;
        if (_next is not null) _dc.Update((before + after) / 2, frames / (double)SampleRate);
    }

    /// <summary>
    /// Hard resync (start-up backlog or a burst after a stall): when the fill is more than 20 ms above target, drop
    /// whole packets down to the target once; the next packet fades in from the last output sample.
    /// </summary>
    private bool ResyncIfFarAboveLocked(double fill, double target)
    {
        if (_next is null || _buffering || fill - target <= DriftController.ResyncSamples) return false;
        int drop = (int)Math.Round((fill - target) / FramesPerPacket);
        for (int i = 0; i < drop && SpanLocked() > 0; i++)
        {
            ClearSlot(_next!.Value);
            Stats.OverflowDrops++;
            _next = unchecked(_next.Value + 1);
            _fadeIn = true;
        }
        Stats.ResampleResyncs++;
        return true;
    }

    private static void ToFloat(ReadOnlySpan<byte> pcm, Span<float> f)
    {
        int n = Math.Min(f.Length, pcm.Length / 2);
        for (int i = 0; i < n; i++) f[i] = (short)(pcm[2 * i] | (pcm[2 * i + 1] << 8)) / 32768f;
    }

    private static void FromFloat(ReadOnlySpan<float> f, Span<byte> pcm)
    {
        int n = Math.Min(f.Length, pcm.Length / 2);
        for (int i = 0; i < n; i++)
        {
            int v = (int)Math.Round(f[i] * 32768.0);
            if (v > 32767) v = 32767; else if (v < -32768) v = -32768;
            pcm[2 * i] = (byte)v; pcm[2 * i + 1] = (byte)(v >> 8);
        }
    }

    private void PullCoreLocked(Span<byte> dst)
    {
        int pos = 0;
        {
            long before = QueuedFramesLocked();
            if (_keepLen > _keepOff)
            {
                int take = Math.Min(dst.Length, _keepLen - _keepOff);
                _keep.AsSpan(_keepOff, take).CopyTo(dst);
                pos += take; _keepOff += take;
                if (_keepOff >= _keepLen) { _keepLen = 0; _keepOff = 0; }
            }
            while (pos < dst.Length)
            {
                int need = dst.Length - pos;
                int neededPackets = (need + BytesPerPacket - 1) / BytesPerPacket;
                if (!CopyPacketLocked(_out, neededPackets))
                {
                    // 0.6.0: keep concealing through an underrun / rebuffer instead of fading to silence.
                    if (!(PlcOn && _plcPlayed && _plc is { } plc)) break;
                    plc.Lost(_plcF);
                    FromFloat(_plcF, _out);
                    Stats.UnderrunConcealed++;
                    _tailPos = UnderrunFadeFrames;
                }
                int take = Math.Min(need, BytesPerPacket);
                _out.AsSpan(0, take).CopyTo(dst[pos..]);
                pos += take;
                if (BytesPerPacket > need)
                {
                    int rem = BytesPerPacket - need;
                    _out.AsSpan(need, rem).CopyTo(_keep);
                    _keepLen = rem; _keepOff = 0;
                }
            }
            if (pos >= dst.Length)
                ApplySlipLocked(dst);
            else
            {
                WriteUnderrunTailLocked(dst[pos..]);
                pos = dst.Length;
            }
            AccountLocked(before, dst.Length / (2 * Channels));
        }
    }

    /// <summary>Begin a short fade from the last played sample so a dry buffer does not end on a step.</summary>
    private void StartUnderrunFadeLocked()
    {
        if (_tailFrom.Length != Channels) _tailFrom = new short[Channels];
        Array.Copy(_lastFrame, _tailFrom, Math.Min(_lastFrame.Length, _tailFrom.Length));
        _tailPos = 0;
        Array.Clear(_lastFrame);
        _fadeIn = true;
    }

    /// <summary>Fills the unplayed part of a pull: the remaining underrun fade, then silence.</summary>
    private void WriteUnderrunTailLocked(Span<byte> dst)
    {
        int fb = 2 * Channels;
        int frames = dst.Length / fb;
        int f = 0;
        for (; f < frames && _tailPos < UnderrunFadeFrames; f++, _tailPos++)
        {
            double g = 0.5 + 0.5 * Math.Cos(Math.PI * (_tailPos + 1) / UnderrunFadeFrames);
            for (int c = 0; c < Channels; c++)
            {
                int v = (int)Math.Round(_tailFrom[c] * g);
                int i = (f * Channels + c) * 2;
                dst[i] = (byte)v; dst[i + 1] = (byte)(v >> 8);
            }
        }
        if (f * fb < dst.Length) dst[(f * fb)..].Clear();
    }

    private void NoteUnderrunLocked()
    {
        long now = _clockFrames;
        long window = (long)(AdaptWindowSec * SampleRate);
        if (AdaptiveTarget && _extraPackets < MaxExtraPackets
            && now - _lastUnderrunAt <= window && now - _lastRaiseAt >= SampleRate)
        {
            _extraPackets++;
            _lastRaiseAt = now;
        }
        _lastUnderrunAt = now;
        _calmSince = now;
    }

    private void AdaptCalmLocked()
    {
        if (!AdaptiveTarget || _extraPackets == 0) return;
        if (_clockFrames - _calmSince >= (long)(CalmSec * SampleRate))
        {
            _extraPackets--;
            _calmSince = _clockFrames;
        }
    }

    /// <summary>Skip or insert at most one frame, crossfading so adjacent samples stay continuous.</summary>
    private void ApplySlipLocked(Span<byte> dst)
    {
        if (!DriftLock || Math.Abs(_slipAcc) < 1) return;
        int fb = 2 * Channels;
        if (fb <= 0 || dst.Length < fb * 2) return;
        if (_slipAcc >= 1)
        {
            if (!TryPopFrame()) return;
            CrossfadeDrop(dst, _extra);
            _slipAcc -= 1;
            Stats.SampleSlips++;
            return;
        }
        // Insert: replay the last frame next time (zero-order hold is itself continuous)
        // and stretch the tail of this buffer so the extra sample is interpolated.
        if (dst.Length < 2 * fb) return;
        int held = _keepLen - _keepOff;
        if (held + fb > _keep.Length) return;
        if (_keepOff > 0 && held > 0)
        {
            Buffer.BlockCopy(_keep, _keepOff, _keep, 0, held);
            _keepLen = held;
            _keepOff = 0;
        }
        else if (held == 0) { _keepLen = 0; _keepOff = 0; }
        if (_keepLen > 0) Buffer.BlockCopy(_keep, 0, _keep, fb, _keepLen);
        dst.Slice(dst.Length - fb, fb).CopyTo(_keep.AsSpan(0, fb));
        _keepLen += fb;
        _keepOff = 0;
        CrossfadeInsert(dst);
        _slipAcc += 1;
        Stats.SampleSlips++;
    }

    private bool TryPopFrame()
    {
        int fb = 2 * Channels;
        if (_keepLen - _keepOff >= fb)
        {
            _keep.AsSpan(_keepOff, fb).CopyTo(_extra);
            _keepOff += fb;
            if (_keepOff >= _keepLen) { _keepLen = 0; _keepOff = 0; }
            return true;
        }
        if (!CopyPacketLocked(_out, 1)) return false;
        _out.AsSpan(0, fb).CopyTo(_extra);
        int rem = BytesPerPacket - fb;
        if (rem > 0)
        {
            _out.AsSpan(fb, rem).CopyTo(_keep);
            _keepLen = rem;
            _keepOff = 0;
        }
        else { _keepLen = 0; _keepOff = 0; }
        return true;
    }

    private void CrossfadeDrop(Span<byte> dst, ReadOnlySpan<byte> extra)
    {
        int fb = 2 * Channels;
        int n = dst.Length / fb;
        int xf = Math.Min(SlipCrossfade, n - 1);
        int start = n - xf;
        for (int f = 0; f < xf; f++)
        {
            double w = (f + 1.0) / (xf + 1.0);
            for (int c = 0; c < Channels; c++)
            {
                int ia = ((start + f) * Channels + c) * 2;
                int sa = (short)(dst[ia] | (dst[ia + 1] << 8));
                int sb;
                if (start + f + 1 < n)
                {
                    int ib = ((start + f + 1) * Channels + c) * 2;
                    sb = (short)(dst[ib] | (dst[ib + 1] << 8));
                }
                else
                    sb = (short)(extra[c * 2] | (extra[c * 2 + 1] << 8));
                int v = (int)Math.Round(sa * (1 - w) + sb * w);
                v = Math.Clamp(v, short.MinValue, short.MaxValue);
                dst[ia] = (byte)v; dst[ia + 1] = (byte)(v >> 8);
            }
        }
    }

    private void CrossfadeInsert(Span<byte> dst)
    {
        int fb = 2 * Channels;
        int n = dst.Length / fb;
        int xf = Math.Min(SlipCrossfade, n - 1);
        if (xf < 2) return;
        int start = n - xf;
        // Stretch the last xf-1 original samples across xf slots (linear interpolation).
        Span<short> tmp = stackalloc short[xf * Channels];
        for (int f = 0; f < xf; f++)
            for (int c = 0; c < Channels; c++)
            {
                int i = ((start + f) * Channels + c) * 2;
                tmp[f * Channels + c] = (short)(dst[i] | (dst[i + 1] << 8));
            }
        for (int f = 0; f < xf; f++)
        {
            double pos = f * (xf - 2) / (double)(xf - 1); // 0 .. xf-2
            int i0 = (int)pos;
            double frac = pos - i0;
            int i1 = Math.Min(i0 + 1, xf - 2);
            for (int c = 0; c < Channels; c++)
            {
                double a = tmp[i0 * Channels + c];
                double b = tmp[i1 * Channels + c];
                int v = (int)Math.Round(a * (1 - frac) + b * frac);
                v = Math.Clamp(v, short.MinValue, short.MaxValue);
                int i = ((start + f) * Channels + c) * 2;
                dst[i] = (byte)v; dst[i + 1] = (byte)(v >> 8);
            }
        }
    }

    private void RememberLast(ReadOnlySpan<byte> pkt)
    {
        if (_lastFrame.Length != Channels) _lastFrame = new short[Channels];
        int fb = 2 * Channels;
        if (pkt.Length < fb) return;
        int baseOff = pkt.Length - fb;
        for (int c = 0; c < Channels; c++)
        {
            int i = baseOff + c * 2;
            _lastFrame[c] = (short)(pkt[i] | (pkt[i + 1] << 8));
        }
    }

    private void FadeInPacket(Span<byte> dst)
    {
        if (!_fadeIn) return;
        _fadeIn = false;
        int fb = 2 * Channels;
        int frames = dst.Length / fb;
        int xf = Math.Min(SlipCrossfade, frames);
        for (int f = 0; f < xf; f++)
        {
            double w = (f + 1.0) / (xf + 1.0);
            for (int c = 0; c < Channels; c++)
            {
                int i = (f * Channels + c) * 2;
                int s = (short)(dst[i] | (dst[i + 1] << 8));
                // Ramp from the sample that preceded the gap (zero after concealment or an underrun fade),
                // so an overflow trim splices without a step.
                double held = _lastFrame.Length > c ? _lastFrame[c] : 0;
                int v = (int)Math.Round(held + (s - held) * w);
                v = Math.Clamp(v, short.MinValue, short.MaxValue);
                dst[i] = (byte)v; dst[i + 1] = (byte)(v >> 8);
            }
        }
    }

    private void WriteConcealment(Span<byte> dst)
    {
        int fb = 2 * Channels;
        int frames = dst.Length / fb;
        double tau = Math.Max(1.0, 0.003 * SampleRate);
        for (int f = 0; f < frames; f++)
        {
            double g = Math.Exp(-f / tau);
            for (int c = 0; c < Channels; c++)
            {
                int v = (int)Math.Round((_lastFrame.Length > c ? _lastFrame[c] : 0) * g);
                v = Math.Clamp(v, short.MinValue, short.MaxValue);
                int i = (f * Channels + c) * 2;
                dst[i] = (byte)v; dst[i + 1] = (byte)(v >> 8);
            }
        }
        Array.Clear(_lastFrame);
        _fadeIn = true;
    }

    private void AccountLocked(long beforeFrames, int pulledFrames)
    {
        if (_next is null || pulledFrames <= 0) return;
        _clockFrames += pulledFrames;
        AdaptCalmLocked();
        if (DriftLock)
            _slipAcc = Math.Clamp(_slipAcc + pulledFrames * (_ppm / 1e6), -2, 2);
        long after = QueuedFramesLocked();
        _winFrames += pulledFrames;
        _winDepthArea += (beforeFrames + after) / 2.0 * pulledFrames;
        if (after < _winMinResidual) _winMinResidual = after;
        if (_winFrames < (long)WindowPackets * FramesPerPacket) return;
        _avgDepthMs = _winDepthArea / _winFrames * 1000.0 / SampleRate;
        _minResidualMs = _winMinResidual * 1000.0 / SampleRate;
        double pktMs = 1000.0 * FramesPerPacket / SampleRate;
        double targetMs = Target * pktMs;
        double bandMs = RenderPeriodMs > 0 ? RenderPeriodMs : pktMs;
        if (CatchUp)
        {
            // Hysteresis: only drop packets when the leftover stayed a full period above the target.
            long high = (long)Math.Round((targetMs + bandMs) / 1000.0 * SampleRate);
            long excess = _winMinResidual - high;
            _pendingDrop = excess >= FramesPerPacket ? (int)(excess / FramesPerPacket) : 0;
        }
        if (DriftLock)
        {
            double err = _avgDepthMs - targetMs;
            double dead = Math.Max(3.0, bandMs * 0.75);
            double dt = _winFrames / (double)SampleRate;
            if (Math.Abs(err) <= dead)
            {
                _offTargetSec = Math.Max(0, _offTargetSec - dt);
                _depthAtPeg = double.NaN;
                _integ *= 0.85;
                _ppm *= 0.80;
            }
            else
            {
                // Stay at ±150 ppm for a static backlog (overflow drains that).
                // Raise toward ±800 ppm only when the cap is pegged and depth is
                // still sliding away — the signature of a clock faster than 150 ppm.
                double cap = BasePpm + (MaxPpm - BasePpm) * Math.Clamp((_offTargetSec - 0.4) / 1.6, 0, 1);
                _integ = Math.Clamp(_integ + err * dt, -80, 80);
                double raw = 8.0 * err + 2.0 * _integ;
                _ppm = Math.Clamp(raw, -cap, cap);
                bool pegged = Math.Abs(raw) >= cap - 0.5;
                // A large standing pile is overflow's job. Raise the cap only when
                // we are pegged on a modest, still-growing error (clock-like).
                if (pegged && Math.Abs(err) < 25.0)
                {
                    if (double.IsNaN(_depthAtPeg)) _depthAtPeg = _avgDepthMs;
                    if (Math.Abs(_avgDepthMs - targetMs) >= Math.Abs(_depthAtPeg - targetMs) + 1.0)
                        _offTargetSec += dt;
                }
                else
                {
                    _depthAtPeg = double.NaN;
                    if (!pegged) _offTargetSec = Math.Max(0, _offTargetSec - dt);
                }
            }
        }
        ResetWindowLocked();
    }

    public byte[] Pull(int nbytes) { var b = new byte[nbytes]; Pull(b); return b; }

    public int DepthPackets { get { lock (_lock) return _count; } }

    /// <summary>Instantaneous queued audio (packets + partly played packet).</summary>
    public double DepthMs(int sampleRate = 48000)
    {
        lock (_lock) return QueuedFramesLocked() * 1000.0 / sampleRate;
    }

    /// <summary>Time-averaged queued audio over the last completed window; NaN before the first window.</summary>
    public double AvgDepthMs { get { lock (_lock) return _avgDepthMs; } }

    /// <summary>Smallest amount left right after a pull during the last window; NaN before the first window.</summary>
    public double MinResidualMs { get { lock (_lock) return _minResidualMs; } }

    public int PendingCatchUp { get { lock (_lock) return _pendingDrop; } }

    /// <summary>Current drift correction in parts-per-million (positive = consume faster): the resampler's when <see cref="Resample"/> is on, else the drift lock's.</summary>
    /// <summary>Frames queued for playback including the resampler's staged input (the drift controller's fill).</summary>
    public double FillFrames { get { lock (_lock) return FillFramesLocked(); } }
    /// <summary>Times the drift controller hit its hard-resync threshold (fill more than 20 ms off target).</summary>
    public long ResampleResyncs { get { lock (_lock) return _dc.Resyncs; } }
    public double DriftPpm { get { lock (_lock) return RsOn ? (_dc.Ratio - 1) * 1e6 : _ppm; } }
}
