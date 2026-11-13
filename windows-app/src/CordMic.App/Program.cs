using CordMic.Core;
namespace CordMic.App;

internal static class Program
{
    private const string MutexName = @"Local\CordMic.SingleInstance";
    private const string ShowEventName = @"Local\CordMic.ShowWizard";
    /// <summary>0.6.2: set by the installer (before it force-closes CordMic) so the running app quits cleanly and flushes its log.</summary>
    internal const string QuitEventName = @"Local\CordMic.Quit";

    [STAThread]
    private static int Main(string[] args)
    {
        // Helper verbs run before the single-instance mutex so an elevated
        // --configure-cable launched from a running tray app actually does work.
        if (CableHelperArgs.MentionsHelperVerb(args))
        {
            if (!CableHelperArgs.TryParse(args, out var verb, out var err) || verb == CableHelperArgs.Verb.None)
            {
                Console.Error.WriteLine(err ?? "invalid helper arguments");
                return CableHelperArgs.ExitUsage;
            }
            return CableHelper.Run(verb);
        }

        bool tray = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        using var mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // Already running (e.g. started at logon): a Start-menu click just opens the guide in that instance.
            try { if (!tray && EventWaitHandle.TryOpenExisting(ShowEventName, out var ev)) { ev.Set(); ev.Dispose(); } }
            catch (Exception) { }
            return 0;
        }

        // Avoid blocking gen-2 GCs pausing the audio threads (the receive and render loops are allocation-light).
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        using var quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        using var ctx = new AppController(openWizard: !tray);
        Application.ThreadException += (_, e) => { ctx.Log("ui exception: " + e.Exception); ctx.FlushLog(); };
        // 0.6.2: write queued log lines before the process goes away (crash, logoff/shutdown, forced exit).
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { ctx.Log("fatal: " + e.ExceptionObject); ctx.FlushLog(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ctx.FlushLog();
        Application.ApplicationExit += (_, _) => ctx.FlushLog();
        Microsoft.Win32.SystemEvents.SessionEnding += (_, e) => { ctx.Log($"windows session ending ({e.Reason})"); ctx.FlushLog(); };

        // Wait for "show" signals from second instances without blocking the UI thread.
        var ui = SynchronizationContext.Current!;
        var reg = ThreadPool.RegisterWaitForSingleObject(show, (_, _) => ui.Post(_ => ctx.ShowWizard(), null), null, -1, false);
        var regQuit = ThreadPool.RegisterWaitForSingleObject(quit, (_, _) => ui.Post(_ => { ctx.Log("quit requested by the installer"); ctx.Quit(); }, null), null, -1, true);
        Application.Run(ctx);
        reg.Unregister(null);
        regQuit.Unregister(null);
        return 0;
    }
}
