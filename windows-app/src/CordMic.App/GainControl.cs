using System.Globalization;
using CordMic.Core;

namespace CordMic.App;

/// <summary>
/// 1.1.1 mic-gain editor: a flat knob, a slider and a numeric dB readout, all showing the same value.
/// Double-click resets to 0 dB. Wheel and arrow keys move 0.5 dB (Shift: 0.1 dB).
/// While this control is hovered or focused, the mouse wheel adjusts gain and does not scroll the Settings page.
/// </summary>
internal sealed class GainControl : Control, IPreferWheelValue
{
    public const int TenthsMin = VoiceChain.MinGainDb * 10;
    public const int TenthsMax = VoiceChain.MaxGainDb * 10;

    private double _db;
    private bool _hover, _knobDown, _barDown;
    private Rectangle _knob, _bar, _thumb;
    private readonly bool _compact;

    public event Action<double>? ValueChanged;

    public GainControl(bool compact = false)
    {
        _compact = compact;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.Slider;
        AccessibleName = L.T("ui.gain");
        UpdateAccessible();
    }

    public double Value
    {
        get => _db;
        set
        {
            var v = VoiceChain.SnapGain(value);
            if (Math.Abs(v - _db) < 0.001) return;
            _db = v;
            UpdateAccessible();
            Invalidate();
        }
    }

    public bool WantsWheel => _hover || ContainsFocus;

    public void SetValueFromHost(double db)
    {
        Value = db; // does not raise ValueChanged
    }

    private void Commit(double db, bool fromUser)
    {
        var v = VoiceChain.SnapGain(db);
        if (Math.Abs(v - _db) < 0.001) return;
        _db = v;
        UpdateAccessible();
        Invalidate();
        if (fromUser) ValueChanged?.Invoke(_db);
    }

    private void UpdateAccessible()
    {
        AccessibleDescription = L.T("ui.gain.access", VoiceChain.FormatGain(_db));
        AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnResize(EventArgs e)
    {
        LayoutParts();
        base.OnResize(e);
    }

    private int Sx(int px) => Math.Max(1, (int)Math.Round(px * DeviceDpi / 96.0));

    private void LayoutParts()
    {
        int k = _compact ? Sx(44) : Sx(52);
        int pad = Sx(2);
        _knob = new Rectangle(pad, Math.Max(pad, (Height - k) / 2), k, k);
        int barH = Sx(22);
        int barX = _knob.Right + Sx(12);
        int barW = Math.Max(Sx(80), Width - barX - pad);
        _bar = new Rectangle(barX, (Height - barH) / 2, barW, barH);
        UpdateThumb();
    }

    private void UpdateThumb()
    {
        float t = (float)((_db - VoiceChain.MinGainDb) / (VoiceChain.MaxGainDb - VoiceChain.MinGainDb));
        t = Math.Clamp(t, 0, 1);
        int tw = Sx(10), th = Sx(18);
        int x = _bar.X + (int)Math.Round(t * Math.Max(0, _bar.Width - tw));
        _thumb = new Rectangle(x, _bar.Y + (_bar.Height - th) / 2, tw, th);
    }

    private double DbFromBar(int x)
    {
        int tw = Math.Max(1, _thumb.Width);
        double t = (x - _bar.X - tw / 2.0) / Math.Max(1, _bar.Width - tw);
        return VoiceChain.MinGainDb + Math.Clamp(t, 0, 1) * (VoiceChain.MaxGainDb - VoiceChain.MinGainDb);
    }

    private static double AngleFromDb(double db)
    {
        // 135° (lower left) = −24, 270° (top) = 0, 405° (lower right) = +24.
        double t = (db - VoiceChain.MinGainDb) / (VoiceChain.MaxGainDb - VoiceChain.MinGainDb);
        return 135 + Math.Clamp(t, 0, 1) * 270;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        LayoutParts();
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // Knob: flat ring, accent arc from 0 dB, pointer. No fill gradient, no shadow.
        float cx = _knob.X + _knob.Width / 2f, cy = _knob.Y + _knob.Height / 2f;
        float r = _knob.Width / 2f - Sx(3);
        var ring = new RectangleF(cx - r, cy - r, r * 2, r * 2);
        using (var pen = new Pen(Theme.Outline, Sx(3)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
            g.DrawArc(pen, ring, 135, 270);
        double ang = AngleFromDb(_db);
        double zero = AngleFromDb(0);
        float sweep = (float)(ang - zero);
        if (Math.Abs(sweep) > 0.4f)
        {
            using var accent = new Pen(Theme.Accent, Sx(3)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            if (sweep >= 0) g.DrawArc(accent, ring, (float)zero, sweep);
            else g.DrawArc(accent, ring, (float)ang, -sweep);
        }
        // 0 dB tick
        DrawTick(g, cx, cy, r, zero, Theme.Ink, Sx(1));
        // pointer
        double rad = ang * Math.PI / 180.0;
        float px = cx + (float)Math.Cos(rad) * (r - Sx(2));
        float py = cy + (float)Math.Sin(rad) * (r - Sx(2));
        using (var pen = new Pen(Theme.Ink, Sx(2)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
            g.DrawLine(pen, cx, cy, px, py);
        float hub = Sx(4);
        using (var b = new SolidBrush(Theme.Ink))
            g.FillEllipse(b, cx - hub, cy - hub, hub * 2, hub * 2);

        if (Focused && ShowFocusCues)
        {
            using var fp = new Pen(Theme.Ink, 1f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
            g.DrawEllipse(fp, _knob.X, _knob.Y, _knob.Width - 1, _knob.Height - 1);
        }

        // Slider track + thumb
        int trackH = Sx(4);
        var track = new Rectangle(_bar.X, _bar.Y + (_bar.Height - trackH) / 2, _bar.Width, trackH);
        using (var b = new SolidBrush(Theme.Outline))
            g.FillRectangle(b, track);
        int filled = Math.Max(0, _thumb.X + _thumb.Width / 2 - track.X);
        using (var b = new SolidBrush(Theme.Accent))
            g.FillRectangle(b, track.X, track.Y, filled, track.Height);
        using (var b = new SolidBrush(Theme.Accent))
            g.FillRectangle(b, _thumb);

        // Readout to the right of the thumb row, or above in compact mode when width allows.
        string text = VoiceChain.FormatGain(_db);
        using var font = new Font(Font.FontFamily, Math.Max(8, 9f * DeviceDpi / 96f), FontStyle.Bold);
        var sz = g.MeasureString(text, font);
        using (var tb = new SolidBrush(Theme.Accent))
            g.DrawString(text, font, tb, _bar.Right - sz.Width, Math.Max(0, _bar.Y - sz.Height + Sx(2)));
    }

    private void DrawTick(Graphics g, float cx, float cy, float r, double angleDeg, Color c, int w)
    {
        double rad = angleDeg * Math.PI / 180.0;
        float x0 = cx + (float)Math.Cos(rad) * (r - Sx(6));
        float y0 = cy + (float)Math.Sin(rad) * (r - Sx(6));
        float x1 = cx + (float)Math.Cos(rad) * (r + Sx(1));
        float y1 = cy + (float)Math.Sin(rad) * (r + Sx(1));
        using var pen = new Pen(c, w) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        g.DrawLine(pen, x0, y0, x1, y1);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        Focus();
        if (_knob.Contains(e.Location)) { _knobDown = true; CommitFromKnob(e.Location); }
        else { _barDown = true; Commit(DbFromBar(e.X), true); }
        Capture = true;
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_knobDown) CommitFromKnob(e.Location);
        else if (_barDown) Commit(DbFromBar(e.X), true);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _knobDown = _barDown = false;
        Capture = false;
        base.OnMouseUp(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        Commit(0, true);
        base.OnMouseDoubleClick(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        int steps = Math.Sign(e.Delta);
        if (steps == 0) { base.OnMouseWheel(e); return; }
        bool fine = (ModifierKeys & Keys.Shift) != 0;
        // Wheel away from the user (positive) raises the gain.
        Commit(VoiceChain.NudgeGain(_db, steps, fine), true);
        if (e is HandledMouseEventArgs h) h.Handled = true;
        base.OnMouseWheel(e);
    }

    protected override bool IsInputKey(Keys keyData)
    {
        var k = keyData & Keys.KeyCode;
        return k is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown
            || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        bool fine = e.Shift;
        switch (e.KeyCode)
        {
            case Keys.Left:
            case Keys.Down:
                Commit(VoiceChain.NudgeGain(_db, -1, fine), true); e.Handled = true; break;
            case Keys.Right:
            case Keys.Up:
                Commit(VoiceChain.NudgeGain(_db, 1, fine), true); e.Handled = true; break;
            case Keys.Home:
                Commit(VoiceChain.MinGainDb, true); e.Handled = true; break;
            case Keys.End:
                Commit(VoiceChain.MaxGainDb, true); e.Handled = true; break;
            case Keys.PageDown:
                Commit(VoiceChain.NudgeGain(_db, -6, fine), true); e.Handled = true; break;
            case Keys.PageUp:
                Commit(VoiceChain.NudgeGain(_db, 6, fine), true); e.Handled = true; break;
        }
        base.OnKeyDown(e);
    }

    private void CommitFromKnob(Point pt)
    {
        float cx = _knob.X + _knob.Width / 2f, cy = _knob.Y + _knob.Height / 2f;
        double deg = Math.Atan2(pt.Y - cy, pt.X - cx) * 180.0 / Math.PI;
        if (deg < 0) deg += 360;
        // Valid travel is 135..360 and 0..45 (i.e. 135..405 wrapped). Clamp out-of-range to the nearer end.
        double travel;
        if (deg >= 135) travel = deg - 135;
        else if (deg <= 45) travel = 225 + deg; // 360-135=225, plus 0..45
        else travel = deg < 90 ? 270 : 0; // the dead zone at the bottom: snap to nearest end
        double t = Math.Clamp(travel / 270.0, 0, 1);
        Commit(VoiceChain.MinGainDb + t * (VoiceChain.MaxGainDb - VoiceChain.MinGainDb), true);
    }
}

/// <summary>Implemented by controls that should receive the mouse wheel themselves instead of scrolling a parent page.</summary>
internal interface IPreferWheelValue
{
    bool WantsWheel { get; }
}
