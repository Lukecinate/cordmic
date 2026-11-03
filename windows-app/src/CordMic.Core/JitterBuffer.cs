namespace CordMic.Core;

public sealed class JitterStats
{
    public long Pushed, Played, Concealed, Late, Duplicates, OverflowDrops, Underruns, Resets;
}

/// <summary>
/// Port of windows-receiver/cordmic_receiver/jitter.py (same behaviour, same tests).
/// Push from the network thread, Pull from the audio callback. Pull never allocates.
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

    public int FramesPerPacket { get; private set; }
    public int Channels { get; }
    public int BytesPerPacket { get; private set; }
    public int Target { get; }
    public int MaxDepth { get; }
    public JitterStats Stats { get; } = new();

    public JitterBuffer(int framesPerPacket, int channels = 1, int targetPackets = 2, int? maxPackets = null)
    {
        if (targetPackets < 1) throw new ArgumentOutOfRangeException(nameof(targetPackets));
        FramesPerPacket = framesPerPacket;
        Channels = channels;
        BytesPerPacket = framesPerPacket * channels * 2;
        Target = targetPackets;
        MaxDepth = maxPackets ?? Math.Max(targetPackets * 3, targetPackets + 4);
        _silence = new byte[BytesPerPacket];
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
        Stats.Resets++;
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
    }

    private byte[]? PopPacketLocked()
    {
        if (_next is null) return null;
        if (_buffering)
        {
            if (SpanLocked() >= Target) _buffering = false;
            else return null;
        }
        if (_buf.Remove(_next.Value, out var pkt))
        {
            _next = unchecked(_next.Value + 1);
            Stats.Played++;
            return pkt;
        }
        if (_buf.Count > 0)
        {
            _next = unchecked(_next.Value + 1);
            Stats.Concealed++;
            return _silence;
        }
        Stats.Underruns++;
        _buffering = true;
        return null;
    }

    /// <summary>Fills dst completely with PCM (silence-padded).</summary>
    public void Pull(Span<byte> dst)
    {
        int pos = 0;
        lock (_lock)
        {
            if (_leftover != null)
            {
                int take = Math.Min(dst.Length, _leftover.Length - _leftoverOff);
                _leftover.AsSpan(_leftoverOff, take).CopyTo(dst);
                pos += take; _leftoverOff += take;
                if (_leftoverOff >= _leftover.Length) { _leftover = null; _leftoverOff = 0; }
            }
            while (pos < dst.Length)
            {
                var pkt = PopPacketLocked();
                if (pkt is null) break;
                int need = dst.Length - pos;
                int take = Math.Min(need, pkt.Length);
                pkt.AsSpan(0, take).CopyTo(dst[pos..]);
                pos += take;
                if (pkt.Length > need) { _leftover = pkt; _leftoverOff = need; }
            }
        }
        if (pos < dst.Length) dst[pos..].Clear();
    }

    public byte[] Pull(int nbytes) { var b = new byte[nbytes]; Pull(b); return b; }

    public int DepthPackets { get { lock (_lock) return _buf.Count; } }

    public double DepthMs(int sampleRate = 48000)
    {
        lock (_lock)
        {
            long frames = (long)_buf.Count * FramesPerPacket +
                          (_leftover is null ? 0 : (_leftover.Length - _leftoverOff) / (2 * Channels));
            return frames * 1000.0 / sampleRate;
        }
    }
}
