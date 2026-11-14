using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using CordMic.Core;
using CordMic.Core.Dsp;
using CordMic.Core.Updates;

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
/// Tray-resident controller. Owns the UDP receiver (bound once on 0.0.0.0 so adapters can come and go; since 0.6.1
/// it accepts packets only from the USB tethering subnet and loopback, see <see cref="SourcePolicy"/>),
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
    private DateTime _lastLocalSettingsUtc = DateTime.MinValue;
    private DateTime _peerSince = DateTime.MinValue;
    private List<RenderDevice> _devices = new();
    private Health? _shownHealth;
    private MainForm? _window;
    private SettingsForm? _settingsWindow;
    private readonly UpdateCoordinator _updates;
    // Audit M5: re-evaluate the six-hour schedule every 30 minutes on a monotonic clock.
    private readonly PeriodicGate _autoCheckGate = new(TimeSpan.FromMinutes(30));
    private BalloonKind _balloon;
    private PostCoalescer _updateUi = null!;
    private Action<UpdateState>? _onUpdateChanged;
    private Action<string>? _onUpdateReady;

    private ToolStripMenuItem _miHeader = null!, _miDetail = null!, _miConnect = null!, _miUpdate = null!, _miFixRoute = null!;

    public AppStatus Status { get; private set; } = new(Health.Waiting, L.T("status.starting"), "", "CordMic");
    public StatsSnapshot? Stats { get; private set; }
    public bool Armed => _armed;
    public JitterPreset Jitter => _settings.Jitter;
    public OutputMode OutputMode => _settings.OutputMode;
    public string? OutputDeviceId => _settings.OutputDeviceId;
    public IReadOnlyList<RenderDevice> Devices => _devices;
    public bool DisplayedNc => DisplayedNoiseMode != NoiseMode.Off;
    public NoiseMode DisplayedNoiseMode => SettingsPresentation.VisibleNoiseMode(_settings.NoiseMode);
    public bool DisplayedVoiceClarity => SettingsPresentation.VisibleVoiceClarity(_settings.VoiceClarity);
    public bool DisplayedAutoVolume => SettingsPresentation.VisibleAutoVolume(_settings.AutoVolume);
    public bool DisplayedFrontFocus => SettingsPresentation.VisibleFrontFocus(_settings.FrontFocus);
    public bool DisplayedFrontFocusAr => _settings.FrontFocusAudioRecord;
    public bool DisplayedAutoUpdate => _settings.AutoUpdate;
    public bool InstallOnQuit => _settings.InstallOnQuit;
    public bool SpeechLow => _rx?.Phone.SpeechLow == true;
    /// <summary>0.7.0 updater state for the Settings window and the tray.</summary>
    public UpdateState Update => _updates.State;
    public InstallKind InstallKind => _updates.Kind;
    public DateTime? LastUpdateCheckUtc => _updates.LastCheckUtc ?? (_settings.LastUpdateCheckUtcTicks > 0 ? new DateTime(_settings.LastUpdateCheckUtcTicks, DateTimeKind.Utc) : null);
    public NoiseChoice DisplayedNoiseChoice => SettingsChoices.Noise(DisplayedNoiseMode, _settings.StudioPreset);
    public DelayChoice DisplayedDelay => SettingsChoices.Delay(_settings.Jitter, _settings.OutputMode);
    public string Language => _settings.Language ?? L.Auto;
    public string? OptimizeStatus => OptimizeHint;
    public bool CarriesInternetViaPhone => _net.Current?.CarriesInternet == true;
    public string CurrentVersion
    {
        get
        {
            var v = typeof(AppController).Assembly.GetName().Version;
            return v is null ? "0.5.5" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }
    public int DisplayedGain => SettingsPresentation.VisibleGain(_settings.MicGainDb);
    public StudioPreset DisplayedStudio => _settings.StudioPreset;
    /// <summary>One-line Studio state for the settings panel, or "" when Studio is off.</summary>
    public string StudioNotice
    {
        get
        {
            if (_settings.StudioPreset == StudioPreset.Off || _rx is not { } rx) return "";
            if (_studioFellBack) return L.T("ui.studio.fallback");
            if (!rx.StudioActive) return L.T("ui.studio.unavailable", rx.StudioUnavailableReason ?? "-");
            if (rx.Peer is not null && rx.Phone.Known && !rx.Phone.StudioBypass) return L.T("ui.studio.noAck");
            return L.T("ui.studio.on");
        }
    }
    public bool MoveCloserHint => _rx is { } rx && rx.Peer is not null && rx.MoveCloserHint;
    public bool MicSilenced => _rx is { } rx && rx.Peer is not null && rx.Phone.Silenced;
    private bool _studioFellBack;
    private DateTime _nextStudioGainSave = DateTime.MinValue;
    public string DetectedSummary => _rx?.Phone.Known == true ? _rx.Phone.DetectedSummary : "";

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
        _settings = AppSettings.Load(Paths.Settings, out var settingsProblem);
        // 0.7.0: schema 1 -> 2 (automatic update downloads on, unless the log shows the user turned them off in 0.6.1+).
        var schemaNote = _settings.MigrateSchema(() => AutoUpdateMigration.ExplicitOffSince061(Paths.Log + ".1", Paths.Log));
        ApplyLanguage();
        Log($"CordMic {Application.ProductVersion} start, exe={Paths.ExePath}");
        foreach (var m in migrated) Log(m);
        if (settingsProblem is not null) Log(settingsProblem);
        if (schemaNote is not null) { Log(schemaNote); Save(); }

        var kind = AppUpdater.DetectKind();
        _updates = AppUpdater.Create(CurrentVersion, kind, () => _ui.Post(_ => Quit(), null), Log);
        // Audit L2: progress arrives every 256 KB; at most one UI refresh is queued at a time.
        _updateUi = new PostCoalescer(a => _ui.Post(_ => a(), null), () => { if (!_quitting) OnUpdateChanged(); });
        _onUpdateChanged = _ => _updateUi.Request();
        _onUpdateReady = tag => _ui.Post(_ => OnUpdateReadyInBackground(tag), null);
        _updates.Changed += _onUpdateChanged;
        _updates.ReadyInBackground += _onUpdateReady;
        Log("install kind: " + kind.ToString().ToLowerInvariant());
        if (kind == InstallKind.Portable)
            _ = Task.Run(() => { int n = PortableSwap.CleanBackups(Paths.ExeDir); if (n > 0) Log($"portable: removed {n} old update backup folder(s)"); });
        RebuildPairingKey();

        _audio = new AudioEngine { Log = Log };
        _audio.Failed += why => _ui.Post(_ => OnAudioFailed(why), null);
        _net.Changed += OnAdapterChanged;

        _menu = BuildMenu();
        _tray = new NotifyIcon { ContextMenuStrip = _menu, Text = "CordMic", Visible = true };
        _tray.Icon = IconFor(Health.Waiting);
        _tray.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWizard(); };
        _tray.BalloonTipClicked += (_, _) => OnBalloonClicked();
        _tray.BalloonTipClosed += (_, _) => _balloon = BalloonKind.None;

        _timer = new System.Windows.Forms.Timer { Interval = 500 };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        _autoCheckGate.TryEnter(); // the startup check below covers the first period
        Tick();
        // Audit M8: offer an update verified in an earlier run right away; check now if due, or if it was unusable.
        _ = _updates.StartupAsync(_settings.AutoUpdate, _settings.AutoUpdate && UpdateCheckDue());

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
            if (_autoCheckGate.TryEnter()) MaybeAutoCheckUpdates(); // a tray app runs for days: re-check every six hours
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
            FrontFocus = _settings.FrontFocus,
            FrontFocusAudioRecord = _settings.FrontFocusAudioRecord,
            Agc = _settings.AutoVolume,
            MicGainDb = Math.Clamp(_settings.MicGainDb, 0, VoiceChain.MaxGainDb),
            Policy = CurrentPolicy(),
        };
        try
        {
            rx.Start();
            _rx = rx;
            _rxError = null;
            rx.PeerChanged += p => _ui.Post(_ => OnPeerChanged(p), null);
            rx.StudioThreadInit = Native.BoostAudioThread;
            rx.StudioFellBack += why => _ui.Post(_ => OnStudioFellBack(why), null);
            if (_settings.StudioPreset != StudioPreset.Off && !_studioFellBack)
                rx.ConfigureStudio(_settings.StudioPreset, _settings.AutoVolume, _settings.StudioAgcGainDb);
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
                ShowBalloon(BalloonKind.Other, 5000, "CordMic", L.T("outmode.fellBack", want.Name), ToolTipIcon.Info);
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
        if (_rx is { } rx) rx.Policy = CurrentPolicy();
    }

    // ---------------------------------------------------------------- trust boundary and pairing (0.6.1, audit H1)

    private byte[]? _pairingKey;

    /// <summary>Tether subnet (when an adapter is up) plus loopback, and the pairing key when the user enabled a code.</summary>
    private SourcePolicy CurrentPolicy()
    {
        var subnets = _net.Current?.Subnet is { } sn ? new[] { sn } : Array.Empty<Ipv4Subnet>();
        return new SourcePolicy(subnets, _pairingKey);
    }

    private void RebuildPairingKey()
    {
        var code = _settings.PairingCode;
        _pairingKey = Pairing.IsValidCode(code) ? Pairing.DeriveKey(code!) : null;
    }

    public bool PairingEnabled => Pairing.IsValidCode(_settings.PairingCode);
    public string? PairingCode => PairingEnabled ? _settings.PairingCode : null;

    /// <summary>Phone waiting for the pairing code, for the status line; null otherwise.</summary>
    public string? PairingWaitingFor => _rx?.PendingPairing?.Address.ToString();

    public void SetPairing(bool on)
    {
        if (on == PairingEnabled) return;
        _settings.PairingCode = on ? Pairing.NewCode() : null;
        ApplyPairingChange(on ? "pairing code on" : "pairing code off");
    }

    public void NewPairingCode()
    {
        if (!PairingEnabled) return;
        _settings.PairingCode = Pairing.NewCode();
        ApplyPairingChange("pairing code changed");
    }

    private void ApplyPairingChange(string what)
    {
        Save();
        RebuildPairingKey();
        // A new policy forgets earlier pairings; disconnect so the phone has to present the new code.
        if (_rx is { } rx) { rx.Policy = CurrentPolicy(); }
        Log(what);
        UpdateStatus();
    }

    private void OnPeerChanged(IPEndPoint? p)
    {
        if (p is not null) { _peerSince = DateTime.UtcNow; return; }
        bool wasLong = _peerSince != DateTime.MinValue && DateTime.UtcNow - _peerSince > TimeSpan.FromSeconds(5);
        _peerSince = DateTime.MinValue;
        _lossWin.Clear();
        if (wasLong)
            ShowBalloon(BalloonKind.Other, 4000, L.T("balloon.lost.title"),
                _net.Current is null ? L.T("balloon.lost.noAdapter") : L.T("balloon.lost.adapter"),
                ToolTipIcon.Warning);
    }

    private void OnStudioFellBack(string why)
    {
        _studioFellBack = true;
        Log("studio: switched to phone noise cancellation for this session (" + why + ")");
        ShowBalloon(BalloonKind.Other, 5000, "CordMic", L.T("balloon.studio.fallback"), ToolTipIcon.Warning);
    }

    /// <summary>Persist the Studio auto-volume gain (the next session starts from it, spec §4).</summary>
    private void SaveStudioGain(bool force)
    {
        if (_rx is not { } rx || !rx.StudioActive || !_settings.AutoVolume) return;
        double g = rx.StudioAgcGainDb;
        if (double.IsNaN(g)) return;
        g = CordMic.Core.Dsp.AgcB3.ClampSaved(g); // 0.6.2: never start a session above +15 dB
        if (!force && (DateTime.UtcNow < _nextStudioGainSave || Math.Abs(g - _settings.StudioAgcGainDb) < 0.5)) return;
        _nextStudioGainSave = DateTime.UtcNow.AddSeconds(30);
        _settings.StudioAgcGainDb = Math.Round(g, 1);
        Save();
    }

    private void UpdateStats()
    {
        if (_rx is null) { Stats = null; return; }
        var s = _rx.Snapshot();
        Stats = s;
        AbsorbPhoneEcho(s.Phone);
        SaveStudioGain(force: false);
        var now = DateTime.UtcNow;
        _lossWin.Enqueue((now, s.Expected, s.Lost));
        while (_lossWin.Count > 1 && now - _lossWin.Peek().t > TimeSpan.FromSeconds(10)) _lossWin.Dequeue();
        var first = _lossWin.Peek();
        long de = s.Expected - first.exp, dl = s.Lost - first.lost;
        RecentLossPct = de > 0 && dl >= 0 ? 100.0 * dl / de : 0;
        _parts = s.Peer is null ? null : new LatencyParts((s.RttMs ?? 1) / 2, s.BufferMs + (_rx.StudioActive ? StudioProcessor.AddedLatencyMs : 0), _audio.OutputMs());
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
        else if (s?.Peer is null && _rx.PendingPairing is { } pend)
            st = new(Health.Warn, L.T("status.pairing.title"), L.T("status.pairing.detail", pend.Address), L.T("status.pairing.title"));
        else if (s?.Peer is null && _rx.RecentUntrustedSource is { } stranger)
            st = new(Health.Warn, L.T("status.untrusted.title"), L.T("status.untrusted.detail", stranger), L.T("status.untrusted.title"));
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
        var connectText = _armed ? L.T("menu.disconnect") : L.T("menu.connect");
        if (_miConnect.Text != connectText) _miConnect.Text = connectText;
    }

    // ---------------------------------------------------------------- menu

    private ContextMenuStrip BuildMenu()
    {
        // 0.7.0: a short tray menu. Every setting lives in the Settings window.
        var m = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = false, Font = Theme.Body, Renderer = new QuietMenuRenderer() };
        _miHeader = new ToolStripMenuItem("CordMic") { Enabled = false, Font = Theme.Strong, Tag = "header" };
        _miDetail = new ToolStripMenuItem("") { Enabled = false, Tag = "detail" };
        _miConnect = new ToolStripMenuItem(_armed ? L.T("menu.disconnect") : L.T("menu.connect"), null, (_, _) => SetArmed(!_armed));
        var miSettings = new ToolStripMenuItem(L.T("menu.settings"), null, (_, _) => ShowSettings(null));
        _miUpdate = new ToolStripMenuItem(L.T("menu.checkUpdate"), null, (_, _) => OnTrayUpdateClicked());
        var miOpen = new ToolStripMenuItem(L.T("menu.wizard"), null, (_, _) => ShowWizard());
        _miFixRoute = new ToolStripMenuItem(L.T("menu.fixRoute"), null, (_, _) => FixRoute()) { Visible = false };
        var miLog = new ToolStripMenuItem(L.T("menu.openLog"), null, (_, _) => OpenLog());
        var miQuit = new ToolStripMenuItem(L.T("menu.quit"), null, (_, _) => _ = ExitFromTrayAsync());
        m.Opening += (_, _) => RefreshTrayUpdateItem();
        m.Items.AddRange(new ToolStripItem[]
        {
            _miHeader, _miDetail, new ToolStripSeparator(),
            miOpen, _miConnect, miSettings, _miUpdate, _miFixRoute, miLog, new ToolStripSeparator(), miQuit,
        });
        return m;
    }

    /// <summary>Tray item: "Check for updates", or "● Install CordMic x.y.z" (bold) once an update is ready.</summary>
    private void RefreshTrayUpdateItem()
    {
        if (_miUpdate is null) return;
        var st = _updates.State;
        string text = st.ReadyToInstall ? "\u25CF " + L.T("menu.updateReady", st.Tag)
            : st.Busy ? L.T("menu.updating")
            : L.T("menu.checkUpdate");
        if (_miUpdate.Text != text) _miUpdate.Text = text;
        _miUpdate.Font = st.ReadyToInstall ? Theme.Strong : Theme.Body;
        _miUpdate.ForeColor = st.ReadyToInstall ? Theme.Accent : Theme.Ink;
    }

    private void OnTrayUpdateClicked()
    {
        var st = _updates.State;
        if (st.ReadyToInstall) { _ = _updates.InstallStagedAsync(); return; }
        ShowSettings(SettingsPage.Updates);
        if (!st.Busy) _ = _updates.CheckAsync(download: _settings.AutoUpdate);
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
        if (_settings.OutputDeviceId == d?.Id && _settings.OutputDeviceName == d?.Name) return;
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

    public void SetLanguage(string code)
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
        if (_settingsWindow is { IsDisposed: false } sw)
        {
            // Deferred: this runs inside the language list's own event, so the old window must not close under it.
            var page = sw.Page;
            sw.BeginInvoke(() => { sw.Close(); ShowSettings(page); });
        }
        UpdateStatus();
    }

    public void SetJitter(JitterPreset p)
    {
        if (_settings.Jitter == p) return;
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

    private CancellationTokenSource? _optimizeCts;
    private bool _optimizeCancelledByUser;

    /// <summary>True while the elevated VB-CABLE helper runs (the button turns into "Stop waiting").</summary>
    public bool OptimizeRunning => _optimizeCts is not null;

    /// <summary>
    /// 0.6.1 (audit M2): starts the elevated helper and waits for it asynchronously (at most 2 minutes), so the UI,
    /// the tray and the 500 ms timer keep running while the UAC prompt is open.
    /// </summary>
    public async Task RequestOptimizeCableAsync()
    {
        if (_optimizeCts is not null) return;
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        _optimizeCts = cts;
        _optimizeCancelledByUser = false;
        OptimizeHint = L.T("ui.cable.optimizing");
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
            using var p = await Task.Run(() => Process.Start(psi)).ConfigureAwait(true);
            if (p is null)
            {
                OptimizeHint = L.T("ui.cable.launchFailed");
                Log("VB-CABLE optimize: failed to start helper");
                return;
            }
            int? code = null;
            try
            {
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(true);
                code = p.ExitCode;
            }
            catch (OperationCanceledException) { }
            if (code is null)
            {
                OptimizeHint = _optimizeCancelledByUser ? L.T("ui.cable.optimizeCancelled") : L.T("ui.cable.optimizeTimeout");
                Log(_optimizeCancelledByUser ? "VB-CABLE optimize: stopped waiting (user)" : "VB-CABLE optimize: helper did not finish within 2 minutes");
            }
            else if (code == CableHelperArgs.ExitOk)
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
        finally
        {
            _optimizeCts = null;
            cts.Dispose();
            UpdateStatus();
        }
    }

    /// <summary>Stops waiting for the helper. An elevated helper cannot be killed from here; it finishes on its own.</summary>
    public void CancelOptimizeCable()
    {
        if (_optimizeCts is not { } cts) return;
        _optimizeCancelledByUser = true;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void ShowMicModeInfo() => MessageBox.Show(
        L.T("dlg.micMode.body"),
        L.T("dlg.micMode.title"), MessageBoxButtons.OK, MessageBoxIcon.Information);

    public void FixRoute()
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

    /// <summary>0.7.0: the one Settings window (pages), opened from the gear, the tray or the update notification.</summary>
    public void ShowSettings(SettingsPage? page)
    {
        if (_settingsWindow is { IsDisposed: false } w)
        {
            if (page is { } pg) w.ShowPage(pg);
            if (w.WindowState == FormWindowState.Minimized) w.WindowState = FormWindowState.Normal;
            w.Activate();
            return;
        }
        _settingsWindow = new SettingsForm(this, page ?? SettingsPage.Sound);
        _settingsWindow.FormClosed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void SetNoiseChoice(NoiseChoice c)
    {
        if (c == DisplayedNoiseChoice) return;
        var (phone, studio) = SettingsChoices.Apply(c, _settings.NoiseMode);
        if (phone != _settings.NoiseMode) SetNoiseMode(phone);
        SetStudioPreset(studio);
    }

    public void SetDelayChoice(DelayChoice c)
    {
        if (c == DelayChoice.Custom || c == DisplayedDelay) return;
        var (j, m) = SettingsChoices.Apply(c, _settings.Jitter, _settings.OutputMode);
        SetJitter(j);
        SetOutputMode(m);
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
        if (_settings.NoiseMode == mode && _settings.NoiseCancellation == (mode != NoiseMode.Off))
            return;
        _settings.NoiseMode = mode;
        _settings.NoiseCancellation = mode != NoiseMode.Off;
        NoteLocalSettingsChanged();
        Save();
        if (_rx is { } rx) { rx.NoiseMode = mode; rx.NotifyLinkControlChanged(SyncField.Noise); }
        Log("noise cancellation -> " + mode.ToString().ToLowerInvariant());
    }

    /// <summary>0.6.0: PC Studio noise reduction (Off = 0.5.7 behaviour, phone-side processing).</summary>
    public void SetStudioPreset(StudioPreset preset)
    {
        if (_settings.StudioPreset == preset) return;
        SaveStudioGain(force: true);
        _settings.StudioPreset = preset;
        _studioFellBack = false;
        Save();
        _rx?.ConfigureStudio(preset, _settings.AutoVolume, _settings.StudioAgcGainDb);
        Log("studio -> " + preset.ToString().ToLowerInvariant());
    }

    public void SetVoiceClarity(bool on)
    {
        if (_settings.VoiceClarity == on) return;
        _settings.VoiceClarity = on;
        NoteLocalSettingsChanged();
        Save();
        if (_rx is { } rx) { rx.VoiceClarity = on; rx.NotifyLinkControlChanged(SyncField.Clarity); }
        Log("voice clarity -> " + (on ? "on" : "off"));
    }

    public void SetAutoVolume(bool on)
    {
        if (_settings.AutoVolume == on) return;
        _settings.AutoVolume = on;
        NoteLocalSettingsChanged();
        Save();
        if (_rx is { } rx)
        {
            rx.Agc = on; rx.NotifyLinkControlChanged(SyncField.Agc);
            if (_settings.StudioPreset != StudioPreset.Off && !_studioFellBack) rx.ConfigureStudio(_settings.StudioPreset, on, _settings.StudioAgcGainDb);
        }
        Log("auto volume -> " + (on ? "on" : "off"));
    }

    public void SetFrontFocus(bool on)
    {
        if (_settings.FrontFocus == on) return;
        _settings.FrontFocus = on;
        if (!on && _settings.FrontFocusAudioRecord)
        {
            _settings.FrontFocusAudioRecord = false;
            if (_rx is { } rx0) rx0.FrontFocusAudioRecord = false;
        }
        NoteLocalSettingsChanged();
        Save();
        if (_rx is { } rx) { rx.FrontFocus = on; rx.NotifyLinkControlChanged(SyncField.FrontFocus); }
        Log("front focus -> " + (on ? "on" : "off"));
    }

    public void SetFrontFocusAudioRecord(bool on)
    {
        if (_settings.FrontFocusAudioRecord == on) return;
        _settings.FrontFocusAudioRecord = on;
        if (on && !_settings.FrontFocus)
        {
            _settings.FrontFocus = true;
            if (_rx is { } rx0) rx0.FrontFocus = true;
        }
        NoteLocalSettingsChanged();
        Save();
        if (_rx is { } rx)
        {
            rx.FrontFocus = _settings.FrontFocus;
            rx.FrontFocusAudioRecord = on;
            rx.NotifyLinkControlChanged(SyncField.FrontFocusAr);
        }
        Log("front focus (higher latency) -> " + (on ? "on" : "off"));
    }

    public void SetAutoUpdate(bool on)
    {
        if (_settings.AutoUpdate == on) return;
        _settings.AutoUpdate = on;
        Save();
        Log("auto update -> " + (on ? "on" : "off"));
        if (on) MaybeAutoCheckUpdates();
    }

    public void ResetAudioSettings()
    {
        _settings.ResetAudioDefaults();
        _studioFellBack = false;
        NoteLocalSettingsChanged();
        Save();
        if (_rx is { } rx)
        {
            rx.ConfigureStudio(StudioPreset.Off, _settings.AutoVolume, _settings.StudioAgcGainDb);
            rx.NoiseMode = _settings.NoiseMode;
            rx.VoiceClarity = _settings.VoiceClarity;
            rx.FrontFocus = _settings.FrontFocus;
            rx.FrontFocusAudioRecord = _settings.FrontFocusAudioRecord;
            rx.Agc = _settings.AutoVolume;
            rx.MicGainDb = _settings.MicGainDb;
            rx.NotifyLinkControlChanged(SyncField.Noise, SyncField.Clarity, SyncField.Agc, SyncField.Gain, SyncField.FrontFocus, SyncField.FrontFocusAr);
        }
        Log("audio settings reset to defaults");
    }

    public void SetMicGain(int db)
    {
        db = Math.Clamp(db, 0, VoiceChain.MaxGainDb);
        if (!SettingsPresentation.GainChanged(_settings.MicGainDb, db)) return;
        _settings.MicGainDb = db;
        NoteLocalSettingsChanged();
        Save();
        if (_rx is { } rx)
        {
            rx.MicGainDb = db;
            rx.Agc = _settings.AutoVolume;
            rx.NotifyLinkControlChanged(SyncField.Gain);
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

    /// <summary>Writes queued log lines now (crash, shutdown, forced exit). Safe from any thread; never throws.</summary>
    public void FlushLog() { try { _log.Flush(); } catch (Exception) { } }

    private bool _quitting;

    public void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        _timer.Stop();
        SaveStudioGain(force: true);
        _tray.Visible = false;
        _window?.Close();
        _audio.Dispose();
        _rx?.Dispose();
        _net.Dispose();
        _settingsWindow?.Close();
        // Audit M2: no updater callbacks after this point.
        if (_onUpdateChanged is not null) _updates.Changed -= _onUpdateChanged;
        if (_onUpdateReady is not null) _updates.ReadyInBackground -= _onUpdateReady;
        _updates.Log = null;
        _updates.Dispose();
        CancelOptimizeCable();
        Log("quit");
        _log.Dispose();
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

    private void NoteLocalSettingsChanged() => _lastLocalSettingsUtc = DateTime.UtcNow;

    /// <summary>
    /// Copy a phone-side change into local settings, but ignore PONGs that still
    /// reflect the value from before the user's last edit.
    /// </summary>
    private void AbsorbPhoneEcho(PhoneLink phone)
    {
        if (!phone.Known) return;

        if (phone.SyncCapable && phone.SyncRevs is { Length: > 0 } revs && _rx is { } rx)
        {
            bool changed = false;
            void Take(SyncField field, Action apply)
            {
                int i = (int)field;
                if (i >= revs.Length) return;
                var remote = revs[i];
                if (!SettingsSync.ShouldApply(rx.Sync[field], remote)) return;
                apply();
                rx.Sync.ApplyRemote(field, remote);
                changed = true;
            }
            Take(SyncField.Noise, () =>
            {
                var nc = SettingsPresentation.NoiseModeFromPhone(phone.NoiseCancellation, phone.NoiseBits, _settings.NoiseMode);
                _settings.NoiseMode = nc;
                _settings.NoiseCancellation = nc != NoiseMode.Off;
                rx.NoiseMode = nc;
            });
            Take(SyncField.Clarity, () =>
            {
                _settings.VoiceClarity = phone.VoiceClarity;
                rx.VoiceClarity = phone.VoiceClarity;
            });
            Take(SyncField.Agc, () =>
            {
                _settings.AutoVolume = phone.Agc;
                rx.Agc = phone.Agc;
            });
            Take(SyncField.Gain, () =>
            {
                if (phone.GainDb >= 0)
                {
                    _settings.MicGainDb = phone.GainDb;
                    rx.MicGainDb = phone.GainDb;
                }
            });
            Take(SyncField.FrontFocus, () =>
            {
                _settings.FrontFocus = phone.FrontFocus;
                rx.FrontFocus = phone.FrontFocus;
            });
            Take(SyncField.FrontFocusAr, () =>
            {
                _settings.FrontFocusAudioRecord = phone.FrontFocusAudioRecord;
                rx.FrontFocusAudioRecord = phone.FrontFocusAudioRecord;
            });
            if (changed) Save();
            return;
        }

        if (!SettingsPresentation.ShouldAcceptPhoneEcho(_lastLocalSettingsUtc, DateTime.UtcNow)) return;
        // With Studio on the phone bypasses its own suppression and AGC for the session; a pre-0.6.0-style echo of
        // that state must not overwrite the user's phone settings (0.6.0 phones keep reporting the stored values).
        if (_rx?.StudioActive == true) return;

        bool staleChanged = false;
        var ncLegacy = SettingsPresentation.NoiseModeFromPhone(phone.NoiseCancellation, phone.NoiseBits, _settings.NoiseMode);
        if (ncLegacy != _settings.NoiseMode)
        {
            _settings.NoiseMode = ncLegacy;
            _settings.NoiseCancellation = ncLegacy != NoiseMode.Off;
            staleChanged = true;
        }
        if (phone.Agc != _settings.AutoVolume)
        {
            _settings.AutoVolume = phone.Agc;
            staleChanged = true;
        }
        // Older phones leave PONG bit 5 clear and pack no mic/dir nibble — do not treat that as Off.
        if ((phone.MicCount > 0 || phone.DirCode != 0) && phone.FrontFocus != _settings.FrontFocus)
        {
            _settings.FrontFocus = phone.FrontFocus;
            staleChanged = true;
        }
        if (phone.GainDb >= 0 && SettingsPresentation.GainChanged(_settings.MicGainDb, phone.GainDb))
        {
            _settings.MicGainDb = phone.GainDb;
            staleChanged = true;
        }
        if (staleChanged) Save();
    }

    // ---------------------------------------------------------------- updates (0.7.0)

    /// <summary>Check button: checks; when a newer version exists and automatic download is on, downloads and verifies it (no install).</summary>
    public void CheckForUpdates() => _ = _updates.CheckAsync(download: _settings.AutoUpdate);

    /// <summary>One click: download if needed, verify, install, restart.</summary>
    public void UpdateNow() => _ = _updates.UpdateNowAsync();

    public void CancelUpdate() => _updates.Cancel();

    public void OpenReleasePage() => AppUpdater.OpenReleasePage(_updates.State.ReleasePage);

    private void OnUpdateChanged()
    {
        if (_quitting) return;
        var st = _updates.State;
        if (st.Phase is UpdatePhase.UpToDate or UpdatePhase.Available or UpdatePhase.Ready && _updates.LastCheckUtc is { } t)
        {
            if (_settings.LastUpdateCheckUtcTicks != t.Ticks) { _settings.LastUpdateCheckUtcTicks = t.Ticks; Save(); }
        }
        RefreshTrayUpdateItem();
        _window?.RefreshUpdateDot();
        _settingsWindow?.RefreshNow();
    }

    private void OnUpdateReadyInBackground(string tag)
    {
        if (_quitting) return;
        RefreshTrayUpdateItem();
        ShowBalloon(BalloonKind.UpdateReady, 10000, "CordMic", L.T("balloon.update.ready2", tag), ToolTipIcon.Info);
    }

    /// <summary>Audit M1: every balloon goes through here so a click is only treated as "install" for the update balloon.</summary>
    private void ShowBalloon(BalloonKind kind, int ms, string title, string text, ToolTipIcon icon)
    {
        if (_quitting) return;
        _balloon = kind;
        _tray.ShowBalloonTip(ms, title, text, icon);
    }

    private void OnBalloonClicked()
    {
        var shown = _balloon;
        _balloon = BalloonKind.None;
        if (BalloonPolicy.InstallOnClick(shown, _updates.State.ReadyToInstall)) _ = _updates.InstallStagedAsync();
    }

    /// <summary>Progress of the "Install VB-CABLE" button, shown under it.</summary>
    public string CableInstallStatus { get; private set; } = "";

    public void MaybeAutoCheckUpdates()
    {
        if (!_settings.AutoUpdate || _quitting || !UpdateCheckDue()) return;
        _ = _updates.CheckAsync(download: true);
    }

    private bool UpdateCheckDue() => UpdateSchedule.IsDue(_settings.LastUpdateCheckUtcTicks, DateTime.UtcNow, AppUpdate.MinCheckInterval);

    public void SetInstallOnQuit(bool on)
    {
        if (_settings.InstallOnQuit == on) return;
        _settings.InstallOnQuit = on;
        Save();
        Log("install on quit -> " + (on ? "on" : "off"));
    }

    /// <summary>Tray Exit: with "Install when I exit" on and an update ready, start it first (the installer restarts CordMic).</summary>
    public async Task ExitFromTrayAsync()
    {
        if (_settings.InstallOnQuit && _updates.State.ReadyToInstall && !_quitting)
        {
            Log("exit: installing the downloaded update first");
            await _updates.InstallStagedAsync().ConfigureAwait(true);
        }
        Quit();
    }

    public async Task InstallVbCableAsync()
    {
        try
        {
            if (CableInstaller.LooksInstalled())
            {
                CableInstallStatus = L.T("ui.cable.already");
                return;
            }
            CableInstallStatus = L.T("ui.cable.downloading");
            var staged = await CableInstaller.DownloadAndVerifyAsync().ConfigureAwait(true);
            CableInstallStatus = L.T("ui.cable.launched");
            Log("VB-CABLE official setup launched; a reboot may be required");
            await CableInstaller.RunSetupAsync(staged).ConfigureAwait(true);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            CableInstallStatus = L.T("ui.cable.uacDeclined");
            Log("VB-CABLE install: UAC declined");
        }
        catch (Exception ex)
        {
            CableInstallStatus = L.T("ui.cable.failed", ex.Message);
            Log("VB-CABLE install: " + ex.Message);
            try { Process.Start(new ProcessStartInfo(VbCable.PageUrl) { UseShellExecute = true }); } catch (Exception) { }
        }
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
