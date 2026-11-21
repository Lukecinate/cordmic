using System.Diagnostics;

namespace CordMic.Tests;

/// <summary>
/// High-resolution waits for real-time test loops. Thread.Sleep on Windows often
/// has a ~15 ms timer tick, which makes 5–10 ms audio cadences systematically late
/// and trips underrun/overflow or "cadence not held" assertions on GitHub-hosted
/// windows-2022 runners.
/// </summary>
internal static class TestPace
{
    public static void WaitUntilSeconds(Stopwatch sw, double targetSeconds) =>
        WaitUntilMs(sw, targetSeconds * 1000.0);

    public static void WaitUntilMs(Stopwatch sw, double targetMs)
    {
        while (true)
        {
            double left = targetMs - sw.Elapsed.TotalMilliseconds;
            if (left <= 0) return;
            if (left > 16) Thread.Sleep(1);
            else Thread.SpinWait(80);
        }
    }
}
