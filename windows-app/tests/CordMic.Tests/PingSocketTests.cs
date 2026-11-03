using System.Net;
using System.Net.Sockets;
using CordMic.Core;
using Xunit;

/// <summary>0.4.2: RTT pings use their own socket so PONGs never queue behind audio on the main port.</summary>
public class PingSocketTests
{
    [Fact]
    public void PingsComeFromSeparatePortAndPongsThereSetRtt()
    {
        using var rx = new Receiver(0, "127.0.0.1", 10, pingInterval: 0.05); rx.Start();
        using var phone = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        phone.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        phone.ReceiveTimeout = 500;
        var to = new IPEndPoint(IPAddress.Loopback, rx.Port);
        // one audio packet makes us the peer
        phone.SendTo(Protocol.Encode(Protocol.TAudio, 0, Receiver.NowUs(), new byte[480], 240, 0), to);
        var buf = new byte[1500];
        int pingPort = -1, pongs = 0;
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until && pongs < 5)
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int n;
            try { n = phone.ReceiveFrom(buf, ref from); } catch (SocketException) { continue; }
            if (!Protocol.TryDecode(buf.AsSpan(0, n), out var h, out _) || h.Type != Protocol.TPing) continue;
            pingPort = ((IPEndPoint)from).Port;
            phone.SendTo(Protocol.Encode(Protocol.TPong, h.Seq, h.TsUs), from); // like the phone: reply to the sender
            pongs++;
        }
        Assert.True(pongs >= 5, $"pings received: {pongs}");
        Assert.NotEqual(rx.Port, pingPort);
        Thread.Sleep(100);
        var s = rx.Snapshot();
        Assert.NotNull(s.RttMs);
        Assert.InRange(s.RttMs!.Value, 0, 100);
    }
}
