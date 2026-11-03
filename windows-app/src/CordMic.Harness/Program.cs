// Console harness: runs the C# receiver core with a fake 5 ms audio clock (no sound card).
// Used to test the C# receiver against windows-receiver/sim_sender.py (or the real phone) on any OS.
//   dotnet run -- --port 50005 --seconds 6 --jitter-ms 10 [--probe 127.0.0.1:50006]
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using CordMic.Core;

int port = Protocol.DefaultPort; double seconds = 10, jitter = 10; string bind = "0.0.0.0";
var probes = new List<IPEndPoint>();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port": port = int.Parse(args[++i]); break;
        case "--seconds": seconds = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--jitter-ms": jitter = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--bind": bind = args[++i]; break;
        case "--probe": probes.Add(IPEndPoint.Parse(args[++i])); break;
    }
}
using var rx = new Receiver(port, bind, jitter, pingInterval: 0.1) { Log = m => Console.Error.WriteLine("[rx] " + m) };
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
