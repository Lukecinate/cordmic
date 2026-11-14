using CordMic.Core;

namespace CordMic.App;

/// <summary>
/// The main window: the round connect button and one status line. 0.7.0: every setting moved to
/// <see cref="SettingsForm"/> (the gear, or Settings… in the tray); a teal dot on the gear means an update is ready.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly AppController _app;
    private ConnectOrb _orb = null!;
    private Label _state = null!, _detail = null!;
    private Button _settingsBtn = null!;
    private LinkLabel _settingsLink = null!;
    private ToolTip? _tip;
    private Image? _gearGlyph;
    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 32 };
    private readonly DpiResources<FontSet> _fonts;
    private int _animTicks;
    private bool _dotShown;

    public MainForm(AppController app)
    {
        _app = app;
        _fonts = new DpiResources<FontSet>(DeviceDpi, Theme.FontsFor);
        Text = "CordMic";
        Icon = Theme.SharedAppIcon();
        BackColor = Theme.Background;
        ForeColor = Theme.Ink;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        // 0.7.1 (audit M7): every size comes from DeviceDpi and the fonts from a per-DPI set, so WinForms must not
        // scale on top of that (AutoScaleMode.Dpi plus manual sizing could scale twice on a non-100% primary screen).
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        BuildUi();

        _anim.Tick += (_, _) => { _orb.Advance(); if (++_animTicks % 8 == 0) RefreshFromApp(); };
        Load += (_, _) =>
        {
            _fonts.Switch(DeviceDpi, _ => RebuildUi()); // created on a monitor whose DPI differs from the system's
            LayoutHero(); RefreshFromApp(); _anim.Start(); _orb.Focus();
        };
        DpiChanged += (_, e) =>
        {
            var loc = e.SuggestedRectangle.Location;
            _fonts.Switch(e.DeviceDpiNew, _ => RebuildUi());
            Location = loc;
        };
        FormClosed += (_, _) => { _anim.Stop(); _anim.Dispose(); };
        Resize += (_, _) => { if (_orb is not null) LayoutHero(); };
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            // Audit M3: the tooltip and the gear bitmap are owned here (Button/ToolTip do not dispose them).
            _tip?.Dispose();
            _gearGlyph?.Dispose();
            _fonts.Dispose();
        }
    }

    private void RebuildUi()
    {
        bool focused = _orb?.Focused == true;
        SuspendLayout();
        var old = Controls.Cast<Control>().ToList();
        Controls.Clear();
        foreach (var c in old) c.Dispose();
        _tip?.Dispose(); _tip = null;
        _gearGlyph?.Dispose(); _gearGlyph = null;
        BuildUi();
        ResumeLayout(true);
        LayoutHero();
        RefreshFromApp();
        if (focused) _orb!.Focus();
    }

    private void BuildUi()
    {
        var fonts = _fonts.Current;
        Font = fonts.Body;
        ClientSize = new Size(Sx(392), Sx(440));

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
            Image = Theme.SharedLogo(), SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(logoS, logoS), BackColor = Color.Transparent, TabStop = false,
            Margin = new Padding(0, 0, HeaderLayout.Px(HeaderLayout.LogoTitleGap, dpi), 0),
            Anchor = AnchorStyles.Left,
        };
        var brand = new Label
        {
            Text = "CordMic", Font = fonts.Title, ForeColor = Theme.Ink, AutoSize = true,
            Margin = Padding.Empty, BackColor = Color.Transparent, Anchor = AnchorStyles.Left,
            UseMnemonic = false,
        };
        int gearS = HeaderLayout.Px(HeaderLayout.SettingsButton, dpi);
        int glyphS = HeaderLayout.Px(HeaderLayout.SettingsGlyph, dpi);
        _gearGlyph = Theme.Glyph("settings", Theme.Muted, glyphS);
        _settingsBtn = new Button
        {
            Text = string.Empty, Size = new Size(gearS, gearS), MinimumSize = new Size(gearS, gearS),
            FlatStyle = FlatStyle.Flat, BackColor = Theme.Background, UseVisualStyleBackColor = false,
            Cursor = Cursors.Hand, Margin = Padding.Empty, Padding = Padding.Empty,
            Image = _gearGlyph,
            ImageAlign = ContentAlignment.MiddleCenter,
            AccessibleName = L.T("ui.settings"), AccessibleRole = AccessibleRole.PushButton,
            Anchor = AnchorStyles.None,
        };
        _settingsBtn.FlatAppearance.BorderSize = 0;
        _settingsBtn.FlatAppearance.MouseOverBackColor = Theme.SurfaceMuted;
        _settingsBtn.FlatAppearance.MouseDownBackColor = Theme.Line;
        _settingsBtn.Click += (_, _) => _app.ShowSettings(null);
        _settingsBtn.Paint += (_, e) => PaintUpdateDot(e.Graphics);
        _tip = new ToolTip();
        _tip.SetToolTip(_settingsBtn, L.T("ui.settings"));
        header.Controls.Add(logo, 0, 0);
        header.Controls.Add(brand, 1, 0);
        header.Controls.Add(_settingsBtn, 2, 0);

        _orb = new ConnectOrb { Size = new Size(Sx(168), Sx(168)), TabStop = true, AccessibleRole = AccessibleRole.PushButton };
        _orb.Click += (_, _) => OnOrb();
        _orb.KeyDown += (_, e) => { if (e.KeyCode is Keys.Enter or Keys.Space) { OnOrb(); e.Handled = true; } };

        _state = new Label
        {
            Font = fonts.Title, ForeColor = Theme.Ink, TextAlign = ContentAlignment.MiddleCenter,
            AutoSize = false, Height = Sx(28), BackColor = Color.Transparent,
        };
        _detail = new Label
        {
            Font = fonts.Body, ForeColor = Theme.Muted, TextAlign = ContentAlignment.TopCenter,
            AutoSize = false, Height = Sx(48), BackColor = Color.Transparent,
        };
        _settingsLink = new LinkLabel
        {
            Text = L.T("ui.openSettings"), Font = fonts.Body, LinkColor = Theme.Accent, ActiveLinkColor = Theme.AccentHover,
            AutoSize = true, BackColor = Color.Transparent, TabStop = true,
        };
        _settingsLink.LinkClicked += (_, _) => _app.ShowSettings(null);

        Controls.Add(header);
        Controls.Add(_orb);
        Controls.Add(_state);
        Controls.Add(_detail);
        Controls.Add(_settingsLink);
        _dotShown = false;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        if (keyData == (Keys.Control | Keys.Oemcomma)) { _app.ShowSettings(null); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void PaintUpdateDot(Graphics g)
    {
        if (!_app.Update.ReadyToInstall) return;
        int d = Sx(8);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var b = new SolidBrush(Theme.Accent);
        g.FillEllipse(b, _settingsBtn.Width - d - Sx(4), Sx(4), d, d);
    }

    /// <summary>Repaints the gear when an update becomes ready or is installed.</summary>
    public void RefreshUpdateDot()
    {
        bool ready = _app.Update.ReadyToInstall;
        if (ready == _dotShown) return;
        _dotShown = ready;
        _settingsBtn.Invalidate();
        _settingsBtn.AccessibleDescription = ready ? L.T("menu.updateReady", _app.Update.Tag) : null;
    }

    private void LayoutHero()
    {
        int w = ClientSize.Width;
        _orb.Location = new Point((w - _orb.Width) / 2, Sx(108));
        _state.SetBounds(Sx(32), _orb.Bottom + Sx(22), w - Sx(64), Sx(30));
        _detail.SetBounds(Sx(32), _state.Bottom + Sx(4), w - Sx(64), Sx(52));
        _settingsLink.Location = new Point((w - _settingsLink.Width) / 2, ClientSize.Height - _settingsLink.Height - Sx(18));
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
        _orb.AccessibleName = _state.Text;
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
        RefreshUpdateDot();
    }

    private int Sx(int px) => (int)Math.Round(px * DeviceDpi / 96.0);
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
        SetStyle(ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

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
        string key = name + ink.ToArgb() + "@" + gs;
        if (_glyph is null || _glyphName != key)
        {
            _glyph?.Dispose();
            _glyph = Theme.Glyph(name, ink, gs);
            _glyphName = key;
        }
        if (_glyph is not null) g.DrawImage(_glyph, (Width - gs) / 2, (Height - gs) / 2, gs, gs);
        if (Focused && ShowFocusCues)
        {
            using var fp = new Pen(Theme.Ink, 1.5f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
            g.DrawEllipse(fp, 1, 1, Width - 3, Height - 3);
        }
    }

    protected override void Dispose(bool disposing) { if (disposing) _glyph?.Dispose(); base.Dispose(disposing); }
}
