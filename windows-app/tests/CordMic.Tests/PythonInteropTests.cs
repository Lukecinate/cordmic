using System.Diagnostics;
using CordMic.Core;
using Xunit;
using Xunit.Abstractions;

/// <summary>Runs the reference Python simulator (windows-receiver/sim_sender.py) against the C# Receiver.
/// Skipped silently when python3 or the folder is not available.</summary>
public class PythonInteropTests
{
    private readonly ITestOutputHelper _out;
    public PythonInteropTests(ITestOutputHelper o) => _out = o;

    static string? FindReceiverDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            var p = Path.Combine(d.FullName, "windows-receiver", "sim_sender.py");
            if (File.Exists(p)) return Path.GetDirectoryName(p);
            d = d.Parent;
        }
        return null;
    }

    [Fact]
    public void PythonSimSenderToCSharpReceiver()
    {
        var dir = FindReceiverDir();
        if (dir == null) { _out.WriteLine("SKIP: windows-receiver not found"); return; }
        using var rx = new Receiver(0, "127.0.0.1", 10, pingInterval: 0.1); rx.Start();
        var psi = new ProcessStartInfo("python3", $"sim_sender.py --host 127.0.0.1 --port {rx.Port} --seconds 2 --loss 0.02 --reorder 0.02")
        { WorkingDirectory = dir, RedirectStandardOutput = true, UseShellExecute = false };
        Process? p;
        try { p = Process.Start(psi); } catch (System.ComponentModel.Win32Exception) { _out.WriteLine("SKIP: no python3"); return; }
        // consume in real time so the jitter buffer behaves like playback
        var b = new byte[480]; var sw = Stopwatch.StartNew(); double next = 0;
        while (!p!.HasExited) { next += 0.005; var w = next - sw.Elapsed.TotalSeconds; if (w > 0) Thread.Sleep(TimeSpan.FromSeconds(w)); rx.Pull(b); }
        var stdout = p.StandardOutput.ReadToEnd();
        _out.WriteLine(stdout);
        var s = rx.Snapshot(); _out.WriteLine(s.Line());
        var m = System.Text.RegularExpressions.Regex.Match(stdout, @"'dropped': (\d+).*'pongs': (\d+)");
        Assert.True(m.Success, stdout);
        Assert.InRange(s.Expected, 398, 400);  // 2 s / 5 ms
        Assert.InRange(s.Lost, int.Parse(m.Groups[1].Value) - 2, int.Parse(m.Groups[1].Value) + 2);
        Assert.True(int.Parse(m.Groups[2].Value) > 5);
        Assert.Contains("HELLO", stdout);
    }
}
