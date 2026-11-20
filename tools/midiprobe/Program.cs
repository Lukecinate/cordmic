using System.Globalization;
using CordMic.Core.Usb;

namespace CordMic.MidiProbe;

/// <summary>
/// M1 USB MIDI feasibility probe. Simulated runs work everywhere; WinMM capture is
/// PENDING-HARDWARE on the user's PC. Route 1b stays out of Auto until G-M1.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
        {
            Console.Write("""
CordMic MIDI probe (M1). Simulated by default.

  cordmic-midiprobe --sim [--loss N] [--truncate] [--count N] [--payload N] [--gap-ms N]
  cordmic-midiprobe --run all     Windows + phone in MIDI mode (PENDING-HARDWARE)

Gates M-1…M-7 are decided only from a hardware run. This tool never enables route 1b.

""");
            return 0;
        }
        if (args[0] == "--sim")
        {
            int count = Arg(args, "--count", 1000);
            int payload = Arg(args, "--payload", 40);
            int gap = Arg(args, "--gap-ms", 1);
            int loss = Arg(args, "--loss", 0);
            bool trunc = args.Contains("--truncate");
            var received = new List<(uint, uint, int, string?)>();
            var body = new byte[payload];
            for (int i = 0; i < body.Length; i++) body[i] = (byte)(i * 17);
            uint time = 0;
            for (uint i = 0; i < (uint)count; i++, time += (uint)gap)
            {
                if (loss > 0 && i % (uint)Math.Max(count / loss, 1) == 3 && received.Count < loss + 1)
                    continue;
                var msg = MidiProbeCodec.Encode(i, time, body);
                if (trunc && i == (uint)count / 2)
                {
                    received.Add((i, time, 0, "truncated"));
                    continue;
                }
                if (!MidiProbeCodec.TryDecode(msg, out var seq, out var t, out var p, out var err) || err is not null)
                {
                    received.Add((i, time, 0, err ?? "invalid"));
                    continue;
                }
                received.Add((seq, t, p.Length, null));
            }
            var st = MidiProbeCodec.Measure(received, 0, count);
            double seconds = count * gap / 1000.0;
            Console.WriteLine("mode=sim");
            Console.WriteLine("gate=PENDING-HARDWARE");
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "expected={0} received={1} lost={2} truncated={3} p99_gap_ms={4:F2}", st.Expected, st.Received, st.Lost, st.Truncated, st.P99GapMs));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "throughput_gate={0} jitter_gate={1}", st.MeetsThroughputGate(seconds), st.MeetsJitterGate()));
            Console.WriteLine("note=Simulated numbers are not a G-M1 decision.");
            return 0;
        }
        if (args[0] == "--run")
        {
            Console.WriteLine("mode=hardware");
            Console.WriteLine("gate=PENDING-HARDWARE");
            Console.WriteLine("note=WinMM midiIn capture runs on the user's Windows PC with the phone in MIDI mode.");
            Console.WriteLine("note=Install the test APK, choose MIDI in the USB notification, then re-run --run all (about 25 min).");
            return 0;
        }
        Console.Error.WriteLine("unknown option; try --help");
        return 2;
    }

    private static int Arg(string[] args, string name, int fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name && int.TryParse(args[i + 1], out int v)) return v;
        return fallback;
    }
}
