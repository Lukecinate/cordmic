namespace CordMic.Core.Transport;

/// <summary>
/// Scriptable USB pipe for CI. Faults are deterministic given a seed. The peer (phone)
/// injects bytes with <see cref="PeerWrite"/>; the host reads them and writes replies.
/// </summary>
public sealed class SimUsbPipe : IUsbPipe
{
    private readonly object _gate = new();
    private readonly List<byte> _inbound = new();
    private readonly List<byte> _outbound = new();
    private readonly SemaphoreSlim _data = new(0, int.MaxValue);
    private bool _disposed, _disconnected, _stalled, _hangReads;
    private int _maxRead = int.MaxValue;
    private int _readDelayMs;
    private int _writeDelayMs;
    private int _bitFlipsRemaining;
    private readonly Random _rng;

    public SimUsbPipe(string instanceId = "sim-0", int? seed = null)
    {
        InstanceId = instanceId;
        _rng = seed is int s ? new Random(s) : new Random(1);
    }

    public string InstanceId { get; private set; }
    public int BytesWritten { get { lock (_gate) return _outbound.Count; } }
    public int BytesQueued { get { lock (_gate) return _inbound.Count; } }

    public void SetMaxRead(int bytes) => _maxRead = Math.Max(1, bytes);
    public void SetReadDelayMs(int ms) => _readDelayMs = Math.Max(0, ms);
    public void SetWriteDelayMs(int ms) => _writeDelayMs = Math.Max(0, ms);
    public void StallNext() => _stalled = true;
    public void Disconnect() { _disconnected = true; TryRelease(); }
    public void HangReads(bool hang = true) => _hangReads = hang;
    public void FlipNextReads(int count) => _bitFlipsRemaining = Math.Max(0, count);
    public void Reenumerate(string newInstanceId)
    {
        Disconnect();
        lock (_gate)
        {
            _inbound.Clear();
            _disconnected = false;
            _stalled = false;
            InstanceId = newInstanceId;
        }
    }

    public void PeerWrite(ReadOnlySpan<byte> data)
    {
        if (_disconnected || _disposed) return;
        bool signal;
        lock (_gate)
        {
            _inbound.AddRange(data.ToArray());
            signal = _inbound.Count > 0;
        }
        if (signal) TryRelease();
    }

    public void PeerWriteFrame(UsbFrameType type, uint seq, ReadOnlySpan<byte> payload) =>
        PeerWrite(FrameCodec.Encode(type, seq, payload));

    public byte[] TakeWritten()
    {
        lock (_gate)
        {
            var a = _outbound.ToArray();
            _outbound.Clear();
            return a;
        }
    }

    public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_disconnected || _disposed) throw UsbPipeException.Disconnected();
        if (_stalled)
        {
            _stalled = false;
            throw UsbPipeException.Stall();
        }
        if (_hangReads)
        {
            await Task.Delay(-1, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        if (_readDelayMs > 0)
            await Task.Delay(_readDelayMs, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disconnected || _disposed) throw UsbPipeException.Disconnected();
            lock (_gate)
            {
                if (_inbound.Count > 0)
                {
                    int n = Math.Min(buffer.Length, Math.Min(_maxRead, _inbound.Count));
                    for (int i = 0; i < n; i++) buffer.Span[i] = _inbound[i];
                    _inbound.RemoveRange(0, n);
                    if (_bitFlipsRemaining > 0 && n > 0)
                    {
                        int i = _rng.Next(n);
                        buffer.Span[i] ^= 0x01;
                        _bitFlipsRemaining--;
                    }
                    return n;
                }
            }
            await _data.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_disconnected || _disposed) throw UsbPipeException.Disconnected();
        if (_stalled)
        {
            _stalled = false;
            throw UsbPipeException.Stall();
        }
        if (_writeDelayMs > 0)
            await Task.Delay(_writeDelayMs, cancellationToken).ConfigureAwait(false);
        lock (_gate) _outbound.AddRange(buffer.ToArray());
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        TryRelease();
        return ValueTask.CompletedTask;
    }

    private void TryRelease()
    {
        try { _data.Release(); } catch (SemaphoreFullException) { }
    }
}
