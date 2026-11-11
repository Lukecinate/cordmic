using System.Numerics;

namespace CordMic.Tests;

/// <summary>Measurement helpers for the 0.6.0 DSP tests.</summary>
internal static class TestDsp
{
    public static void Fft(Complex[] a)
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            var wl = new Complex(Math.Cos(ang), Math.Sin(ang));
            for (int i = 0; i < n; i += len)
            {
                Complex w = 1;
                for (int k = 0; k < len / 2; k++)
                {
                    var u = a[i + k]; var v = a[i + k + len / 2] * w;
                    a[i + k] = u + v; a[i + k + len / 2] = u - v; w *= wl;
                }
            }
        }
    }

    /// <summary>
    /// THD+N of a tone, as in research exp/asrc.py but with a Blackman-Harris window: windowed spectrum of 65536 samples from <paramref name="start"/>,
    /// tone power = ±20 bins (±15 Hz) around the peak, everything else is distortion plus noise. dB relative to the tone.
    /// </summary>
    public static double ToneSpurDb(ReadOnlySpan<float> y, int start)
    {
        const int N = 65536;
        var a = new Complex[N];
        for (int i = 0; i < N; i++)
        {
            double t = 2 * Math.PI * i / (N - 1); // 4-term Blackman-Harris: sidelobes -92 dB (a Hann window leaks to about -82 dB here)
            a[i] = y[start + i] * (0.35875 - 0.48829 * Math.Cos(t) + 0.14128 * Math.Cos(2 * t) - 0.01168 * Math.Cos(3 * t));
        }
        Fft(a);
        var p = new double[N / 2 + 1];
        int k = 0;
        for (int i = 0; i <= N / 2; i++) { p[i] = a[i].Magnitude * a[i].Magnitude; if (p[i] > p[k]) k = i; }
        double sig = 0, tot = 0;
        for (int i = 0; i <= N / 2; i++) { tot += p[i]; if (Math.Abs(i - k) <= 20) sig += p[i]; }
        return 10 * Math.Log10((tot - sig) / sig);
    }
}
