namespace CordMic.Core;

/// <summary>0.8.0 M0 (F7): device names from Windows often have trailing spaces or doubled whitespace.</summary>
public static class DeviceNames
{
    public static string Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var chars = name.Trim().ToCharArray();
        int w = 0;
        bool gap = false;
        foreach (char c in chars)
        {
            if (char.IsWhiteSpace(c)) { gap = true; continue; }
            if (gap && w > 0) chars[w++] = ' ';
            gap = false;
            chars[w++] = c;
        }
        return new string(chars, 0, w);
    }
}
