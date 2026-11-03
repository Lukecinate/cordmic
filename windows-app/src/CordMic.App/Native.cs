using System.Runtime.InteropServices;

namespace CordMic.App;

internal static partial class Native
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>Index of the interface Windows would use to reach <paramref name="destAddr"/> (network byte order).</summary>
    [DllImport("iphlpapi.dll")]
    public static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    /// <summary>Register the calling thread with MMCSS ("Pro Audio") so the scheduler favours it under game load.</summary>
    public static void BoostAudioThread()
    {
        try { uint i = 0; AvSetMmThreadCharacteristics("Pro Audio", ref i); } catch (Exception) { }
    }
}
