using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CordMic.Core;
using Xunit;
using Xunit.Abstractions;

/// <summary>UDP loopback: fake phone -> 127.0.0.1 -> C# Receiver -> real-time 5 ms consumer.</summary>
public class LoopbackTests
{
    private readonly ITestOutputHelper _out;
    public LoopbackTests(ITestOutputHelper o) => _out = o;

    sealed class Clock
    {
        public long NonSilent; public double MaxDepth; volatile bool _stop; Thread? _t;
        public void Start(Receiver rx)
        {
            _t = new Thread(() =>
            {
                var b = new byte[480]; var sw = Stopwatch.StartNew(); double next = 0;
                while (!_stop)
                {
                    next += 0.005; var w = next - sw.Elapsed.TotalSeconds;
                    if (w > 0) Thread.Sleep(TimeSpan.FromSeconds(w));
                    rx.Pull(b);
                    if (b.Any(x => x != 0)) NonSilent++;
                    MaxDepth = Math.Max(MaxDepth, rx.Jitter.DepthMs());
                }
            }) { IsBackground = true };
            _t.Start();
        }
        public void Stop() { _stop = true; _t?.Join(1000); }
    }

    /// <summary>Minimal C# phone simulator (mirrors sim_sender.py).</summary>
    static (int sent, int dropped, int pongs, bool hello) Send(int port, double seconds, double loss, double jitterMs, double reorder, int seed)
    {
        var rnd = new Random(seed);
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        s.ReceiveTimeout = 50;
        var to = new IPEndPoint(IPAddress.Loopback, port);
        int pongs = 0; bool hello = false; bool stop = false;
        var ctl = new Thread(() =>
        {
            var buf = new byte[256]; EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            while (!stop)
            {
                try
                {
                    int n = s.ReceiveFrom(buf, ref from);
                    if (!Protocol.TryDecode(buf.AsSpan(0, n), out var h, out _)) continue;
                    if (h.Type == Protocol.TPing) { s.SendTo(Protocol.Encode(Protocol.TPong, h.Seq, h.TsUs), from); pongs++; }
                    else if (h.Type == Protocol.THello) hello = true;
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { break; }
            }
        }) { IsBackground = true };
        ctl.Start();
        s.SendTo(Protocol.Encode(Protocol.TDiscover, 0, 0), to);
        int n = (int)(seconds / 0.005), sent = 0, dropped = 0; byte[]? held = null;
        var sw = Stopwatch.StartNew();
        var pcm = new byte[480];
        for (int seq = 0; seq < n; seq++)
        {
            var w = seq * 0.005 - sw.Elapsed.TotalSeconds;
            if (w > 0) Thread.Sleep(TimeSpan.FromSeconds(w));
            for (int i = 0; i < 240; i++)
            {
                short v = (short)(0.3 * 32767 * Math.Sin(2 * Math.PI * 440 * (seq * 240 + i) / 48000));
                pcm[2 * i] = (byte)v; pcm[2 * i + 1] = (byte)(v >> 8);
            }
            var p = Protocol.Encode(Protocol.TAudio, (uint)seq, Receiver.NowUs(), pcm, 240, 0);
            if (rnd.NextDouble() < loss) { dropped++; continue; }
            if (jitterMs > 0) Thread.Sleep(TimeSpan.FromMilliseconds(rnd.NextDouble() * jitterMs));
            if (reorder > 0 && held == null && rnd.NextDouble() < reorder) { held = p; continue; }
            s.SendTo(p, to); sent++;
            if (held != null) { s.SendTo(held, to); sent++; held = null; }
        }
        s.SendTo(Protocol.Encode(Protocol.TBye, 0, 0), to);
        Thread.Sleep(100); stop = true; ctl.Join(500);
        return (sent, dropped, pongs, hello);
    }

    [Fact]
    public void CleanLink()
    {
        using var rx = new Receiver(0, "127.0.0.1", 10, pingInterval: 0.1); rx.Start();
        var clk = new Clock(); clk.Start(rx);
        var r = Send(rx.Port, 3, 0, 0, 0, 1); Thread.Sleep(100); clk.Stop();
        var s = rx.Snapshot(); _out.WriteLine(s.Line());
        Assert.True(r.hello);
        Assert.Equal(0, s.Lost);
        Assert.True(r.pongs > 5);
        Assert.NotNull(s.RttMs);
        Assert.True(clk.NonSilent > 0.9 * 600, $"nonsilent={clk.NonSilent}");
        Assert.True(clk.MaxDepth < 40);
    }

    [Fact]
    public void LossyJitteryLink()
    {
        using var rx = new Receiver(0, "127.0.0.1", 10, pingInterval: 0.1); rx.Start();
        var clk = new Clock(); clk.Start(rx);
        var r = Send(rx.Port, 3, 0.02, 3, 0.02, 7); Thread.Sleep(100); clk.Stop();
        var s = rx.Snapshot(); _out.WriteLine(s.Line());
        Assert.InRange(s.Lost, r.dropped - 2, r.dropped + 2);
        Assert.True(s.Reordered > 0);
        Assert.True(clk.MaxDepth < 40);
    }

    [Fact]
    public void ProactiveHelloReachesPhoneWithoutDiscover()
    {
        // Phone listens on a fixed port (Android >= 0.2.0 binds 50005 on the tether interface).
        using var phone = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        phone.Bind(new IPEndPoint(IPAddress.Loopback, 0)); phone.ReceiveTimeout = 3000;
        using var rx = new Receiver(0, "127.0.0.1", 10, pingInterval: 0.1);
        rx.SetProbeTargets(new[] { (IPEndPoint)phone.LocalEndPoint! });
        rx.Start();
        var buf = new byte[64]; EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        int n = phone.ReceiveFrom(buf, ref from);
        Assert.True(Protocol.TryDecode(buf.AsSpan(0, n), out var h, out _));
        Assert.Equal(Protocol.THello, h.Type);
        Assert.Equal(rx.Port, ((IPEndPoint)from).Port);
    }

    [Fact]
    public void PeerTimeoutClearsPeer()
    {
        using var rx = new Receiver(0, "127.0.0.1", 10, pingInterval: 0.1, peerTimeout: 0.5); rx.Start();
        var events = new List<IPEndPoint?>(); rx.PeerChanged += p => { lock (events) events.Add(p); };
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.SendTo(Protocol.Encode(Protocol.TAudio, 1, 1, new byte[480], 240, 0), new IPEndPoint(IPAddress.Loopback, rx.Port));
        Thread.Sleep(1200);
        lock (events) { Assert.Equal(2, events.Count); Assert.NotNull(events[0]); Assert.Null(events[1]); }
    }
}
