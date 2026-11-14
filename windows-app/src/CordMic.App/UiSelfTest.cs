using CordMic.Core;

namespace CordMic.App;

/// <summary>
/// 0.7.2: <c>CordMic.exe --self-test-ui</c>. Builds the main window and every Settings page in English and Indonesian at
/// 100%, 150% and 200% without showing them, and writes the result to <c>%LOCALAPPDATA%\CordMic\logs\ui-self-test.txt</c>
/// and the log. Exit code 0 = all pages built. WinForms cannot run on the Linux build machine, so this is the check
/// that runs on a real PC (the 0.7.0/0.7.1 Settings crash would have failed it on the first page).
/// </summary>
internal static class UiSelfTest
{
    public const string Arg = "--self-test-ui";
    public static readonly int[] Dpis = { 96, 144, 192 };

    public static int Run(AppController app, bool quiet)
    {
        var lines = new List<string> { $"CordMic {app.CurrentVersion} UI self-test {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
        int failures = 0;
        var lang = L.Current;
        try
        {
            foreach (var code in new[] { L.English, L.Indonesian })
            {
                L.Set(code);
                try
                {
                    using (var m = new MainForm(app)) { m.SelfTest(); lines.Add($"[{code}] main window: ok"); }
                }
                catch (Exception ex) { failures++; lines.Add($"[{code}] main window: FAILED {ex}"); }
                foreach (var dpi in Dpis)
                {
                    try
                    {
                        using var f = new SettingsForm(app, SettingsPage.Connection);
                        foreach (var l in f.SelfTest(dpi)) lines.Add($"[{code}] settings {l}");
                    }
                    catch (Exception ex) { failures++; lines.Add($"[{code}] settings @ {dpi * 100 / 96}%: FAILED {ex}"); }
                }
            }
        }
        finally { L.Set(lang); }
        lines.Add(failures == 0 ? "result: all windows built" : $"result: {failures} failure(s)");
        var outPath = Path.Combine(Paths.LogDir, "ui-self-test.txt");
        try { Directory.CreateDirectory(Paths.LogDir); File.WriteAllLines(outPath, lines); } catch (Exception) { }
        foreach (var l in lines) app.Log("ui self-test: " + l);
        app.FlushLog();
        if (!quiet)
            MessageBox.Show((failures == 0 ? "All windows built." : $"{failures} failure(s).") + "\n\n" + outPath,
                "CordMic UI self-test", MessageBoxButtons.OK, failures == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        return failures == 0 ? 0 : 1;
    }
}
