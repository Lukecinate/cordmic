namespace CordMic.App;

internal static class Program
{
    private const string MutexName = @"Local\CordMic.SingleInstance";
    private const string ShowEventName = @"Local\CordMic.ShowWizard";

    [STAThread]
    private static int Main(string[] args)
    {
        bool tray = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        using var mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // Already running (e.g. started at logon): a Start-menu click just opens the guide in that instance.
            try { if (!tray && EventWaitHandle.TryOpenExisting(ShowEventName, out var ev)) { ev.Set(); ev.Dispose(); } }
            catch (Exception) { }
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        using var ctx = new AppController(openWizard: !tray);
        Application.ThreadException += (_, e) => ctx.Log("ui exception: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ctx.Log("fatal: " + e.ExceptionObject);

        // Wait for "show" signals from second instances without blocking the UI thread.
        var ui = SynchronizationContext.Current!;
        var reg = ThreadPool.RegisterWaitForSingleObject(show, (_, _) => ui.Post(_ => ctx.ShowWizard(), null), null, -1, false);
        Application.Run(ctx);
        reg.Unregister(null);
        return 0;
    }
}
