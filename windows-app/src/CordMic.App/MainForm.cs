using System.Diagnostics;
using CordMic.Core;

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
    private readonly ComboBox _output, _latency, _outMode;
    private readonly CheckBox _nc, _startup;
    private readonly TrackBar _gain;
    private readonly Label _gainValue, _noCable;
    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 32 };
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

        _settings = BuildSettings(out _output, out _latency, out _outMode, out _nc, out _gain, out _gainValue, out _startup, out _noCable);
        _settings.Visible = false;

        Controls.Add(header);
        Controls.Add(_orb);
        Controls.Add(_state);
        Controls.Add(_detail);
        Controls.Add(_settings);

        _anim.Tick += (_, _) => { _orb.Advance(); if (++_animTicks % 8 == 0) RefreshFromApp(); };
        Load += (_, _) => { LayoutHero(); FillDevices(); RefreshFromApp(); _anim.Start(); };
        FormClosed += (_, _) => _anim.Dispose();
        Resize += (_, _) => LayoutHero();
    }

    private Panel BuildSettings(out ComboBox output, out ComboBox latency, out ComboBox outMode, out CheckBox nc, out TrackBar gain,
        out Label gainValue, out CheckBox startup, out Label noCable)
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

        nc = new CheckBox
        {
            Text = L.T("ui.nc"), Font = Theme.Body, ForeColor = Theme.Ink, AutoSize = true,
            Location = new Point(x, y), BackColor = Theme.Surface,
        };
        var ncBox = nc;
        ncBox.CheckedChanged += (_, _) => { if (!_suppress) _app.SetNoiseCancellation(ncBox.Checked); };
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
            if (!_suppress) _app.SetMicGain(gainBar.Value);
        };
        y += Sx(42);

        startup = new CheckBox
        {
            Text = L.T("menu.startup"), Font = Theme.Body, ForeColor = Theme.Ink, AutoSize = true,
            Location = new Point(x, y), BackColor = Theme.Surface,
        };
        var startupBox = startup;
        startupBox.CheckedChanged += (_, _) => { if (!_suppress) _app.SetStartup(startupBox.Checked); };
        y += Sx(32);

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
        y += Sx(20);

        noCable.Location = new Point(x, y);
        y += Sx(28);
        p.Controls.Add(output);
        p.Controls.Add(latency);
        p.Controls.Add(outMode);
        p.Controls.Add(nc);
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

        _suppress = true;
        try
        {
            if ((bool)_nc.Checked != _app.DisplayedNc) _nc.Checked = _app.DisplayedNc;
            int g = Math.Clamp(_app.DisplayedGain, 0, VoiceChain.MaxGainDb);
            if (_gain.Value != g) _gain.Value = g;
            _gainValue.Text = L.T("ui.gainValue", _gain.Value);
            _gainValue.Location = new Point(_settings.Width - _gainValue.Width - Sx(24), _gain.Top - Sx(20));
            try { if (_startup.Checked != Startup.IsEnabled()) _startup.Checked = Startup.IsEnabled(); }
            catch (Exception) { }
            for (int i = 0; i < _latency.Items.Count; i++)
                if (_latency.Items[i] is PresetItem it && it.Preset == _app.Jitter) { if (_latency.SelectedIndex != i) _latency.SelectedIndex = i; break; }
            for (int i = 0; i < _outMode.Items.Count; i++)
                if (_outMode.Items[i] is OutModeItem it && it.Mode == _app.OutputMode) { if (_outMode.SelectedIndex != i) _outMode.SelectedIndex = i; break; }
        }
        finally { _suppress = false; }
    }

    private void FillDevices()
    {
        _app.RecheckDevices();
        _suppress = true;
        try
        {
            _output.Items.Clear();
            var auto = _app.CableName is null ? L.T("menu.output.autoNone") : L.T("menu.output.auto", _app.CableName);
            _output.Items.Add(new DeviceItem(null, auto));
            foreach (var d in _app.Devices.OrderBy(d => DeviceRules.VirtualCableRank(d.Name) is var r && r >= 0 ? r : 99).ThenBy(d => d.Name))
                _output.Items.Add(new DeviceItem(d, d.Name));
            int sel = 0;
            for (int i = 0; i < _output.Items.Count; i++)
                if (_output.Items[i] is DeviceItem it && it.Device?.Id == _app.OutputDeviceId) { sel = i; break; }
            if (_output.Items.Count > 0) _output.SelectedIndex = sel;
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
