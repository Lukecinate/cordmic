using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CordMic.Core.Dsp;

namespace CordMic.Core;

public sealed record StatsSnapshot(
    string? Peer, long Received, long Expected, long Lost, double LossPct, long Reordered, long Late,
    long Concealed, long Underruns, long OverflowDrops, double DepthMs, double? RttMs, double? RttMinMs,
    double ArrivalJitterMs, double PacketMs, double LevelDbfs, double SecondsSinceAudio,
    double BufferAvgMs = double.NaN, double BufferMinMs = double.NaN, long CatchUpDrops = 0, long SampleSlips = 0,
    double RenderPeriodMs = 0, double JitterTargetMs = 0, int JitterTargetPackets = 0,
    double UnderrunPerMin = double.NaN, double OverflowPerMin = double.NaN,
    PhoneLink Phone = default,
    double DriftPpm = double.NaN, long UnderrunConcealed = 0, string Studio = "off", double StudioCpuMs = double.NaN,
    double StudioAgcGainDb = double.NaN, double SnrDb = double.NaN)
{
    /// <summary>Jitter-buffer delay to show: the time-averaged depth once a window completed, else the instantaneous depth.</summary>
    public double BufferMs => double.IsNaN(BufferAvgMs) ? DepthMs : BufferAvgMs;

    static string Ms(double v, string fmt = "F1") => double.IsNaN(v) ? "-" : v.ToString(fmt);
    static string Rate(double perMin) => double.IsNaN(perMin) ? "-" : perMin.ToString("F1");

    public string Line() =>
        $"peer={Peer ?? "-"} rx={Received} lost={Lost} ({LossPct:F2}%) reord={Reordered} late={Late} " +
        $"plc={Concealed} underrun={Underruns} ovf={OverflowDrops} buf={DepthMs:F1}ms " +
        $"jitter={ArrivalJitterMs:F2}ms rtt={(RttMs is { } r ? r.ToString("F2") : "-")}ms " +
        $"(min {(RttMinMs is { } m ? m.ToString("F2") : "-")}) pkt={PacketMs:F1}ms level={LevelDbfs:F0}dBFS " +
        $"bufavg={(double.IsNaN(BufferAvgMs) ? "-" : BufferAvgMs.ToString("F1"))}ms " +
        $"bufmin={(double.IsNaN(BufferMinMs) ? "-" : BufferMinMs.ToString("F1"))}ms catchup={CatchUpDrops} slip={SampleSlips} " +
        $"period={(RenderPeriodMs > 0 ? RenderPeriodMs.ToString("F1") : "-")}ms " +
        $"jtarget={Ms(JitterTargetMs)}ms/{JitterTargetPackets}pkt " +
        $"urate={Rate(UnderrunPerMin)}/min orate={Rate(OverflowPerMin)}/min " +
        $"nc={Phone.NoiseText} agc={Phone.AgcText} gain={Phone.GainText} phone={Phone.Mode} " +
        $"dir={Phone.DirText} mics={Phone.MicText} " +
        $"floor={Phone.FloorText} gate={Phone.GateText} agcgain={Phone.AgcGainText} exp={Phone.ExpanderText}" +
        (string.IsNullOrEmpty(Phone.Name) ? "" : $" dev={Phone.Name.Replace(' ', '_')}") +
        $" drift={Ms(DriftPpm, "F0")}ppm uplc={UnderrunConcealed} studio={Studio} studiocpu={Ms(StudioCpuMs, "F2")}ms " +
        $"studiogain={Ms(StudioAgcGainDb)}dB snr={Ms(SnrDb, "F0")}dB silenced={(Phone.Silenced ? 1 : 0)} devns={(Phone.NsSuspected ? 1 : 0)}";
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
    private double _renderPeriodMs;
    private readonly PingBackoff _pingBackoff = new();
    private long _pingMuteUntil;
    private bool _pingDropLogged;
    private byte _ctrlSeq = 1;
    private PhoneLink _phone;
    private long _rateTicks, _rateUnderruns, _rateOverflows;
    private double _underrunPerMin = double.NaN, _overflowPerMin = double.NaN;
    private readonly byte[] _pingPkt = new byte[Protocol.HeaderSize + LinkControl.Size + SettingsSync.Size];
    // 0.6.0 Studio noise reduction (PC side) and the distance hint.
    private volatile StudioProcessor? _studio;
    private StudioPreset _studioPreset = StudioPreset.Off;
    private bool _studioAutoVolume;
    private double _studioStartGainDb = AgcB3.DefaultStartGainDb;
    private volatile bool _studioFailed;
    private readonly SnrHint _hint = new();
    // 0.6.1 trust boundary (audit H1): source filter, pairing and the peer lock. The pairing tables are touched only
    // by the RX thread.
    private volatile SourcePolicy _policy = SourcePolicy.LoopbackOnly;
    private SourcePolicy? _policySeen;
    private readonly Dictionary<IPAddress, (byte[] Nonce, long Issued, long LastSent)> _challenges = new();
    private readonly HashSet<IPAddress> _paired = new();
    private readonly Dictionary<IPAddress, (int Failures, long Until)> _pairFailures = new();
    private readonly LogLimiter _logLimit = new(TimeSpan.FromSeconds(10));
    private readonly object _jbSwap = new();
    private volatile IPEndPoint? _pendingPairing;
    private volatile IPAddress? _lastUntrusted;
    private long _lastUntrustedTicks;
    private long _pendingPairingTicks;
    private const int MaxPairFailures = 5;

    public int Port { get; private set; }
    public int FramesPerPacket { get; private set; }
    public IPEndPoint? Peer { get; private set; }
    public JitterBuffer Jitter => _jb;
    public event Action<IPEndPoint?>? PeerChanged;
    public Action<string>? Log { get; set; }

    /// <summary>
    /// Which sources may connect (0.6.1). Defaults to loopback only; the app sets the USB tethering subnet and, when
    /// the user enabled it, the pairing key. Changing the policy forgets earlier pairings.
    /// </summary>
    public SourcePolicy Policy
    {
        get => _policy;
        set => _policy = value ?? SourcePolicy.LoopbackOnly;
    }

    /// <summary>Phone that sent audio but has not answered the pairing challenge (seen within the last 3 s), else null.</summary>
    public IPEndPoint? PendingPairing =>
        _pendingPairing is { } p && Seconds(Stopwatch.GetTimestamp() - Interlocked.Read(ref _pendingPairingTicks)) < 3 ? p : null;

    /// <summary>Source outside the tether subnet that sent CordMic packets within the last 5 s (shown in the UI), else null.</summary>
    public IPAddress? RecentUntrustedSource =>
        _lastUntrusted is { } a && Seconds(Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastUntrustedTicks)) < 5 ? a : null;

    /// <summary>True when the RX and ping threads exited on the last <see cref="Stop"/>.</summary>
    public bool StoppedCleanly { get; private set; } = true;
    /// <summary>Called once on the receive thread (the Windows app raises it to MMCSS "Pro Audio").</summary>
    public Action? RxThreadInit { get; set; }
    /// <summary>Called once on the Studio worker thread (the Windows app raises it to MMCSS "Pro Audio").</summary>
    public Action? StudioThreadInit { get; set; }
    /// <summary>Raised (from an audio thread) when Studio missed 3 deadlines in 1 s and was switched off for the session.</summary>
    public event Action<string>? StudioFellBack;

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
        Sync.SeedPcDefaults();
    }

    public static ulong NowUs() => (ulong)(Stopwatch.GetTimestamp() * 1_000_000.0 / Stopwatch.Frequency);
    private static double Seconds(long ticks) => ticks * 1.0 / Stopwatch.Frequency;

    private JitterBuffer MakeJb(int fpp)
    {
        FramesPerPacket = fpp;
        double pktMs = fpp * 1000.0 / _sampleRate;
        int target = JitterPolicy.EffectiveTargetPackets(_jitterMs, PolicyPeriodMs, pktMs);
        int max = JitterPolicy.MaxDepthPackets(_jitterMs, PolicyPeriodMs, pktMs);
        int high = JitterPolicy.HighWaterPackets(_jitterMs, PolicyPeriodMs, pktMs);
        // 0.6.0: pitch-repeat concealment and the cubic drift resampler replace the 3 ms decay and sample slips.
        return new JitterBuffer(fpp, _channels, target, max, _sampleRate, high)
        {
            CatchUp = false,
            DriftLock = false,
            Resample = true,
            PitchConcealment = true,
            AdaptiveTarget = true,
            RenderPeriodMs = PolicyPeriodMs,
        };
    }

    /// <summary>
    /// Consumer period the jitter policy plans for: the render period, or with Studio on at least one 10 ms Studio frame
    /// (the worker pulls the jitter buffer in 480-sample blocks).
    /// </summary>
    private double PolicyPeriodMs => StudioActive ? Math.Max(_renderPeriodMs, StudioProcessor.Frame * 1000.0 / 48000) : _renderPeriodMs;

    public double JitterMs => _jitterMs;
    /// <summary>WASAPI engine period the jitter target was floored against. 0 until the renderer reports it.</summary>
    public double RenderPeriodMs => _renderPeriodMs;
    /// <summary>Target actually in use (preset, raised when the render period cannot sustain it).</summary>
    public double EffectiveJitterMs => _jb.Target * FramesPerPacket * 1000.0 / _sampleRate;

    /// <summary>Voice-chain settings pushed to a 0.5.0 phone on the next ping. A 0.4.2 phone ignores the payload.</summary>
    public NoiseMode NoiseMode { get; set; } = NoiseMode.Light;
    public bool NoiseCancellation
    {
        get => NoiseMode != NoiseMode.Off;
        set => NoiseMode = value ? (NoiseMode == NoiseMode.Off ? NoiseMode.Light : NoiseMode) : NoiseMode.Off;
    }
    public bool VoiceClarity { get; set; } = true;
    public bool FrontFocus { get; set; } = true;
    public bool FrontFocusAudioRecord { get; set; }
    public SyncClock Sync { get; } = new();
    public bool Agc { get; set; } = true;
    public int MicGainDb { get; set; } = VoiceChain.DefaultGainDb;
    public PhoneLink Phone => _phone;

    /// <summary>Bumps the control counter so the phone applies <see cref="NoiseCancellation"/> and <see cref="MicGainDb"/>.</summary>
    public byte ControlSeq => _ctrlSeq;
    public void NotifyLinkControlChanged() => _ctrlSeq = (byte)(_ctrlSeq == 255 ? 1 : _ctrlSeq + 1);

    public void NotifyLinkControlChanged(SyncField field)
    {
        Sync.Bump(field, SyncOrigin.Pc);
        NotifyLinkControlChanged();
    }

    public void NotifyLinkControlChanged(params SyncField[] fields)
    {
        foreach (var f in fields) Sync.Bump(f, SyncOrigin.Pc);
        NotifyLinkControlChanged();
    }

    /// <summary>Change the jitter-buffer target at runtime (rebuilds the buffer when the packet count changes).</summary>
    public void SetJitterMs(double ms)
    {
        _jitterMs = ms;
        ApplyTarget(force: true);
    }

    /// <summary>
    /// Tell the buffer how much audio one render callback consumes. The effective target is raised to
    /// one full pull plus one packet so the callback cannot empty the buffer. No-op when the packet
    /// count does not change.
    /// </summary>
    public void SetRenderPeriodMs(double periodMs)
    {
        if (periodMs < 0) periodMs = 0;
        if (Math.Abs(periodMs - _renderPeriodMs) < 0.05 && _jb.BaseTarget == TargetPackets()) return;
        _renderPeriodMs = periodMs;
        ApplyTarget(force: false);
        _studio?.SetRenderPeriodMs(periodMs);
    }

    private int TargetPackets()
    {
        double pktMs = Math.Max(1, FramesPerPacket) * 1000.0 / _sampleRate;
        return JitterPolicy.EffectiveTargetPackets(_jitterMs, PolicyPeriodMs, pktMs);
    }

    private void ApplyTarget(bool force)
    {
        // 0.6.1 (audit L3): the UI thread, the ThreadPool (Studio fallback) and the RX thread (packet size change)
        // all rebuild the buffer; one lock keeps a swap from being lost.
        lock (_jbSwap) ApplyTargetLocked(force);
    }

    private void ApplyTargetLocked(bool force)
    {
        int target = TargetPackets();
        double pktMs = FramesPerPacket * 1000.0 / _sampleRate;
        if (!force && _jb.BaseTarget == target && _jb.FramesPerPacket == FramesPerPacket
            && Math.Abs(_jb.RenderPeriodMs - PolicyPeriodMs) < 0.05)
        {
            LogTarget(pktMs, target);
            return;
        }
        if (_jb.BaseTarget != target || _jb.FramesPerPacket != FramesPerPacket
            || Math.Abs(_jb.RenderPeriodMs - PolicyPeriodMs) >= 0.05 || force)
            _jb = MakeJb(FramesPerPacket);
        LogTarget(pktMs, target);
    }

    private void LogTarget(double pktMs, int target)
    {
        string period = _renderPeriodMs > 0 ? $"{_renderPeriodMs:F1} ms" : "n/a";
        Log?.Invoke($"jitter target {target * pktMs:F1} ms = {target} pkt (preset {_jitterMs:F0} ms, render period {period})");
    }

    /// <summary>True while PC Studio noise reduction runs (configured, RNNoise loaded, not fallen back).</summary>
    public bool StudioActive => _studio is not null && !_studioFailed;
    public StudioPreset StudioPreset => _studioPreset;
    /// <summary>Why Studio is not running although configured (RNNoise missing, mono only, CPU fallback), else null.</summary>
    public string? StudioUnavailableReason { get; private set; }
    /// <summary>Current Studio auto-volume gain (persisted by the app as the next session's start gain), NaN when off.</summary>
    public double StudioAgcGainDb => _studio is { } st && _studioAutoVolume ? st.AgcGainDb : double.NaN;
    /// <summary>Move-closer hint state (received speech-to-noise ratio below 15 dB for 10 s).</summary>
    public bool MoveCloserHint => _hint.Visible;
    public double SnrDb => _hint.SnrDb;

    /// <summary>
    /// Switch PC Studio noise reduction (0.6.0). Off restores the 0.5.7 path exactly (phone suppression). With a preset
    /// the phone is asked (FlagStudio) to send raw capture, and RNNoise + WhisperGuard + B3 auto-volume run on a worker
    /// thread. Returns false (and leaves Studio off) when RNNoise cannot load or the stream is not mono.
    /// </summary>
    public bool ConfigureStudio(StudioPreset preset, bool autoVolume, double startGainDb)
    {
        bool same = preset == _studioPreset && autoVolume == _studioAutoVolume && (_studio is not null || preset == StudioPreset.Off);
        _studioPreset = preset; _studioAutoVolume = autoVolume; _studioStartGainDb = startGainDb;
        if (same && !_studioFailed) return preset == StudioPreset.Off || _studio is not null;
        var old = _studio;
        _studio = null;
        _studioFailed = false;
        StudioUnavailableReason = null;
        old?.Dispose();
        if (preset != StudioPreset.Off)
        {
            if (_channels != 1) StudioUnavailableReason = "Studio needs a mono stream";
            else if (!Rnnoise.IsAvailable) StudioUnavailableReason = "RNNoise library not found: " + Rnnoise.UnavailableReason;
            else
            {
                try
                {
                    var st = new StudioProcessor(new StudioChain(preset, autoVolume, startGainDb), buf => _jb.Pull(buf), _renderPeriodMs, StudioThreadInit);
                    st.FellBack += OnStudioFellBack;
                    _studio = st;
                }
                catch (Exception e) { StudioUnavailableReason = e.Message; }
            }
            if (StudioUnavailableReason is { } why) Log?.Invoke($"studio unavailable: {why}");
            else Log?.Invoke($"studio on: {preset}, auto volume {(autoVolume ? $"on (start {startGainDb:F1} dB)" : "off")}, +{StudioProcessor.AddedLatencyMs:F0} ms");
        }
        else if (old is not null) Log?.Invoke("studio off");
        ApplyTarget(force: false);
        NotifyLinkControlChanged();
        return preset == StudioPreset.Off || _studio is not null;
    }

    private void OnStudioFellBack(string why)
    {
        _studioFailed = true;
        StudioUnavailableReason = "CPU too slow for Studio (" + why + ")";
        Log?.Invoke($"studio fell back to phone noise reduction for this session: {why}");
        var st = _studio;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            ApplyTarget(force: false);
            NotifyLinkControlChanged();
            if (st is not null && ReferenceEquals(_studio, st)) { _studio = null; st.Dispose(); }
            StudioFellBack?.Invoke(why);
        });
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
            try { ps.SendBufferSize = 64 * 1024; } catch (SocketException) { }
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
        // 0.6.1 (audit L2): close the sockets first so blocked receives return at once; then the joins are short.
        var s = _sock; var ps = _pingSock;
        try { s?.Close(); } catch (Exception) { }
        try { ps?.Close(); } catch (Exception) { }
        bool rxDone = _rx?.Join(1000) ?? true;
        bool pingDone = _ping?.Join(1000) ?? true;
        StoppedCleanly = rxDone && pingDone;
        if (!StoppedCleanly) Log?.Invoke($"receiver threads did not stop within 1 s (rx={(rxDone ? "done" : "running")}, ping={(pingDone ? "done" : "running")})");
        _sock = null; _pingSock = null;
    }

    public void Dispose()
    {
        Stop();
        var st = _studio; _studio = null; st?.Dispose();
    }

    private void Send(byte[] data, EndPoint to)
    {
        try { _sock?.SendTo(data, to); } catch (Exception e) { LogLimited("send", $"send failed: {e.Message}"); }
    }

    private void SetPeer(IPEndPoint addr)
    {
        if (!addr.Equals(Peer))
        {
            LogLimited("peer:" + addr.Address, $"phone connected: {addr}");
            Peer = addr;
            PeerChanged?.Invoke(addr);
        }
        _peerLastTicks = Stopwatch.GetTimestamp();
    }

    private void ClearPeer(string why)
    {
        if (Peer is not { } old) return;
        Log?.Invoke($"phone {old} {why}");
        // A new session from this address has to pair again when pairing is on.
        _paired.Remove(old.Address);
        Peer = null;
        PeerChanged?.Invoke(null);
    }

    private void LogLimited(string key, string msg)
    {
        if (_logLimit.ShouldLog(key, out int suppressed)) Log?.Invoke(LogLimiter.WithCount(msg, suppressed));
    }

    private void RxLoop()
    {
        try { RxThreadInit?.Invoke(); } catch (Exception) { }
        var buf = new byte[4096];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        var sock = _sock;
        if (sock is null) return;
        while (!_stop)
        {
            int n;
            try
            {
                n = sock.ReceiveFrom(buf, ref from);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
            {
                CheckPeerTimeout(); continue;
            }
            catch (SocketException) { CheckPeerTimeout(); continue; } // WSAECONNRESET etc.
            catch (ObjectDisposedException) { break; }
            ulong arrival = NowUs();
            var addr = (IPEndPoint)from;
            try { HandlePacket(buf, n, addr, arrival); }
            catch (Exception e)
            {
                // 0.6.1 (audit N5): one bad packet must never end the receive thread (and with it the process).
                LogLimited("rx-exception", $"packet from {addr} ignored after an error: {e.GetType().Name}: {e.Message}");
            }
            CheckPeerTimeout();
        }
    }

    /// <summary>
    /// Trust boundary (0.6.1, audit H1 and L5). In order: the source must be on the USB tethering subnet (or loopback);
    /// while a phone is connected, packets from any other address are ignored until it times out or says BYE; AUDIO
    /// must be well-formed before it can connect a peer or resize the jitter buffer; and with a pairing code set, a new
    /// phone must answer a challenge first.
    /// </summary>
    private void HandlePacket(byte[] buf, int n, IPEndPoint addr, ulong arrival)
    {
        if (!Protocol.TryDecode(buf.AsSpan(0, n), out var h, out _)) return;
        var policy = _policy;
        if (!ReferenceEquals(policy, _policySeen))
        {
            _policySeen = policy;
            _paired.Clear(); _challenges.Clear(); _pairFailures.Clear();
            Log?.Invoke("accepting: " + policy);
        }
        if (!policy.IsTrustedNetwork(addr.Address))
        {
            _lastUntrusted = addr.Address;
            Interlocked.Exchange(ref _lastUntrustedTicks, Stopwatch.GetTimestamp());
            LogLimited("untrusted:" + addr.Address, $"ignored packets from {addr.Address}: not on the USB tethering adapter");
            return;
        }
        var peer = Peer;
        bool fromPeer = peer is not null && peer.Address.Equals(addr.Address);
        bool locked = peer is not null && !fromPeer;
        switch (h.Type)
        {
            case Protocol.TAudio:
                if (locked) { LogLimited("locked:" + addr.Address, $"ignored {addr}: connected to {peer} until it disconnects"); return; }
                if (!Protocol.IsAcceptableAudio(h, _sampleRate, _channels, out var why))
                {
                    LogLimited("badaudio:" + addr.Address, $"ignored audio from {addr}: {why}");
                    return;
                }
                if (!IsAdmitted(addr, policy, fromPeer)) { Challenge(addr, policy); return; }
                SetPeer(new IPEndPoint(addr.Address, addr.Port));
                OnAudio(h, buf.AsSpan(Protocol.HeaderSize, h.PayloadLength), arrival);
                break;
            case Protocol.TDiscover:
                if (locked) { LogLimited("locked:" + addr.Address, $"ignored discover from {addr}: connected to {peer}"); return; }
                if (!IsAdmitted(addr, policy, fromPeer)) { Challenge(addr, policy); return; }
                LogLimited("discover:" + addr.Address, $"discover from {addr}");
                Send(Protocol.Encode(Protocol.THello, h.Seq, NowUs()), addr);
                SetPeer(new IPEndPoint(addr.Address, addr.Port));
                break;
            case Protocol.TPairProof:
                if (locked) return;
                OnPairProof(addr, policy, buf.AsSpan(Protocol.HeaderSize, Math.Max(0, n - Protocol.HeaderSize)), h.Seq);
                break;
            case Protocol.TPong:
                // Phone status and settings are taken only from the connected phone.
                if (!fromPeer) return;
                OnPong(h, buf.AsSpan(Protocol.HeaderSize, Math.Max(0, n - Protocol.HeaderSize)), arrival);
                break;
            case Protocol.TPing:
                if (locked || !IsAdmitted(addr, policy, fromPeer)) return;
                Send(Protocol.Encode(Protocol.TPong, h.Seq, h.TsUs), addr);
                break;
            case Protocol.TBye:
                if (fromPeer) ClearPeer("said bye");
                break;
        }
    }

    private bool IsAdmitted(IPEndPoint addr, SourcePolicy policy, bool fromPeer)
    {
        if (!policy.PairingRequired) return true;
        return _paired.Contains(addr.Address);
    }

    private void Challenge(IPEndPoint addr, SourcePolicy policy)
    {
        long now = Stopwatch.GetTimestamp();
        _pendingPairing = new IPEndPoint(addr.Address, addr.Port);
        Interlocked.Exchange(ref _pendingPairingTicks, now);
        if (_pairFailures.TryGetValue(addr.Address, out var f) && f.Failures >= MaxPairFailures && now < f.Until) return;
        if (_challenges.TryGetValue(addr.Address, out var c))
        {
            if (Seconds(now - c.LastSent) < 1.0) return;
            // A nonce lives 5 s, so a proof in flight still matches; then a fresh one is issued.
            if (Seconds(now - c.Issued) >= 5.0) c = (Pairing.NewNonce(), now, now);
            else c = (c.Nonce, c.Issued, now);
        }
        else
        {
            if (_challenges.Count >= 16) _challenges.Clear();
            c = (Pairing.NewNonce(), now, now);
            LogLimited("pair:" + addr.Address, $"pairing code required: challenge sent to {addr}");
        }
        _challenges[addr.Address] = c;
        Send(Protocol.Encode(Protocol.TPairChallenge, 0, NowUs(), c.Nonce), addr);
    }

    private void OnPairProof(IPEndPoint addr, SourcePolicy policy, ReadOnlySpan<byte> payload, uint seq)
    {
        if (policy.PairingKey is not { } key) return;
        if (!_challenges.TryGetValue(addr.Address, out var c)) return;
        long now = Stopwatch.GetTimestamp();
        if (_pairFailures.TryGetValue(addr.Address, out var f) && f.Failures >= MaxPairFailures && now < f.Until) return;
        // Single use: the nonce is consumed whether or not the proof is right.
        _challenges.Remove(addr.Address);
        if (!Pairing.Verify(key, c.Nonce, payload))
        {
            int fails = (f.Until > now ? f.Failures : 0) + 1;
            _pairFailures[addr.Address] = (fails, now + 60 * Stopwatch.Frequency);
            LogLimited("pairfail:" + addr.Address, $"wrong pairing code from {addr} ({fails} of {MaxPairFailures} before a 60 s pause)");
            return;
        }
        _pairFailures.Remove(addr.Address);
        _paired.Add(addr.Address);
        _pendingPairing = null;
        Log?.Invoke($"phone {addr} paired");
        Send(Protocol.Encode(Protocol.THello, seq, NowUs()), addr);
        SetPeer(new IPEndPoint(addr.Address, addr.Port));
    }

    private void OnPong(in PacketHeader h, ReadOnlySpan<byte> payload, ulong arrival)
    {
        double rtt = ((long)arrival - (long)h.TsUs) / 1000.0;
        if (rtt >= 0 && rtt < 5000)
            lock (_lock)
            {
                _rttMs = _rttMs is { } r ? 0.8 * r + 0.2 * rtt : rtt;
                _rttMin = _rttMin is { } m ? Math.Min(m, rtt) : rtt;
                if (LinkControl.TryReadPong(payload, out var phone)) _phone = phone;
            }
    }

    private void CheckPeerTimeout()
    {
        if (Peer != null && Seconds(Stopwatch.GetTimestamp() - _peerLastTicks) > _peerTimeout)
            ClearPeer("timed out");
    }

    private void OnAudio(in PacketHeader h, ReadOnlySpan<byte> payload, ulong arrivalUs)
    {
        if (h.Frames != FramesPerPacket)
        {
            lock (_jbSwap)
            {
                if (h.Frames != FramesPerPacket)
                {
                    LogLimited("pktsize", $"packet size changed {FramesPerPacket} -> {h.Frames} frames");
                    _jb = MakeJb(h.Frames);
                }
            }
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
            // 0.5.0 sets bits 12–14. All-zero flags are what 0.4.2 sends, so they do not count as a report.
            if ((h.Flags & 0x7000) != 0)
                _phone = _phone with
                {
                    Known = true, NoiseCancellation = h.NoiseCancellation, Agc = h.Agc, AAudio = h.AAudio,
                    GainDb = _phone.Known ? _phone.GainDb : -1,
                };
        }
        _jb.Push(h.Seq, payload);
        if (_channels == 1) _hint.AddS16(payload);
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
                        if (Protocol.TryDecode(buf.AsSpan(0, n), out var h, out _) && h.Type == Protocol.TPong)
                            OnPong(h, buf.AsSpan(Protocol.HeaderSize, Math.Max(0, n - Protocol.HeaderSize)), arrival);
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
                long nowTicks = Stopwatch.GetTimestamp();
                if (nowTicks < _pingMuteUntil) continue;
                _pingSeq++;
                var payload = _pingPkt.AsSpan(Protocol.HeaderSize);
                LinkControl.WritePing(payload, NoiseCancellation, Agc, MicGainDb, _ctrlSeq, extended: true, VoiceClarity, NoiseMode, FrontFocus, FrontFocusAudioRecord);
                byte flags2 = SettingsSync.FlagPeerSync | SettingsSync.FlagWantDiag;
                if (FrontFocusAudioRecord) flags2 |= SettingsSync.FlagFrontFocusAr;
                if (VoiceClarity) flags2 |= SettingsSync.FlagVoiceClarity;
                if (StudioActive) flags2 |= SettingsSync.FlagStudio;
                SettingsSync.Write(payload.Slice(LinkControl.Size), flags2, Sync.Revs);
                Protocol.WriteHeader(_pingPkt, Protocol.TPing, _pingSeq, NowUs());
                if (_pingSock is { } p2)
                {
                    try
                    {
                        p2.SendTo(_pingPkt, peer);
                        if (_pingBackoff.OnSuccess()) Log?.Invoke("ping send recovered");
                    }
                    catch (SocketException e) when (PingBackoff.IsNoBuffer(e.ErrorCode) || PingBackoff.IsNoBuffer((int)e.SocketErrorCode))
                    {
                        // WSAENOBUFS: the stack is out of buffer space. Wait, and don't log every ping.
                        if (_pingBackoff.OnNoBuffer())
                            Log?.Invoke($"ping send paused ({e.SocketErrorCode}); backing off {_pingBackoff.Delay.TotalMilliseconds:F0} ms");
                        _pingMuteUntil = Stopwatch.GetTimestamp() + (long)(_pingBackoff.Delay.TotalSeconds * Stopwatch.Frequency);
                    }
                    catch (Exception e)
                    {
                        if (!_pingDropLogged) { Log?.Invoke($"ping send failed: {e.Message}"); _pingDropLogged = true; }
                    }
                }
                else Send(_pingPkt, peer);
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
            long now = Stopwatch.GetTimestamp();
            if (_rateTicks == 0)
            {
                _rateTicks = now; _rateUnderruns = js.Underruns; _rateOverflows = js.OverflowDrops;
            }
            else
            {
                double sec = Seconds(now - _rateTicks);
                if (sec >= 10)
                {
                    _underrunPerMin = (js.Underruns - _rateUnderruns) / sec * 60.0;
                    _overflowPerMin = (js.OverflowDrops - _rateOverflows) / sec * 60.0;
                    _rateTicks = now; _rateUnderruns = js.Underruns; _rateOverflows = js.OverflowDrops;
                }
            }
            double pktMs = FramesPerPacket * 1000.0 / _sampleRate;
            return new StatsSnapshot(
                peer?.ToString(), _received, expected, lost, expected > 0 ? 100.0 * lost / expected : 0,
                _reordered, js.Late, js.Concealed, js.Underruns, js.OverflowDrops, jb.DepthMs(_sampleRate),
                _rttMs, _rttMin, _jitterUs / 1000.0, pktMs, _levelDb,
                _lastAudioTicks == 0 ? double.PositiveInfinity : Seconds(now - _lastAudioTicks),
                jb.AvgDepthMs, jb.MinResidualMs, js.CatchUpDrops, js.SampleSlips,
                _renderPeriodMs, jb.Target * pktMs, jb.Target, _underrunPerMin, _overflowPerMin, _phone,
                jb.DriftPpm, js.UnderrunConcealed, StudioText(), StudioCpu(), StudioAgcGainDb, _hint.SnrDb);
        }
    }

    private string StudioText()
    {
        if (_studioPreset == StudioPreset.Off) return "off";
        string p = _studioPreset.ToString().ToLowerInvariant();
        if (_studioFailed) return p + "-fallback";
        if (_studio is null) return p + "-unavailable";
        return _phone.StudioBypass ? p : p + "-nophoneack";
    }

    private double StudioCpu() => _studio is { } st ? st.TakeCpu().MeanMs : double.NaN;

    /// <summary>Render thread: Studio output when Studio runs, else the jitter buffer directly (0.5.7 path).</summary>
    public void Pull(Span<byte> dst)
    {
        var st = _studio;
        if (st is not null && !_studioFailed) st.Pull(dst);
        else _jb.Pull(dst);
    }
}
