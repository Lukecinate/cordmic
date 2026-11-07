using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using CordMic.Core;

namespace CordMic.App;

internal enum Health { Waiting, Ok, Warn, Error }

internal sealed record AppStatus(Health Health, string Title, string Detail, string Tooltip);

/// <summary>PC-side delay estimate split by stage (ms). Network = RTT/2 (USB link + both UDP stacks);
/// Buffer = time-averaged jitter-buffer wait; Output = average time our samples sit in the WASAPI endpoint
/// buffer. Not included: phone capture, the Windows engine period, VB-CABLE's own buffer, the recording app.</summary>
internal sealed record LatencyParts(double NetworkMs, double BufferMs, double OutputMs)
{
    public double TotalMs => NetworkMs + BufferMs + OutputMs;
}

/// <summary>
/// Tray-resident controller. Owns the UDP receiver (bound once on 0.0.0.0 so adapters can come and go),
/// the WASAPI output, the tether-adapter watcher and the tray menu. Everything mutable runs on the UI thread;
/// background events are marshalled with SynchronizationContext.Post.
/// </summary>
internal sealed class AppController : ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly FileLog _log;
    private readonly SynchronizationContext _ui;
    private readonly NotifyIcon _tray;
    private ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly NetWatcher _net = new();
    private readonly AudioEngine _audio;
    private readonly Dictionary<Health, Icon> _icons = new();
    private readonly Queue<(DateTime t, long exp, long lost)> _lossWin = new();
    private string? _lastDeviceList;
    private string? _loggedPeriodHint;

    private bool _armed = true;
    private Receiver? _rx;
    private string? _rxError;
    private DateTime _nextRxTry = DateTime.MinValue;
    private DateTime _nextAudioTry = DateTime.MinValue;
    private DateTime _nextStatsLog = DateTime.MinValue;
    private DateTime _peerSince = DateTime.MinValue;
    private List<RenderDevice> _devices = new();
    private Health? _shownHealth;
    private MainForm? _window;

    private ToolStripMenuItem _miHeader = null!, _miDetail = null!, _miOutput = null!, _miJitter = null!, _miOutMode = null!,
                              _miStartup = null!, _miFixRoute = null!;

    public AppStatus Status { get; private set; } = new(Health.Waiting, L.T("status.starting"), "", "CordMic");
    public StatsSnapshot? Stats { get; private set; }
    public bool Armed => _armed;
    public JitterPreset Jitter => _settings.Jitter;
    public OutputMode OutputMode => _settings.OutputMode;
    public string? OutputDeviceId => _settings.OutputDeviceId;
    public IReadOnlyList<RenderDevice> Devices => _devices;
    public bool DisplayedNc => DisplayedNoiseMode != NoiseMode.Off;
    public NoiseMode DisplayedNoiseMode
    {
        get
        {
            if (Stats?.Phone.Known == true)
            {
                var p = Stats.Phone;
                if (!p.NoiseCancellation) return NoiseMode.Off;
                return p.NoiseBits == 2 ? NoiseMode.Strong : p.NoiseBits == 1 ? NoiseMode.Light : _settings.NoiseMode;
            }
            return _settings.NoiseMode;
        }
    }
    public bool DisplayedVoiceClarity => _settings.VoiceClarity;
    public bool DisplayedAutoVolume => Stats?.Phone.Known == true ? Stats.Phone.Agc : _settings.AutoVolume;
    public int DisplayedGain => Stats?.Phone is { Known: true, GainDb: >= 0 } p ? p.GainDb : Math.Clamp(_settings.MicGainDb, 0, VoiceChain.MaxGainDb);
    /// <summary>Name of the output the controller would open right now, for the connect window.</summary>
    public string? WantedOutputName => WantedOutput()?.Name;
    public TetherAdapter? Adapter => _net.Current;
    public string? CableName { get; private set; }
    public string? ActiveOutput => _audio.ActiveName;
    public double RecentLossPct { get; private set; }
    private LatencyParts? _parts;

    public AppController(bool openWizard)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Directory.CreateDirectory(Paths.LogDir);
        _log = new FileLog(Paths.Log);
        var migrated = Migration.Run();
        _settings = AppSettings.Load(Paths.Settings);
        ApplyLanguage();
        Log($"CordMic {Application.ProductVersion} start, exe={Paths.ExePath}");
        foreach (var m in migrated) Log(m);

        _audio = new AudioEngine();
        _audio.Failed += why => _ui.Post(_ => OnAudioFailed(why), null);
        _net.Changed += OnAdapterChanged;

        _menu = BuildMenu();
        _tray = new NotifyIcon { ContextMenuStrip = _menu, Text = "CordMic", Visible = true };
        _tray.Icon = IconFor(Health.Waiting);
        _tray.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWizard(); };

        _timer = new System.Windows.Forms.Timer { Interval = 500 };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();

        if (openWizard || !_settings.FirstRunDone) ShowWizard();
    }

    // ---------------------------------------------------------------- loop

    private void Tick()
    {
        try
        {
            _net.Tick(Log);
            EnsureReceiver();
            if (_audio.ConsumeDevicesDirty()) RefreshDevices();
            EnsureOutput();
            UpdateStats();
            UpdateStatus();
        }
        catch (Exception ex) { Log("tick error: " + ex); }
    }

    private void EnsureReceiver()
    {
        if (!_armed)
        {
            if (_rx is not null) { _audio.Stop(); _rx.Dispose(); _rx = null; Stats = null; }
            return;
        }
        if (_rx is not null || DateTime.UtcNow < _nextRxTry) return;
        var rx = new Receiver(_settings.Port, "0.0.0.0", Presets.Ms(_settings.Jitter))
        {
            Log = Log, RxThreadInit = Native.BoostAudioThread,
            NoiseMode = _settings.NoiseMode, VoiceClarity = _settings.VoiceClarity,
            Agc = _settings.AutoVolume,
            MicGainDb = Math.Clamp(_settings.MicGainDb, 0, VoiceChain.MaxGainDb),
        };
        try
        {
            rx.Start();
            _rx = rx;
            _rxError = null;
            rx.PeerChanged += p => _ui.Post(_ => OnPeerChanged(p), null);
            UpdateProbes();
            if (_audio.ActiveId is null) _nextAudioTry = DateTime.MinValue;
        }
        catch (Exception ex)
        {
            rx.Dispose();
            _rxError = L.T("status.portBusy.detail", _settings.Port);
            if (_nextRxTry == DateTime.MinValue || Status.Health != Health.Error) Log($"bind udp {_settings.Port} failed: {ex.Message}");
            _nextRxTry = DateTime.UtcNow.AddSeconds(5);
        }
    }

    private void RefreshDevices()
    {
        try { _devices = _audio.ListRender(); }
        catch (Exception ex) { Log("enumerate devices failed: " + ex.Message); _devices = new(); }
        CableName = DeviceRules.PickVirtualCable(_devices.Select(d => d.Name));
        string list = string.Join(" | ", _devices.Select(d => d.Name));
        if (list != _lastDeviceList)
        {
            Log("render devices: " + list);
            _lastDeviceList = list;
        }
        RefreshCableStatus();
        _nextAudioTry = DateTime.MinValue; // re-evaluate right away
    }

    private RenderDevice? WantedOutput()
    {
        if (_settings.OutputDeviceId is { } id)
            return _devices.FirstOrDefault(d => d.Id == id);
        return CableName is null ? null : _devices.FirstOrDefault(d => d.Name == CableName);
    }

    private void EnsureOutput()
    {
        if (_rx is null) return;
        var want = WantedOutput();
        if (want is null)
        {
            if (_audio.ActiveId is not null) { Log("output device gone, stopping playback"); _audio.Stop(); }
            return;
        }
        if (_audio.ActiveId == want.Id && _audio.Playing) return;
        if (DateTime.UtcNow < _nextAudioTry) return;
        try
        {
            _audio.Start(want.Id, _rx, _settings.OutputChannels, _settings.OutputLatencyMs, _settings.OutputMode, Log);
            _rx.SetRenderPeriodMs(_audio.PeriodMs);
            Log($"output -> {want.Name} ({_audio.Describe})");
            UpdatePeriodHint();
            if (_settings.OutputMode == OutputMode.Exclusive && _audio.ActiveMode != OutputMode.Exclusive)
                _tray.ShowBalloonTip(5000, "CordMic", L.T("outmode.fellBack", want.Name), ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _audio.Fail(ex.Message);
            Log($"open output '{want.Name}' failed: {ex.Message}");
            _nextAudioTry = DateTime.UtcNow.AddSeconds(3);
        }
    }

    private void OnAudioFailed(string why)
    {
        Log("playback stopped: " + why);
        _audio.Fail(why);
        _audio.ConsumeDevicesDirty();
        RefreshDevices();
        _nextAudioTry = DateTime.UtcNow.AddSeconds(1);
    }

    private void OnAdapterChanged(TetherAdapter? a) => UpdateProbes();

    private void UpdateProbes()
    {
        var a = _net.Current;
        _rx?.SetProbeTargets(a?.PhoneIp is { } ip ? new[] { new IPEndPoint(ip, _settings.Port) } : Array.Empty<IPEndPoint>());
    }

    private void OnPeerChanged(IPEndPoint? p)
    {
        if (p is not null) { _peerSince = DateTime.UtcNow; return; }
        bool wasLong = _peerSince != DateTime.MinValue && DateTime.UtcNow - _peerSince > TimeSpan.FromSeconds(5);
        _peerSince = DateTime.MinValue;
        _lossWin.Clear();
        if (wasLong)
            _tray.ShowBalloonTip(4000, L.T("balloon.lost.title"),
                _net.Current is null ? L.T("balloon.lost.noAdapter") : L.T("balloon.lost.adapter"),
                ToolTipIcon.Warning);
    }

    private void UpdateStats()
    {
        if (_rx is null) { Stats = null; return; }
        var s = _rx.Snapshot();
        Stats = s;
        var now = DateTime.UtcNow;
        _lossWin.Enqueue((now, s.Expected, s.Lost));
        while (_lossWin.Count > 1 && now - _lossWin.Peek().t > TimeSpan.FromSeconds(10)) _lossWin.Dequeue();
        var first = _lossWin.Peek();
        long de = s.Expected - first.exp, dl = s.Lost - first.lost;
        RecentLossPct = de > 0 && dl >= 0 ? 100.0 * dl / de : 0;
        _parts = s.Peer is null ? null : new LatencyParts((s.RttMs ?? 1) / 2, s.BufferMs, _audio.OutputMs());
        if (s.Peer is not null && now >= _nextStatsLog)
        {
            _nextStatsLog = now.AddSeconds(10);
            Log("stats " + s.Line());
        }
    }

    /// <summary>Estimated PC-side delay split by stage; null while no phone is connected. Updated every 500 ms.</summary>
    public LatencyParts? LatencyBreakdown() => Stats?.Peer is null ? null : _parts;

    /// <summary>Estimated PC-side delay: half RTT (USB) + average jitter-buffer wait + measured WASAPI fill.
    /// Excludes phone capture, the Windows engine period, the virtual cable and the recording app.
    /// Before 0.4.2 the output part was the nominal 10 ms setting, so older numbers are not directly comparable.</summary>
    public double? EstLatencyMs() => LatencyBreakdown()?.TotalMs;

    private void UpdateStatus()
    {
        var s = Stats;
        var a = _net.Current;
        AppStatus st;
        if (_rx is null)
            st = new(Health.Error, L.T("status.portBusy.title"), _rxError ?? L.T("status.portBusy.fallback"), L.T("status.portBusy.tip"));
        else if (WantedOutput() is null)
        {
            bool manual = _settings.OutputDeviceId is not null;
            st = manual
                ? new(Health.Warn, L.T("status.outputGone.title"), L.T("status.outputGone.detail", _settings.OutputDeviceName), L.T("status.outputGone.tip"))
                : new(Health.Warn, L.T("status.noCable.title"), L.T("status.noCable.detail"), L.T("status.noCable.tip"));
        }
        else if (!_audio.Playing)
            st = new(Health.Error, L.T("status.audioError.title"), _audio.LastError ?? L.T("status.audioError.fallback"), L.T("status.audioError.tip"));
        else if (s?.Peer is null)
        {
            st = a is null
                ? new(Health.Waiting, L.T("status.waiting.title"), L.T("status.waiting.detail"), L.T("status.waiting.tip"))
                : new(Health.Waiting, L.T("status.detected.title"), L.T("status.detected.detail", a.Description, a.PhoneIp), L.T("status.detected.tip"));
        }
        else
        {
            var lat = EstLatencyMs() ?? 0;
            var rtt = s.RttMs is { } r ? $"{r:F1}" : "-";
            bool silent = s.SecondsSinceAudio > 1.5;
            bool viaPhone = a?.CarriesInternet == true;
            var latS = lat.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
            var lossS = RecentLossPct.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var parts = LatencyBreakdown();
            var tip = L.T("status.connected.tip", latS, rtt, lossS,
                (parts?.BufferMs ?? s.BufferMs).ToString("F0", inv),
                (parts?.NetworkMs ?? 0).ToString("F0", inv),
                (parts?.OutputMs ?? 0).ToString("F0", inv));
            if (silent) st = new(Health.Warn, L.T("status.silent.title"), L.T("status.silent.detail"), L.T("status.silent.tip"));
            else if (viaPhone) st = new(Health.Warn, L.T("status.viaPhone.title"), L.T("status.viaPhone.detail"), tip + L.T("status.viaPhone.tipSuffix"));
            else st = new(Health.Ok, L.T("status.connected.title"), L.T("status.connected.detail", latS, rtt, lossS), tip);
        }
        Status = st;

        if (_shownHealth != st.Health) { _tray.Icon = IconFor(st.Health); _shownHealth = st.Health; }
        var tt = st.Tooltip.Length > 127 ? st.Tooltip[..127] : st.Tooltip;
        if (_tray.Text != tt) _tray.Text = tt;
        _miHeader.Text = st.Title;
        _miDetail.Text = st.Detail.Length > 90 ? st.Detail[..90] + "…" : st.Detail;
        _miFixRoute.Visible = a?.CarriesInternet == true;
    }

    // ---------------------------------------------------------------- menu

    private ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true, Font = Theme.Body, Renderer = new QuietMenuRenderer() };
        _miHeader = new ToolStripMenuItem("CordMic") { Enabled = false, Font = Theme.Strong, Tag = "header" };
        _miDetail = new ToolStripMenuItem("") { Enabled = false, Tag = "detail" };
        _miOutput = new ToolStripMenuItem(L.T("menu.output"));
        _miOutput.DropDownOpening += (_, _) => FillOutputMenu();
        _miOutput.DropDownItems.Add("…");
        _miJitter = new ToolStripMenuItem(L.T("menu.jitter"));
        foreach (var p in Enum.GetValues<JitterPreset>())
        {
            var it = new ToolStripMenuItem(Presets.Label(p)) { Tag = p };
            it.Click += (_, _) => SetJitter((JitterPreset)it.Tag!);
            _miJitter.DropDownItems.Add(it);
        }
        _miJitter.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripMenuItem it in _miJitter.DropDownItems) it.Checked = (JitterPreset)it.Tag! == _settings.Jitter;
        };
        _miOutMode = new ToolStripMenuItem(L.T("menu.outmode"));
        foreach (var om in OutputModes.All)
        {
            var it = new ToolStripMenuItem(OutputModes.Label(om)) { Tag = om };
            it.Click += (_, _) => SetOutputMode((OutputMode)it.Tag!);
            _miOutMode.DropDownItems.Add(it);
        }
        _miOutMode.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripMenuItem it in _miOutMode.DropDownItems) it.Checked = (OutputMode)it.Tag! == _settings.OutputMode;
        };
        var miMode = new ToolStripMenuItem(L.T("menu.micMode"), null, (_, _) => ShowMicModeInfo());
        var miWizard = new ToolStripMenuItem(L.T("menu.wizard"), null, (_, _) => ShowWizard());
        _miFixRoute = new ToolStripMenuItem(L.T("menu.fixRoute"), null, (_, _) => FixRoute()) { Visible = false };
        var miLog = new ToolStripMenuItem(L.T("menu.openLog"), null, (_, _) => OpenLog());
        _miStartup = new ToolStripMenuItem(L.T("menu.startup"), null, (_, _) => ToggleStartup()) { CheckOnClick = false };
        var miLang = new ToolStripMenuItem(L.T("menu.language"));
        foreach (var (code, label) in new[] { (L.Auto, L.T("menu.language.auto")), (L.English, "English"), (L.Indonesian, "Bahasa Indonesia") })
        {
            var it = new ToolStripMenuItem(label) { Tag = code };
            it.Click += (_, _) => SetLanguage((string)it.Tag!);
            miLang.DropDownItems.Add(it);
        }
        miLang.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripMenuItem it in miLang.DropDownItems) it.Checked = (string)it.Tag! == (_settings.Language ?? L.Auto);
        };
        var miQuit = new ToolStripMenuItem(L.T("menu.quit"), null, (_, _) => Quit());
        m.Opening += (_, _) => { try { _miStartup.Checked = Startup.IsEnabled(); } catch (Exception) { } };
        m.Items.AddRange(new ToolStripItem[]
        {
            _miHeader, _miDetail, new ToolStripSeparator(),
            _miOutput, _miOutMode, _miJitter, miMode, new ToolStripSeparator(),
            miWizard, _miFixRoute, miLog, _miStartup, miLang, new ToolStripSeparator(), miQuit,
        });
        return m;
    }

    private void FillOutputMenu()
    {
        RefreshDevices();
        _miOutput.DropDownItems.Clear();
        var auto = new ToolStripMenuItem(CableName is null ? L.T("menu.output.autoNone") : L.T("menu.output.auto", CableName))
        { Checked = _settings.OutputDeviceId is null };
        auto.Click += (_, _) => SetOutput(null);
        _miOutput.DropDownItems.Add(auto);
        _miOutput.DropDownItems.Add(new ToolStripSeparator());
        foreach (var d in _devices.OrderBy(d => DeviceRules.VirtualCableRank(d.Name) is var r && r >= 0 ? r : 99).ThenBy(d => d.Name))
        {
            var mic = DeviceRules.MicNameFor(d.Name);
            var it = new ToolStripMenuItem(mic is null ? d.Name : L.T("menu.output.micFor", d.Name, mic)) { Checked = _settings.OutputDeviceId == d.Id };
            it.Click += (_, _) => SetOutput(d);
            _miOutput.DropDownItems.Add(it);
        }
    }

    public void SetOutput(RenderDevice? d)
    {
        if (d is not null && DeviceRules.VirtualCableRank(d.Name) < 0)
        {
            var ok = MessageBox.Show(
                L.T("dlg.notCable", d.Name),
                "CordMic", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (ok != DialogResult.Yes) return;
        }
        _settings.OutputDeviceId = d?.Id;
        _settings.OutputDeviceName = d?.Name;
        Save();
        _audio.Stop();
        _nextAudioTry = DateTime.MinValue;
        Log("output preference: " + (d?.Name ?? "auto"));
        Tick();
    }

    private void ApplyLanguage()
    {
        L.Set(L.Resolve(_settings.Language, Native.OsUiLanguage()));
    }

    private void SetLanguage(string code)
    {
        _settings.Language = code;
        Save();
        ApplyLanguage();
        Log($"language -> {code} ({L.Current})");
        var old = _menu;
        _menu = BuildMenu();
        _tray.ContextMenuStrip = _menu;
        old.Dispose();
        _shownHealth = null;
        if (_window is { IsDisposed: false }) { _window.Close(); ShowWizard(); }
        UpdateStatus();
    }

    public void SetJitter(JitterPreset p)
    {
        _settings.Jitter = p;
        Save();
        _rx?.SetJitterMs(Presets.Ms(p));
        Log($"jitter preset -> {p} ({Presets.Ms(p)} ms)");
    }

    public void SetOutputMode(OutputMode m)
    {
        if (_settings.OutputMode == m) return;
        _settings.OutputMode = m;
        Save();
        Log($"output mode -> {m}");
        _audio.Stop();
        _nextAudioTry = DateTime.MinValue;
        EnsureOutput();
        UpdateStatus();
    }

    public bool CableFound { get; private set; }
    public bool CableNeedsOptimize { get; private set; }
    public string? OptimizeHint { get; private set; }
    public string? PeriodHint { get; private set; }

    public void RefreshCableStatus()
    {
        try
        {
            var info = CableHelper.Inspect();
            CableFound = info.Found;
            CableNeedsOptimize = info.NeedsOptimize;
            if (!info.Found) return;
            if (OptimizeHint is null)
                OptimizeHint = info.NeedsOptimize ? L.T("ui.cable.needed") : L.T("ui.cable.optimized");
            else if (info.NeedsOptimize && OptimizeHint == L.T("ui.cable.optimized"))
                OptimizeHint = L.T("ui.cable.needed");
            else if (!info.NeedsOptimize && OptimizeHint == L.T("ui.cable.needed"))
                OptimizeHint = L.T("ui.cable.optimized");
        }
        catch (Exception ex) { Log("cable inspect: " + ex.Message); }
    }

    void UpdatePeriodHint()
    {
        PeriodHint = null;
        if (_audio.EngineMinMs > 0 && _audio.PeriodMs > _audio.EngineMinMs * 1.4 + 0.5)
        {
            PeriodHint = L.T("ui.cable.periodHint", _audio.PeriodMs.ToString("F1"), _audio.EngineMinMs.ToString("F1"));
            if (PeriodHint != _loggedPeriodHint)
            {
                Log(PeriodHint);
                _loggedPeriodHint = PeriodHint;
            }
        }
    }

    public void RequestOptimizeCable()
    {
        var psi = new ProcessStartInfo
        {
            FileName = Paths.ExePath,
            Arguments = CableHelperArgs.Configure,
            UseShellExecute = true,
            Verb = "runas",
            ErrorDialog = false,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p is null)
            {
                OptimizeHint = L.T("ui.cable.launchFailed");
                Log("VB-CABLE optimize: failed to start helper");
                return;
            }
            p.WaitForExit(120_000);
            int code = p.HasExited ? p.ExitCode : -1;
            if (code == CableHelperArgs.ExitOk)
            {
                OptimizeHint = L.T("ui.cable.optimized");
                Log("VB-CABLE optimize: ok");
                _audio.Stop();
                _nextAudioTry = DateTime.MinValue;
                RefreshCableStatus();
                EnsureOutput();
            }
            else if (code == CableHelperArgs.ExitNoCable)
            {
                OptimizeHint = L.T("ui.cable.notFound");
                Log("VB-CABLE optimize: no matching stereo endpoints");
            }
            else
            {
                OptimizeHint = L.T("ui.cable.failed", code);
                Log($"VB-CABLE optimize: exit {code}");
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            OptimizeHint = L.T("ui.cable.uacDeclined");
            Log("VB-CABLE optimize: UAC declined; continuing without changes");
        }
        catch (Exception ex)
        {
            OptimizeHint = L.T("ui.cable.launchFailed");
            Log("VB-CABLE optimize failed to launch: " + ex.Message);
        }
        UpdateStatus();
    }

    private void ShowMicModeInfo() => MessageBox.Show(
        L.T("dlg.micMode.body"),
        L.T("dlg.micMode.title"), MessageBoxButtons.OK, MessageBoxIcon.Information);

    private void FixRoute()
    {
        if (!File.Exists(Paths.MetricScript))
        {
            MessageBox.Show(L.T("dlg.noScript"), "CordMic", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{Paths.MetricScript}\" -Once")
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            Log("route fix requested (elevated)");
        }
        catch (System.ComponentModel.Win32Exception) { Log("route fix cancelled at UAC"); }
    }

    public void OpenLog()
    {
        try { Process.Start(new ProcessStartInfo(File.Exists(Paths.Log) ? Paths.Log : Paths.LogDir) { UseShellExecute = true }); }
        catch (Exception ex) { Log("open log failed: " + ex.Message); }
    }

    private void ToggleStartup()
    {
        try { Startup.Set(!Startup.IsEnabled()); Log("startup -> " + Startup.IsEnabled()); }
        catch (Exception ex) { MessageBox.Show(L.T("dlg.startupFailed", ex.Message), "CordMic"); }
    }

    public void ShowWizard()
    {
        if (_window is { IsDisposed: false }) { _window.Activate(); if (_window.WindowState == FormWindowState.Minimized) _window.WindowState = FormWindowState.Normal; return; }
        _window = new MainForm(this);
        _window.FormClosed += (_, _) =>
        {
            if (!_settings.FirstRunDone) { _settings.FirstRunDone = true; Save(); }
            _window = null;
        };
        _window.Show();
        _window.Activate();
    }

    public void SetArmed(bool on)
    {
        if (_armed == on)
        {
            if (on) { _nextRxTry = DateTime.MinValue; _nextAudioTry = DateTime.MinValue; Tick(); }
            return;
        }
        _armed = on;
        Log(on ? "connect" : "disconnect");
        if (!on) { _audio.Stop(); _rx?.Dispose(); _rx = null; Stats = null; }
        else { _nextRxTry = DateTime.MinValue; _nextAudioTry = DateTime.MinValue; }
        Tick();
    }

    /// <summary>Error state: stay armed and try the socket and the output device again now.</summary>
    public void RetryConnect()
    {
        _armed = true;
        _nextRxTry = DateTime.MinValue;
        _nextAudioTry = DateTime.MinValue;
        if (_rx is null) { Tick(); return; }
        _audio.Stop();
        Tick();
    }

    public void SetNoiseCancellation(bool on) => SetNoiseMode(on ? NoiseMode.Light : NoiseMode.Off);

    public void SetNoiseMode(NoiseMode mode)
    {
        _settings.NoiseMode = mode;
        _settings.NoiseCancellation = mode != NoiseMode.Off;
        Save();
        if (_rx is { } rx) { rx.NoiseMode = mode; rx.NotifyLinkControlChanged(); }
        Log("noise cancellation -> " + mode.ToString().ToLowerInvariant());
    }

    public void SetVoiceClarity(bool on)
    {
        _settings.VoiceClarity = on;
        Save();
        if (_rx is { } rx) { rx.VoiceClarity = on; rx.NotifyLinkControlChanged(); }
        Log("voice clarity -> " + (on ? "on" : "off"));
    }

    public void SetAutoVolume(bool on)
    {
        _settings.AutoVolume = on;
        Save();
        if (_rx is { } rx) { rx.Agc = on; rx.NotifyLinkControlChanged(); }
        Log("auto volume -> " + (on ? "on" : "off"));
    }

    public void ResetAudioSettings()
    {
        _settings.ResetAudioDefaults();
        Save();
        if (_rx is { } rx)
        {
            rx.NoiseMode = _settings.NoiseMode;
            rx.VoiceClarity = _settings.VoiceClarity;
            rx.Agc = _settings.AutoVolume;
            rx.MicGainDb = _settings.MicGainDb;
            rx.NotifyLinkControlChanged();
        }
        Log("audio settings reset to defaults");
    }

    public void SetMicGain(int db)
    {
        db = Math.Clamp(db, 0, VoiceChain.MaxGainDb);
        _settings.MicGainDb = db;
        Save();
        if (_rx is { } rx)
        {
            rx.MicGainDb = db;
            rx.Agc = _settings.AutoVolume;
            rx.NotifyLinkControlChanged();
        }
        Log($"mic gain -> +{db} dB");
    }

    public void SetStartup(bool on)
    {
        try { Startup.Set(on); Log("startup -> " + on); }
        catch (Exception ex) { MessageBox.Show(L.T("dlg.startupFailed", ex.Message), "CordMic"); }
    }

    /// <summary>Wizard step 1 polls this so a freshly installed cable shows up without restarting.</summary>
    public void RecheckDevices() { _audio.ConsumeDevicesDirty(); RefreshDevices(); EnsureOutput(); UpdateStatus(); }

    public void Quit()
    {
        _timer.Stop();
        _tray.Visible = false;
        _window?.Close();
        _audio.Dispose();
        _rx?.Dispose();
        _net.Dispose();
        Log("quit");
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tray.Dispose(); _menu.Dispose(); _timer.Dispose(); foreach (var i in _icons.Values) i.Dispose(); }
        base.Dispose(disposing);
    }

    private Icon IconFor(Health h)
    {
        if (_icons.TryGetValue(h, out var i)) return i;
        return _icons[h] = Theme.TrayIcon(h);
    }

    private void Save()
    {
        try { _settings.Save(Paths.Settings); } catch (Exception ex) { Log("save settings failed: " + ex.Message); }
    }

    public void Log(string msg)
    {
        try { _log.Write(msg); } catch (Exception) { }
    }
}
