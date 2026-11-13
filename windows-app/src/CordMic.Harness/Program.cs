// Console harness: runs the C# receiver core with a fake 5 ms audio clock (no sound card).
// Used to test the C# receiver against windows-receiver/sim_sender.py (or the real phone) on any OS.
//   dotnet run -- --port 50005 --seconds 6 --jitter-ms 10 [--probe 127.0.0.1:50006] [--any-source] [--pairing-code 123456]
// Since 0.6.1 the receiver accepts loopback only unless --any-source is given (a real phone on another host).
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using CordMic.Core;
using CordMic.Core.Dsp;

// Dev tool for tools/harness (0.6.0): run the shipping Studio chain on raw float32 48 kHz mono.
//   dotnet run -- studio --preset balanced|whisper|max --in x.f32 --out y.f32 [--autovol] [--start-gain 15] [--vad v.f32] [--gain g.f32]
// Output is aligned with the input (chain delay removed); --vad / --gain are per 480-sample processing frame.
if (args.Length > 0 && args[0] == "studio") return StudioHost.Run(args);

int port = Protocol.DefaultPort; double seconds = 10, jitter = 10; string bind = "0.0.0.0";
var probes = new List<IPEndPoint>();
bool anySource = false; string? pairingCode = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port": port = int.Parse(args[++i]); break;
        case "--seconds": seconds = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--jitter-ms": jitter = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--bind": bind = args[++i]; break;
        case "--probe": probes.Add(IPEndPoint.Parse(args[++i])); break;
        case "--any-source": anySource = true; break;
        case "--pairing-code": pairingCode = args[++i]; break;
    }
}
using var rx = new Receiver(port, bind, jitter, pingInterval: 0.1)
{
    Log = m => Console.Error.WriteLine("[rx] " + m),
    Policy = new SourcePolicy(null, pairingCode is null ? null : Pairing.DeriveKey(pairingCode), anySource),
};
rx.SetProbeTargets(probes);
rx.Start();
Console.Error.WriteLine($"READY port={rx.Port}");
var block = new byte[480];
long nonsilent = 0, blocks = 0; double maxDepth = 0;
var sw = Stopwatch.StartNew();
var next = 0.0;
while (sw.Elapsed.TotalSeconds < seconds)
{
    next += 0.005;
    var wait = next - sw.Elapsed.TotalSeconds;
    if (wait > 0) Thread.Sleep(TimeSpan.FromSeconds(wait));
    rx.Pull(block);
    blocks++;
    foreach (var b in block) if (b != 0) { nonsilent++; break; }
    maxDepth = Math.Max(maxDepth, rx.Jitter.DepthMs());
}
var s = rx.Snapshot();
Console.Error.WriteLine(s.Line());
Console.WriteLine(JsonSerializer.Serialize(new
{
    s.Received, s.Expected, s.Lost, s.Reordered, s.Late, s.Concealed, s.Underruns, s.OverflowDrops,
    s.RttMs, s.RttMinMs, s.ArrivalJitterMs, blocks, nonsilent, maxDepthMs = maxDepth,
}));
return 0;

static class StudioHost
{
    /// <summary>The PC instance of the 0.5.7 Strong suppressor exactly as the Studio chain runs it (aligned output).</summary>
    static int StrongOnly(string inp, string outp)
    {
        var bytes = File.ReadAllBytes(inp);
        var x = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, x, 0, x.Length * 4);
        int d = StudioChain.StrongDelay;
        var s = new short[x.Length + d + 480];
        for (int i = 0; i < x.Length; i++) s[i] = (short)Math.Clamp((int)Math.Round(x[i] * 32768.0), short.MinValue, short.MaxValue);
        var vc = new VoiceChain(48000) { Mode = NoiseMode.Strong, Agc = false, Expander = NoiseMode.Off, VoiceClarity = false, GainDb = 0 };
        for (int f = 0; f + 480 <= s.Length; f += 480) vc.Process(s.AsSpan(f, 480));
        var y = new float[x.Length];
        for (int i = 0; i < x.Length; i++) y[i] = s[i + d] / 32768f;
        var ob = new byte[y.Length * 4];
        Buffer.BlockCopy(y, 0, ob, 0, ob.Length);
        File.WriteAllBytes(outp, ob);
        return 0;
    }

    public static int Run(string[] a)
    {
        string preset = "balanced", inp = "", outp = "", vadOut = "", gainOut = "", mixOut = ""; bool autovol = false; double start = AgcB3.DefaultStartGainDb;
        for (int i = 1; i < a.Length; i++)
            switch (a[i])
            {
                case "--preset": preset = a[++i]; break;
                case "--in": inp = a[++i]; break;
                case "--out": outp = a[++i]; break;
                case "--vad": vadOut = a[++i]; break;
                case "--gain": gainOut = a[++i]; break;
                case "--mix": mixOut = a[++i]; break;
                case "--autovol": autovol = true; break;
                case "--start-gain": start = double.Parse(a[++i], System.Globalization.CultureInfo.InvariantCulture); break;
            }
        if (preset == "strong") return StrongOnly(inp, outp);
        var p = preset switch { "whisper" => StudioPreset.Whisper, "max" => StudioPreset.Max, _ => StudioPreset.Balanced };
        var bytes = File.ReadAllBytes(inp);
        var x = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, x, 0, x.Length * 4);
        int n = x.Length / StudioChain.Frame * StudioChain.Frame;
        // Pad by the chain delay so the output can be returned aligned with the input.
        int d = StudioChain.RnnoiseDelay, total = (n + d + StudioChain.Frame - 1) / StudioChain.Frame * StudioChain.Frame;
        var buf = new float[total];
        Array.Copy(x, buf, n);
        var vads = new List<float>(); var gains = new List<float>(); var mixes = new List<float>();
        using var c = new StudioChain(p, autovol, start);
        for (int f = 0; f < total; f += StudioChain.Frame) { c.Process(buf.AsSpan(f, StudioChain.Frame)); vads.Add((float)c.LastVad); gains.Add((float)c.Agc.GainDb); mixes.Add((float)(c.Guard?.Mix ?? 0)); }
        var y = new float[x.Length];
        Array.Copy(buf, d, y, 0, Math.Min(n, total - d));
        var ob = new byte[y.Length * 4];
        Buffer.BlockCopy(y, 0, ob, 0, ob.Length);
        File.WriteAllBytes(outp, ob);
        if (vadOut != "") { var vb = new byte[vads.Count * 4]; Buffer.BlockCopy(vads.ToArray(), 0, vb, 0, vb.Length); File.WriteAllBytes(vadOut, vb); }
        if (mixOut != "") { var mb = new byte[mixes.Count * 4]; Buffer.BlockCopy(mixes.ToArray(), 0, mb, 0, mb.Length); File.WriteAllBytes(mixOut, mb); }
        if (gainOut != "") { var gb = new byte[gains.Count * 4]; Buffer.BlockCopy(gains.ToArray(), 0, gb, 0, gb.Length); File.WriteAllBytes(gainOut, gb); }
        Console.Error.WriteLine($"studio {p}: {n} samples, delay {d}, final AGC gain {c.Agc.GainDb:F1} dB");
        return 0;
    }
}
