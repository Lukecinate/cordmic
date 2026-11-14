using System.Drawing.Drawing2D;
using System.Globalization;
using CordMic.Core;
using CordMic.Core.Updates;

namespace CordMic.App;

/// <summary>
/// One setting in the Settings window. Each row lays its own children out for a given width (device pixels) and
/// returns its height; the page stacks rows with <see cref="SettingsLayout.Stack"/>. <see cref="RefreshValue"/> pulls
/// the current value from <see cref="AppController"/> without overwriting a control the user is editing.
/// </summary>
internal abstract class Row : DoubleBufferedPanel
{
    protected readonly SettingsForm F;
    protected bool Suppress;
    public Func<bool>? ShowWhen { get; init; }
    public bool Wanted { get; private set; } = true;

    protected Row(SettingsForm f) { F = f; BackColor = Theme.Surface; Margin = Padding.Empty; }

    protected int Sx(int v) => F.Sx(v);
    /// <summary>Fonts for the window's current DPI (rows are rebuilt when it changes).</summary>
    protected FontSet Fonts => F.Fonts;

    public abstract int Arrange(int width);

    /// <summary>Returns true when text changed so heights may differ (the page then lays out again).</summary>
    public virtual bool RefreshValue()
    {
        bool want = ShowWhen?.Invoke() ?? true;
        if (want != Wanted) { Wanted = want; Visible = want; }
        return false;
    }

    internal static int Measure(Label l, int w) =>
        string.IsNullOrEmpty(l.Text) ? 0
            : TextRenderer.MeasureText(l.Text, l.Font, new Size(Math.Max(1, w), int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix).Height;

    protected static Label Lbl(Font font, Color color, string? text = null) => new()
    {
        Font = font, ForeColor = color, BackColor = Color.Transparent, AutoSize = false, UseMnemonic = false, Text = text ?? "",
    };

    /// <summary>Places a label at (x, y) with width w; returns the y below it (unchanged when the label is empty).</summary>
    protected static int Place(Label l, int x, int y, int w)
    {
        if (string.IsNullOrEmpty(l.Text)) { l.Visible = false; return y; }
        l.Visible = true;
        int h = Measure(l, w);
        l.SetBounds(x, y, w, h);
        return y + h;
    }

    protected static bool SetText(Control c, string? t)
    {
        t ??= "";
        if (c.Text == t) return false;
        c.Text = t;
        return true;
    }

    protected static bool CanWrite(Control c) =>
        SettingsPresentation.ShouldApplyToControl(c.Focused, c.Capture, c is ComboBox { DroppedDown: true });

    protected Button MakeButton(string text, bool primary)
    {
        var b = primary ? Theme.Primary(text) : Theme.Secondary(text);
        b.Font = primary ? Fonts.Strong : Fonts.Body;
        b.AutoSize = false;
        b.MinimumSize = Size.Empty;
        b.Padding = Padding.Empty;
        return b;
    }

    protected void SizeButton(Button b)
    {
        int tw = TextRenderer.MeasureText(b.Text, b.Font).Width;
        b.Size = new Size(Math.Max(Sx(112), tw + Sx(32)), Sx(36));
    }

    protected LinkLabel MakeLink(string text, Action onClick)
    {
        var l = new LinkLabel
        {
            Text = text, Font = Fonts.Body, AutoSize = false, UseMnemonic = false, BackColor = Color.Transparent,
            LinkColor = Theme.Accent, ActiveLinkColor = Theme.AccentHover, VisitedLinkColor = Theme.Accent,
            LinkBehavior = LinkBehavior.HoverUnderline, TabStop = true,
        };
        l.LinkClicked += (_, _) => onClick();
        return l;
    }

    protected static int PlaceLink(LinkLabel l, int x, int y)
    {
        var sz = TextRenderer.MeasureText(l.Text, l.Font);
        l.SetBounds(x, y, sz.Width + 4, sz.Height + 2);
        return y + sz.Height + 2;
    }
}

/// <summary>Implemented by the scrolling page so focused lists and sliders hand the mouse wheel back to it.</summary>
internal interface IWheelScroll { void Wheel(int delta); }

/// <summary>
/// Mouse-wheel guard: a focused slider or list would otherwise change its value while the user scrolls the page
/// (changing mic gain by accident). The wheel scrolls the page unless the list is open.
/// </summary>
internal static class WheelGuard
{
    private const int WM_MOUSEWHEEL = 0x020A;

    public static bool Forward(Control c, ref Message m)
    {
        if (m.Msg != WM_MOUSEWHEEL || c is ComboBox { DroppedDown: true }) return false;
        for (var p = c.Parent; p is not null; p = p.Parent)
        {
            if (p is IWheelScroll w)
            {
                w.Wheel((short)(((long)m.WParam >> 16) & 0xFFFF));
                return true;
            }
        }
        return false;
    }
}

internal sealed class PageTrackBar : TrackBar
{
    protected override void WndProc(ref Message m) { if (!WheelGuard.Forward(this, ref m)) base.WndProc(ref m); }
}

internal sealed class PageComboBox : ComboBox
{
    protected override void WndProc(ref Message m) { if (!WheelGuard.Forward(this, ref m)) base.WndProc(ref m); }
}

/// <summary>On/off switch drawn in the theme colours; still a CheckBox for keyboard (Space) and screen readers.</summary>
internal sealed class ToggleSwitch : CheckBox
{
    private readonly SettingsForm _f;

    public ToggleSwitch(SettingsForm f)
    {
        _f = f;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        AutoSize = false; Text = ""; Cursor = Cursors.Hand; TabStop = true;
    }

    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int pad = _f.Sx(3);
        var track = new RectangleF(pad, pad, Width - 2 * pad - 1, Height - 2 * pad - 1);
        Color fill = !Enabled ? Theme.Line : Checked ? Theme.Accent : Theme.Outline;
        using (var path = Theme.Rounded(track, track.Height / 2))
        using (var b = new SolidBrush(fill))
            g.FillPath(b, path);
        float d = track.Height - _f.Sx(6);
        float kx = Checked ? track.Right - _f.Sx(3) - d : track.Left + _f.Sx(3);
        using (var kb = new SolidBrush(Color.White))
            g.FillEllipse(kb, kx, track.Top + _f.Sx(3), d, d);
        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(Theme.Ink) { DashStyle = DashStyle.Dot };
            using var path = Theme.Rounded(new RectangleF(1, 1, Width - 3, Height - 3), (Height - 3) / 2f);
            g.DrawPath(pen, path);
        }
    }
}

internal sealed class ToggleRow : Row
{
    private readonly Label _title, _help, _extra;
    private readonly ToggleSwitch _toggle;
    private readonly Func<bool> _get;
    private LinkLabel? _link;

    public Func<string?>? Extra { get; init; }
    public (string Text, Action OnClick)? ExtraLink { init { if (value is { } v) { _link = MakeLink(v.Text, v.OnClick); Controls.Add(_link); } } }

    public ToggleRow(SettingsForm f, string title, string? help, Func<bool> get, Action<bool> set) : base(f)
    {
        _get = get;
        _title = Lbl(Fonts.Strong, Theme.Ink, title);
        _help = Lbl(Fonts.Small, Theme.Muted, help);
        _extra = Lbl(Fonts.Body, Theme.Ink);
        _toggle = new ToggleSwitch(f) { AccessibleName = title, AccessibleDescription = help };
        _toggle.CheckedChanged += (_, _) => { if (!Suppress) set(_toggle.Checked); };
        _title.Click += (_, _) => { _toggle.Focus(); _toggle.Checked = !_toggle.Checked; };
        Controls.AddRange(new Control[] { _title, _help, _extra, _toggle });
    }

    public override int Arrange(int width)
    {
        int tw = Sx(46), th = Sx(26), gap = Sx(16);
        int textW = width - tw - gap;
        int y = Place(_title, 0, 0, textW);
        if (!string.IsNullOrEmpty(_help.Text)) y = Place(_help, 0, y + Sx(2), textW);
        if (!string.IsNullOrEmpty(_extra.Text)) y = Place(_extra, 0, y + Sx(8), textW); else _extra.Visible = false;
        if (_link is not null)
        {
            _link.Visible = !string.IsNullOrEmpty(_extra.Text);
            if (_link.Visible) y = PlaceLink(_link, 0, y + Sx(4));
        }
        _toggle.SetBounds(width - tw, 0, tw, th);
        return Math.Max(y, th);
    }

    public override bool RefreshValue()
    {
        base.RefreshValue();
        if (!_toggle.Capture && _toggle.Checked != _get())
        {
            Suppress = true;
            _toggle.Checked = _get();
            Suppress = false;
        }
        return Extra is not null && SetText(_extra, Extra());
    }
}

internal sealed class ChoiceRow : Row
{
    private sealed record Item(object Key, string Text) { public override string ToString() => Text; }

    private readonly Label _title, _help;
    private readonly PageComboBox _combo;
    private readonly Func<List<(object Key, string Text)>> _items;
    private readonly Func<object> _get;
    private readonly Func<object?, object?, bool> _eq;
    private string _sig = "";

    public ChoiceRow(SettingsForm f, string title, string? help, Func<List<(object, string)>> items, Func<object> get, Action<object> set,
        Func<object?, object?, bool>? keyEquals = null, Action? onDropDown = null) : base(f)
    {
        _items = items; _get = get; _eq = keyEquals ?? Equals;
        _title = Lbl(Fonts.Strong, Theme.Ink, title);
        _help = Lbl(Fonts.Small, Theme.Muted, help);
        _combo = new PageComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Font = Fonts.Body, FlatStyle = FlatStyle.System, AccessibleName = title, AccessibleDescription = help };
        _combo.SelectionChangeCommitted += (_, _) =>
        {
            if (Suppress || _combo.SelectedItem is not Item it) return;
            set(it.Key);
            Sync(force: true); // the app may have declined (e.g. output that is not a virtual cable)
        };
        _combo.DropDown += (_, _) => { onDropDown?.Invoke(); Fill(); };
        Controls.AddRange(new Control[] { _title, _combo, _help });
        Fill();
    }

    private void Fill()
    {
        var list = _items();
        var sig = string.Join("\u0001", list.Select(i => i.Text));
        if (sig == _sig) return;
        _sig = sig;
        Suppress = true;
        _combo.BeginUpdate();
        _combo.Items.Clear();
        foreach (var (k, t) in list) _combo.Items.Add(new Item(k, t));
        _combo.EndUpdate();
        Suppress = false;
        Sync(force: true);
    }

    private void Sync(bool force)
    {
        if (!force && !CanWrite(_combo)) return;
        var cur = _get();
        for (int i = 0; i < _combo.Items.Count; i++)
        {
            if (_combo.Items[i] is Item it && _eq(it.Key, cur))
            {
                if (_combo.SelectedIndex != i) { Suppress = true; _combo.SelectedIndex = i; Suppress = false; }
                return;
            }
        }
    }

    public override int Arrange(int width)
    {
        int y = Place(_title, 0, 0, width);
        int cw = Math.Min(width, Sx(420));
        _combo.SetBounds(0, y + Sx(6), cw, _combo.PreferredHeight);
        y = _combo.Bottom;
        if (!string.IsNullOrEmpty(_help.Text)) y = Place(_help, 0, y + Sx(6), width);
        return y;
    }

    public override bool RefreshValue()
    {
        base.RefreshValue();
        if (CanWrite(_combo)) { Fill(); Sync(force: false); }
        return false;
    }
}

internal sealed class SliderRow : Row
{
    private readonly Label _title, _value, _help;
    private readonly PageTrackBar _bar;
    private readonly Func<int> _get;
    private readonly Func<int, string> _fmt;
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 250 };

    public SliderRow(SettingsForm f, string title, string? help, int min, int max, Func<int> get, Action<int> set, Func<int, string> fmt) : base(f)
    {
        _get = get; _fmt = fmt;
        _title = Lbl(Fonts.Strong, Theme.Ink, title);
        _value = Lbl(Fonts.Strong, Theme.Accent);
        _value.TextAlign = ContentAlignment.TopRight;
        _help = Lbl(Fonts.Small, Theme.Muted, help);
        _bar = new PageTrackBar { Minimum = min, Maximum = max, TickFrequency = 6, SmallChange = 1, LargeChange = 3, AutoSize = false, BackColor = Theme.Surface, AccessibleName = title, AccessibleDescription = help };
        _bar.ValueChanged += (_, _) =>
        {
            _value.Text = _fmt(_bar.Value);
            if (Suppress) return;
            _debounce.Stop(); _debounce.Start();
        };
        _debounce.Tick += (_, _) => { _debounce.Stop(); set(_bar.Value); };
        // Audit L3: a change still waiting in the debounce is applied, not dropped, when the row goes away.
        Disposed += (_, _) => { if (_debounce.Enabled) { _debounce.Stop(); try { set(_bar.Value); } catch (Exception) { } } _debounce.Dispose(); };
        Controls.AddRange(new Control[] { _title, _value, _bar, _help });
    }

    public override int Arrange(int width)
    {
        int vw = Sx(90);
        Place(_title, 0, 0, width - vw);
        int y = Place(_value, width - vw, 0, vw);
        y = Math.Max(y, _title.Bottom);
        _bar.SetBounds(-Sx(4), y + Sx(4), Math.Min(width, Sx(440)) + Sx(8), Sx(34));
        y = _bar.Bottom;
        if (!string.IsNullOrEmpty(_help.Text)) y = Place(_help, 0, y + Sx(2), width);
        return y;
    }

    public override bool RefreshValue()
    {
        base.RefreshValue();
        if (!_debounce.Enabled && CanWrite(_bar))
        {
            int v = Math.Clamp(_get(), _bar.Minimum, _bar.Maximum);
            if (_bar.Value != v) { Suppress = true; _bar.Value = v; Suppress = false; }
        }
        return SetText(_value, _fmt(_bar.Value)) && false;
    }
}

/// <summary>A short row of mutually exclusive buttons (Delay or stability). None is pressed when the value is custom.</summary>
internal sealed class SegmentedRow : Row
{
    private readonly Label _title, _help, _extra;
    private readonly List<(object Key, RadioButton Button)> _buttons = new();
    private readonly Func<object> _get;
    public Func<string?>? Extra { get; init; }

    public SegmentedRow(SettingsForm f, string title, string? help, List<(object, string)> options, Func<object> get, Action<object> set) : base(f)
    {
        _get = get;
        _title = Lbl(Fonts.Strong, Theme.Ink, title);
        _help = Lbl(Fonts.Small, Theme.Muted, help);
        _extra = Lbl(Fonts.Small, Theme.Ink);
        Controls.AddRange(new Control[] { _title, _help, _extra });
        foreach (var (key, text) in options)
        {
            var rb = new RadioButton
            {
                Text = text, Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleCenter,
                Font = Fonts.Body, BackColor = Theme.Surface, ForeColor = Theme.Ink, AutoSize = false, Cursor = Cursors.Hand,
                UseMnemonic = false, AccessibleName = title + ": " + text,
            };
            rb.FlatAppearance.BorderColor = Theme.Outline;
            rb.FlatAppearance.CheckedBackColor = Theme.AccentSoft;
            rb.FlatAppearance.MouseOverBackColor = Theme.SurfaceMuted;
            var k = key;
            rb.CheckedChanged += (_, _) =>
            {
                rb.FlatAppearance.BorderColor = rb.Checked ? Theme.Accent : Theme.Outline;
                rb.Font = rb.Checked ? Fonts.Strong : Fonts.Body;
                if (!Suppress && rb.Checked) set(k);
            };
            _buttons.Add((key, rb));
            Controls.Add(rb);
        }
    }

    public override int Arrange(int width)
    {
        int y = Place(_title, 0, 0, width);
        if (!string.IsNullOrEmpty(_help.Text)) y = Place(_help, 0, y + Sx(2), width);
        y += Sx(8);
        int n = _buttons.Count, gap = Sx(8), h = Sx(36);
        int bw = Math.Min(Sx(170), (width - gap * (n - 1)) / Math.Max(1, n));
        bool stacked = bw < Sx(112);
        for (int i = 0; i < n; i++)
        {
            var b = _buttons[i].Button;
            if (stacked) { b.SetBounds(0, y, Math.Min(width, Sx(320)), h); y += h + gap; }
            else b.SetBounds(i * (bw + gap), y, bw, h);
        }
        if (!stacked) y += h; else y -= gap;
        if (!string.IsNullOrEmpty(_extra.Text)) y = Place(_extra, 0, y + Sx(8), width); else _extra.Visible = false;
        return y;
    }

    public override bool RefreshValue()
    {
        base.RefreshValue();
        if (!_buttons.Any(b => b.Button.Focused || b.Button.Capture))
        {
            var cur = _get();
            Suppress = true;
            foreach (var (k, b) in _buttons) if (b.Checked != Equals(k, cur)) b.Checked = Equals(k, cur);
            Suppress = false;
        }
        return Extra is not null && SetText(_extra, Extra());
    }
}

/// <summary>Vertical radio list with one help line per option (Noise reduction).</summary>
internal sealed class RadioRow : Row
{
    private readonly Label _title, _help, _note;
    private readonly List<(object Key, RadioButton Button, Label Help)> _opts = new();
    private readonly Func<object> _get;
    public Func<string?>? Note { get; init; }

    public RadioRow(SettingsForm f, string title, string? help, List<(object, string, string)> options, Func<object> get, Action<object> set) : base(f)
    {
        _get = get;
        _title = Lbl(Fonts.Strong, Theme.Ink, title);
        _help = Lbl(Fonts.Small, Theme.Muted, help);
        _note = Lbl(Fonts.Small, Theme.Amber);
        Controls.AddRange(new Control[] { _title, _help, _note });
        foreach (var (key, text, optHelp) in options)
        {
            var rb = new RadioButton { Text = text, Font = Fonts.Body, ForeColor = Theme.Ink, BackColor = Color.Transparent, AutoSize = false, UseMnemonic = false, AccessibleDescription = optHelp };
            var hl = Lbl(Fonts.Small, Theme.Muted, optHelp);
            var k = key;
            rb.CheckedChanged += (_, _) => { rb.Font = rb.Checked ? Fonts.Strong : Fonts.Body; if (!Suppress && rb.Checked) set(k); };
            hl.Click += (_, _) => { rb.Focus(); rb.Checked = true; };
            _opts.Add((key, rb, hl));
            Controls.Add(rb);
            Controls.Add(hl);
        }
    }

    public override int Arrange(int width)
    {
        int y = Place(_title, 0, 0, width);
        if (!string.IsNullOrEmpty(_help.Text)) y = Place(_help, 0, y + Sx(2), width);
        y += Sx(6);
        int indent = Sx(24);
        foreach (var (_, rb, hl) in _opts)
        {
            int rh = Math.Max(Sx(24), TextRenderer.MeasureText(rb.Text, Fonts.Strong).Height + Sx(6));
            rb.SetBounds(0, y + Sx(4), width, rh);
            y = Place(hl, indent, rb.Bottom - Sx(2), width - indent);
        }
        if (!string.IsNullOrEmpty(_note.Text)) y = Place(_note, 0, y + Sx(10), width); else _note.Visible = false;
        return y;
    }

    public override bool RefreshValue()
    {
        base.RefreshValue();
        if (!_opts.Any(o => o.Button.Focused || o.Button.Capture))
        {
            var cur = _get();
            Suppress = true;
            foreach (var (k, rb, _) in _opts) if (rb.Checked != Equals(k, cur)) rb.Checked = Equals(k, cur);
            Suppress = false;
        }
        return Note is not null && SetText(_note, Note());
    }
}

internal sealed class ButtonRow : Row
{
    private readonly Label _help, _status;
    private readonly Button _button;
    private readonly Func<string?> _statusText;
    public Func<string>? Caption { get; init; }

    public ButtonRow(SettingsForm f, string text, string? help, Func<Task> onClick, Func<string?> status) : base(f)
    {
        _statusText = status;
        _button = MakeButton(text, primary: false);
        _button.AccessibleDescription = help;
        _help = Lbl(Fonts.Small, Theme.Muted, help);
        _status = Lbl(Fonts.Small, Theme.Ink);
        _button.Click += async (_, _) =>
        {
            try { await onClick(); }
            catch (Exception ex) { F.App.Log("settings action failed: " + ex.GetType().Name); }
        };
        Controls.AddRange(new Control[] { _button, _help, _status });
    }

    public override int Arrange(int width)
    {
        SizeButton(_button);
        _button.Location = new Point(0, 0);
        int y = _button.Bottom;
        if (!string.IsNullOrEmpty(_help.Text)) y = Place(_help, 0, y + Sx(6), width);
        if (!string.IsNullOrEmpty(_status.Text)) y = Place(_status, 0, y + Sx(6), width); else _status.Visible = false;
        return y;
    }

    public override bool RefreshValue()
    {
        base.RefreshValue();
        bool changed = SetText(_status, _statusText());
        if (Caption is not null) changed |= SetText(_button, Caption());
        return changed;
    }
}

internal sealed class LinkRow : Row
{
    private readonly LinkLabel _link;
    private readonly Label _help;

    public LinkRow(SettingsForm f, string text, string? help, Action onClick) : base(f)
    {
        _link = MakeLink(text, onClick);
        _link.AccessibleDescription = help;
        _help = Lbl(Fonts.Small, Theme.Muted, help);
        Controls.AddRange(new Control[] { _link, _help });
    }

    public override int Arrange(int width)
    {
        int y = PlaceLink(_link, 0, 0);
        if (!string.IsNullOrEmpty(_help.Text)) y = Place(_help, 0, y + Sx(2), width);
        return y;
    }
}

internal sealed class StatusRow : Row
{
    private readonly Label _title, _line1, _line2, _help;
    private readonly Func<string?> _l1, _l2;

    public StatusRow(SettingsForm f, string title, string? help, Func<string?> line1, Func<string?> line2) : base(f)
    {
        _l1 = line1; _l2 = line2;
        _title = Lbl(Fonts.Strong, Theme.Ink, title);
        _line1 = Lbl(Fonts.Body, Theme.Ink);
        _line2 = Lbl(Fonts.Small, Theme.Muted);
        _help = Lbl(Fonts.Small, Theme.Muted, help);
        Controls.AddRange(new Control[] { _title, _line1, _line2, _help });
    }

    public override int Arrange(int width)
    {
        int y = Place(_title, 0, 0, width);
        if (!string.IsNullOrEmpty(_line1.Text)) y = Place(_line1, 0, y + Sx(4), width); else _line1.Visible = false;
        if (!string.IsNullOrEmpty(_line2.Text)) y = Place(_line2, 0, y + Sx(2), width); else _line2.Visible = false;
        if (!string.IsNullOrEmpty(_help.Text)) y = Place(_help, 0, y + Sx(6), width);
        return y;
    }

    public override bool RefreshValue()
    {
        base.RefreshValue();
        return SetText(_line1, _l1()) | SetText(_line2, _l2());
    }
}

/// <summary>Read-only information; hidden while there is nothing to say.</summary>
internal sealed class NoteRow : Row
{
    private readonly Label _title, _text;
    private readonly Func<string?> _get;
    private readonly Func<Color> _color;

    public NoteRow(SettingsForm f, string title, Func<string?> text, Func<Color> color) : base(f)
    {
        _get = text; _color = color;
        _title = Lbl(Fonts.Strong, Theme.Ink, title);
        _text = Lbl(Fonts.Body, Theme.Muted);
        Controls.AddRange(new Control[] { _title, _text });
        ShowWhen = () => !string.IsNullOrEmpty(_get());
    }

    public override int Arrange(int width)
    {
        int y = Place(_title, 0, 0, width);
        return Place(_text, 0, y + Sx(4), width);
    }

    public override bool RefreshValue()
    {
        base.RefreshValue();
        _text.ForeColor = _color();
        return SetText(_text, _get());
    }
}

/// <summary>
/// The update card: status line, one primary action (Check / Update now / Install and restart / Try again), Cancel
/// while busy, a progress bar with Download → Verify → Install → Restart, and what to expect (permission prompt or
/// portable swap). Errors are plain sentences with the download page as the way out.
/// </summary>
internal sealed class UpdateCardRow : Row
{
    private readonly Label _title, _meta, _status, _note;
    private readonly Button _primary, _cancel;
    private readonly LinkLabel _page;
    private readonly ThinProgress _bar;
    private readonly StepStrip _steps;
    private Action? _primaryAction;
    private UpdateState? _last;

    public UpdateCardRow(SettingsForm f) : base(f)
    {
        var app = f.App;
        _title = Lbl(Fonts.Strong, Theme.Ink, L.T("set.update.card"));
        _meta = Lbl(Fonts.Small, Theme.Muted);
        _meta.TextAlign = ContentAlignment.TopRight;
        _status = Lbl(Fonts.Body, Theme.Ink);
        _note = Lbl(Fonts.Small, Theme.Muted, L.T(app.InstallKind == InstallKind.Portable ? "set.update.notePortable" : "set.update.noteInstalled"));
        _primary = MakeButton(L.T("set.update.check"), primary: true);
        _cancel = MakeButton(L.T("set.update.cancel"), primary: false);
        _page = MakeLink(L.T("set.releasePage") + " \u2197", app.OpenReleasePage);
        _bar = new ThinProgress(f);
        _steps = new StepStrip(f);
        _primary.Click += (_, _) => _primaryAction?.Invoke();
        _cancel.Click += (_, _) => app.CancelUpdate();
        Controls.AddRange(new Control[] { _title, _meta, _status, _bar, _steps, _primary, _cancel, _page, _note });
    }

    private bool ShowProgress(UpdateState s) => s.Phase == UpdatePhase.Downloading;
    private bool ShowSteps(UpdateState s) => s.Phase is UpdatePhase.Downloading or UpdatePhase.Ready or UpdatePhase.Installing;
    private bool ShowNote(UpdateState s) => s.Phase is UpdatePhase.Available or UpdatePhase.Downloading or UpdatePhase.Ready or UpdatePhase.Installing;

    public override int Arrange(int width)
    {
        var s = _last ?? F.App.Update;
        int pad = Sx(18), inner = width - 2 * pad;
        int mw = Math.Min(Sx(220), inner / 2);
        Place(_meta, width - pad - mw, pad + Sx(2), mw);
        int y = Place(_title, pad, pad, inner - mw - Sx(8));
        y = Place(_status, pad, y + Sx(6), inner);
        _bar.Visible = ShowProgress(s);
        if (_bar.Visible) { _bar.SetBounds(pad, y + Sx(10), inner, Sx(6)); y = _bar.Bottom; }
        _steps.Visible = ShowSteps(s);
        if (_steps.Visible) { _steps.SetBounds(pad, y + Sx(10), inner, Sx(22)); y = _steps.Bottom; }
        y += Sx(14);
        SizeButton(_primary);
        _primary.Location = new Point(pad, y);
        int x = _primary.Right + Sx(10);
        if (_cancel.Visible) { SizeButton(_cancel); _cancel.Location = new Point(x, y); x = _cancel.Right + Sx(14); }
        if (_page.Visible)
        {
            var sz = TextRenderer.MeasureText(_page.Text, _page.Font);
            if (x + sz.Width > pad + inner) { PlaceLink(_page, pad, _primary.Bottom + Sx(8)); y = _page.Bottom - _primary.Height; }
            else _page.SetBounds(x, y + (_primary.Height - sz.Height) / 2, sz.Width + 4, sz.Height + 2);
        }
        y += _primary.Height;
        _note.Visible = ShowNote(s);
        if (_note.Visible) y = Place(_note, pad, y + Sx(12), inner);
        return y + pad;
    }

    public override bool RefreshValue()
    {
        base.RefreshValue();
        var app = F.App;
        var s = app.Update;
        bool layout = _last is null || _last.Phase != s.Phase || _last.Error != s.Error || _last.Cancelled != s.Cancelled;
        _last = s;

        layout |= SetText(_meta, LastChecked(app.LastUpdateCheckUtc));
        layout |= SetText(_status, StatusText(s));
        _status.ForeColor = s.Phase == UpdatePhase.Failed ? Theme.Amber : Theme.Ink;

        string caption;
        bool enabled = true;
        switch (s.Phase)
        {
            case UpdatePhase.Available:
            case UpdatePhase.Downloading:
                caption = L.T("set.update.now"); _primaryAction = app.UpdateNow; break;
            case UpdatePhase.Ready:
                caption = L.T("set.update.install"); _primaryAction = app.UpdateNow; break;
            case UpdatePhase.Installing:
                caption = L.T("set.update.install"); _primaryAction = null; enabled = false; break;
            case UpdatePhase.Failed when s.Error is UpdateError.NotWritable or UpdateError.NoAsset:
                caption = L.T("set.releasePage") + " \u2197"; _primaryAction = app.OpenReleasePage; break;
            case UpdatePhase.Failed when s.Error == UpdateError.Declined && s.Tag is not null:
                caption = L.T("set.update.install"); _primaryAction = app.UpdateNow; break;
            case UpdatePhase.Failed:
                caption = L.T("set.update.retry"); _primaryAction = s.Tag is not null ? app.UpdateNow : app.CheckForUpdates; break;
            case UpdatePhase.Checking:
                caption = L.T("set.update.check"); _primaryAction = null; enabled = false; break;
            default:
                caption = L.T("set.update.check"); _primaryAction = app.CheckForUpdates; break;
        }
        layout |= SetText(_primary, caption);
        _primary.Enabled = enabled;

        bool showCancel = s.Phase is UpdatePhase.Checking or UpdatePhase.Downloading;
        bool showPage = s.Phase == UpdatePhase.Failed && s.Error is not (UpdateError.NotWritable or UpdateError.NoAsset);
        if (_cancel.Visible != showCancel) { _cancel.Visible = showCancel; layout = true; }
        if (_page.Visible != showPage) { _page.Visible = showPage; layout = true; }

        _bar.Fraction = s.Progress.Fraction;
        _steps.SetState(s.Phase switch
        {
            UpdatePhase.Downloading => (0, 0),
            UpdatePhase.Ready => (2, 2),
            UpdatePhase.Installing => (2, 2),
            _ => (0, -1),
        }, s.Phase == UpdatePhase.Installing);
        Invalidate();
        return layout;
    }

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("F1", CultureInfo.CurrentCulture);

    internal static string StatusText(UpdateState s) => s.Phase switch
    {
        UpdatePhase.Idle => s.Cancelled ? L.T("set.update.cancelled") : L.T("set.update.idle"),
        UpdatePhase.Checking => L.T("set.update.checking"),
        UpdatePhase.UpToDate => L.T("set.update.current"),
        UpdatePhase.Available => L.T("set.update.available", s.Tag ?? ""),
        UpdatePhase.Downloading => s.Progress.Total is > 0
            ? L.T("set.update.downloading", s.Tag ?? "", Mb(s.Progress.Received), Mb(s.Progress.Total.Value))
            : L.T("set.update.downloadingNoTotal", s.Tag ?? "", Mb(s.Progress.Received)),
        UpdatePhase.Ready => L.T("set.update.ready", s.Tag ?? ""),
        UpdatePhase.Installing => L.T("set.update.installing", s.Tag ?? ""),
        UpdatePhase.Failed => s.Error switch
        {
            UpdateError.Network => L.T("set.update.err.network"),
            UpdateError.NoAsset => L.T("set.update.err.noAsset"),
            UpdateError.HashMismatch => L.T("set.update.err.hash"),
            UpdateError.TooLarge => L.T("set.update.err.tooLarge"),
            UpdateError.NotWritable => L.T("set.update.err.notWritable"),
            UpdateError.Declined => L.T("set.update.err.declined"),
            UpdateError.InstallFailed => L.T("set.update.err.install"),
            UpdateError.Downgrade => L.T("set.update.current"),
            _ => L.T("set.update.err.other", s.Detail ?? s.Error.ToString()),
        },
        _ => "",
    };

    internal static string LastChecked(DateTime? utc)
    {
        if (utc is not { } t) return L.T("set.update.never");
        var ago = DateTime.UtcNow - t;
        string when = ago < TimeSpan.FromMinutes(1) ? L.T("set.update.justNow")
            : ago < TimeSpan.FromHours(1) ? L.T("set.update.minutesAgo", (int)ago.TotalMinutes)
            : ago < TimeSpan.FromHours(48) ? L.T("set.update.hoursAgo", (int)ago.TotalHours)
            : t.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        return L.T("set.update.lastChecked", when);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool ready = _last?.ReadyToInstall == true;
        using var path = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Sx(8));
        using var pen = new Pen(ready ? Theme.Accent : Theme.Line, ready ? Sx(2) : 1);
        g.DrawPath(pen, path);
    }
}

internal sealed class ThinProgress : Control
{
    private readonly SettingsForm _f;
    private double? _fraction;

    public ThinProgress(SettingsForm f)
    {
        _f = f;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AccessibleRole = AccessibleRole.ProgressBar;
    }

    public double? Fraction
    {
        get => _fraction;
        set { if (_fraction == value) return; _fraction = value; AccessibleDescription = value is { } v ? $"{v:P0}" : null; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0, 0, Width - 1, Height - 1);
        using (var track = Theme.Rounded(r, r.Height / 2))
        using (var b = new SolidBrush(Theme.Line))
            g.FillPath(b, track);
        float w = (float)((_fraction ?? 0.08) * r.Width);
        if (w < r.Height) w = r.Height;
        using var fill = Theme.Rounded(new RectangleF(0, 0, w, r.Height), r.Height / 2);
        using var fb = new SolidBrush(Theme.Accent);
        g.FillPath(fb, fill);
    }
}

/// <summary>Download → Verify → Install → Restart, drawn as small numbered dots with labels.</summary>
internal sealed class StepStrip : Control
{
    private readonly SettingsForm _f;
    private static readonly string[] Keys = { "set.update.step.download", "set.update.step.verify", "set.update.step.install", "set.update.step.restart" };
    private int _done, _active = -1;
    private bool _busy;
    private FontSet Fonts => _f.Fonts;

    public StepStrip(SettingsForm f)
    {
        _f = f;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = Fonts.Small;
    }

    /// <param name="state">(number of finished steps, index of the current step or -1)</param>
    public void SetState((int done, int active) state, bool busy)
    {
        if (state.done == _done && state.active == _active && busy == _busy) return;
        (_done, _active, _busy) = (state.done, state.active, busy);
        AccessibleDescription = _active >= 0 ? L.T(Keys[_active]) : null;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int d = _f.Sx(10), x = 0, gap = _f.Sx(18);
        for (int i = 0; i < Keys.Length; i++)
        {
            bool done = i < _done, active = i == _active;
            int cy = (Height - d) / 2;
            using (var b = new SolidBrush(done ? Theme.Accent : active ? Theme.AccentSoft : Theme.Surface))
                g.FillEllipse(b, x, cy, d, d);
            using (var p = new Pen(done || active ? Theme.Accent : Theme.Outline, _f.Sx(1)))
                g.DrawEllipse(p, x, cy, d, d);
            x += d + _f.Sx(6);
            string text = L.T(Keys[i]);
            var font = active ? Fonts.Strong : Fonts.Small;
            var sz = TextRenderer.MeasureText(g, text, font, new Size(int.MaxValue, Height), TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, text, font, new Rectangle(x, 0, sz.Width + 2, Height), done || active ? Theme.Ink : Theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            x += sz.Width + gap;
            if (x > Width) break;
        }
    }
}
