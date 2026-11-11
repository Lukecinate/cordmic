using System.Diagnostics;
using CordMic.Core;
using CordMic.Core.Dsp;

namespace CordMic.App;

/// <summary>
/// One small window: a round connect button, one status line, and a settings panel.
/// Same palette, logo and icon set as the rest of CordMic. No second page.
/// </summary>
internal sealed class MainForm : Form
{
    private const string CableUrl = "https://vb-audio.com/Cable/";
    private readonly AppController _app;
    private readonly ConnectOrb _orb;
    private readonly Label _state, _detail;
    private readonly Panel _settings;
    private readonly Button _settingsBtn;
    private readonly ComboBox _output, _latency, _outMode, _ncMode;
    private readonly CheckBox _clarity, _autoVol, _frontFocus, _startup;
    private CheckBox _frontFocusAr = null!, _autoUpdate = null!;
    private readonly TrackBar _gain;
    private readonly Label _gainValue, _noCable;
    private Label _cableHint = null!;
    private Label _periodHint = null!;
    private Label _detected = null!;
    private Label _speechLow = null!;
    private ComboBox _studio = null!;
    private Label _studioNote = null!;
    private Label _updateStatus = null!;
    private Button _optimize = null!;
    private Button _installCable = null!;
    private Button _checkUpdate = null!;
    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 32 };
    private readonly System.Windows.Forms.Timer _gainDebounce = new() { Interval = 180 };
    private bool _settingsOpen, _suppress;
    private int _animTicks;

    public MainForm(AppController app)
    {
        _app = app;
        Text = "CordMic";
        Icon = Theme.AppIcon();
        Font = Theme.Body;
        BackColor = Theme.Background;
        ForeColor = Theme.Ink;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(Sx(392), Sx(500));

        double dpi = DeviceDpi;
        int headerH = HeaderLayout.Px(HeaderLayout.Height, dpi);
        var header = new TableLayoutPanel
        {
            ColumnCount = 3, RowCount = 1,
            Location = Point.Empty, Size = new Size(ClientSize.Width, headerH),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Theme.Background, Margin = Padding.Empty,
            Padding = new Padding(
                HeaderLayout.Px(HeaderLayout.PadLeft, dpi),
                HeaderLayout.Px(HeaderLayout.PadY, dpi),
                HeaderLayout.Px(HeaderLayout.PadRight, dpi),
                HeaderLayout.Px(HeaderLayout.PadY, dpi)),
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize,
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        int logoS = HeaderLayout.Px(HeaderLayout.LogoSize, dpi);
        var logo = new PictureBox
        {
            Image = Theme.Logo(), SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(logoS, logoS), BackColor = Color.Transparent, TabStop = false,
            Margin = new Padding(0, 0, HeaderLayout.Px(HeaderLayout.LogoTitleGap, dpi), 0),
            Anchor = AnchorStyles.Left,
        };
        var brand = new Label
        {
            Text = "CordMic", Font = Theme.Title, ForeColor = Theme.Ink, AutoSize = true,
            Margin = Padding.Empty, BackColor = Color.Transparent, Anchor = AnchorStyles.Left,
            UseMnemonic = false,
        };
        int gearS = HeaderLayout.Px(HeaderLayout.SettingsButton, dpi);
        int glyphS = HeaderLayout.Px(HeaderLayout.SettingsGlyph, dpi);
        _settingsBtn = new Button
        {
            Text = string.Empty, Size = new Size(gearS, gearS), MinimumSize = new Size(gearS, gearS),
            FlatStyle = FlatStyle.Flat, BackColor = Theme.Background, UseVisualStyleBackColor = false,
            Cursor = Cursors.Hand, Margin = Padding.Empty, Padding = Padding.Empty,
            Image = Theme.Glyph("settings", Theme.Muted, glyphS),
            ImageAlign = ContentAlignment.MiddleCenter,
            AccessibleName = L.T("ui.settings"), AccessibleRole = AccessibleRole.PushButton,
            Anchor = AnchorStyles.None,
        };
        _settingsBtn.FlatAppearance.BorderSize = 0;
        _settingsBtn.FlatAppearance.MouseOverBackColor = Theme.SurfaceMuted;
        _settingsBtn.FlatAppearance.MouseDownBackColor = Theme.Line;
        _settingsBtn.Click += (_, _) => SetSettings(!_settingsOpen);
        new ToolTip().SetToolTip(_settingsBtn, L.T("ui.settings"));
        header.Controls.Add(logo, 0, 0);
        header.Controls.Add(brand, 1, 0);
        header.Controls.Add(_settingsBtn, 2, 0);

        _orb = new ConnectOrb { Size = new Size(Sx(168), Sx(168)) };
        _orb.Click += (_, _) => OnOrb();

        _state = new Label
        {
            Font = Theme.Title, ForeColor = Theme.Ink, TextAlign = ContentAlignment.MiddleCenter,
            AutoSize = false, Height = Sx(28), BackColor = Color.Transparent,
        };
        _detail = new Label
        {
            Font = Theme.Body, ForeColor = Theme.Muted, TextAlign = ContentAlignment.TopCenter,
            AutoSize = false, Height = Sx(48), BackColor = Color.Transparent,
        };

        _settings = BuildSettings(out _output, out _latency, out _outMode, out _ncMode, out _clarity, out _autoVol, out _frontFocus, out _gain, out _gainValue, out _startup, out _noCable);
        _settings.Visible = false;

        Controls.Add(header);
        Controls.Add(_orb);
        Controls.Add(_state);
        Controls.Add(_detail);
        Controls.Add(_settings);

        _anim.Tick += (_, _) => { _orb.Advance(); if (++_animTicks % 8 == 0) RefreshFromApp(); };
        _gainDebounce.Tick += (_, _) => { _gainDebounce.Stop(); ApplyGain(); };
        Load += (_, _) => { LayoutHero(); FillDevices(); RefreshFromApp(); _anim.Start(); };
        FormClosed += (_, _) => { _gainDebounce.Dispose(); _anim.Dispose(); };
        Resize += (_, _) => LayoutHero();
    }

    private Panel BuildSettings(out ComboBox output, out ComboBox latency, out ComboBox outMode, out ComboBox ncMode, out CheckBox clarity,
        out CheckBox autoVol, out CheckBox frontFocus, out TrackBar gain, out Label gainValue, out CheckBox startup, out Label noCable)
    {
        var p = new Panel { BackColor = Theme.Surface, Width = ClientSize.Width };
        p.Paint += (_, e) => { using var pen = new Pen(Theme.Line); e.Graphics.DrawLine(pen, 0, 0, p.Width, 0); };
        int x = Sx(24), w = ClientSize.Width - Sx(48), y = Sx(18);

        noCable = new Label
        {
            Text = L.T("ui.noCable"), Font = Theme.Small, ForeColor = Theme.Amber,
            Location = new Point(x, y), MaximumSize = new Size(w, 0), AutoSize = true, Visible = false, BackColor = Theme.Surface,
        };
        p.Controls.Add(noCable);

        output = Combo(p, x, ref y, w, L.T("menu.output"));
        var outputBox = output;
        outputBox.SelectedIndexChanged += (_, _) => { if (!_suppress) OnOutput(); };
        latency = Combo(p, x, ref y, w, L.T("ui.latency"));
        var latencyBox = latency;
        foreach (var preset in Enum.GetValues<JitterPreset>())
            latencyBox.Items.Add(new PresetItem(preset, Presets.Label(preset)));
        latencyBox.SelectedIndexChanged += (_, _) => { if (!_suppress && latencyBox.SelectedItem is PresetItem it) _app.SetJitter(it.Preset); };

        outMode = Combo(p, x, ref y, w, L.T("menu.outmode"));
        var outModeBox = outMode;
        foreach (var om in OutputModes.All)
            outModeBox.Items.Add(new OutModeItem(om, OutputModes.Label(om)));
        outModeBox.SelectedIndexChanged += (_, _) => { if (!_suppress && outModeBox.SelectedItem is OutModeItem it) _app.SetOutputMode(it.Mode); };

        ncMode = Combo(p, x, ref y, w, L.T("ui.nc"));
        var ncBox = ncMode;
        ncBox.Items.Add(new NcModeItem(NoiseMode.Off, L.T("ui.nc.off")));
        ncBox.Items.Add(new NcModeItem(NoiseMode.Light, L.T("ui.nc.light")));
        ncBox.Items.Add(new NcModeItem(NoiseMode.Strong, L.T("ui.nc.strong")));
        ncBox.SelectedIndexChanged += (_, _) =>
        {
            if (!_suppress && ncBox.SelectedItem is NcModeItem it) _app.SetNoiseMode(it.Mode);
        };

        _studio = Combo(p, x, ref y, w, L.T("ui.studio"));
        _studio.Items.Add(new StudioItem(StudioPreset.Off, L.T("ui.studio.off")));
        _studio.Items.Add(new StudioItem(StudioPreset.Balanced, L.T("ui.studio.balanced")));
        _studio.Items.Add(new StudioItem(StudioPreset.Whisper, L.T("ui.studio.whisper")));
        _studio.Items.Add(new StudioItem(StudioPreset.Max, L.T("ui.studio.max")));
        _studio.SelectedIndexChanged += (_, _) =>
        {
            if (!_suppress && _studio.SelectedItem is StudioItem it) { _app.SetStudioPreset(it.Preset); RefreshFromApp(); }
        };
        _studioNote = new Label
        {
            Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = false,
            Location = new Point(x, y - Sx(6)), Width = w, Height = Sx(48), BackColor = Theme.Surface, Visible = false,
        };
        y += Sx(44);

        clarity = new CheckBox
        {
            Text = L.T("ui.clarity"), Font = Theme.Body, ForeColor = Theme.Ink, AutoSize = true,
            Location = new Point(x, y), BackColor = Theme.Surface,
        };
        var clarityBox = clarity;
        clarityBox.CheckedChanged += (_, _) => { if (!_suppress) _app.SetVoiceClarity(clarityBox.Checked); };
        y += Sx(32);

        autoVol = new CheckBox
        {
            Text = L.T("ui.autovol"), Font = Theme.Body, ForeColor = Theme.Ink, AutoSize = true,
            Location = new Point(x, y), BackColor = Theme.Surface,
        };
        var autoBox = autoVol;
        autoBox.CheckedChanged += (_, _) => { if (!_suppress) _app.SetAutoVolume(autoBox.Checked); };
        y += Sx(32);

        frontFocus = new CheckBox
        {
            Text = L.T("ui.frontFocus"), Font = Theme.Body, ForeColor = Theme.Ink, AutoSize = true,
            Location = new Point(x, y), BackColor = Theme.Surface,
        };
        var frontBox = frontFocus;
        frontBox.CheckedChanged += (_, _) => { if (!_suppress) _app.SetFrontFocus(frontBox.Checked); };
        y += Sx(32);

        _frontFocusAr = new CheckBox
        {
            Text = L.T("ui.frontFocusAr"), Font = Theme.Body, ForeColor = Theme.Ink, AutoSize = true,
            Location = new Point(x, y), BackColor = Theme.Surface,
        };
        _frontFocusAr.CheckedChanged += (_, _) => { if (!_suppress) _app.SetFrontFocusAudioRecord(_frontFocusAr.Checked); };
        y += Sx(28);

        _speechLow = new Label
        {
            Font = Theme.Small, ForeColor = Theme.Amber, AutoSize = false,
            Location = new Point(x, y), Width = w, Height = Sx(36),
            BackColor = Theme.Surface, Text = L.T("ui.speechLow"), Visible = false,
        };
        y += Sx(8);

        _detected = new Label
        {
            Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = false,
            Location = new Point(x, y), Width = w, Height = Sx(32),
            BackColor = Theme.Surface,
        };
        p.Controls.Add(_detected);
        y += Sx(34);

        var gainLabel = new Label
        {
            Text = L.T("ui.gain"), Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = true,
            Location = new Point(x, y), BackColor = Theme.Surface,
        };
        gainValue = new Label
        {
            Font = Theme.Small, ForeColor = Theme.Ink, AutoSize = true, BackColor = Theme.Surface,
        };
        y += Sx(18);
        gain = new TrackBar
        {
            Minimum = 0, Maximum = VoiceChain.MaxGainDb, TickFrequency = 6, SmallChange = 1, LargeChange = 3,
            Location = new Point(x, y), Width = w, Height = Sx(36), BackColor = Theme.Surface,
        };
        var gainBar = gain;
        var gainText = gainValue;
        gainBar.ValueChanged += (_, _) =>
        {
            gainText.Text = L.T("ui.gainValue", gainBar.Value);
            if (_suppress) return;
            _gainDebounce.Stop();
            _gainDebounce.Start();
        };
        gainBar.MouseUp += (_, _) => { _gainDebounce.Stop(); ApplyGain(); };
        gainBar.KeyUp += (_, _) => { _gainDebounce.Stop(); ApplyGain(); };
        y += Sx(42);

        startup = new CheckBox
        {
            Text = L.T("menu.startup"), Font = Theme.Body, ForeColor = Theme.Ink, AutoSize = true,
            Location = new Point(x, y), BackColor = Theme.Surface,
        };
        var startupBox = startup;
        startupBox.CheckedChanged += (_, _) => { if (!_suppress) _app.SetStartup(startupBox.Checked); };
        y += Sx(32);

        var reset = new LinkLabel
        {
            Text = L.T("ui.resetAudio"), Font = Theme.Body, LinkColor = Theme.Accent, ActiveLinkColor = Theme.AccentHover,
            AutoSize = true, Location = new Point(x, y),
        };
        reset.LinkClicked += (_, _) => { if (!_suppress) { _app.ResetAudioSettings(); RefreshFromApp(); } };
        y += Sx(28);

        var logs = new LinkLabel
        {
            Text = L.T("ui.logs"), Font = Theme.Body, LinkColor = Theme.Accent, ActiveLinkColor = Theme.AccentHover,
            AutoSize = true, Location = new Point(x, y),
        };
        logs.LinkClicked += (_, _) => _app.OpenLog();
        var get = new LinkLabel
        {
            Text = L.T("ui.getCable"), Font = Theme.Body, LinkColor = Theme.Accent, ActiveLinkColor = Theme.AccentHover,
            AutoSize = true,
        };
        get.LinkClicked += (_, _) => Open(CableUrl);
        y += Sx(28);
        get.Location = new Point(x + Sx(120), logs.Top);
        y += Sx(8);

        _installCable = new Button
        {
            Text = L.T("ui.installCable"), Font = Theme.Body, FlatStyle = FlatStyle.Flat,
            Location = new Point(x, y), Width = w, Height = Sx(32),
            BackColor = Theme.Surface, ForeColor = Theme.Ink, Cursor = Cursors.Hand,
        };
        _installCable.FlatAppearance.BorderColor = Theme.Line;
        _installCable.Click += async (_, _) => { if (!_suppress) await _app.InstallVbCableAsync(); };
        y += Sx(38);

        _autoUpdate = new CheckBox
        {
            Text = L.T("ui.autoUpdate"), Font = Theme.Body, ForeColor = Theme.Ink, AutoSize = true,
            Location = new Point(x, y), BackColor = Theme.Surface,
        };
        _autoUpdate.CheckedChanged += (_, _) => { if (!_suppress) _app.SetAutoUpdate(_autoUpdate.Checked); };
        y += Sx(32);

        _checkUpdate = new Button
        {
            Text = L.T("ui.checkUpdate"), Font = Theme.Body, FlatStyle = FlatStyle.Flat,
            Location = new Point(x, y), Width = w, Height = Sx(32),
            BackColor = Theme.Surface, ForeColor = Theme.Ink, Cursor = Cursors.Hand,
        };
        _checkUpdate.FlatAppearance.BorderColor = Theme.Line;
        _checkUpdate.Click += async (_, _) => { if (!_suppress) await _app.CheckForUpdatesAsync(interactive: true); };
        y += Sx(38);

        _updateStatus = new Label
        {
            Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = false,
            Location = new Point(x, y), Width = w, Height = Sx(36),
            BackColor = Theme.Surface,
        };
        y += Sx(38);

        _optimize = new Button
        {
            Text = L.T("ui.cable.optimize"), Font = Theme.Body, FlatStyle = FlatStyle.Flat,
            Location = new Point(x, y), Width = w, Height = Sx(32),
            BackColor = Theme.Surface, ForeColor = Theme.Ink, Cursor = Cursors.Hand,
        };
        _optimize.FlatAppearance.BorderColor = Theme.Line;
        _optimize.Click += (_, _) => _app.RequestOptimizeCable();
        p.Controls.Add(_optimize);
        y += Sx(38);

        _cableHint = new Label
        {
            Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = false,
            Location = new Point(x, y), Width = w, Height = Sx(36),
            BackColor = Theme.Surface,
        };
        p.Controls.Add(_cableHint);
        y += Sx(38);

        _periodHint = new Label
        {
            Font = Theme.Small, ForeColor = Theme.Amber, AutoSize = false,
            Location = new Point(x, y), Width = w, Height = Sx(36),
            BackColor = Theme.Surface,
        };
        p.Controls.Add(_periodHint);
        y += Sx(20);

        noCable.Location = new Point(x, y);
        y += Sx(28);
        p.Controls.Add(output);
        p.Controls.Add(latency);
        p.Controls.Add(outMode);
        p.Controls.Add(ncMode);
        p.Controls.Add(_studio);
        p.Controls.Add(_studioNote);
        p.Controls.Add(clarity);
        p.Controls.Add(autoVol);
        p.Controls.Add(frontFocus);
        p.Controls.Add(_frontFocusAr);
        p.Controls.Add(_speechLow);
        p.Controls.Add(_installCable);
        p.Controls.Add(_autoUpdate);
        p.Controls.Add(_checkUpdate);
        p.Controls.Add(_updateStatus);
        p.Controls.Add(reset);
        p.Controls.Add(gainLabel);
        p.Controls.Add(gainValue);
        p.Controls.Add(gain);
        p.Controls.Add(startup);
        p.Controls.Add(logs);
        p.Controls.Add(get);
        p.Height = y;
        return p;
    }

    private ComboBox Combo(Panel p, int x, ref int y, int w, string caption)
    {
        var cap = new Label
        {
            Text = caption, Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = true,
            Location = new Point(x, y), BackColor = Theme.Surface,
        };
        y += Sx(18);
        var box = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
            Font = Theme.Body, Location = new Point(x, y), Width = w, BackColor = Theme.Surface,
        };
        y += Sx(36);
        p.Controls.Add(cap);
        return box;
    }

    private void SetSettings(bool open)
    {
        _settingsOpen = open;
        _settings.Visible = open;
        _settingsBtn.BackColor = open ? Theme.SurfaceMuted : Theme.Background;
        int h = open ? Sx(500) + _settings.Height : Sx(500);
        ClientSize = new Size(Sx(392), h);
        LayoutHero();
        if (open) { FillDevices(); RefreshFromApp(); }
    }

    private void LayoutHero()
    {
        int w = ClientSize.Width;
        _orb.Location = new Point((w - _orb.Width) / 2, Sx(108));
        _state.SetBounds(Sx(32), _orb.Bottom + Sx(22), w - Sx(64), Sx(30));
        _detail.SetBounds(Sx(32), _state.Bottom + Sx(4), w - Sx(64), Sx(52));
        _settings.SetBounds(0, ClientSize.Height - _settings.Height, w, _settings.Height);
    }

    private void OnOrb()
    {
        switch (Face())
        {
            case ConnectFace.Disconnected: _app.SetArmed(true); break;
            case ConnectFace.Error: _app.RetryConnect(); break;
            default: _app.SetArmed(false); break;
        }
        RefreshFromApp();
    }

    private ConnectFace Face()
    {
        if (!_app.Armed) return ConnectFace.Disconnected;
        var s = _app.Status;
        if (s.Health == Health.Error) return ConnectFace.Error;
        if (_app.CableName is null && _app.OutputDeviceId is null) return ConnectFace.Error;
        if (s.Health == Health.Ok) return ConnectFace.Connected;
        if (s.Health == Health.Warn && _app.Stats?.Peer is not null) return ConnectFace.Connected;
        if (s.Health == Health.Warn && _app.WantedOutputName is null && _app.OutputDeviceId is not null) return ConnectFace.Error;
        return ConnectFace.Searching;
    }

    private void RefreshFromApp()
    {
        var face = Face();
        _orb.SetFace(face);
        _state.Text = face switch
        {
            ConnectFace.Connected => L.T("ui.connected"),
            ConnectFace.Searching => L.T("ui.searching"),
            ConnectFace.Error => L.T("ui.error"),
            _ => L.T("ui.disconnected"),
        };
        var st = _app.Status;
        var stats = _app.Stats;
        if (face == ConnectFace.Connected && stats?.Peer is { } peer)
        {
            var ip = peer.Split(':')[0];
            var who = string.IsNullOrEmpty(stats.Phone.Name) ? ip : stats.Phone.Name + "  ·  " + ip;
            var lat = _app.EstLatencyMs();
            _detail.Text = lat is { } ms ? $"{who}  ·  {ms.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)} ms" : who;
        }
        else _detail.Text = st.Detail;

        _noCable.Visible = _app.CableName is null && _app.OutputDeviceId is null;
        _optimize.Visible = _app.CableFound || _app.CableNeedsOptimize;
        _optimize.Enabled = true;
        _cableHint.Text = _app.OptimizeHint ?? "";
        _cableHint.ForeColor = _app.CableNeedsOptimize ? Theme.Amber : Theme.Muted;
        _periodHint.Text = _app.PeriodHint ?? "";
        _periodHint.Visible = !string.IsNullOrEmpty(_app.PeriodHint);

        _suppress = true;
        try
        {
            SelectCombo(_ncMode, i => _ncMode.Items[i] is NcModeItem it && it.Mode == _app.DisplayedNoiseMode);
            if (CanWrite(_clarity) && _clarity.Checked != _app.DisplayedVoiceClarity) _clarity.Checked = _app.DisplayedVoiceClarity;
            if (CanWrite(_autoVol) && _autoVol.Checked != _app.DisplayedAutoVolume) _autoVol.Checked = _app.DisplayedAutoVolume;
            if (CanWrite(_frontFocus) && _frontFocus.Checked != _app.DisplayedFrontFocus) _frontFocus.Checked = _app.DisplayedFrontFocus;
            if (CanWrite(_frontFocusAr) && _frontFocusAr.Checked != _app.DisplayedFrontFocusAr) _frontFocusAr.Checked = _app.DisplayedFrontFocusAr;
            SelectCombo(_studio, i => _studio.Items[i] is StudioItem it && it.Preset == _app.DisplayedStudio);
            var notes = new List<string>();
            if (_app.MicSilenced) notes.Add(L.T("ui.mic.silenced"));
            if (!string.IsNullOrEmpty(_app.StudioNotice)) notes.Add(_app.StudioNotice);
            if (_app.MoveCloserHint) notes.Add(L.T("ui.hint.closer"));
            _studioNote.Text = string.Join("\n", notes);
            _studioNote.Visible = notes.Count > 0;
            _studioNote.ForeColor = _app.MicSilenced || _app.MoveCloserHint || _app.StudioNotice != L.T("ui.studio.on") ? Theme.Amber : Theme.Muted;
            _speechLow.Visible = _app.SpeechLow;
            _speechLow.Text = L.T("ui.speechLow");
            if (_speechLow.Visible) _speechLow.Height = Sx(36);
            _detected.Text = string.IsNullOrEmpty(_app.DetectedSummary) ? "" : _app.DetectedSummary;
            if (CanWrite(_autoUpdate) && _autoUpdate.Checked != _app.DisplayedAutoUpdate) _autoUpdate.Checked = _app.DisplayedAutoUpdate;
            _updateStatus.Text = _app.UpdateCheckStatus ?? "";
            _installCable.Visible = _app.CableName is null && _app.OutputDeviceId is null;
            int g = Math.Clamp(_app.DisplayedGain, 0, VoiceChain.MaxGainDb);
            if (CanWrite(_gain) && _gain.Value != g) _gain.Value = g;
            _gainValue.Text = L.T("ui.gainValue", _gain.Value);
            _gainValue.Location = new Point(_settings.Width - _gainValue.Width - Sx(24), _gain.Top - Sx(20));
            try { if (CanWrite(_startup) && _startup.Checked != Startup.IsEnabled()) _startup.Checked = Startup.IsEnabled(); }
            catch (Exception) { }
            SelectCombo(_latency, i => _latency.Items[i] is PresetItem it && it.Preset == _app.Jitter);
            SelectCombo(_outMode, i => _outMode.Items[i] is OutModeItem it && it.Mode == _app.OutputMode);
        }
        finally { _suppress = false; }
    }

    private static bool CanWrite(Control c) =>
        SettingsPresentation.ShouldApplyToControl(c.Focused, c.Capture, c is ComboBox box && box.DroppedDown);

    private void SelectCombo(ComboBox box, Func<int, bool> match)
    {
        if (!CanWrite(box)) return;
        for (int i = 0; i < box.Items.Count; i++)
            if (match(i)) { if (box.SelectedIndex != i) box.SelectedIndex = i; break; }
    }

    private void ApplyGain()
    {
        if (_suppress) return;
        _app.SetMicGain(_gain.Value);
    }

    private void FillDevices()
    {
        _app.RecheckDevices();
        var auto = _app.CableName is null ? L.T("menu.output.autoNone") : L.T("menu.output.auto", _app.CableName);
        var items = new List<DeviceItem> { new(null, auto) };
        foreach (var d in _app.Devices.OrderBy(d => DeviceRules.VirtualCableRank(d.Name) is var r && r >= 0 ? r : 99).ThenBy(d => d.Name))
            items.Add(new DeviceItem(d, d.Name));

        bool same = _output.Items.Count == items.Count;
        if (same)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (_output.Items[i] is not DeviceItem cur || cur.Device?.Id != items[i].Device?.Id || cur.Label != items[i].Label)
                { same = false; break; }
            }
        }

        if (!CanWrite(_output) && same) return;

        _suppress = true;
        try
        {
            if (!same && CanWrite(_output))
            {
                _output.BeginUpdate();
                try
                {
                    _output.Items.Clear();
                    foreach (var it in items) _output.Items.Add(it);
                }
                finally { _output.EndUpdate(); }
            }
            int sel = 0;
            for (int i = 0; i < _output.Items.Count; i++)
                if (_output.Items[i] is DeviceItem it && it.Device?.Id == _app.OutputDeviceId) { sel = i; break; }
            if (CanWrite(_output) && _output.Items.Count > 0 && _output.SelectedIndex != sel)
                _output.SelectedIndex = sel;
        }
        finally { _suppress = false; }
    }

    private void OnOutput()
    {
        if (_output.SelectedItem is not DeviceItem it) return;
        if (it.Device is { } d && DeviceRules.VirtualCableRank(d.Name) < 0)
        {
            var ok = MessageBox.Show(L.T("dlg.notCable", d.Name), "CordMic", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (ok != DialogResult.Yes) { FillDevices(); return; }
        }
        _app.SetOutput(it.Device);
    }

    private int Sx(int px) => (int)Math.Round(px * DeviceDpi / 96.0);

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { }
    }

    private sealed record PresetItem(JitterPreset Preset, string Label) { public override string ToString() => Label; }
    private sealed record OutModeItem(OutputMode Mode, string Label) { public override string ToString() => Label; }
    private sealed record NcModeItem(NoiseMode Mode, string Label) { public override string ToString() => Label; }
    private sealed record StudioItem(StudioPreset Preset, string Label) { public override string ToString() => Label; }
    private sealed record DeviceItem(RenderDevice? Device, string Label) { public override string ToString() => Label; }
}

internal enum ConnectFace { Disconnected, Searching, Connected, Error }

/// <summary>The one round button. Fill colour eases toward the new state; searching sweeps an arc.</summary>
internal sealed class ConnectOrb : Control
{
    private ConnectFace _face = ConnectFace.Disconnected;
    private ConnectFace _from = ConnectFace.Disconnected;
    private float _blend = 1;
    private float _angle;
    private Bitmap? _glyph;
    private string _glyphName = "";

    public ConnectOrb()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
    }

    public void SetFace(ConnectFace face)
    {
        if (face == _face) return;
        _from = Displayed();
        _face = face;
        _blend = 0;
        Invalidate();
    }

    public void Advance()
    {
        _angle = (_angle + 7f) % 360f;
        if (_blend < 1) _blend = Math.Min(1, _blend + 0.18f);
        Invalidate();
    }

    private ConnectFace Displayed() => _blend >= 1 ? _face : _from;

    private static Color Of(ConnectFace f) => f switch
    {
        ConnectFace.Connected => Theme.Accent,
        ConnectFace.Error => Theme.Red,
        _ => Theme.Surface,
    };

    private static Color Lerp(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Background);
        var rect = new RectangleF(2, 2, Width - 5, Height - 5);
        Color fill = _blend >= 1 ? Of(_face) : Lerp(Of(_from), Of(_face), _blend);
        bool solid = fill.ToArgb() == Theme.Accent.ToArgb() || fill.ToArgb() == Theme.Red.ToArgb()
            || (_blend > 0.5f && (_face == ConnectFace.Connected || _face == ConnectFace.Error));
        using (var b = new SolidBrush(fill)) g.FillEllipse(b, rect);
        if (!solid)
        {
            using var pen = new Pen(Theme.Accent, 2.5f);
            g.DrawEllipse(pen, rect);
        }
        if (_face == ConnectFace.Searching || (_blend < 1 && _from == ConnectFace.Searching))
        {
            using var pen = new Pen(Theme.Accent, 3f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            g.DrawArc(pen, rect, _angle, 80);
        }
        string name = _face switch
        {
            ConnectFace.Searching => "search",
            ConnectFace.Error => "error_outline",
            _ => "mic",
        };
        Color ink = solid ? Color.White : Theme.Accent;
        int gs = (int)(Width * 0.38);
        if (_glyph is null || _glyphName != name + ink.ToArgb())
        {
            _glyph?.Dispose();
            _glyph = Theme.Glyph(name, ink, gs);
            _glyphName = name + ink.ToArgb();
        }
        if (_glyph is not null) g.DrawImage(_glyph, (Width - gs) / 2, (Height - gs) / 2, gs, gs);
    }

    protected override void Dispose(bool disposing) { if (disposing) _glyph?.Dispose(); base.Dispose(disposing); }
}
