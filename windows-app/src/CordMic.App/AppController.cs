using System.Diagnostics;
using System.Net;
using CordMic.Core;

namespace CordMic.App;

internal enum Health { Waiting, Ok, Warn, Error }

internal sealed record AppStatus(Health Health, string Title, string Detail, string Tooltip);

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
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly NetWatcher _net = new();
    private readonly AudioEngine _audio;
    private readonly Dictionary<Health, Icon> _icons = new();
    private readonly Queue<(DateTime t, long exp, long lost)> _lossWin = new();

    private Receiver? _rx;
    private string? _rxError;
    private DateTime _nextRxTry = DateTime.MinValue;
    private DateTime _nextAudioTry = DateTime.MinValue;
    private DateTime _nextStatsLog = DateTime.MinValue;
    private DateTime _peerSince = DateTime.MinValue;
    private List<RenderDevice> _devices = new();
    private Health? _shownHealth;
    private WizardForm? _wizard;

    private ToolStripMenuItem _miHeader = null!, _miDetail = null!, _miOutput = null!, _miJitter = null!,
                              _miStartup = null!, _miFixRoute = null!;

    public AppStatus Status { get; private set; } = new(Health.Waiting, "Memulai…", "", "CordMic");
    public StatsSnapshot? Stats { get; private set; }
    public TetherAdapter? Adapter => _net.Current;
    public string? CableName { get; private set; }
    public string? ActiveOutput => _audio.ActiveName;
    public double RecentLossPct { get; private set; }

    public AppController(bool openWizard)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Directory.CreateDirectory(Paths.LogDir);
        _log = new FileLog(Paths.Log);
        var migrated = Migration.Run();
        _settings = AppSettings.Load(Paths.Settings);
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
        if (_rx is not null || DateTime.UtcNow < _nextRxTry) return;
        var rx = new Receiver(_settings.Port, "0.0.0.0", Presets.Ms(_settings.Jitter)) { Log = Log, RxThreadInit = Native.BoostAudioThread };
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
            _rxError = $"Port UDP {_settings.Port} dipakai aplikasi lain (mis. receiver Python). Tutup lalu tunggu.";
            if (_nextRxTry == DateTime.MinValue || Status.Health != Health.Error) Log($"bind udp {_settings.Port} failed: {ex.Message}");
            _nextRxTry = DateTime.UtcNow.AddSeconds(5);
        }
    }

    private void RefreshDevices()
    {
        try { _devices = _audio.ListRender(); }
        catch (Exception ex) { Log("enumerate devices failed: " + ex.Message); _devices = new(); }
        CableName = DeviceRules.PickVirtualCable(_devices.Select(d => d.Name));
        Log("render devices: " + string.Join(" | ", _devices.Select(d => d.Name)));
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
            _audio.Start(want.Id, _rx, _settings.OutputChannels, _settings.OutputLatencyMs);
            Log($"output -> {want.Name} (wasapi shared, event, {_settings.OutputLatencyMs} ms)");
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
            _tray.ShowBalloonTip(4000, "Mic HP terputus",
                _net.Current is null ? "Cek kabel dan nyalakan lagi Tethering USB di HP." : "Buka CordMic di HP dan tekan Start.",
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
        if (s.Peer is not null && now >= _nextStatsLog)
        {
            _nextStatsLog = now.AddSeconds(10);
            Log("stats " + s.Line());
        }
    }

    /// <summary>Estimated PC-side delay: half RTT (USB) + jitter buffer + WASAPI buffer. Excludes phone capture and the app's own input buffer.</summary>
    public double? EstLatencyMs()
    {
        if (Stats is not { } s || s.Peer is null) return null;
        return (s.RttMs ?? 1) / 2 + s.DepthMs + _audio.LatencyMs;
    }

    private void UpdateStatus()
    {
        var s = Stats;
        var a = _net.Current;
        AppStatus st;
        if (_rx is null)
            st = new(Health.Error, "Port sibuk", _rxError ?? "Tidak bisa membuka port UDP.", "CordMic: port UDP sibuk\nTutup aplikasi lain yang memakai port 50005");
        else if (WantedOutput() is null)
        {
            bool manual = _settings.OutputDeviceId is not null;
            st = manual
                ? new(Health.Warn, "Perangkat output hilang", $"\"{_settings.OutputDeviceName}\" tidak ditemukan. Pilih lagi di menu Perangkat output.", "CordMic: perangkat output tidak ada\nPilih ulang di menu")
                : new(Health.Warn, "Mic virtual belum ada", "Pasang VB-CABLE dulu. Buka Panduan penyiapan.", "CordMic: mic virtual belum terpasang\nKlik dua kali untuk panduan");
        }
        else if (!_audio.Playing)
            st = new(Health.Error, "Output audio error", _audio.LastError ?? "Membuka perangkat audio…", "CordMic: output audio error\nMencoba lagi otomatis");
        else if (s?.Peer is null)
        {
            st = a is null
                ? new(Health.Waiting, "Menunggu HP", "Colok kabel USB dan nyalakan Tethering USB di HP.", "CordMic: menunggu HP\nColok kabel & nyalakan Tethering USB")
                : new(Health.Waiting, "HP terdeteksi", $"Adapter {a.Description} ({a.PhoneIp}). Buka CordMic di HP lalu tekan Start.", "CordMic: HP terdeteksi\nBuka CordMic di HP, tekan Start");
        }
        else
        {
            var lat = EstLatencyMs() ?? 0;
            var rtt = s.RttMs is { } r ? $"{r:F1}" : "-";
            bool silent = s.SecondsSinceAudio > 1.5;
            bool viaPhone = a?.CarriesInternet == true;
            var tip = $"CordMic: terhubung\nLatensi ~{lat:F0} ms (sisi PC) · RTT {rtt} ms\nLoss {RecentLossPct:F1}% · buffer {s.DepthMs:F0} ms";
            if (silent) st = new(Health.Warn, "Terhubung, audio berhenti", "HP tersambung tapi tidak mengirim suara. Cek aplikasi di HP.", "CordMic: terhubung tapi tidak ada suara\nCek aplikasi di HP");
            else if (viaPhone) st = new(Health.Warn, "Terhubung (cek jaringan)", "Internet PC lewat HP. Pakai menu \"Perbaiki prioritas jaringan\" agar game tetap pakai LAN/Wi-Fi.", tip + "\n! Internet lewat HP");
            else st = new(Health.Ok, "Terhubung", $"Latensi ~{lat:F0} ms sisi PC, RTT {rtt} ms, loss {RecentLossPct:F1}% (10 dtk).", tip);
        }
        Status = st;

        if (_shownHealth != st.Health) { _tray.Icon = IconFor(st.Health); _shownHealth = st.Health; }
        var tt = st.Tooltip.Length > 127 ? st.Tooltip[..127] : st.Tooltip;
        if (_tray.Text != tt) _tray.Text = tt;
        _miHeader.Text = "CordMic – " + st.Title;
        _miDetail.Text = st.Detail.Length > 90 ? st.Detail[..90] + "…" : st.Detail;
        _miFixRoute.Visible = a?.CarriesInternet == true;
    }

    // ---------------------------------------------------------------- menu

    private ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip { ShowImageMargin = false, Font = Theme.Body };
        _miHeader = new ToolStripMenuItem("CordMic") { Enabled = false, Font = Theme.Strong };
        _miDetail = new ToolStripMenuItem("") { Enabled = false };
        _miOutput = new ToolStripMenuItem("Perangkat output");
        _miOutput.DropDownOpening += (_, _) => FillOutputMenu();
        _miOutput.DropDownItems.Add("…");
        _miJitter = new ToolStripMenuItem("Jitter buffer");
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
        var miMode = new ToolStripMenuItem("Mode mic (diatur di HP)…", null, (_, _) => ShowMicModeInfo());
        var miWizard = new ToolStripMenuItem("Panduan penyiapan…", null, (_, _) => ShowWizard());
        _miFixRoute = new ToolStripMenuItem("Perbaiki prioritas jaringan HP…", null, (_, _) => FixRoute()) { Visible = false };
        var miLog = new ToolStripMenuItem("Buka log", null, (_, _) => OpenLog());
        _miStartup = new ToolStripMenuItem("Jalan saat Windows mulai", null, (_, _) => ToggleStartup()) { CheckOnClick = false };
        var miQuit = new ToolStripMenuItem("Keluar", null, (_, _) => Quit());
        m.Opening += (_, _) => { try { _miStartup.Checked = Startup.IsEnabled(); } catch (Exception) { } };
        m.Items.AddRange(new ToolStripItem[]
        {
            _miHeader, _miDetail, new ToolStripSeparator(),
            _miOutput, _miJitter, miMode, new ToolStripSeparator(),
            miWizard, _miFixRoute, miLog, _miStartup, new ToolStripSeparator(), miQuit,
        });
        return m;
    }

    private void FillOutputMenu()
    {
        RefreshDevices();
        _miOutput.DropDownItems.Clear();
        var auto = new ToolStripMenuItem(CableName is null ? "Otomatis (belum ada mic virtual)" : $"Otomatis: {CableName}")
        { Checked = _settings.OutputDeviceId is null };
        auto.Click += (_, _) => SetOutput(null);
        _miOutput.DropDownItems.Add(auto);
        _miOutput.DropDownItems.Add(new ToolStripSeparator());
        foreach (var d in _devices.OrderBy(d => DeviceRules.VirtualCableRank(d.Name) is var r && r >= 0 ? r : 99).ThenBy(d => d.Name))
        {
            var mic = DeviceRules.MicNameFor(d.Name);
            var it = new ToolStripMenuItem(mic is null ? d.Name : $"{d.Name}  →  mic: {mic}") { Checked = _settings.OutputDeviceId == d.Id };
            it.Click += (_, _) => SetOutput(d);
            _miOutput.DropDownItems.Add(it);
        }
    }

    private void SetOutput(RenderDevice? d)
    {
        if (d is not null && DeviceRules.VirtualCableRank(d.Name) < 0)
        {
            var ok = MessageBox.Show(
                $"\"{d.Name}\" bukan kabel virtual. Suara HP akan keluar di speaker/headset ini dan TIDAK muncul sebagai mikrofon.\n\nTetap pakai? (berguna hanya untuk tes dengar)",
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

    private void SetJitter(JitterPreset p)
    {
        _settings.Jitter = p;
        Save();
        _rx?.SetJitterMs(Presets.Ms(p));
        Log($"jitter preset -> {p} ({Presets.Ms(p)} ms)");
    }

    private void ShowMicModeInfo() => MessageBox.Show(
        "Mode mikrofon dipilih di aplikasi CordMic di HP (sebelum menekan Start):\n\n" +
        "• Voice Recognition – latensi paling rendah, suara natural. Disarankan untuk game.\n" +
        "• Unprocessed – mentah tanpa filter (kalau HP mendukung).\n" +
        "• Voice Communication – peredam bising + echo cancel dari HP, latensi sedikit lebih tinggi.\n\n" +
        "Tips: kalau sudah pakai Voice Communication, matikan Noise Suppression/Echo Cancellation di Discord supaya tidak diproses dua kali.",
        "Mode mic", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private void FixRoute()
    {
        if (!File.Exists(Paths.MetricScript))
        {
            MessageBox.Show("Skrip perbaikan tidak ditemukan. Pasang ulang CordMic lewat installer.", "CordMic", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

    private void OpenLog()
    {
        try { Process.Start(new ProcessStartInfo(File.Exists(Paths.Log) ? Paths.Log : Paths.LogDir) { UseShellExecute = true }); }
        catch (Exception ex) { Log("open log failed: " + ex.Message); }
    }

    private void ToggleStartup()
    {
        try { Startup.Set(!Startup.IsEnabled()); Log("startup -> " + Startup.IsEnabled()); }
        catch (Exception ex) { MessageBox.Show("Gagal mengubah startup: " + ex.Message, "CordMic"); }
    }

    public void ShowWizard()
    {
        if (_wizard is { IsDisposed: false }) { _wizard.Activate(); if (_wizard.WindowState == FormWindowState.Minimized) _wizard.WindowState = FormWindowState.Normal; return; }
        _wizard = new WizardForm(this);
        _wizard.FormClosed += (_, _) =>
        {
            if (!_settings.FirstRunDone && (CableName is not null || _wizard!.Finished))
            {
                _settings.FirstRunDone = true;
                Save();
            }
            _wizard = null;
        };
        _wizard.Show();
        _wizard.Activate();
    }

    /// <summary>Wizard step 1 polls this so a freshly installed cable shows up without restarting.</summary>
    public void RecheckDevices() { _audio.ConsumeDevicesDirty(); RefreshDevices(); EnsureOutput(); UpdateStatus(); }

    public void Quit()
    {
        _timer.Stop();
        _tray.Visible = false;
        _wizard?.Close();
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
        int size = SystemInformation.SmallIconSize.Width;
        var c = h switch { Health.Ok => Theme.Ok, Health.Warn => Theme.Warn, Health.Error => Theme.Bad, _ => Theme.Wait };
        return _icons[h] = Theme.TrayIcon(c, Math.Max(16, size));
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
