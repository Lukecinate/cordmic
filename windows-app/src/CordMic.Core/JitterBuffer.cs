namespace CordMic.Core;

public sealed class JitterStats
{
    public long Pushed, Played, Concealed, Late, Duplicates, OverflowDrops, Underruns, Resets;
    /// <summary>Packets removed by the soft catch-up (drift / backlog control), each spliced with a crossfade.</summary>
    public long CatchUpDrops;
}

/// <summary>
/// Port of windows-receiver/cordmic_receiver/jitter.py (same behaviour, same tests).
/// Push from the network thread, Pull from the audio callback. Pull never allocates.
///
/// Latency control (0.4.2):
/// * Playback (re)starts once max(target, packets needed by the current pull) are buffered, so a 10 ms
///   output period does not underrun straight after a 5 ms prebuffer.
/// * Every ~0.5 s of playout the buffer looks at the smallest amount of audio that was still queued right
///   after a pull. If even that minimum is above the target, the excess is a standing backlog (a burst
///   that never drained, or the phone's clock running slightly faster than the PC's). It is removed one
///   packet at a time by crossfading two consecutive packets into one, so delay returns to the target
///   instead of floating between target and the hard limit.
/// * The hard limit (<see cref="MaxDepth"/>) still drops the oldest packets at once for large bursts.
/// </summary>
public sealed class JitterBuffer
{
    private readonly object _lock = new();
    private readonly Dictionary<uint, byte[]> _buf = new();
    private uint? _next;
    private bool _buffering = true;
    private byte[]? _leftover;
    private int _leftoverOff;
    private byte[] _silence;
    private byte[] _splice;

    // catch-up window state (all in frames)
    private long _winFrames;
    private long _winMinResidual = long.MaxValue;
    private double _winDepthArea;
    private int _pendingDrop;
    private double _avgDepthMs = double.NaN, _minResidualMs = double.NaN;

    public int FramesPerPacket { get; private set; }
    public int Channels { get; }
    public int SampleRate { get; }
    public int BytesPerPacket { get; private set; }
    public int Target { get; }
    public int MaxDepth { get; }
    /// <summary>Soft catch-up on/off (on by default).</summary>
    public bool CatchUp { get; set; } = true;
    /// <summary>Length of one catch-up decision window, in packets of playout.</summary>
    public int WindowPackets { get; set; } = 100;
    public JitterStats Stats { get; } = new();

    public JitterBuffer(int framesPerPacket, int channels = 1, int targetPackets = 2, int? maxPackets = null, int sampleRate = 48000)
    {
        if (targetPackets < 1) throw new ArgumentOutOfRangeException(nameof(targetPackets));
        FramesPerPacket = framesPerPacket;
        Channels = channels;
        SampleRate = sampleRate;
        BytesPerPacket = framesPerPacket * channels * 2;
        Target = targetPackets;
        MaxDepth = maxPackets ?? Math.Max(targetPackets * 3, targetPackets + 4);
        _silence = new byte[BytesPerPacket];
        _splice = new byte[BytesPerPacket];
    }

    public void Push(uint seq, ReadOnlySpan<byte> payload) => Push(seq, payload.ToArray());

    public void Push(uint seq, byte[] payload)
    {
        lock (_lock)
        {
            if (payload.Length != BytesPerPacket)
            {
                FramesPerPacket = payload.Length / (2 * Channels);
                BytesPerPacket = payload.Length;
                _silence = new byte[BytesPerPacket];
                _splice = new byte[BytesPerPacket];
                ResetLocked();
            }
            Stats.Pushed++;
            _next ??= seq;
            long d = Protocol.SeqDiff(seq, _next.Value);
            if (d < 0)
            {
                if (d < -1000) { ResetLocked(); _next = seq; }  // sender restarted
                else { Stats.Late++; return; }
            }
            else if (d > 5000) { ResetLocked(); _next = seq; }  // huge jump: sender restarted
            if (_buf.ContainsKey(seq)) { Stats.Duplicates++; return; }
            _buf[seq] = payload;
            TrimLocked();
        }
    }

    private void ResetLocked()
    {
        _buf.Clear();
        _next = null;
        _buffering = true;
        _leftover = null; _leftoverOff = 0;
        _pendingDrop = 0;
        ResetWindowLocked();
        Stats.Resets++;
    }

    private void ResetWindowLocked()
    {
        _winFrames = 0; _winMinResidual = long.MaxValue; _winDepthArea = 0;
    }

    private int SpanLocked()
    {
        if (_buf.Count == 0 || _next is null) return 0;
        long newest = long.MinValue;
        foreach (var s in _buf.Keys) newest = Math.Max(newest, Protocol.SeqDiff(s, _next.Value));
        return (int)newest + 1;
    }

    private void TrimLocked()
    {
        int span = SpanLocked();
        if (span <= MaxDepth) return;
        int drop = span - Target;
        for (int i = 0; i < drop; i++)
        {
            _buf.Remove(_next!.Value);
            Stats.OverflowDrops++;  // counts skipped (missing) slots too, like Python
            _next = unchecked(_next.Value + 1);
        }
        _pendingDrop = 0;
    }

    private byte[]? PopPacketLocked(int neededPackets)
    {
        if (_next is null) return null;
        if (_buffering)
        {
            if (SpanLocked() >= Math.Max(Target, neededPackets)) _buffering = false;
            else return null;
        }
        uint cur = _next.Value;
        if (_buf.TryGetValue(cur, out var pkt))
        {
            uint nxt = unchecked(cur + 1);
            if (_pendingDrop > 0 && _buf.TryGetValue(nxt, out var b) && b.Length == pkt.Length)
            {
                // Catch-up: play two consecutive packets as one, crossfading a -> b. Starts exactly where
                // the previous output ended (a[0]) and ends where the following packet starts (b[last]).
                CrossfadeInto(pkt, b, _splice, Channels);
                _buf.Remove(cur); _buf.Remove(nxt);
                _next = unchecked(cur + 2);
                _pendingDrop--;
                Stats.CatchUpDrops++;
                Stats.Played++;
                return _splice;
            }
            _buf.Remove(cur);
            _next = nxt;
            Stats.Played++;
            return pkt;
        }
        if (_buf.Count > 0)
        {
            _next = unchecked(cur + 1);
            Stats.Concealed++;
            return _silence;
        }
        Stats.Underruns++;
        _buffering = true;
        _pendingDrop = 0;
        return null;
    }

    /// <summary>Linear crossfade of two equally long s16 interleaved packets into dst.</summary>
    public static void CrossfadeInto(byte[] a, byte[] b, byte[] dst, int channels)
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
        (long)_buf.Count * FramesPerPacket + (_leftover is null ? 0 : (_leftover.Length - _leftoverOff) / (2 * Channels));

    /// <summary>Fills dst completely with PCM (silence-padded).</summary>
    public void Pull(Span<byte> dst)
    {
        int pos = 0;
        lock (_lock)
        {
            long before = QueuedFramesLocked();
            if (_leftover != null)
            {
                int take = Math.Min(dst.Length, _leftover.Length - _leftoverOff);
                _leftover.AsSpan(_leftoverOff, take).CopyTo(dst);
                pos += take; _leftoverOff += take;
                if (_leftoverOff >= _leftover.Length) { _leftover = null; _leftoverOff = 0; }
            }
            while (pos < dst.Length)
            {
                int need = dst.Length - pos;
                var pkt = PopPacketLocked((need + BytesPerPacket - 1) / BytesPerPacket);
                if (pkt is null) break;
                int take = Math.Min(need, pkt.Length);
                pkt.AsSpan(0, take).CopyTo(dst[pos..]);
                pos += take;
                if (pkt.Length > need) { _leftover = pkt; _leftoverOff = need; }
            }
            AccountLocked(before, dst.Length / (2 * Channels));
        }
        if (pos < dst.Length) dst[pos..].Clear();
    }

    private void AccountLocked(long beforeFrames, int pulledFrames)
    {
        if (_next is null || pulledFrames <= 0) return;
        long after = QueuedFramesLocked();
        _winFrames += pulledFrames;
        _winDepthArea += (beforeFrames + after) / 2.0 * pulledFrames;
        if (after < _winMinResidual) _winMinResidual = after;
        if (_winFrames < (long)WindowPackets * FramesPerPacket) return;
        _avgDepthMs = _winDepthArea / _winFrames * 1000.0 / SampleRate;
        _minResidualMs = _winMinResidual * 1000.0 / SampleRate;
        if (CatchUp)
        {
            long excess = _winMinResidual - (long)Target * FramesPerPacket;
            _pendingDrop = excess >= FramesPerPacket ? (int)(excess / FramesPerPacket) : 0;
        }
        ResetWindowLocked();
    }

    public byte[] Pull(int nbytes) { var b = new byte[nbytes]; Pull(b); return b; }

    public int DepthPackets { get { lock (_lock) return _buf.Count; } }

    /// <summary>Instantaneous queued audio (packets + partly played packet).</summary>
    public double DepthMs(int sampleRate = 48000)
    {
        lock (_lock) return QueuedFramesLocked() * 1000.0 / sampleRate;
    }

    /// <summary>Time-averaged queued audio over the last completed window (≈ average wait in the buffer); NaN before the first window.</summary>
    public double AvgDepthMs { get { lock (_lock) return _avgDepthMs; } }

    /// <summary>Smallest amount left right after a pull during the last window (safety margin actually used); NaN before the first window.</summary>
    public double MinResidualMs { get { lock (_lock) return _minResidualMs; } }

    public int PendingCatchUp { get { lock (_lock) return _pendingDrop; } }
}
