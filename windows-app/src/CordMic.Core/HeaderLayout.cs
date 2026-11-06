namespace CordMic.Core;

/// <summary>
/// 96-dpi design sizes for the connect-window header: logo and title on the left,
/// settings control on the right. The window and the layout tests share these
/// numbers so the two cannot drift apart.
/// </summary>
public static class HeaderLayout
{
    public const int DesignWidth = 392;
    public const int Height = 56;
    public const int PadLeft = 24;
    public const int PadRight = 16;
    public const int PadY = 10;
    public const int LogoSize = 28;
    public const int LogoTitleGap = 8;
    public const int TitleWidth = 96;
    public const int TitleHeight = 24;
    public const int SettingsButton = 36;
    public const int SettingsGlyph = 22;

    public static int Px(int design, double dpi) => (int)Math.Round(design * dpi / 96.0);

    public readonly record struct Box(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;

        public bool Intersects(Box other) =>
            X < other.Right && Right > other.X && Y < other.Bottom && Bottom > other.Y;

        public bool Contains(Box inner) =>
            inner.X >= X && inner.Y >= Y && inner.Right <= Right && inner.Bottom <= Bottom;
    }

    public static (Box Header, Box Logo, Box Title, Box Settings) Measure(int clientWidth, double dpi)
    {
        if (clientWidth <= 0) throw new ArgumentOutOfRangeException(nameof(clientWidth));
        if (dpi <= 0) throw new ArgumentOutOfRangeException(nameof(dpi));

        int S(int v) => Px(v, dpi);
        int h = S(Height);
        var header = new Box(0, 0, clientWidth, h);
        var logo = new Box(S(PadLeft), (h - S(LogoSize)) / 2, S(LogoSize), S(LogoSize));
        var settings = new Box(
            clientWidth - S(PadRight) - S(SettingsButton),
            (h - S(SettingsButton)) / 2,
            S(SettingsButton),
            S(SettingsButton));
        var title = new Box(
            logo.Right + S(LogoTitleGap),
            (h - S(TitleHeight)) / 2,
            S(TitleWidth),
            S(TitleHeight));
        return (header, logo, title, settings);
    }
}
