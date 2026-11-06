using CordMic.Core;

namespace CordMic.Tests;

public class HeaderLayoutTests
{
    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    public void HeaderBoxesDoNotOverlapAtCommonDpi(double dpi)
    {
        int width = HeaderLayout.Px(HeaderLayout.DesignWidth, dpi);
        var (header, logo, title, settings) = HeaderLayout.Measure(width, dpi);

        Assert.True(header.Contains(logo), "logo outside header");
        Assert.True(header.Contains(title), "title outside header");
        Assert.True(header.Contains(settings), "settings outside header");
        Assert.False(logo.Intersects(title), "logo overlaps title");
        Assert.False(logo.Intersects(settings), "logo overlaps settings");
        Assert.False(title.Intersects(settings), "title overlaps settings");
        Assert.True(logo.X < header.Width / 2, "logo should sit on the left");
        Assert.True(settings.X > header.Width / 2, "settings should sit on the right");
        Assert.True(title.Right < settings.X, "title should not reach the settings button");
        Assert.True(settings.Right <= header.Right);
        Assert.True(logo.Right <= title.X);
    }
}
