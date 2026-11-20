using System.Diagnostics;

namespace CordMic.Core.Usb;

/// <summary>
/// Sends GET_PROTOCOL, the six identifying strings, and START with the timeouts in the plan (1 s each,
/// 5 s wall-clock budget). Pure of WinUSB; uses <see cref="IAoaChannel"/>.
/// </summary>
public sealed class AoaHandshaker
{
    private readonly IAoaChannel _channel;
    private readonly string _appVersion;
    private readonly string _installSerial;

    public AoaHandshaker(IAoaChannel channel, string appVersion, string installSerial)
    {
        _channel = channel;
        _appVersion = appVersion;
        _installSerial = installSerial;
    }

    public async Task<AoaHandshakeResult> RunAsync(CancellationToken cancellationToken)
    {
        var budget = new CancellationTokenSource(AoaProtocol.HandshakeBudgetMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        var setups = AoaProtocol.HandshakeSetups(_appVersion, _installSerial);
        int version = 0;
        try
        {
            // GET_PROTOCOL
            var get = setups[0];
            var buf = new byte[Math.Max(2, get.Data.Length)];
            int n;
            try { n = await _channel.TransferAsync(get, buf, linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            { return new(AoaHandshakeStatus.Timeout, 0, "get-protocol-timeout"); }
            if (n < 2 || !AoaProtocol.TryReadProtocolVersion(buf.AsSpan(0, n), out version))
                return new(AoaHandshakeStatus.ProtocolFailed, version, "get-protocol-failed");
            if (version < 1)
                return new(AoaHandshakeStatus.Unsupported, version, "protocol-unsupported");

            for (int i = 1; i < setups.Count - 1; i++)
            {
                var s = setups[i];
                try { await _channel.TransferAsync(s, s.Data, linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                { return new(AoaHandshakeStatus.Timeout, version, "send-string-timeout"); }
            }

            var start = setups[^1];
            try { await _channel.TransferAsync(start, Array.Empty<byte>(), linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            { return new(AoaHandshakeStatus.Timeout, version, "start-timeout"); }

            return new(AoaHandshakeStatus.Ok, version, AoaProtocol.DescribeResult(true, version, true));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(AoaHandshakeStatus.Cancelled, version, "cancelled");
        }
        catch (Exception ex)
        {
            return new(AoaHandshakeStatus.StartFailed, version, ex.Message);
        }
    }
}

/// <summary>Scripted control-transfer channel for tests.</summary>
public sealed class SimAoaChannel : IAoaChannel
{
    public int ProtocolVersion { get; set; } = 1;
    public int DelayMs { get; set; }
    public bool FailGetProtocol { get; set; }
    public bool FailStart { get; set; }
    public int Transfers { get; private set; }
    public List<byte> Requests { get; } = new();

    public async Task<int> TransferAsync(AoaProtocol.ControlSetup setup, byte[] buffer, CancellationToken cancellationToken)
    {
        Transfers++;
        Requests.Add(setup.Request);
        if (DelayMs > 0)
            await Task.Delay(DelayMs, cancellationToken).ConfigureAwait(false);
        if (setup.Request == AoaProtocol.GetProtocol)
        {
            if (FailGetProtocol) return 0;
            if (buffer.Length >= 2)
            {
                buffer[0] = (byte)(ProtocolVersion & 0xFF);
                buffer[1] = (byte)((ProtocolVersion >> 8) & 0xFF);
            }
            return 2;
        }
        if (setup.Request == AoaProtocol.Start && FailStart)
            throw new InvalidOperationException("start-failed");
        return setup.Data.Length;
    }
}
