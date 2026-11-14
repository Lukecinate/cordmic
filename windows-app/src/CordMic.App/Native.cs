using System.Runtime.InteropServices;

namespace CordMic.App;

internal static partial class Native
{
    [DllImport("gdi32.dll")]
    public static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont, IntPtr pdv, ref uint pcFonts);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>Index of the interface Windows would use to reach <paramref name="destAddr"/> (network byte order).</summary>
    [DllImport("iphlpapi.dll")]
    public static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    private static int _systemDpi;

    /// <summary>System DPI (the DPI GDI+ uses to turn font points into pixels in this process); 96 if unknown.</summary>
    public static int SystemDpi()
    {
        if (_systemDpi > 0) return _systemDpi;
        int d = 96;
        try { d = (int)GetDpiForSystem(); } catch (Exception) { }
        return _systemDpi = d > 0 ? d : 96;
    }

    /// <summary>Windows display language as a culture-like tag ("id" for Indonesian), independent of
    /// globalization-invariant mode. Falls back to CultureInfo.CurrentUICulture.</summary>
    public static string OsUiLanguage()
    {
        try
        {
            // Primary language ID lives in the low 10 bits of the LANGID; 0x21 = LANG_INDONESIAN.
            var langId = GetUserDefaultUILanguage();
            if ((langId & 0x3FF) == 0x21) return "id";
            if (langId != 0) return "en";
        }
        catch (Exception) { }
        return CordMic.Core.L.OsLanguageFromCulture();
    }

    /// <summary>Register the calling thread with MMCSS ("Pro Audio") so the scheduler favours it under game load.</summary>
    public static void BoostAudioThread()
    {
        try { uint i = 0; AvSetMmThreadCharacteristics("Pro Audio", ref i); } catch (Exception) { }
    }
}
