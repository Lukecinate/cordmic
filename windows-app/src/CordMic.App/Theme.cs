using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using CordMic.Core;

namespace CordMic.App;

/// <summary>
/// "Quiet utility" tokens, shared with the Android app: two neutrals + one teal accent, amber/red only for states.
/// No gradients, no glass, no shadows. Plus Jakarta Sans is embedded (OFL); Segoe UI is the fallback.
/// </summary>
internal static class Theme
{
    // neutrals
    public static readonly Color Background = Color.FromArgb(0xF3, 0xF4, 0xF6);
    public static readonly Color Surface = Color.White;
    public static readonly Color SurfaceMuted = Color.FromArgb(0xEB, 0xED, 0xF0);
    public static readonly Color Ink = Color.FromArgb(0x16, 0x18, 0x1C);
    public static readonly Color Muted = Color.FromArgb(0x55, 0x5B, 0x65);
    public static readonly Color Outline = Color.FromArgb(0xC9, 0xCD, 0xD3);
    public static readonly Color Line = Color.FromArgb(0xE3, 0xE5, 0xE9);
    // accent + states
    public static readonly Color Accent = Color.FromArgb(0x00, 0x7A, 0x7F);
    public static readonly Color AccentHover = Color.FromArgb(0x00, 0x66, 0x6A);
    public static readonly Color AccentSoft = Color.FromArgb(0xD9, 0xEE, 0xEF);
    public static readonly Color Amber = Color.FromArgb(0x8A, 0x53, 0x00);
    public static readonly Color AmberSoft = Color.FromArgb(0xFB, 0xEF, 0xD9);
    public static readonly Color MeterAmber = Color.FromArgb(0xD9, 0x9A, 0x1E);
    public static readonly Color Red = Color.FromArgb(0xB3, 0x26, 0x1E);
    public static readonly Color RedSoft = Color.FromArgb(0xFB, 0xE7, 0xE5);

    public static (Color soft, Color strong) Tone(Health h) => h switch
    {
        Health.Ok => (AccentSoft, Accent),
        Health.Warn => (AmberSoft, Amber),
        Health.Error => (RedSoft, Red),
        _ => (SurfaceMuted, Muted),
    };

    // ------------------------------------------------------------ fonts

    private static readonly PrivateFontCollection Pfc = new();
    private static readonly List<IntPtr> GdiFonts = new();
    private static readonly FontFamily RegularFamily, SemiBoldFamily;
    public static readonly bool UsingBrandFont;

    static Theme()
    {
        FontFamily? reg = null, semi = null;
        try
        {
            LoadFont("PlusJakartaSans-Regular.ttf");
            LoadFont("PlusJakartaSans-SemiBold.ttf");
            reg = Pfc.Families.FirstOrDefault(f => f.Name == "Plus Jakarta Sans");
            semi = Pfc.Families.FirstOrDefault(f => f.Name == "Plus Jakarta Sans SemiBold");
        }
        catch (Exception) { reg = semi = null; }
        UsingBrandFont = reg is not null && semi is not null;
        RegularFamily = UsingBrandFont ? reg! : new FontFamily("Segoe UI");
        SemiBoldFamily = UsingBrandFont ? semi! : SafeFamily("Segoe UI Semibold", "Segoe UI");
        Body = new Font(RegularFamily, 10.5f);
        Small = new Font(RegularFamily, 9f);
        Strong = new Font(SemiBoldFamily, 10.5f);
        StateTitle = new Font(SemiBoldFamily, 13.5f);
        Title = new Font(SemiBoldFamily, 16f);
        Brand = new Font(SemiBoldFamily, 11.5f);
        StatValue = new Font(SemiBoldFamily, 12f);
    }

    /// <summary>Registers the font for GDI+ (PrivateFontCollection) and GDI (TextRenderer, used by WinForms labels).</summary>
    private static void LoadFont(string name)
    {
        using var st = typeof(Theme).Assembly.GetManifestResourceStream("fonts." + name);
        if (st is null) return;
        var data = new byte[st.Length];
        st.ReadExactly(data);
        var mem = Marshal.AllocCoTaskMem(data.Length);
        Marshal.Copy(data, 0, mem, data.Length);
        Pfc.AddMemoryFont(mem, data.Length);   // GDI+ keeps a pointer: do not free while the app runs
        uint n = 0;
        var h = Native.AddFontMemResourceEx(mem, (uint)data.Length, IntPtr.Zero, ref n);
        if (h != IntPtr.Zero) GdiFonts.Add(h);
    }

    private static FontFamily SafeFamily(string name, string fallback)
    {
        try { return new FontFamily(name); } catch (ArgumentException) { return new FontFamily(fallback); }
    }

    public static readonly Font Body, Small, Strong, StateTitle, Title, Brand, StatValue;

    /// <summary>
    /// 0.7.1 (audit M7): fonts for a window at <paramref name="dpi"/>. At the system DPI these are the shared fonts
    /// (not owned, never disposed); on a monitor with another scale they are new fonts sized
    /// points × dpi / system DPI, owned by the set and disposed with it.
    /// </summary>
    public static FontSet FontsFor(int dpi)
    {
        int sys = Native.SystemDpi();
        if (dpi <= 0 || dpi == sys) return new FontSet(Body, Small, Strong, StateTitle, Title, Brand, owned: false);
        Font F(FontFamily fam, float pt) => new(fam, FontScale.Points(pt, dpi, sys));
        return new FontSet(F(RegularFamily, 10.5f), F(RegularFamily, 9f), F(SemiBoldFamily, 10.5f), F(SemiBoldFamily, 13.5f),
            F(SemiBoldFamily, 16f), F(SemiBoldFamily, 11.5f), owned: true);
    }

    private static Icon? _appIcon;
    private static Image? _logo;

    /// <summary>Audit M3: one shared window icon (forms do not dispose their Icon).</summary>
    public static Icon SharedAppIcon() => _appIcon ??= AppIcon();

    /// <summary>Audit M3: one shared logo bitmap (PictureBox does not dispose its Image).</summary>
    public static Image? SharedLogo() => _logo ??= Logo();

    // ------------------------------------------------------------ controls

    public static Button Primary(string text)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(132, 40),
            FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.White, Font = Strong,
            Padding = new Padding(16, 4, 16, 4), Cursor = Cursors.Hand, UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = AccentHover;
        b.FlatAppearance.MouseDownBackColor = AccentHover;
        b.EnabledChanged += (_, _) => b.BackColor = b.Enabled ? Accent : Outline;
        return b;
    }

    /// <summary>Outlined, neutral. Used for anything that is not the one primary action.</summary>
    public static Button Secondary(string text)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(100, 40),
            FlatStyle = FlatStyle.Flat, BackColor = Surface, ForeColor = Ink, Font = Body,
            Padding = new Padding(12, 4, 12, 4), Cursor = Cursors.Hand, UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderColor = Outline;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.MouseOverBackColor = SurfaceMuted;
        b.FlatAppearance.MouseDownBackColor = Line;
        return b;
    }

    public static Label Text(string text, Font? font = null, Color? color = null, int maxWidth = 560) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(maxWidth, 0), Font = font ?? Body,
        ForeColor = color ?? Ink, Margin = new Padding(0, 0, 0, 10), UseMnemonic = false, BackColor = Color.Transparent,
    };

    // ------------------------------------------------------------ assets (.ico / .png, embedded)

    public static Icon AppIcon() => LoadIcon("cordmic.ico", SystemInformation.IconSize.Width) ?? SystemIcons.Application;

    /// <summary>The CordMic logo tile (embedded 96 px PNG), drawn scaled in the wizard header.</summary>
    public static Image? Logo()
    {
        using var st = typeof(Theme).Assembly.GetManifestResourceStream("logo.png");
        return st is null ? null : new Bitmap(st);
    }

    /// <summary>Tray icon for a state, from the embedded multi-size .ico files (16/24/32/48/64/128/256).</summary>
    public static Icon TrayIcon(Health h)
    {
        var name = h switch { Health.Ok => "tray-streaming.ico", Health.Warn => "tray-warning.ico", Health.Error => "tray-error.ico", _ => "tray-waiting.ico" };
        return LoadIcon(name, SystemInformation.SmallIconSize.Width) ?? AppIcon();
    }

    private static Icon? LoadIcon(string name, int size)
    {
        using var st = typeof(Theme).Assembly.GetManifestResourceStream(name);
        return st is null ? null : new Icon(st, size, size);
    }

    /// <summary>Material Icons Round glyph (alpha-only PNG) recoloured to <paramref name="color"/>.</summary>
    public static Bitmap? Glyph(string name, Color color, int size)
    {
        using var st = typeof(Theme).Assembly.GetManifestResourceStream("icons." + name + ".png");
        if (st is null) return null;
        using var src = new Bitmap(st);
        var dst = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var cm = new ColorMatrix(new[]
        {
            new float[] { 0, 0, 0, 0, 0 }, new float[] { 0, 0, 0, 0, 0 }, new float[] { 0, 0, 0, 0, 0 },
            new float[] { 0, 0, 0, 1, 0 },
            new float[] { color.R / 255f, color.G / 255f, color.B / 255f, 0, 1 },
        });
        using var ia = new ImageAttributes();
        ia.SetColorMatrix(cm);
        g.DrawImage(src, new Rectangle(0, 0, size, size), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
        return dst;
    }

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>Fonts for one window DPI (see <see cref="Theme.FontsFor"/>).</summary>
internal sealed class FontSet : IDisposable
{
    public Font Body { get; }
    public Font Small { get; }
    public Font Strong { get; }
    public Font StateTitle { get; }
    public Font Title { get; }
    public Font Brand { get; }
    private readonly bool _owned;

    public FontSet(Font body, Font small, Font strong, Font stateTitle, Font title, Font brand, bool owned)
    { Body = body; Small = small; Strong = strong; StateTitle = stateTitle; Title = title; Brand = brand; _owned = owned; }

    public void Dispose()
    {
        if (!_owned) return;
        foreach (var f in new[] { Body, Small, Strong, StateTitle, Title, Brand }) f.Dispose();
    }
}

/// <summary>White rounded card on the grey window background. Flat: no border, no shadow.</summary>
internal sealed class Card : FlowLayoutPanel
{
    public Card(int width)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(width, 0);
        MaximumSize = new Size(width, 0);
        Padding = new Padding(20, 18, 20, 10);
        Margin = new Padding(0, 0, 0, 14);
        BackColor = Theme.Surface;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Background);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), 12 * DeviceDpi / 96f);
        using var b = new SolidBrush(Theme.Surface);
        e.Graphics.FillPath(b, path);
    }
}

/// <summary>State glyph in a tinted circle (same as the Android status card).</summary>
internal sealed class StateBadge : Control
{
    private Health _health = Health.Waiting;
    private string _glyph = "mic";
    private Bitmap? _bmp;

    public StateBadge()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(40, 40);
        Margin = new Padding(0, 0, 12, 0);
        BackColor = Theme.Surface;
    }

    public void Set(Health h, string glyph)
    {
        if (h == _health && glyph == _glyph && _bmp is not null) return;
        _health = h; _glyph = glyph;
        _bmp?.Dispose(); _bmp = null;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var (soft, strong) = Theme.Tone(_health);
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(soft)) e.Graphics.FillEllipse(b, 0, 0, Width - 1, Height - 1);
        int g = Width / 2;
        _bmp ??= Theme.Glyph(_glyph, strong, g);
        if (_bmp is not null) e.Graphics.DrawImage(_bmp, (Width - g) / 2, (Height - g) / 2, g, g);
    }

    protected override void Dispose(bool disposing) { if (disposing) _bmp?.Dispose(); base.Dispose(disposing); }
}

/// <summary>Rounded input-level meter, -60..0 dBFS. Teal, amber from -9 dB, red from -1 dB (clipping risk).</summary>
internal sealed class LevelMeter : Control
{
    private double _db = -90;
    public double Dbfs { get => _db; set { if (Math.Abs(value - _db) > 0.05) { _db = value; Invalidate(); } } }

    public LevelMeter()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Size = new Size(320, 10);
        Margin = new Padding(0, 6, 0, 12);
        BackColor = Theme.Surface;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float r = Height / 2f;
        using (var p = Theme.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), r))
        using (var b = new SolidBrush(Theme.SurfaceMuted)) g.FillPath(b, p);
        var f = Math.Clamp((_db + 60) / 60.0, 0, 1);
        if (f <= 0) return;
        float w = Math.Max(Height, (float)(Width * f)) - 1;
        var c = _db >= -1 ? Theme.Red : _db >= -9 ? Theme.MeterAmber : Theme.Accent;
        using (var p = Theme.Rounded(new RectangleF(0, 0, w, Height - 1), r))
        using (var b = new SolidBrush(c)) g.FillPath(b, p);
    }
}

/// <summary>Flat tray menu in the same palette: white surface, muted hover, hairline separators, no gradients.</summary>
internal sealed class QuietMenuRenderer : ToolStripProfessionalRenderer
{
    public QuietMenuRenderer() : base(new QuietColors()) { RoundedEdges = false; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = (e.Item.Tag as string) switch
        {
            "header" => Theme.Ink,
            "detail" => Theme.Muted,
            _ => e.Item.Enabled ? Theme.Ink : Theme.Outline,
        };
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Theme.Muted;
        base.OnRenderArrow(e);
    }

    private sealed class QuietColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Surface;
        public override Color ImageMarginGradientBegin => Theme.Surface;
        public override Color ImageMarginGradientMiddle => Theme.Surface;
        public override Color ImageMarginGradientEnd => Theme.Surface;
        public override Color MenuBorder => Theme.Outline;
        public override Color MenuItemBorder => Theme.SurfaceMuted;
        public override Color MenuItemSelected => Theme.SurfaceMuted;
        public override Color MenuItemSelectedGradientBegin => Theme.SurfaceMuted;
        public override Color MenuItemSelectedGradientEnd => Theme.SurfaceMuted;
        public override Color MenuItemPressedGradientBegin => Theme.Line;
        public override Color MenuItemPressedGradientEnd => Theme.Line;
        public override Color SeparatorDark => Theme.Line;
        public override Color SeparatorLight => Theme.Surface;
        public override Color CheckBackground => Theme.AccentSoft;
        public override Color CheckSelectedBackground => Theme.AccentSoft;
        public override Color CheckPressedBackground => Theme.AccentSoft;
    }
}
