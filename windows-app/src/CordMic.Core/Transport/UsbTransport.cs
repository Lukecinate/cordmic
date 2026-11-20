using System.Diagnostics;
using System.Threading.Channels;

namespace CordMic.Core.Transport;

/// <summary>Reader and writer loops over an <see cref="IUsbPipe"/> (real or simulated).</summary>
public sealed class UsbTransport : ITransport
{
    private readonly IUsbPipe _pipe;
    private readonly UsbTransportOptions _opt;
    private readonly Channel<UsbFrame> _audio;
    private readonly Channel<UsbFrame> _control;
    private readonly Channel<UsbFrame> _write;
    private readonly FrameDecoder _decoder = new();
    private readonly TransportHealth _health = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _reader, _writer;
    private int _started;
    private uint _outSeq;
    private uint _expectSeq;
    private bool _haveSeq;
    private long _lastFrameStamp;
    private int _writeFails;

    public UsbTransport(IUsbPipe pipe, UsbTransportOptions? options = null)
    {
        _pipe = pipe;
        _opt = options ?? new UsbTransportOptions();
        _audio = Channel.CreateBounded<UsbFrame>(new BoundedChannelOptions(_opt.AudioChannelCap)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = true,
        });
        _control = Channel.CreateBounded<UsbFrame>(new BoundedChannelOptions(_opt.ControlChannelCap)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true,
        });
        _write = Channel.CreateBounded<UsbFrame>(new BoundedChannelOptions(_opt.WriterQueueCap)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        Frames = _audio.Reader;
        Control = _control.Reader;
    }

    public ChannelReader<UsbFrame> Frames { get; }
    public ChannelReader<UsbFrame> Control { get; }
    public TransportHealth Health => _health;
    public FrameDecoder Decoder => _decoder;
    public bool IsSuspect { get; private set; }
    public string InstanceId => _pipe.InstanceId;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        _lastFrameStamp = Stopwatch.GetTimestamp();
        _reader = Task.Run(() => ReadLoopAsync(_cts.Token));
        _writer = Task.Run(() => WriteLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async ValueTask SendAsync(UsbFrame frame, CancellationToken cancellationToken)
    {
        if (frame.Type == UsbFrameType.Keepalive)
        {
            // Coalesce: drop a queued keepalive if one is already waiting at the tail is hard
            // on a channel; the writer loop sends at most one keepalive per period instead.
        }
        using var wait = new CancellationTokenSource(_opt.WriteTimeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, wait.Token, _cts.Token);
        try
        {
            await _write.Writer.WriteAsync(frame, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (wait.IsCancellationRequested)
        {
            _health.ControlDropped++;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _write.Writer.TryComplete();
        _audio.Writer.TryComplete();
        _control.Writer.TryComplete();
        try { if (_reader is not null) await _reader.ConfigureAwait(false); } catch (Exception) { }
        try { if (_writer is not null) await _writer.ConfigureAwait(false); } catch (Exception) { }
        await _pipe.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        int timeouts = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (SilenceExpired()) { Lose("silence"); return; }
                int n;
                try
                {
                    using var to = new CancellationTokenSource(_opt.ReadTimeoutMs);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, to.Token);
                    n = await _pipe.ReadAsync(buf, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _health.ReaderTimeouts++;
                    timeouts++;
                    if (timeouts >= _opt.ConsecutiveReadTimeoutsToSuspect) IsSuspect = true;
                    if (SilenceExpired()) { Lose("silence"); return; }
                    continue;
                }
                catch (UsbPipeException ex)
                {
                    Lose(ex.Code);
                    return;
                }
                if (n <= 0) continue;
                timeouts = 0;
                _health.BytesIn += n;
                _decoder.Feed(buf.AsSpan(0, n));
                while (_decoder.TryDequeue(out var frame, out var status, out var ver))
                {
                    if (status == FrameDecodeStatus.UnsupportedVersion)
                    {
                        Lose("frame-version");
                        return;
                    }
                    if (status != FrameDecodeStatus.Valid || !_decoder.IsTrusted) continue;
                    NoteSeq(frame.Seq);
                    _health.FramesIn++;
                    _lastFrameStamp = Stopwatch.GetTimestamp();
                    if (frame.Type == UsbFrameType.Audio)
                    {
                        if (_audio.Reader.Count >= _opt.AudioChannelCap) _health.AudioDroppedOld++;
                        _audio.Writer.TryWrite(frame);
                    }
                    else if (frame.Type is UsbFrameType.Control or UsbFrameType.Bye or UsbFrameType.Keepalive)
                    {
                        if (frame.Type == UsbFrameType.Control && HelloPayload.TryParse(frame.Payload, out var hello))
                        {
                            if (hello.FrameVersion > FrameCodec.Version) { Lose("hello-version"); return; }
                            _health.HelloOk = true;
                        }
                        if (frame.Type == UsbFrameType.Bye) { Lose("bye"); return; }
                        if (!_control.Writer.TryWrite(frame))
                        {
                            // Wait briefly, then drop (plan: 100 ms).
                            using var w = new CancellationTokenSource(100);
                            try { await _control.Writer.WriteAsync(frame, w.Token).ConfigureAwait(false); }
                            catch (OperationCanceledException) { _health.ControlDropped++; }
                        }
                    }
                }
                _health.CrcErrors = _decoder.BadCrc;
                _health.HeaderErrors = _decoder.BadHeader;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            Lose("reader-exception");
        }
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        var buf = new byte[FrameCodec.MaxFrameSize];
        long lastSend = Stopwatch.GetTimestamp();
        try
        {
            var hello = new HelloPayload(FrameCodec.Version, (ushort)FrameCodec.MaxPayload, 0, _opt.AppVersion);
            await _write.Writer.WriteAsync(new UsbFrame(UsbFrameType.Control, 0, hello.Encode()), ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                UsbFrame frame;
                int waitMs = _opt.KeepaliveMs;
                try
                {
                    using var to = new CancellationTokenSource(waitMs);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, to.Token);
                    frame = await _write.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    frame = new UsbFrame(UsbFrameType.Keepalive, NextSeq(), Array.Empty<byte>());
                }
                frame = new UsbFrame(frame.Type, NextSeq(), frame.Payload);
                if (!FrameCodec.TryWrite(buf, frame.Type, frame.Seq, frame.Payload, out int n))
                    continue;
                try
                {
                    using var wto = new CancellationTokenSource(_opt.WriteTimeoutMs);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, wto.Token);
                    await _pipe.WriteAsync(buf.AsMemory(0, n), linked.Token).ConfigureAwait(false);
                    _health.FramesOut++;
                    _health.BytesOut += n;
                    _writeFails = 0;
                    lastSend = Stopwatch.GetTimestamp();
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _health.WriterTimeouts++;
                    _writeFails++;
                    if (_writeFails >= _opt.ConsecutiveWriteFailsToLose) { Lose("write-timeout"); return; }
                }
                catch (UsbPipeException ex)
                {
                    Lose(ex.Code);
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            Lose("writer-exception");
        }
    }

    private uint NextSeq() => _outSeq++;

    private void NoteSeq(uint seq)
    {
        if (!_haveSeq) { _expectSeq = seq + 1; _haveSeq = true; return; }
        if (seq != _expectSeq)
        {
            if (SerialMath.IsGap(seq, _expectSeq)) _health.SequenceGaps++;
        }
        _expectSeq = seq + 1;
    }

    private bool SilenceExpired()
    {
        long dt = Stopwatch.GetTimestamp() - _lastFrameStamp;
        double ms = dt * 1000.0 / Stopwatch.Frequency;
        return ms >= _opt.SilenceLostMs;
    }

    private void Lose(string reason)
    {
        _health.LinkLost = true;
        _health.LinkLostReason = reason;
        _cts.Cancel();
    }
}
