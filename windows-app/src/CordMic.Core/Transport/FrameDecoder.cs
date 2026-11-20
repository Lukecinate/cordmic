namespace CordMic.Core.Transport;

/// <summary>
/// Streaming decoder. On CRC or header error it skips one byte and looks for the next magic.
/// Two consecutive valid frames are required before the stream is trusted again. Never throws
/// on malformed input. The gather buffer is capped.
/// </summary>
public sealed class FrameDecoder
{
    public const int BufferCap = (FrameCodec.HeaderSize + FrameCodec.MaxPayload) * 2;

    private readonly byte[] _buf = new byte[BufferCap];
    private int _len;
    private int _goodStreak;
    private bool _trusted;
    private long _badHeader, _badCrc, _resyncs, _skipped, _unsupported, _unknown, _ok;

    public bool IsTrusted => _trusted;
    public long BadHeader => _badHeader;
    public long BadCrc => _badCrc;
    public long Resyncs => _resyncs;
    public long SkippedBytes => _skipped;
    public long UnsupportedVersion => _unsupported;
    public long UnknownType => _unknown;
    public long ValidFrames => _ok;

    public void Reset()
    {
        _len = 0;
        _goodStreak = 0;
        _trusted = false;
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        int o = 0;
        while (o < data.Length)
        {
            int space = BufferCap - _len;
            if (space == 0)
            {
                // Drop the oldest byte so a desync cannot grow without bound.
                Array.Copy(_buf, 1, _buf, 0, _len - 1);
                _len--;
                _skipped++;
                space = 1;
            }
            int n = Math.Min(space, data.Length - o);
            data.Slice(o, n).CopyTo(_buf.AsSpan(_len));
            _len += n;
            o += n;
        }
    }

    public bool TryDequeue(out UsbFrame frame, out FrameDecodeStatus status, out byte version)
    {
        frame = default;
        status = FrameDecodeStatus.NeedMore;
        version = 0;
        while (_len > 0)
        {
            var r = FrameCodec.TryDecode(_buf.AsSpan(0, _len));
            status = r.Status;
            version = r.Version;
            if (r.Status == FrameDecodeStatus.NeedMore) return false;
            if (r.Consumed <= 0) return false;
            if (r.Status is FrameDecodeStatus.BadHeader or FrameDecodeStatus.BadCrc)
            {
                if (r.Status == FrameDecodeStatus.BadHeader) _badHeader++;
                else _badCrc++;
                Skip(r.Consumed);
                LoseTrust();
                continue;
            }
            // A complete, CRC-valid frame (possibly unknown type or other version).
            Skip(r.Consumed);
            if (r.Status == FrameDecodeStatus.UnsupportedVersion)
            {
                _unsupported++;
                frame = r.Frame!.Value;
                LoseTrust();
                return true;
            }
            if (r.Status == FrameDecodeStatus.UnknownType)
            {
                _unknown++;
                NoteGood();
                // Forward compatible: skip by length, do not surface as a typed frame.
                continue;
            }
            _ok++;
            NoteGood();
            frame = r.Frame!.Value;
            status = FrameDecodeStatus.Valid;
            return true;
        }
        status = FrameDecodeStatus.NeedMore;
        return false;
    }

    private void Skip(int n)
    {
        if (n >= _len) { _len = 0; return; }
        Array.Copy(_buf, n, _buf, 0, _len - n);
        _len -= n;
        if (n == 1) _skipped++;
    }

    private void LoseTrust()
    {
        if (_trusted || _goodStreak > 0) _resyncs++;
        _goodStreak = 0;
        _trusted = false;
    }

    private void NoteGood()
    {
        if (_goodStreak < 2) _goodStreak++;
        if (_goodStreak >= 2) _trusted = true;
    }
}
