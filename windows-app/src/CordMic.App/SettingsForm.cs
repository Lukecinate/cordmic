using System.Diagnostics;
using System.Drawing.Drawing2D;
using CordMic.Core;
using CordMic.Core.Dsp;
using CordMic.Core.Updates;

namespace CordMic.App;

/// <summary>
/// 0.7.0 Settings window: pages on the left (Connection, Sound, Noise &amp; voice, Updates, Advanced), one scrolling
/// column of settings on the right, each with a plain-language label and one help line. What is shown and in which
/// order comes from <see cref="SettingsCatalog"/>; geometry from <see cref="SettingsLayout"/> (device pixels for the
/// window's current DPI, re-laid out on every resize or DPI change). Keyboard: Tab / Shift+Tab, arrow keys inside a
/// group, Ctrl+Tab / Ctrl+Shift+Tab (or Ctrl+PageDown/PageUp) for pages, Esc closes.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly AppController _app;
    private readonly Panel _nav;
    private readonly List<NavItem> _navItems = new();
    private readonly Label _version;
    private readonly PagePanel _content;
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 500 };
    private SettingsPage _page;
    private readonly DpiResources<FontSet> _fonts;

    public SettingsPage Page => _page;
    /// <summary>Fonts for this window's current DPI (audit M7).</summary>
    internal FontSet Fonts => _fonts.Current;

    public SettingsForm(AppController app, SettingsPage page)
    {
        _app = app;
        _fonts = new DpiResources<FontSet>(DeviceDpi, Theme.FontsFor);
        Text = L.T("set.title");
        Icon = Theme.SharedAppIcon();
        Font = Fonts.Body;
        BackColor = Theme.Surface;
        ForeColor = Theme.Ink;
        AutoScaleMode = AutoScaleMode.None; // every position is computed from DeviceDpi in the layout passes below
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        ShowInTaskbar = true;

        _nav = new DoubleBufferedPanel { BackColor = Theme.Background, Dock = DockStyle.Left };
        _nav.Paint += (_, e) => { using var pen = new Pen(Theme.Line); e.Graphics.DrawLine(pen, _nav.Width - 1, 0, _nav.Width - 1, _nav.Height); };
        foreach (var p in SettingsCatalog.Pages)
        {
            var it = new NavItem(this, p, L.T(SettingsCatalog.TitleKey(p)));
            it.Click += (_, _) => ShowPage(it.Page);
            _navItems.Add(it);
            _nav.Controls.Add(it);
        }
        _version = new Label
        {
            Font = Fonts.Small, ForeColor = Theme.Muted, BackColor = Color.Transparent, AutoSize = false, UseMnemonic = false,
            Text = L.T("set.version", _app.CurrentVersion) + (_app.InstallKind == InstallKind.Portable ? "\n" + L.T("set.portable") : ""),
        };
        _nav.Controls.Add(_version);
        _nav.Layout += (_, _) => LayoutNav();

        _content = new PagePanel(this) { Dock = DockStyle.Fill, BackColor = Theme.Surface };
        Controls.Add(_content);
        Controls.Add(_nav);

        _refresh.Tick += (_, _) => RefreshNow();
        Load += (_, _) => { ApplyFonts(DeviceDpi, rebuild: false); ApplyInitialSize(); ShowPage(page); _refresh.Start(); };
        // Audit M7: per-monitor DPI. Move to the rectangle Windows suggests, rebuild fonts for the new DPI (the old ones
        // are disposed after every control switched), then lay everything out again.
        DpiChanged += (_, e) =>
        {
            Bounds = e.SuggestedRectangle;
            ApplyMinimum();
            _nav.Width = Sx(SettingsLayout.NavWidth);
            if (!ApplyFonts(e.DeviceDpiNew, rebuild: true)) _content.Rebuild();
        };
        FormClosed += (_, _) => { _refresh.Stop(); _refresh.Dispose(); };
    }

    private bool ApplyFonts(int dpi, bool rebuild) => _fonts.Switch(dpi, fs =>
    {
        Font = fs.Body;
        _version.Font = fs.Small;
        foreach (var it in _navItems) it.ApplyFonts();
        if (rebuild) _content.Rebuild();
        else _content.ApplyHeaderFonts();
    });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _fonts.Dispose();
    }

    internal int Sx(int logical) => SettingsLayout.Px(logical, DeviceDpi);
    internal AppController App => _app;

    private void ApplyInitialSize()
    {
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        var (w, h) = SettingsLayout.InitialSize(DeviceDpi, wa.Width, wa.Height);
        ApplyMinimum();
        Size = new Size(w, h);
        Location = new Point(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2);
        _nav.Width = Sx(SettingsLayout.NavWidth);
    }

    private void ApplyMinimum()
    {
        var wa = Screen.FromControl(this).WorkingArea;
        var (mw, mh) = SettingsLayout.MinimumSize(DeviceDpi, wa.Width, wa.Height);
        MinimumSize = new Size(mw, mh);
    }

    private void LayoutNav()
    {
        int y = Sx(14), x = Sx(10), w = _nav.Width - Sx(20), h = Sx(38);
        foreach (var it in _navItems) { it.SetBounds(x, y, w, h); y += h + Sx(2); }
        int vh = Sx(40);
        _version.SetBounds(Sx(22), _nav.ClientSize.Height - vh - Sx(12), w - Sx(12), vh);
    }

    public void ShowPage(SettingsPage p)
    {
        _page = p;
        foreach (var it in _navItems) { it.Selected = it.Page == p; it.TabIndex = (int)it.Page; }
        _content.Show(p);
        Text = L.T("set.title") + " – " + L.T(SettingsCatalog.TitleKey(p));
    }

    /// <summary>Pulls current values into the visible page (500 ms timer, and immediately on updater events).</summary>
    public void RefreshNow()
    {
        if (IsDisposed) return;
        foreach (var it in _navItems) it.Badge = it.Page == SettingsPage.Updates && _app.Update.ReadyToInstall;
        _content.RefreshRows();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            if (ActiveControl is ComboBox { DroppedDown: true }) return base.ProcessCmdKey(ref msg, keyData);
            Close();
            return true;
        }
        if (keyData is (Keys.Control | Keys.Tab) or (Keys.Control | Keys.PageDown)) { Step(+1); return true; }
        if (keyData is (Keys.Control | Keys.Shift | Keys.Tab) or (Keys.Control | Keys.PageUp)) { Step(-1); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void Step(int d)
    {
        int n = SettingsCatalog.Pages.Length;
        var next = SettingsCatalog.Pages[(((int)_page + d) % n + n) % n];
        ShowPage(next);
        _navItems[(int)next].Focus();
    }

    // ------------------------------------------------------------------ page

    /// <summary>Scrolling page: title, one lead line, then the rows, stacked by <see cref="SettingsLayout.Stack"/>.</summary>
    private sealed class PagePanel : DoubleBufferedPanel, IWheelScroll
    {
        private readonly SettingsForm _f;
        private readonly Label _title, _lead, _keys;
        private readonly List<Row> _rows = new();
        private SettingsPage _page;
        private bool _arranging;

        public PagePanel(SettingsForm f)
        {
            _f = f;
            AutoScroll = true;
            _title = new Label { Font = f.Fonts.Title, ForeColor = Theme.Ink, BackColor = Color.Transparent, AutoSize = false, UseMnemonic = false };
            _lead = new Label { Font = f.Fonts.Body, ForeColor = Theme.Muted, BackColor = Color.Transparent, AutoSize = false, UseMnemonic = false };
            _keys = new Label { Font = f.Fonts.Small, ForeColor = Theme.Muted, BackColor = Color.Transparent, AutoSize = false, UseMnemonic = false, Text = L.T("set.keys") };
        }

        public void Show(SettingsPage p) { _page = p; Rebuild(); }

        public void Wheel(int delta)
        {
            int lines = SystemInformation.MouseWheelScrollLines;
            int step = lines < 0 ? ClientSize.Height : _f.Sx(16) * Math.Max(1, lines);
            int y = -AutoScrollPosition.Y - delta * step / 120;
            AutoScrollPosition = new Point(0, Math.Max(0, y));
        }

        public void ApplyHeaderFonts()
        {
            _title.Font = _f.Fonts.Title;
            _lead.Font = _f.Fonts.Body;
            _keys.Font = _f.Fonts.Small;
        }

        public void Rebuild()
        {
            SuspendLayout();
            ApplyHeaderFonts();
            foreach (var r in _rows) r.Dispose();
            _rows.Clear();
            Controls.Clear();
            AutoScrollPosition = Point.Empty;
            _title.Text = L.T(SettingsCatalog.TitleKey(_page));
            _lead.Text = _page == SettingsPage.Updates
                ? L.T(SettingsCatalog.LeadKey(_page), _f.App.CurrentVersion)
                : L.T(SettingsCatalog.LeadKey(_page));
            Controls.Add(_title);
            Controls.Add(_lead);
            int tab = 0;
            foreach (var item in SettingsCatalog.Items(_page))
            {
                var row = RowFactory.Create(_f, item);
                if (row is null) continue;
                row.TabIndex = tab++;
                _rows.Add(row);
                Controls.Add(row);
            }
            _keys.TabIndex = tab;
            Controls.Add(_keys);
            foreach (var r in _rows) r.RefreshValue();
            ResumeLayout(false);
            PerformLayout();
        }

        public void RefreshRows()
        {
            bool relayout = false;
            foreach (var r in _rows)
            {
                bool wasShown = r.Wanted;
                if (r.RefreshValue()) relayout = true;
                if (r.Wanted != wasShown) relayout = true;
            }
            if (relayout) PerformLayout();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_arranging) return;
            _arranging = true;
            try { Arrange(); }
            finally { _arranging = false; }
        }

        private void Arrange()
        {
            double dpi = _f.DeviceDpi;
            int padX = _f.Sx(SettingsLayout.ContentPadX), padY = _f.Sx(SettingsLayout.ContentPadY);
            int w = Math.Max(_f.Sx(200), ClientSize.Width - 2 * padX);
            int oy = AutoScrollPosition.Y;
            int th = Row.Measure(_title, w), lh = Row.Measure(_lead, w);
            _title.SetBounds(padX, padY + oy, w, th);
            _lead.SetBounds(padX, padY + th + _f.Sx(4) + oy, w, lh);
            int header = th + _f.Sx(4) + lh + _f.Sx(10);
            var shown = _rows.Where(r => r.Wanted).ToList();
            var heights = shown.Select(r => r.Arrange(w)).ToList();
            var (tops, total) = SettingsLayout.Stack(heights, dpi, header);
            for (int i = 0; i < shown.Count; i++) shown[i].SetBounds(padX, tops[i] + oy, w, heights[i]);
            int kh = Row.Measure(_keys, w);
            _keys.SetBounds(padX, total + oy, w, kh);
            total += kh + padY;
            if (AutoScrollMinSize.Height != total) AutoScrollMinSize = new Size(0, total);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(Theme.Line);
            var shown = _rows.Where(r => r.Wanted).ToList();
            for (int i = 1; i < shown.Count; i++)
            {
                int y = shown[i].Top - _f.Sx(SettingsLayout.RowGap) / 2;
                e.Graphics.DrawLine(pen, shown[i].Left, y, shown[i].Right, y);
            }
        }

    }

    // ------------------------------------------------------------------ nav

    private sealed class NavItem : Control
    {
        private readonly SettingsForm _f;
        private bool _selected, _badge, _hover;
        public SettingsPage Page { get; }

        public NavItem(SettingsForm f, SettingsPage page, string text)
        {
            _f = f; Page = page; Text = text;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            TabStop = true; Cursor = Cursors.Hand; Font = f.Fonts.Body;
            AccessibleRole = AccessibleRole.PageTab; AccessibleName = text;
        }

        public bool Selected { get => _selected; set { if (_selected == value) return; _selected = value; ApplyFonts(); } }
        public void ApplyFonts() { Font = _selected ? _f.Fonts.Strong : _f.Fonts.Body; Invalidate(); }
        public bool Badge { get => _badge; set { if (_badge == value) return; _badge = value; AccessibleDescription = value ? L.T("menu.updateReady", _f.App.Update.Tag) : null; Invalidate(); } }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override bool IsInputKey(Keys k) => k is Keys.Up or Keys.Down || base.IsInputKey(k);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Enter or Keys.Space) { OnClick(EventArgs.Empty); e.Handled = true; return; }
            if (e.KeyCode is Keys.Up or Keys.Down)
            {
                int n = SettingsCatalog.Pages.Length;
                var next = SettingsCatalog.Pages[(((int)Page + (e.KeyCode == Keys.Down ? 1 : -1)) % n + n) % n];
                _f.ShowPage(next);
                _f._navItems[(int)next].Focus();
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Background);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (_selected || _hover)
            {
                using var path = Theme.Rounded(r, _f.Sx(6));
                using var b = new SolidBrush(_selected ? Theme.Surface : Theme.SurfaceMuted);
                g.FillPath(b, path);
            }
            if (_selected)
            {
                using var bar = new SolidBrush(Theme.Accent);
                g.FillRectangle(bar, 0, _f.Sx(8), _f.Sx(3), Height - _f.Sx(16));
            }
            var tr = new Rectangle(_f.Sx(12), 0, Width - _f.Sx(40), Height);
            TextRenderer.DrawText(g, Text, Font, tr, Theme.Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (_badge)
            {
                int d = _f.Sx(9);
                using var b = new SolidBrush(Theme.Accent);
                g.FillEllipse(b, Width - _f.Sx(14) - d, (Height - d) / 2, d, d);
            }
            if (Focused && ShowFocusCues)
            {
                using var pen = new Pen(Theme.Ink) { DashStyle = DashStyle.Dot };
                g.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
            }
        }
    }
}

/// <summary>Panel with double buffering (no flicker while the page scrolls or the 500 ms refresh runs).</summary>
internal class DoubleBufferedPanel : Panel
{
    public DoubleBufferedPanel() { DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); }
}

/// <summary>Builds the control for one catalog item and binds it to <see cref="AppController"/>.</summary>
internal static class RowFactory
{
    public static Row? Create(SettingsForm f, SettingItem item)
    {
        var app = f.App;
        string T(string k) => L.T(k);
        string? H(SettingItem i) => i.HelpKey is null ? null : L.T(i.HelpKey);
        switch (item.Id)
        {
            // Connection
            case "phone":
                return new StatusRow(f, T(item.LabelKey), H(item), () => app.Status.Title, () => app.Status.Detail);
            case "pairing":
                return new ToggleRow(f, T(item.LabelKey), H(item), () => app.PairingEnabled, v => app.SetPairing(v))
                {
                    Extra = () => app.PairingCode is { } pc ? L.T("set.pairing.code", pc[..3] + " " + pc[3..])
                        + (app.PairingWaitingFor is { } wf ? "\n" + L.T("ui.pair.waiting", wf) : "") : null,
                    ExtraLink = (L.T("ui.pair.new"), () => app.NewPairingCode()),
                };
            case "startup":
                return new ToggleRow(f, T(item.LabelKey), H(item), SafeStartup, v => app.SetStartup(v));
            case "language":
                return new ChoiceRow(f, T(item.LabelKey), H(item),
                    () => new List<(object, string)> { (L.Auto, L.T("menu.language.auto")), (L.English, "English"), (L.Indonesian, "Bahasa Indonesia") },
                    () => app.Language, k => app.SetLanguage((string)k));
            // Sound
            case "output":
                return new ChoiceRow(f, T(item.LabelKey), H(item), () => OutputItems(app), () => (object?)app.OutputDeviceId ?? AutoKey.Value,
                    k => app.SetOutput(k is RenderDevice d ? d : null), keyEquals: (a, b) => KeyOf(a) == KeyOf(b), onDropDown: app.RecheckDevices);
            case "installCable":
                return new ButtonRow(f, T(item.LabelKey), H(item), async () => await app.InstallVbCableAsync(), () => app.CableInstallStatus)
                { ShowWhen = () => app.CableName is null && app.OutputDeviceId is null };
            case "autoVolume":
                return new ToggleRow(f, T(item.LabelKey), H(item), () => app.DisplayedAutoVolume, v => app.SetAutoVolume(v));
            case "gain":
                return new SliderRow(f, T(item.LabelKey), H(item), 0, VoiceChain.MaxGainDb, () => app.DisplayedGain, v => app.SetMicGain(v), v => L.T("ui.gainValue", v));
            case "delay":
                return new SegmentedRow(f, T(item.LabelKey), H(item),
                    SettingsChoices.DelayChoices.Select(c => ((object)c, L.T(SettingsChoices.DelayKey(c)))).ToList(),
                    () => app.DisplayedDelay, k => app.SetDelayChoice((DelayChoice)k))
                {
                    Extra = () =>
                    {
                        var parts = new List<string>();
                        if (app.DisplayedDelay == DelayChoice.Custom) parts.Add(L.T("set.delay.custom"));
                        if (app.EstLatencyMs() is { } ms) parts.Add(L.T("set.delay.now", ms.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)));
                        return parts.Count == 0 ? null : string.Join(" ", parts);
                    },
                };
            // Noise & voice
            case "noise":
                return new RadioRow(f, T(item.LabelKey), H(item),
                    SettingsChoices.NoiseChoices.Select(c => ((object)c, L.T(SettingsChoices.NoiseKey(c)), L.T(SettingsChoices.NoiseKey(c) + ".help"))).ToList(),
                    () => app.DisplayedNoiseChoice, k => app.SetNoiseChoice((NoiseChoice)k))
                {
                    Note = () =>
                    {
                        var notes = new List<string>();
                        if (app.MicSilenced) notes.Add(L.T("ui.mic.silenced"));
                        if (!string.IsNullOrEmpty(app.StudioNotice) && app.StudioNotice != L.T("ui.studio.on")) notes.Add(app.StudioNotice);
                        if (app.MoveCloserHint) notes.Add(L.T("ui.hint.closer"));
                        return notes.Count == 0 ? null : string.Join("\n", notes);
                    },
                };
            case "clarity":
                return new ToggleRow(f, T(item.LabelKey), H(item), () => app.DisplayedVoiceClarity, v => app.SetVoiceClarity(v));
            case "frontFocus":
                return new ToggleRow(f, T(item.LabelKey), H(item), () => app.DisplayedFrontFocus, v => app.SetFrontFocus(v));
            case "detected":
                return new NoteRow(f, T(item.LabelKey), () =>
                {
                    var lines = new List<string>();
                    if (!string.IsNullOrEmpty(app.DetectedSummary)) lines.Add(app.DetectedSummary);
                    if (app.SpeechLow) lines.Add(L.T("ui.speechLow"));
                    return lines.Count == 0 ? null : string.Join("\n", lines);
                }, () => app.SpeechLow ? Theme.Amber : Theme.Muted);
            // Updates
            case "updateCard":
                return new UpdateCardRow(f);
            case "autoUpdate":
                return new ToggleRow(f, T(item.LabelKey), H(item), () => app.DisplayedAutoUpdate, v => app.SetAutoUpdate(v));
            case "installOnQuit":
                return new ToggleRow(f, T(item.LabelKey), H(item), () => app.InstallOnQuit, v => app.SetInstallOnQuit(v));
            case "releasePage":
                return new LinkRow(f, T(item.LabelKey) + " \u2197", null, app.OpenReleasePage);
            // Advanced
            case "jitter":
                return new ChoiceRow(f, T(item.LabelKey), H(item),
                    () => Enum.GetValues<JitterPreset>().Select(p => ((object)p, Presets.Label(p))).ToList(),
                    () => app.Jitter, k => app.SetJitter((JitterPreset)k));
            case "outMode":
                return new ChoiceRow(f, T(item.LabelKey), H(item),
                    () => OutputModes.All.Select(m => ((object)m, OutputModes.Label(m))).ToList(),
                    () => app.OutputMode, k => app.SetOutputMode((OutputMode)k));
            case "frontFocusAr":
                return new ToggleRow(f, T(item.LabelKey), H(item), () => app.DisplayedFrontFocusAr, v => app.SetFrontFocusAudioRecord(v));
            case "optimizeCable":
                return new ButtonRow(f, T(item.LabelKey), H(item), async () =>
                {
                    if (app.OptimizeRunning) { app.CancelOptimizeCable(); return; }
                    await app.RequestOptimizeCableAsync();
                }, () => string.Join("\n", new[] { app.OptimizeStatus, app.PeriodHint }.Where(s => !string.IsNullOrEmpty(s))))
                {
                    ShowWhen = () => app.CableFound || app.CableNeedsOptimize || app.OptimizeRunning,
                    Caption = () => app.OptimizeRunning ? L.T("ui.cable.optimizeCancel") : L.T("ui.cable.optimize"),
                };
            case "fixRoute":
                return new ButtonRow(f, T(item.LabelKey), H(item), () => { app.FixRoute(); return Task.CompletedTask; }, () => null);
            case "micMode":
                return new LinkRow(f, T(item.LabelKey), null, app.ShowMicModeInfo);
            case "getCable":
                return new LinkRow(f, T(item.LabelKey) + " \u2197", null, () => { try { Process.Start(new ProcessStartInfo("https://vb-audio.com/Cable/") { UseShellExecute = true }); } catch (Exception) { } });
            case "openLog":
                return new LinkRow(f, T(item.LabelKey), null, app.OpenLog);
            case "resetAudio":
                return new LinkRow(f, T(item.LabelKey), H(item), app.ResetAudioSettings);
        }
        return null;
    }

    private sealed class AutoKey { public static readonly AutoKey Value = new(); }

    private static string KeyOf(object? o) => o switch { RenderDevice d => d.Id, string s => s, _ => "" };

    private static bool SafeStartup() { try { return Startup.IsEnabled(); } catch (Exception) { return false; } }

    private static List<(object, string)> OutputItems(AppController app)
    {
        var auto = app.CableName is null ? L.T("menu.output.autoNone") : L.T("menu.output.auto", app.CableName);
        var items = new List<(object, string)> { (AutoKey.Value, auto) };
        foreach (var d in app.Devices.OrderBy(d => DeviceRules.VirtualCableRank(d.Name) is var r && r >= 0 ? r : 99).ThenBy(d => d.Name))
            items.Add((d, d.Name));
        return items;
    }
}
