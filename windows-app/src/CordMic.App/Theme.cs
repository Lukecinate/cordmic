using System.Drawing.Drawing2D;

namespace CordMic.App;

/// <summary>
/// One quiet palette: white surface, near-black ink, one green accent that also means "connected".
/// Status colours are functional only (tray dot + wizard status row). No gradients, no shadows, no glass.
/// </summary>
internal static class Theme
{
    public static readonly Color Surface = Color.White;
    public static readonly Color Ink = Color.FromArgb(0x1C, 0x1C, 0x1C);
    public static readonly Color Muted = Color.FromArgb(0x5F, 0x63, 0x68);
    public static readonly Color Line = Color.FromArgb(0xE4, 0xE4, 0xE1);
    public static readonly Color Note = Color.FromArgb(0xF4, 0xF3, 0xEF);
    public static readonly Color Accent = Color.FromArgb(0x1F, 0x7A, 0x4D);
    public static readonly Color AccentHover = Color.FromArgb(0x18, 0x63, 0x3E);

    public static readonly Color Ok = Accent;
    public static readonly Color Wait = Color.FromArgb(0x8A, 0x8F, 0x96);
    public static readonly Color Warn = Color.FromArgb(0xC2, 0x7C, 0x0E);
    public static readonly Color Bad = Color.FromArgb(0xC0, 0x39, 0x2B);

    public const string FontFamily = "Segoe UI";
    public static readonly Font Body = new(FontFamily, 10.5f);
    public static readonly Font Small = new(FontFamily, 9f);
    public static readonly Font Strong = new(FontFamily, 10.5f, FontStyle.Bold);
    public static readonly Font Title = new("Segoe UI Semibold", 17f);
    public static readonly Font Brand = new("Segoe UI Semibold", 11f);

    public static Button Primary(string text)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(120, 38),
            FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.White, Font = Strong,
            Padding = new Padding(14, 4, 14, 4), Cursor = Cursors.Hand, UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = AccentHover;
        b.FlatAppearance.MouseDownBackColor = AccentHover;
        b.EnabledChanged += (_, _) => b.BackColor = b.Enabled ? Accent : Color.FromArgb(0xB8, 0xC9, 0xBF);
        return b;
    }

    public static Button Secondary(string text)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(100, 38),
            FlatStyle = FlatStyle.Flat, BackColor = Surface, ForeColor = Ink, Font = Body,
            Padding = new Padding(12, 4, 12, 4), Cursor = Cursors.Hand, UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(0xCC, 0xCC, 0xC8);
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.MouseOverBackColor = Note;
        return b;
    }

    public static Label Text(string text, Font? font = null, Color? color = null, int maxWidth = 560) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(maxWidth, 0), Font = font ?? Body,
        ForeColor = color ?? Ink, Margin = new Padding(0, 0, 0, 10), UseMnemonic = false,
    };

    /// <summary>Tray icon: a solid status dot with a white mic glyph. Drawn at runtime for the current DPI.</summary>
    public static Icon TrayIcon(Color c, int size)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float s = size;
            using (var br = new SolidBrush(c)) g.FillEllipse(br, 0.5f, 0.5f, s - 1.5f, s - 1.5f);
            float cx = s / 2f, cw = s * 0.24f, top = s * 0.17f, bot = s * 0.55f;
            using var white = new SolidBrush(Color.White);
            using var path = new GraphicsPath();
            path.AddArc(cx - cw / 2, top, cw, cw, 180, 180);
            path.AddArc(cx - cw / 2, bot - cw, cw, cw, 0, 180);
            path.CloseFigure();
            g.FillPath(white, path);
            float lw = Math.Max(1.4f, s * 0.075f), r = s * 0.2f;
            using var pen = new Pen(Color.White, lw) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, cx - r, bot - r * 1.2f, 2 * r, 2 * r * 0.95f, 0, 180);
            g.DrawLine(pen, cx, bot + r * 0.7f, cx, s * 0.82f);
        }
        var h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { Native.DestroyIcon(h); }
    }

    public static Icon AppIcon()
    {
        using var st = typeof(Theme).Assembly.GetManifestResourceStream("cordmic.ico");
        return st is null ? SystemIcons.Application : new Icon(st);
    }
}

/// <summary>Small round status dot used in the wizard.</summary>
internal sealed class Dot : Control
{
    private Color _color = Theme.Wait;
    public Color DotColor { get => _color; set { _color = value; Invalidate(); } }
    public Dot()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Size = new Size(12, 12);
        Margin = new Padding(0, 6, 8, 0);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Surface);
        using var b = new SolidBrush(_color);
        e.Graphics.FillEllipse(b, 0, 0, Width - 1, Height - 1);
    }
}

/// <summary>Flat horizontal input-level meter (dBFS, -60..0).</summary>
internal sealed class LevelMeter : Control
{
    private double _db = -90;
    public double Dbfs { get => _db; set { _db = value; Invalidate(); } }
    public LevelMeter()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Size = new Size(320, 8);
        Margin = new Padding(20, 4, 0, 10);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Line);
        var f = Math.Clamp((_db + 60) / 60.0, 0, 1);
        using var b = new SolidBrush(_db > -3 ? Theme.Warn : Theme.Accent);
        e.Graphics.FillRectangle(b, 0, 0, (int)(Width * f), Height);
    }
}
