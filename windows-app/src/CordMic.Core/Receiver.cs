using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace CordMic.Core;

public sealed record StatsSnapshot(
    string? Peer, long Received, long Expected, long Lost, double LossPct, long Reordered, long Late,
    long Concealed, long Underruns, long OverflowDrops, double DepthMs, double? RttMs, double? RttMinMs,
    double ArrivalJitterMs, double PacketMs, double LevelDbfs, double SecondsSinceAudio,
    double BufferAvgMs = double.NaN, double BufferMinMs = double.NaN, long CatchUpDrops = 0)
{
    /// <summary>Jitter-buffer delay to show: the time-averaged depth once a window completed, else the instantaneous depth.</summary>
    public double BufferMs => double.IsNaN(BufferAvgMs) ? DepthMs : BufferAvgMs;

    public string Line() =>
        $"peer={Peer ?? "-"} rx={Received} lost={Lost} ({LossPct:F2}%) reord={Reordered} late={Late} " +
        $"plc={Concealed} underrun={Underruns} ovf={OverflowDrops} buf={DepthMs:F1}ms " +
        $"jitter={ArrivalJitterMs:F2}ms rtt={(RttMs is { } r ? r.ToString("F2") : "-")}ms " +
        $"(min {(RttMinMs is { } m ? m.ToString("F2") : "-")}) pkt={PacketMs:F1}ms level={LevelDbfs:F0}dBFS " +
        $"bufavg={(double.IsNaN(BufferAvgMs) ? "-" : BufferAvgMs.ToString("F1"))}ms " +
        $"bufmin={(double.IsNaN(BufferMinMs) ? "-" : BufferMinMs.ToString("F1"))}ms catchup={CatchUpDrops}";
}

/// <summary>
/// UDP receiver: answers DISCOVER with HELLO, pings the phone for RTT, feeds AUDIO into the jitter buffer.
/// Since 0.4.2 RTT pings go out from a second socket on an ephemeral port with its own thread: the phone
/// answers to the sender's address, so PONGs never queue behind audio packets on the main socket and the
/// RTT shows the link, not the PC's audio receive path.
/// Port of windows-receiver/cordmic_receiver/server.py plus proactive HELLO probes to the phone
/// (the USB-tethering gateway), so discovery works even if Windows drops inbound broadcasts.
/// </summary>
public sealed class Receiver : IDisposable
{
    private readonly object _lock = new();
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly double _pingInterval, _peerTimeout;
    private readonly IPAddress _bind;
    private Socket? _sock, _pingSock;
    private Thread? _rx, _ping;
    private volatile bool _stop;
    private volatile JitterBuffer _jb;
    private double _jitterMs;
    private long _peerLastTicks;
    private long _lastAudioTicks;

    // loss / reorder accounting (RTP style)
    private uint? _firstSeq, _maxSeq;
    private long _received, _reordered;
    private long? _transitPrev;
    private double _jitterUs;
    private uint _pingSeq;
    private double? _rttMs, _rttMin;
    private double _levelDb = -120;
    private IPEndPoint[] _probes = Array.Empty<IPEndPoint>();

    public int Port { get; private set; }
    public int FramesPerPacket { get; private set; }
    public IPEndPoint? Peer { get; private set; }
    public JitterBuffer Jitter => _jb;
    public event Action<IPEndPoint?>? PeerChanged;
    public Action<string>? Log { get; set; }
    /// <summary>Called once on the receive thread (the Windows app raises it to MMCSS "Pro Audio").</summary>
    public Action? RxThreadInit { get; set; }

    public Receiver(int port = Protocol.DefaultPort, string bind = "0.0.0.0", double jitterMs = 10,
                    int sampleRate = 48000, int channels = 1, int framesPerPacket = 240,
                    double pingInterval = 0.5, double peerTimeout = 3.0)
    {
        Port = port;
        _bind = IPAddress.Parse(bind);
        _sampleRate = sampleRate;
        _channels = channels;
        _pingInterval = pingInterval;
        _peerTimeout = peerTimeout;
        _jitterMs = jitterMs;
        _jb = MakeJb(framesPerPacket);
    }

    public static ulong NowUs() => (ulong)(Stopwatch.GetTimestamp() * 1_000_000.0 / Stopwatch.Frequency);
    private static double Seconds(long ticks) => ticks * 1.0 / Stopwatch.Frequency;

    private JitterBuffer MakeJb(int fpp)
    {
        double pktMs = fpp * 1000.0 / _sampleRate;
        int target = Math.Max(1, (int)Math.Round(_jitterMs / pktMs, MidpointRounding.ToEven));
        FramesPerPacket = fpp;
        return new JitterBuffer(fpp, _channels, target, sampleRate: _sampleRate);
    }

    public double JitterMs => _jitterMs;

    /// <summary>Change the jitter-buffer target at runtime (rebuilds the buffer; a few ms glitch).</summary>
    public void SetJitterMs(double ms)
    {
        _jitterMs = ms;
        _jb = MakeJb(FramesPerPacket);
        Log?.Invoke($"jitter target {ms:F0} ms = {_jb.Target} pkt");
    }

    /// <summary>Endpoints (phone IP : port) that get a HELLO every second while no phone is connected.</summary>
    public void SetProbeTargets(IEnumerable<IPEndPoint> targets) => _probes = targets.ToArray();

    public void Start()
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try { s.ReceiveBufferSize = 1 << 20; } catch (SocketException) { }
        if (OperatingSystem.IsWindows())
        {
            // SIO_UDP_CONNRESET = 0x9800000C: ignore ICMP port-unreachable resets.
            try { s.IOControl(unchecked((int)0x9800000C), new byte[4], null); } catch (Exception) { }
        }
        s.EnableBroadcast = true;
        try { s.Bind(new IPEndPoint(_bind, Port)); } catch { s.Dispose(); throw; }
        s.ReceiveTimeout = 200;
        Port = ((IPEndPoint)s.LocalEndPoint!).Port;
        _sock = s;
        try
        {
            var ps = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            if (OperatingSystem.IsWindows())
                try { ps.IOControl(unchecked((int)0x9800000C), new byte[4], null); } catch (Exception) { }
            ps.Bind(new IPEndPoint(_bind, 0));
            ps.ReceiveTimeout = 50;
            _pingSock = ps;
        }
        catch (Exception e) { _pingSock = null; Log?.Invoke($"ping socket unavailable, using main socket: {e.Message}"); }
        _stop = false;
        _rx = new Thread(RxLoop) { Name = "cordmic-rx", IsBackground = true, Priority = ThreadPriority.Highest };
        _ping = new Thread(PingLoop) { Name = "cordmic-ping", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _rx.Start(); _ping.Start();
        Log?.Invoke($"listening on udp {_bind}:{Port} (jitter target {_jitterMs:F1} ms = {_jb.Target} pkt)");
    }

    public void Stop()
    {
        _stop = true;
        _rx?.Join(1000); _ping?.Join(1000);
        _sock?.Close(); _sock = null;
        _pingSock?.Close(); _pingSock = null;
    }

    public void Dispose() => Stop();

    private void Send(byte[] data, EndPoint to)
    {
        try { _sock?.SendTo(data, to); } catch (Exception e) { Log?.Invoke($"send failed: {e.Message}"); }
    }

    private void SetPeer(IPEndPoint addr)
    {
        if (!addr.Equals(Peer))
        {
            Log?.Invoke($"phone connected: {addr}");
            Peer = addr;
            PeerChanged?.Invoke(addr);
        }
        _peerLastTicks = Stopwatch.GetTimestamp();
    }

    private void ClearPeer(string why)
    {
        if (Peer is null) return;
        Log?.Invoke($"phone {Peer} {why}");
        Peer = null;
        PeerChanged?.Invoke(null);
    }

    private void RxLoop()
    {
        try { RxThreadInit?.Invoke(); } catch (Exception) { }
        var buf = new byte[4096];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (!_stop)
        {
            int n;
            try
            {
                n = _sock!.ReceiveFrom(buf, ref from);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
            {
                CheckPeerTimeout(); continue;
            }
            catch (SocketException) { CheckPeerTimeout(); continue; } // WSAECONNRESET etc.
            catch (ObjectDisposedException) { break; }
            ulong arrival = NowUs();
            var addr = (IPEndPoint)from;
            if (!Protocol.TryDecode(buf.AsSpan(0, n), out var h, out _)) continue;
            switch (h.Type)
            {
                case Protocol.TAudio:
                    SetPeer(new IPEndPoint(addr.Address, addr.Port));
                    OnAudio(h, buf.AsSpan(Protocol.HeaderSize, h.PayloadLength), arrival);
                    break;
                case Protocol.TDiscover:
                    Log?.Invoke($"discover from {addr}");
                    Send(Protocol.Encode(Protocol.THello, h.Seq, NowUs()), addr);
                    SetPeer(new IPEndPoint(addr.Address, addr.Port));
                    break;
                case Protocol.TPong:
                    OnPong(h, arrival);
                    break;
                case Protocol.TPing:
                    Send(Protocol.Encode(Protocol.TPong, h.Seq, h.TsUs), addr);
                    break;
                case Protocol.TBye:
                    ClearPeer("said bye");
                    break;
            }
            CheckPeerTimeout();
        }
    }

    private void OnPong(in PacketHeader h, ulong arrival)
    {
        double rtt = ((long)arrival - (long)h.TsUs) / 1000.0;
        if (rtt >= 0 && rtt < 5000)
            lock (_lock)
            {
                _rttMs = _rttMs is { } r ? 0.8 * r + 0.2 * rtt : rtt;
                _rttMin = _rttMin is { } m ? Math.Min(m, rtt) : rtt;
            }
    }

    private void CheckPeerTimeout()
    {
        if (Peer != null && Seconds(Stopwatch.GetTimestamp() - _peerLastTicks) > _peerTimeout)
            ClearPeer("timed out");
    }

    private void OnAudio(in PacketHeader h, ReadOnlySpan<byte> payload, ulong arrivalUs)
    {
        if (h.SampleRate != _sampleRate || h.Channels != _channels)
        {
            Log?.Invoke($"format mismatch: got {h.SampleRate} Hz/{h.Channels} ch");
            return;
        }
        if (h.Frames != FramesPerPacket)
        {
            Log?.Invoke($"packet size changed {FramesPerPacket} -> {h.Frames} frames");
            _jb = MakeJb(h.Frames);
        }
        lock (_lock)
        {
            _received++;
            if (_maxSeq is null || Math.Abs(Protocol.SeqDiff(h.Seq, _maxSeq.Value)) > 1000)
            {
                _firstSeq = h.Seq; _maxSeq = h.Seq; _received = 1; _transitPrev = null;
            }
            else if (Protocol.SeqDiff(h.Seq, _maxSeq.Value) > 0) _maxSeq = h.Seq;
            else _reordered++;
            long transit = (long)arrivalUs - (long)h.TsUs;
            if (_transitPrev is { } tp)
            {
                double d = Math.Abs(transit - tp);
                _jitterUs += (d - _jitterUs) / 16.0;
            }
            _transitPrev = transit;
            int peak = 0;
            for (int i = 0; i + 1 < payload.Length; i += 2)
            {
                int v = Math.Abs((int)(short)(payload[i] | (payload[i + 1] << 8)));
                if (v > peak) peak = v;
            }
            double db = peak > 0 ? 20 * Math.Log10(peak / 32768.0) : -120;
            _levelDb = db > _levelDb ? db : _levelDb * 0.9 + db * 0.1;
            _lastAudioTicks = Stopwatch.GetTimestamp();
        }
        _jb.Push(h.Seq, payload);
    }

    private void PingLoop()
    {
        var probeEvery = TimeSpan.FromSeconds(1);
        var lastProbe = DateTime.MinValue;
        var buf = new byte[256];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        long interval = (long)(_pingInterval * Stopwatch.Frequency);
        long nextAt = Stopwatch.GetTimestamp() + interval;
        while (!_stop)
        {
            var ps = _pingSock;
            if (ps is null) { Thread.Sleep(TimeSpan.FromSeconds(_pingInterval)); }
            else
            {
                // Wait for PONGs on the dedicated socket until the next ping is due.
                while (!_stop && Stopwatch.GetTimestamp() < nextAt)
                {
                    try
                    {
                        int n = ps.ReceiveFrom(buf, ref from);
                        ulong arrival = NowUs();
                        if (Protocol.TryDecode(buf.AsSpan(0, n), out var h, out _) && h.Type == Protocol.TPong) OnPong(h, arrival);
                    }
                    catch (SocketException) { }
                    catch (ObjectDisposedException) { return; }
                }
            }
            nextAt = Stopwatch.GetTimestamp() + interval;
            if (_stop) break;
            var peer = Peer;
            if (peer != null)
            {
                _pingSeq++;
                var ping = Protocol.Encode(Protocol.TPing, _pingSeq, NowUs());
                if (_pingSock is { } p2)
                {
                    try { p2.SendTo(ping, peer); } catch (Exception e) { Log?.Invoke($"ping send failed: {e.Message}"); }
                }
                else Send(ping, peer);
            }
            else if (DateTime.UtcNow - lastProbe >= probeEvery)
            {
                lastProbe = DateTime.UtcNow;
                foreach (var p in _probes) Send(Protocol.Encode(Protocol.THello, 0, NowUs()), p);
            }
        }
    }

    public StatsSnapshot Snapshot()
    {
        lock (_lock)
        {
            long expected = _firstSeq is null ? 0 : Protocol.SeqDiff(_maxSeq!.Value, _firstSeq.Value) + 1;
            long lost = Math.Max(0, expected - _received);
            var jb = _jb;
            var js = jb.Stats;
            var peer = Peer;
            return new StatsSnapshot(
                peer?.ToString(), _received, expected, lost, expected > 0 ? 100.0 * lost / expected : 0,
                _reordered, js.Late, js.Concealed, js.Underruns, js.OverflowDrops, jb.DepthMs(_sampleRate),
                _rttMs, _rttMin, _jitterUs / 1000.0, FramesPerPacket * 1000.0 / _sampleRate, _levelDb,
                _lastAudioTicks == 0 ? double.PositiveInfinity : Seconds(Stopwatch.GetTimestamp() - _lastAudioTicks),
                jb.AvgDepthMs, jb.MinResidualMs, js.CatchUpDrops);
        }
    }

    public void Pull(Span<byte> dst) => _jb.Pull(dst);
}
