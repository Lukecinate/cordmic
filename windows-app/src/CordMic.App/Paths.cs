using Microsoft.Win32;

namespace CordMic.App;

internal static class Paths
{
    public static string AppData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CordMic");
    public static string Settings => Path.Combine(AppData, "settings.json");
    public static string LogDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CordMic", "logs");
    public static string Log => Path.Combine(LogDir, "cordmic.log");
    public static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;
    public static string ExeDir => Path.GetDirectoryName(ExePath)!;
    public static string MetricScript => Path.Combine(ExeDir, "tools", "set-tether-metric.ps1");
    /// <summary>Parent of the per-run private download directories (0.6.1, audit M1).</summary>
    public static string Staging => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CordMic", "staging");

    // Pre-0.3.0 product name. Used only for one-time migration (settings + autostart), see Migration below.
    public const string LegacyName = "UsbMic";
    public static string LegacyAppData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyName);
}

/// <summary>
/// One-time upgrade from the old "UsbMic" brand (&lt;= 0.2.0): copies %APPDATA%\UsbMic\settings.json into
/// %APPDATA%\CordMic when CordMic has no settings yet, and replaces the old HKCU Run value with the CordMic one.
/// Old folders are left in place (harmless, and allow a rollback).
/// </summary>
internal static class Migration
{
    public static List<string> Run()
    {
        var notes = new List<string>();
        try
        {
            var oldSettings = Path.Combine(Paths.LegacyAppData, "settings.json");
            if (CordMic.Core.LegacySettings.MigrateIfNeeded(oldSettings, Paths.Settings))
                notes.Add($"migrated settings from {oldSettings}");
        }
        catch (Exception ex) { notes.Add("settings migration failed: " + ex.Message); }

        try
        {
            if (Startup.MigrateLegacy()) notes.Add("migrated autostart entry from " + Paths.LegacyName);
        }
        catch (Exception ex) { notes.Add("autostart migration failed: " + ex.Message); }
        return notes;
    }
}

/// <summary>"Start with Windows" = HKCU Run value (per user, no admin needed). The installer sets the same value.</summary>
internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "CordMic";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(Name) is string s && s.Length > 0;
    }

    /// <summary>If the old "UsbMic" Run value exists: remove it and, since autostart was on, enable CordMic instead.</summary>
    public static bool MigrateLegacy()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (k?.GetValue(Paths.LegacyName) is not string) return false;
        k.DeleteValue(Paths.LegacyName, false);
        if (k.GetValue(Name) is null) k.SetValue(Name, $"\"{Paths.ExePath}\" --tray");
        return true;
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (on) k.SetValue(Name, $"\"{Paths.ExePath}\" --tray");
        else k.DeleteValue(Name, false);
    }
}
