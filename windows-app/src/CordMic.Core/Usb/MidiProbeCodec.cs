namespace CordMic.Core.Usb;

/// <summary>
/// SysEx test pattern for the M1 MIDI feasibility probe. Uses the development SysEx ID 0x7D
/// (not a registered manufacturer ID). Production Auto will not use this path unless G-M1
/// enables route 1b.
/// </summary>
public static class MidiProbeCodec
{
    public const byte StatusSysEx = 0xF0;
    public const byte StatusEnd = 0xF7;
    public const byte DevId = 0x7D;
    public const byte KindPattern = 0x01;
    public const int HeaderBytes = 2 /* F0 7D */ + 1 /* kind */ + 5 /* seq 5*7bit */ + 5 /* time 5*7bit */ + 2 /* len */;
    public const int MaxPackedPayload = 256;

    /// <summary>Pack 8-bit bytes into 7-bit MIDI data bytes (high bits collected every 7 bytes).</summary>
    public static byte[] Pack7(ReadOnlySpan<byte> src)
    {
        if (src.Length > MaxPackedPayload) throw new ArgumentOutOfRangeException(nameof(src));
        var dst = new List<byte>(src.Length + (src.Length + 6) / 7);
        int i = 0;
        while (i < src.Length)
        {
            int n = Math.Min(7, src.Length - i);
            byte hi = 0;
            for (int b = 0; b < n; b++)
                if ((src[i + b] & 0x80) != 0) hi |= (byte)(1 << b);
            dst.Add(hi);
            for (int b = 0; b < n; b++) dst.Add((byte)(src[i + b] & 0x7F));
            i += n;
        }
        return dst.ToArray();
    }

    public static byte[] Unpack7(ReadOnlySpan<byte> src, int rawLen)
    {
        if (rawLen < 0 || rawLen > MaxPackedPayload) throw new ArgumentOutOfRangeException(nameof(rawLen));
        var dst = new byte[rawLen];
        int si = 0, di = 0;
        while (di < rawLen)
        {
            if (si >= src.Length) throw new FormatException("truncated 7-bit payload");
            byte hi = src[si++];
            int n = Math.Min(7, rawLen - di);
            for (int b = 0; b < n; b++)
            {
                if (si >= src.Length) throw new FormatException("truncated 7-bit payload");
                byte v = src[si++];
                if (v > 0x7F) throw new FormatException("high bit set in data");
                if ((hi & (1 << b)) != 0) v |= 0x80;
                dst[di++] = v;
            }
        }
        return dst;
    }

    public static byte[] Encode(uint seq, uint timeMs, ReadOnlySpan<byte> payload)
    {
        var packed = Pack7(payload);
        if (packed.Length > 400) throw new ArgumentOutOfRangeException(nameof(payload));
        var msg = new List<byte>(HeaderBytes + packed.Length + 1)
        {
            StatusSysEx,
            DevId,
            KindPattern,
        };
        WriteU32_7(msg, seq);
        WriteU32_7(msg, timeMs);
        msg.Add((byte)(payload.Length & 0x7F));
        msg.Add((byte)((payload.Length >> 7) & 0x7F));
        msg.AddRange(packed);
        msg.Add(StatusEnd);
        return msg.ToArray();
    }

    public static bool TryDecode(ReadOnlySpan<byte> msg, out uint seq, out uint timeMs, out byte[] payload, out string? error)
    {
        seq = 0; timeMs = 0; payload = Array.Empty<byte>(); error = null;
        if (msg.Length < HeaderBytes + 1) { error = "too-short"; return false; }
        if (msg[0] != StatusSysEx) { error = "not-sysex"; return false; }
        if (msg[^1] != StatusEnd) { error = "truncated"; return false; }
        if (msg[1] != DevId) { error = "foreign-id"; return false; }
        if (msg[2] != KindPattern) { error = "unknown-kind"; return false; }
        int o = 3;
        seq = ReadU32_7(msg, ref o);
        timeMs = ReadU32_7(msg, ref o);
        if (o + 2 >= msg.Length) { error = "truncated"; return false; }
        int len = msg[o] | (msg[o + 1] << 7);
        o += 2;
        if (len > MaxPackedPayload) { error = "too-long"; return false; }
        var packed = msg[o..^1];
        try { payload = Unpack7(packed, len); }
        catch (Exception) { error = "truncated"; return false; }
        return true;
    }

    public static MidiProbeStats Measure(IReadOnlyList<(uint Seq, uint TimeMs, int PayloadLen, string? Error)> received, uint firstSeq, int expectedCount)
    {
        var st = new MidiProbeStats { Expected = expectedCount };
        var seen = new HashSet<uint>();
        int? last = null;
        long lastTime = -1;
        var gaps = new List<double>();
        foreach (var r in received)
        {
            if (r.Error == "truncated") { st.Truncated++; continue; }
            if (r.Error is not null) { st.Invalid++; continue; }
            st.Received++;
            st.Bytes += r.PayloadLen;
            if (!seen.Add(r.Seq)) { st.Duplicates++; continue; }
            if (last is int l && r.Seq != (uint)(l + 1) && r.Seq != l) st.ReorderedOrLost++;
            last = (int)r.Seq;
            if (lastTime >= 0)
            {
                double gap = (int)r.TimeMs - lastTime;
                if (gap < 0) gap += 1L << 32; // 32-bit wrap, not a wall clock
                gaps.Add(gap);
            }
            lastTime = r.TimeMs;
        }
        int unique = seen.Count;
        st.Lost = Math.Max(0, expectedCount - unique);
        if (gaps.Count > 0)
        {
            gaps.Sort();
            st.P99GapMs = Percentile(gaps, 0.99);
            st.MedianGapMs = Percentile(gaps, 0.50);
        }
        return st;
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        double idx = p * (sorted.Count - 1);
        int lo = (int)idx;
        int hi = Math.Min(lo + 1, sorted.Count - 1);
        double f = idx - lo;
        return sorted[lo] * (1 - f) + sorted[hi] * f;
    }

    private static void WriteU32_7(List<byte> dst, uint v)
    {
        for (int i = 0; i < 5; i++)
        {
            dst.Add((byte)(v & 0x7F));
            v >>= 7;
        }
    }

    private static uint ReadU32_7(ReadOnlySpan<byte> msg, ref int o)
    {
        uint v = 0;
        for (int i = 0; i < 5; i++)
        {
            if (o >= msg.Length) throw new FormatException("truncated");
            v |= (uint)(msg[o++] & 0x7F) << (7 * i);
        }
        return v;
    }
}

public sealed class MidiProbeStats
{
    public int Expected { get; set; }
    public int Received { get; set; }
    public int Lost { get; set; }
    public int Truncated { get; set; }
    public int Duplicates { get; set; }
    public int ReorderedOrLost { get; set; }
    public int Invalid { get; set; }
    public long Bytes { get; set; }
    public double MedianGapMs { get; set; }
    public double P99GapMs { get; set; }

    /// <summary>Gate M-2 at the Opus-rate 10-minute run: ≥ 200 kbit/s, 0 lost, 0 truncated.</summary>
    public bool MeetsThroughputGate(double durationSec, double minKbit = 200)
    {
        if (durationSec <= 0) return false;
        double kbit = (Bytes * 8.0) / 1000.0 / durationSec;
        return kbit >= minKbit && Lost == 0 && Truncated == 0;
    }

    /// <summary>Gate M-4: p99 inter-arrival ≤ 10 ms.</summary>
    public bool MeetsJitterGate(double p99LimitMs = 10) => P99GapMs <= p99LimitMs;
}
