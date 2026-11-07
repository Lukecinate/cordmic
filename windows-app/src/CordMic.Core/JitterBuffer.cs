namespace CordMic.Core;

public sealed class JitterStats
{
    public long Pushed, Played, Concealed, Late, Duplicates, OverflowDrops, Underruns, Resets;
    /// <summary>Packets removed by the soft catch-up (drift / backlog control), each spliced with a crossfade.</summary>
    public long CatchUpDrops;
    /// <summary>Single-sample skip/insert adjustments from the drift lock.</summary>
    public long SampleSlips;
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
public sealed class JitterBuffer
{
    private readonly object _lock = new();
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
    private int _slip;
    private int _slipCooldown;
    private double _avgDepthMs = double.NaN, _minResidualMs = double.NaN;

    public int FramesPerPacket { get; private set; }
    public int Channels { get; }
    public int SampleRate { get; }
    public int BytesPerPacket { get; private set; }
    public int Target { get; }
    public int MaxDepth { get; }
    /// <summary>Overflow trim target (usually one period above <see cref="Target"/>).</summary>
    public int HighWater { get; }
    /// <summary>Packet-drop catch-up. Off in production; kept for tests and emergency use.</summary>
    public bool CatchUp { get; set; }
    /// <summary>Skip or insert one PCM frame per pull when depth stays outside the hysteresis band.</summary>
    public bool DriftLock { get; set; }
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
        Target = targetPackets;
        MaxDepth = maxPackets ?? Math.Max(targetPackets * 3, targetPackets + 4);
        HighWater = highWaterPackets ?? targetPackets;
        if (HighWater < Target) throw new ArgumentOutOfRangeException(nameof(highWaterPackets));
        Alloc(BytesPerPacket);
    }

    private void Alloc(int bytes)
    {
        BytesPerPacket = bytes;
        // Span can briefly be MaxDepth+1 between insert and trim; the ring must be larger than that
        // so two live sequence numbers never share a slot.
        _cap = NextPow2(Math.Max(8, MaxDepth + 2));
        _mask = _cap - 1;
        _pool = new byte[_cap * bytes];
        _seq = new uint[_cap];
        _occ = new byte[_cap];
        _silence = new byte[bytes];
        _out = new byte[bytes];
        _keep = new byte[bytes];
        _count = 0;
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
        _slip = 0;
        _slipCooldown = 0;
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
            Stats.Played++;
            return true;
        }
        if (_count > 0)
        {
            _next = unchecked(cur + 1);
            Stats.Concealed++;
            _silence.AsSpan(0, BytesPerPacket).CopyTo(dst);
            return true;
        }
        Stats.Underruns++;
        _buffering = true;
        _pendingDrop = 0;
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

    /// <summary>Fills dst completely with PCM (silence-padded). Does not allocate.</summary>
    public void Pull(Span<byte> dst)
    {
        int pos = 0;
        lock (_lock)
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
                if (!CopyPacketLocked(_out, neededPackets)) break;
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
            AccountLocked(before, dst.Length / (2 * Channels));
        }
        if (pos < dst.Length) dst[pos..].Clear();
    }

    /// <summary>Skip or insert one frame without allocating. No-ops if the extra frame is not available.</summary>
    private void ApplySlipLocked(Span<byte> dst)
    {
        if (!DriftLock || _slip == 0) return;
        if (_slipCooldown > 0) return;
        int fb = 2 * Channels;
        if (fb <= 0 || dst.Length < fb) return;
        if (_slip > 0)
        {
            if (_keepLen - _keepOff >= fb)
            {
                _keepOff += fb;
                if (_keepOff >= _keepLen) { _keepLen = 0; _keepOff = 0; }
                Stats.SampleSlips++; _slipCooldown = FramesPerPacket;
                return;
            }
            if (!CopyPacketLocked(_out, 1)) return;
            int rem = BytesPerPacket - fb;
            if (rem > 0)
            {
                _out.AsSpan(fb, rem).CopyTo(_keep);
                _keepLen = rem;
                _keepOff = 0;
            }
            else { _keepLen = 0; _keepOff = 0; }
            Stats.SampleSlips++; _slipCooldown = FramesPerPacket;
            return;
        }
        // Insert: put the last played frame back at the front of the leftover so it plays again.
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
        Stats.SampleSlips++; _slipCooldown = FramesPerPacket;
    }

    private void AccountLocked(long beforeFrames, int pulledFrames)
    {
        if (_next is null || pulledFrames <= 0) return;
        if (_slipCooldown > 0) _slipCooldown = Math.Max(0, _slipCooldown - pulledFrames);
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
            if (_minResidualMs > targetMs + bandMs && _avgDepthMs > targetMs + bandMs)
                _slip = 1;
            else if (_avgDepthMs < targetMs - bandMs * 0.25 && _minResidualMs > 0)
                _slip = -1;
            else if (_avgDepthMs >= targetMs - bandMs * 0.1 && _avgDepthMs <= targetMs + bandMs * 0.5)
                _slip = 0;
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
}
