using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using CordMic.Core;
using CordMic.Core.Dsp;
using Xunit.Abstractions;

namespace CordMic.Tests;

/// <summary>Regression tests for the 0.6.1 audit fixes (H1, M1, L1, L3, L4, L5, L6 helpers, L7, N5).</summary>
public class Fixes061Tests
{
    private readonly ITestOutputHelper _out;
    public Fixes061Tests(ITestOutputHelper o) { _out = o; }

    private static readonly byte[] Pcm240 = new byte[480];

    private static byte[] Audio(uint seq, ushort frames = 240, ushort? flags = null, int? payload = null)
        => Protocol.Encode(Protocol.TAudio, seq, Receiver.NowUs(), new byte[payload ?? frames * 2], frames, flags ?? Protocol.MakeFlags());

    private static Socket Client(string ip)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Parse(ip), 0));
        s.ReceiveTimeout = 1500;
        return s;
    }

    private static bool WaitFor(Func<bool> cond, int ms = 1500)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; Thread.Sleep(10); }
        return cond();
    }

    /// <summary>Policy that emulates "tether subnet 127.0.0.0/29" with loopback not implicitly trusted.</summary>
    private static SourcePolicy Tether(byte[]? key = null) =>
        new(new[] { Ipv4Subnet.From(IPAddress.Parse("127.0.0.1"), 29) }, key, trustLoopback: false);

    // ------------------------------------------------------------------ H1: source filter, peer lock, pairing

    [Fact]
    public void Subnet_And_Policy_Membership()
    {
        var sn = Ipv4Subnet.From(IPAddress.Parse("192.168.42.129"), 24);
        Assert.Equal("192.168.42.0/24", sn.ToString());
        Assert.True(sn.Contains(IPAddress.Parse("192.168.42.1")));
        Assert.False(sn.Contains(IPAddress.Parse("192.168.43.1")));
        Assert.False(sn.Contains(IPAddress.IPv6Loopback));
        var p = new SourcePolicy(new[] { sn });
        Assert.True(p.IsTrustedNetwork(IPAddress.Loopback));
        Assert.True(p.IsTrustedNetwork(IPAddress.Parse("192.168.42.200")));
        Assert.False(p.IsTrustedNetwork(IPAddress.Parse("10.0.0.5")));
        Assert.False(SourcePolicy.LoopbackOnly.IsTrustedNetwork(IPAddress.Parse("192.168.42.200")));
        Assert.False(p.PairingRequired);
    }

    [Fact]
    public void Receiver_IgnoresSourcesOutsideTheTetherSubnet()
    {
        using var rx = new Receiver(0, "0.0.0.0", 10, pingInterval: 0.1) { Policy = Tether() };
        rx.Start();
        var to = new IPEndPoint(IPAddress.Loopback, rx.Port);
        using var stranger = Client("127.0.0.9");
        for (uint i = 0; i < 20; i++) stranger.SendTo(Audio(i), to);
        stranger.SendTo(Protocol.Encode(Protocol.TDiscover, 1, 0), to);
        Assert.True(WaitFor(() => rx.RecentUntrustedSource is not null));
        Assert.Null(rx.Peer);
        Assert.Equal(0, rx.Snapshot().Received);
        using var phone = Client("127.0.0.2");
        phone.SendTo(Audio(1), to);
        Assert.True(WaitFor(() => rx.Peer is not null));
        Assert.Equal(IPAddress.Parse("127.0.0.2"), rx.Peer!.Address);
    }

    [Fact]
    public void Receiver_LocksToFirstPhone_UntilItLeaves()
    {
        using var rx = new Receiver(0, "0.0.0.0", 10, pingInterval: 0.1, peerTimeout: 5) { Policy = Tether() };
        rx.Start();
        var to = new IPEndPoint(IPAddress.Loopback, rx.Port);
        using var a = Client("127.0.0.2");
        using var b = Client("127.0.0.3");
        a.SendTo(Audio(1), to);
        Assert.True(WaitFor(() => rx.Peer?.Address.Equals(IPAddress.Parse("127.0.0.2")) == true));
        // Another host on the link cannot take over, inject audio or disconnect the phone.
        for (uint i = 0; i < 10; i++) b.SendTo(Audio(100 + i), to);
        b.SendTo(Protocol.Encode(Protocol.TDiscover, 2, 0), to);
        b.SendTo(Protocol.Encode(Protocol.TBye, 3, 0), to);
        Thread.Sleep(200);
        Assert.Equal(IPAddress.Parse("127.0.0.2"), rx.Peer!.Address);
        Assert.Equal(1, rx.Snapshot().Received);
        // A forged PONG with settings from b is ignored.
        var pong = new byte[Protocol.HeaderSize + 64];
        Protocol.WriteHeader(pong, Protocol.TPong, 1, Receiver.NowUs());
        int used = LinkControl.WritePong(pong.AsSpan(Protocol.HeaderSize), true, true, true, false, true, 24, "evil");
        b.SendTo(pong.AsSpan(0, Protocol.HeaderSize + used).ToArray(), to);
        Thread.Sleep(150);
        Assert.False(rx.Phone.Known && rx.Phone.Name == "evil");
        // BYE from the phone itself frees the lock; then b may connect.
        a.SendTo(Protocol.Encode(Protocol.TBye, 4, 0), to);
        Assert.True(WaitFor(() => rx.Peer is null));
        b.SendTo(Audio(200), to);
        Assert.True(WaitFor(() => rx.Peer?.Address.Equals(IPAddress.Parse("127.0.0.3")) == true));
    }

    [Fact]
    public void Pairing_ProofRoundTrip_And_SharedTestVector()
    {
        // Same vector in android/app/src/test/.../PairingTest.kt.
        var key = Pairing.DeriveKey("123456");
        Assert.Equal("a3d6f690e4fb5fedc18714c2116777fe4370cabf2740e6e519a32303a8b0945d", Convert.ToHexString(key).ToLowerInvariant());
        var nonce = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var proof = new byte[Pairing.ProofSize];
        Pairing.WriteProof(key, nonce, proof);
        Assert.Equal("dd42ece9fdf11f5558e3580e75a5fc0e", Convert.ToHexString(proof, 16, 16).ToLowerInvariant());
        Assert.True(Pairing.Verify(key, nonce, proof));
        Assert.False(Pairing.Verify(Pairing.DeriveKey("123457"), nonce, proof));
        var other = (byte[])nonce.Clone(); other[0] ^= 1;
        Assert.False(Pairing.Verify(key, other, proof));
        Assert.False(Pairing.Verify(key, nonce, proof.AsSpan(0, 20)));
        Assert.True(Pairing.IsValidCode(Pairing.NewCode()));
        Assert.False(Pairing.IsValidCode("12345a"));
        Assert.False(Pairing.IsValidCode("1234567"));
    }

    [Fact]
    public void Receiver_WithPairingCode_RequiresAFreshProof()
    {
        var key = Pairing.DeriveKey("424242");
        using var rx = new Receiver(0, "0.0.0.0", 10, pingInterval: 0.1, peerTimeout: 5) { Policy = Tether(key) };
        rx.Start();
        var to = new IPEndPoint(IPAddress.Loopback, rx.Port);
        using var phone = Client("127.0.0.2");
        var buf = new byte[256];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);

        byte[] NextChallenge()
        {
            phone.SendTo(Audio(1), to);
            while (true)
            {
                int n = phone.ReceiveFrom(buf, ref from);
                Assert.True(Protocol.TryDecode(buf.AsSpan(0, n), out var h, out _));
                if (h.Type == Protocol.TPairChallenge) return buf.AsSpan(Protocol.HeaderSize, Pairing.NonceSize).ToArray();
            }
        }

        var nonce = NextChallenge();
        Assert.Null(rx.Peer);
        Assert.NotNull(rx.PendingPairing);
        Assert.Equal(0, rx.Snapshot().Received);

        // Wrong code: rejected.
        var bad = new byte[Pairing.ProofSize];
        Pairing.WriteProof(Pairing.DeriveKey("000000"), nonce, bad);
        phone.SendTo(Protocol.Encode(Protocol.TPairProof, 1, 0, bad), to);
        Thread.Sleep(150);
        Assert.Null(rx.Peer);

        // Right code on a fresh challenge: HELLO back and connected.
        Thread.Sleep(1100); // challenges are re-sent at most once per second
        nonce = NextChallenge();
        var good = new byte[Pairing.ProofSize];
        Pairing.WriteProof(key, nonce, good);
        phone.SendTo(Protocol.Encode(Protocol.TPairProof, 2, 0, good), to);
        bool hello = false;
        for (int i = 0; i < 5 && !hello; i++)
        {
            int n = phone.ReceiveFrom(buf, ref from);
            hello = Protocol.TryDecode(buf.AsSpan(0, n), out var h, out _) && h.Type == Protocol.THello;
        }
        Assert.True(hello);
        Assert.True(WaitFor(() => rx.Peer is not null));
        phone.SendTo(Audio(2), to);
        Assert.True(WaitFor(() => rx.Snapshot().Received >= 1));

        // Replaying the same proof after the session ends does not pair again (nonce is single-use).
        phone.SendTo(Protocol.Encode(Protocol.TBye, 3, 0), to);
        Assert.True(WaitFor(() => rx.Peer is null));
        phone.SendTo(Protocol.Encode(Protocol.TPairProof, 4, 0, good), to);
        Thread.Sleep(200);
        Assert.Null(rx.Peer);
    }

    // ------------------------------------------------------------------ L5: malformed AUDIO

    [Fact]
    public void MalformedAudio_NeitherConnectsNorResizesTheJitterBuffer()
    {
        using var rx = new Receiver(0, "127.0.0.1", 10, pingInterval: 0.1); rx.Start();
        var to = new IPEndPoint(IPAddress.Loopback, rx.Port);
        double target0 = rx.EffectiveJitterMs;
        using var s = Client("127.0.0.1");
        ushort nonPcm = (ushort)(Protocol.MakeFlags() | 0x0001);
        s.SendTo(Audio(1, frames: 65535, flags: nonPcm, payload: 16), to);   // codec != PCM16, huge frame count
        s.SendTo(Audio(2, frames: 0, payload: 0), to);                       // zero frames
        s.SendTo(Audio(3, frames: 2000), to);                                // > 40 ms, consistent length
        s.SendTo(Audio(4, flags: Protocol.MakeFlags(channels: 2)), to);      // wrong channel count
        Thread.Sleep(250);
        Assert.Null(rx.Peer);
        Assert.Equal(240, rx.FramesPerPacket);
        Assert.Equal(240, rx.Jitter.FramesPerPacket);
        Assert.Equal(target0, rx.EffectiveJitterMs);
        Assert.Equal(0, rx.Snapshot().Received);
        s.SendTo(Audio(5, frames: 480), to);                                 // 10 ms is valid and may resize
        Assert.True(WaitFor(() => rx.FramesPerPacket == 480 && rx.Peer is not null));
    }

    [Fact]
    public void AudioHeaderValidation()
    {
        Assert.True(Protocol.TryDecode(Audio(1), out var ok, out _));
        Assert.True(Protocol.IsAcceptableAudio(ok, 48000, 1, out _));
        Assert.True(Protocol.TryDecode(Audio(1, frames: 1921), out var big, out _));
        Assert.False(Protocol.IsAcceptableAudio(big, 48000, 1, out var why));
        Assert.Contains("frame count", why);
        Assert.True(Protocol.TryDecode(Audio(1, flags: (ushort)(Protocol.MakeFlags() | 3), payload: 7), out var codec, out _));
        Assert.False(Protocol.IsAcceptableAudio(codec, 48000, 1, out why));
        Assert.Contains("codec", why);
        Assert.True(Protocol.TryDecode(Audio(1, flags: Protocol.MakeFlags(sampleRate: 16000)), out var rate, out _));
        Assert.False(Protocol.IsAcceptableAudio(rate, 48000, 1, out _));
    }

    // ------------------------------------------------------------------ L3: concurrent jitter-buffer swaps

    [Fact]
    public void ConcurrentReconfiguration_KeepsBufferAndPacketSizeConsistent()
    {
        using var rx = new Receiver(0, "127.0.0.1", 10, pingInterval: 0.1); rx.Start();
        var to = new IPEndPoint(IPAddress.Loopback, rx.Port);
        using var s = Client("127.0.0.1");
        bool stop = false;
        var ui = new Thread(() => { int i = 0; while (!Volatile.Read(ref stop)) { rx.SetJitterMs(i++ % 2 == 0 ? 5 : 20); rx.SetRenderPeriodMs(i % 3 == 0 ? 10 : 3); } });
        ui.Start();
        for (uint i = 0; i < 400; i++) s.SendTo(Audio(i, frames: i % 2 == 0 ? (ushort)240 : (ushort)480), to);
        Thread.Sleep(200);
        Volatile.Write(ref stop, true);
        ui.Join();
        s.SendTo(Audio(1000, frames: 240), to);
        Assert.True(WaitFor(() => rx.FramesPerPacket == 240));
        Thread.Sleep(50);
        Assert.Equal(rx.FramesPerPacket, rx.Jitter.FramesPerPacket);
    }

    // ------------------------------------------------------------------ L1: Studio dispose race

    [Fact]
    public void StudioProcessor_PullAfterDispose_IsSilentAndSafe()
    {
        if (!Rnnoise.IsAvailable) return;
        var st = new StudioProcessor(new StudioChain(StudioPreset.Balanced, autoVolume: false), dst => dst.Clear(), 10);
        var buf = new byte[960];
        st.Pull(buf);
        var puller = new Thread(() => { var b = new byte[960]; for (int i = 0; i < 2000; i++) st.Pull(b); });
        puller.Start();
        Thread.Sleep(5);
        st.Dispose();
        puller.Join();
        Assert.True(st.NativeReleased);
        buf.AsSpan().Fill(7);
        st.Pull(buf);
        Assert.All(buf, b => Assert.Equal(0, b));
        st.Dispose(); // idempotent
    }

    // ------------------------------------------------------------------ L4: logging

    [Fact]
    public void LogLimiter_LogsFirst_ThenCountsRepeats()
    {
        var lim = new LogLimiter(TimeSpan.FromSeconds(10));
        long f = System.Diagnostics.Stopwatch.Frequency;
        Assert.True(lim.ShouldLog("k", 0, out int s0)); Assert.Equal(0, s0);
        for (int i = 1; i <= 99; i++) Assert.False(lim.ShouldLog("k", i * f / 100, out _));
        Assert.True(lim.ShouldLog("other", f, out _));
        Assert.True(lim.ShouldLog("k", 11 * f, out int s1));
        Assert.Equal(99, s1);
        Assert.Equal("x (99 similar suppressed)", LogLimiter.WithCount("x", s1));
    }

    [Fact]
    public void FileLog_QueuesOffThread_AndIsBounded()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cordmic-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Without the writer thread nothing touches the disk until Flush, and the queue stops at MaxQueued.
            var path = Path.Combine(dir, "bounded.log");
            var log = new FileLog(path, backgroundWriter: false);
            int total = FileLog.MaxQueued * 3;
            for (int i = 0; i < total; i++) log.Write("line " + i);
            Assert.False(File.Exists(path));
            Assert.Equal(FileLog.MaxQueued * 2, log.Dropped);
            log.Flush();
            var lines = File.ReadAllLines(path);
            Assert.Equal(FileLog.MaxQueued + 1, lines.Length);
            Assert.EndsWith("line 0", lines[0]);
            Assert.Contains($"({FileLog.MaxQueued * 2} log lines dropped: queue full)", lines[^1]);

            // With the writer thread, lines arrive in order without an explicit flush.
            var path2 = Path.Combine(dir, "live.log");
            var live = new FileLog(path2);
            for (int i = 0; i < 100; i++) live.Write("n " + i);
            Assert.True(WaitFor(() => File.Exists(path2) && File.ReadAllLines(path2).Length == 100, 3000));
            live.Dispose();
            Assert.EndsWith("n 99", File.ReadAllLines(path2)[^1]);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ------------------------------------------------------------------ L7: settings durability

    [Fact]
    public void Settings_SaveKeepsBackup_AndCorruptFileIsKeptAside()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cordmic-set-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "settings.json");
            new AppSettings { MicGainDb = 9 }.Save(path);
            new AppSettings { MicGainDb = 12, PairingCode = "123456" }.Save(path);
            Assert.True(File.Exists(path + ".bak"));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
            Assert.Equal(12, AppSettings.Load(path).MicGainDb);
            Assert.Equal("123456", AppSettings.Load(path).PairingCode);

            File.WriteAllText(path, "{ not json");
            var s = AppSettings.Load(path, out var problem);
            Assert.Equal(9, s.MicGainDb); // the backup (previous save)
            Assert.NotNull(problem);
            var bad = Directory.GetFiles(dir, "settings.json.bad-*");
            Assert.Single(bad);
            Assert.Equal("{ not json", File.ReadAllText(bad[0]));
            // A second corrupt load keeps the first copy and adds another one.
            AppSettings.Load(path, out _);
            Assert.Equal(2, Directory.GetFiles(dir, "settings.json.bad-*").Length);

            File.WriteAllText(path, "{\"PairingCode\":\"12x\"}");
            File.Delete(path + ".bak");
            Assert.Null(AppSettings.Load(path).PairingCode);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ------------------------------------------------------------------ M1: staged downloads

    [Fact]
    public void SecureFiles_PrivateDirectory_And_VerifiedHandle()
    {
        var root = Path.Combine(Path.GetTempPath(), "cordmic-sec-" + Guid.NewGuid().ToString("N"));
        try
        {
            var d1 = SecureFiles.CreatePrivateDirectory(root, "update-");
            var d2 = SecureFiles.CreatePrivateDirectory(root, "update-");
            Assert.NotEqual(d1, d2);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(d1));
            var f = Path.Combine(d1, "setup.exe");
            var data = System.Text.Encoding.ASCII.GetBytes("installer bytes");
            SecureFiles.WriteNew(f, data);
            Assert.Throws<IOException>(() => SecureFiles.WriteNew(f, data)); // never overwrites
            string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();
            using (var h = SecureFiles.OpenLockedVerified(f, sha))
            {
                Assert.Equal(0, h.Position);
                Assert.False(h.CanWrite);
            }
            Assert.Throws<InvalidDataException>(() => SecureFiles.OpenLockedVerified(f, new string('0', 64)));
            Assert.Equal("CordMic-Setup-0.6.1.exe", AppUpdate.SafeAssetFileName("CordMic-Setup-0.6.1.exe", "x.exe"));
            Assert.Equal("x.exe", AppUpdate.SafeAssetFileName("..\\evil.exe", "x.exe"));
            Assert.Equal("x.exe", AppUpdate.SafeAssetFileName(".hidden", "x.exe"));
            Assert.Equal("x.exe", AppUpdate.SafeAssetFileName(null, "x.exe"));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (n, t) in entries)
            {
                using var w = new StreamWriter(z.CreateEntry(n).Open());
                w.Write(t);
            }
        return ms.ToArray();
    }

    [Fact]
    public void VbCable_ExtractLocked_VerifiesEveryFile_AndRejectsZipSlip()
    {
        var root = Path.Combine(Path.GetTempPath(), "cordmic-vb-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dir = SecureFiles.CreatePrivateDirectory(root, "vbcable-");
            using (var locks = new LockedFiles())
            {
                var setup = VbCable.ExtractLocked(Zip(("VBCABLE_Setup_x64.exe", "MZ"), ("sub/vbMmeCable64_win10.inf", "inf")), dir, locks);
                Assert.Equal(Path.Combine(dir, "VBCABLE_Setup_x64.exe"), setup);
                Assert.Equal(2, locks.Handles.Count);
                Assert.All(locks.Handles, h => Assert.False(h.CanWrite));
            }
            var dir2 = SecureFiles.CreatePrivateDirectory(root, "vbcable-");
            using (var locks = new LockedFiles())
                Assert.Throws<InvalidDataException>(() => VbCable.ExtractLocked(Zip(("../evil.exe", "x"), ("VBCABLE_Setup_x64.exe", "MZ")), dir2, locks));
            Assert.False(File.Exists(Path.Combine(root, "evil.exe")));
            var dir3 = SecureFiles.CreatePrivateDirectory(root, "vbcable-");
            using (var locks = new LockedFiles())
                Assert.Throws<InvalidDataException>(() => VbCable.ExtractLocked(Zip(("readme.txt", "x")), dir3, locks));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // ------------------------------------------------------------------ N5: receive loop survives

    [Fact]
    public void ReceiveLoop_SurvivesGarbage_AndKeepsWorking()
    {
        using var rx = new Receiver(0, "127.0.0.1", 10, pingInterval: 0.1); rx.Start();
        var to = new IPEndPoint(IPAddress.Loopback, rx.Port);
        using var s = Client("127.0.0.1");
        var rnd = new Random(5);
        for (int i = 0; i < 300; i++)
        {
            var b = new byte[rnd.Next(0, 200)];
            rnd.NextBytes(b);
            if (b.Length >= 4 && i % 2 == 0) { b[0] = (byte)'U'; b[1] = (byte)'M'; b[2] = 1; b[3] = (byte)rnd.Next(0, 10); }
            s.SendTo(b, to);
        }
        s.SendTo(Audio(1), to);
        Assert.True(WaitFor(() => rx.Peer is not null && rx.Snapshot().Received >= 1));
    }
}
