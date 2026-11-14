namespace CordMic.Core;

/// <summary>Pages of the 0.7.0 Settings window, in navigation order.</summary>
public enum SettingsPage { Connection, Sound, NoiseVoice, Updates, Advanced }

/// <summary>How a setting is shown. The Windows app builds the control; the docs renderer draws the same list.</summary>
public enum SettingKind { Status, Toggle, Choice, Radio, Segmented, Slider, Button, Link, Note, UpdateCard }

/// <summary>One row of a settings page: plain-language label, one short help line, optional options.</summary>
public sealed record SettingItem(string Id, SettingKind Kind, string LabelKey, string? HelpKey = null, IReadOnlyList<string>? OptionKeys = null,
    IReadOnlyList<string>? OptionHelpKeys = null);

/// <summary>
/// Single source of truth for what the Settings window shows, in which order, with which words. Strings live in
/// <see cref="L"/> (English and Indonesian); tests check that every key exists in both.
/// </summary>
public static class SettingsCatalog
{
    public static readonly SettingsPage[] Pages = Enum.GetValues<SettingsPage>();

    public static string TitleKey(SettingsPage p) => p switch
    {
        SettingsPage.Connection => "set.page.connection",
        SettingsPage.Sound => "set.page.sound",
        SettingsPage.NoiseVoice => "set.page.noise",
        SettingsPage.Updates => "set.page.updates",
        _ => "set.page.advanced",
    };

    public static string LeadKey(SettingsPage p) => TitleKey(p) + ".lead";

    public static IReadOnlyList<SettingItem> Items(SettingsPage p) => p switch
    {
        SettingsPage.Connection => new SettingItem[]
        {
            new("phone", SettingKind.Status, "set.phone", "set.phone.help"),
            new("pairing", SettingKind.Toggle, "set.pairing", "set.pairing.help"),
            new("startup", SettingKind.Toggle, "set.startup", "set.startup.help"),
            new("language", SettingKind.Choice, "set.language", "set.language.help",
                new[] { "menu.language.auto", "set.language.en", "set.language.id" }),
        },
        SettingsPage.Sound => new SettingItem[]
        {
            new("output", SettingKind.Choice, "set.output", "set.output.help"),
            new("installCable", SettingKind.Button, "ui.installCable", "set.installCable.help"),
            new("autoVolume", SettingKind.Toggle, "set.autoVolume", "set.autoVolume.help"),
            new("gain", SettingKind.Slider, "set.gain", "set.gain.help"),
            new("delay", SettingKind.Segmented, "set.delay", "set.delay.help",
                SettingsChoices.DelayChoices.Select(SettingsChoices.DelayKey).ToArray()),
        },
        SettingsPage.NoiseVoice => new SettingItem[]
        {
            new("noise", SettingKind.Radio, "set.noise", "set.noise.help",
                SettingsChoices.NoiseChoices.Select(SettingsChoices.NoiseKey).ToArray(),
                SettingsChoices.NoiseChoices.Select(c => SettingsChoices.NoiseKey(c) + ".help").ToArray()),
            new("clarity", SettingKind.Toggle, "set.clarity", "set.clarity.help"),
            new("frontFocus", SettingKind.Toggle, "set.frontFocus", "set.frontFocus.help"),
            new("detected", SettingKind.Note, "set.detected"),
        },
        SettingsPage.Updates => new SettingItem[]
        {
            new("updateCard", SettingKind.UpdateCard, "set.update.card"),
            new("autoUpdate", SettingKind.Toggle, "set.autoUpdate", "set.autoUpdate.help"),
            new("installOnQuit", SettingKind.Toggle, "set.installOnQuit", "set.installOnQuit.help"),
            new("releasePage", SettingKind.Link, "set.releasePage"),
        },
        _ => new SettingItem[]
        {
            new("jitter", SettingKind.Choice, "set.jitter", "set.jitter.help"),
            new("outMode", SettingKind.Choice, "set.outMode", "set.outMode.help"),
            new("frontFocusAr", SettingKind.Toggle, "set.frontFocusAr", "set.frontFocusAr.help"),
            new("optimizeCable", SettingKind.Button, "ui.cable.optimize", "set.optimizeCable.help"),
            new("fixRoute", SettingKind.Button, "set.fixRoute", "set.fixRoute.help"),
            new("micMode", SettingKind.Link, "set.micMode"),
            new("getCable", SettingKind.Link, "ui.getCable"),
            new("openLog", SettingKind.Link, "ui.logs"),
            new("resetAudio", SettingKind.Link, "ui.resetAudio", "set.resetAudio.help"),
        },
    };

    /// <summary>Every string key the catalog uses (for the localisation tests).</summary>
    public static IEnumerable<string> AllKeys()
    {
        foreach (var p in Pages)
        {
            yield return TitleKey(p);
            yield return LeadKey(p);
            foreach (var it in Items(p))
            {
                yield return it.LabelKey;
                if (it.HelpKey != null) yield return it.HelpKey;
                foreach (var k in it.OptionKeys ?? Array.Empty<string>()) yield return k;
                foreach (var k in it.OptionHelpKeys ?? Array.Empty<string>()) yield return k;
            }
        }
    }
}

/// <summary>
/// Geometry of the Settings window in device pixels for a given DPI. The window starts at a comfortable size, never
/// larger than 90 % of the monitor's work area, and the page content scrolls vertically when it is taller than the
/// window, so nothing ends up off-screen at 100–200 % scaling (the 0.6.x panel grew past the screen).
/// </summary>
public static class SettingsLayout
{
    public const int DefaultWidth = 760, DefaultHeight = 600;   // logical (96 DPI) pixels
    public const int MinWidth = 560, MinHeight = 420;
    public const int NavWidth = 184, ContentPadX = 28, ContentPadY = 22, RowGap = 14, HelpGap = 3;
    /// <summary>Narrowest the text column may get; the window minimum keeps it at or above this.</summary>
    public const int MinContentWidth = MinWidth - NavWidth - 2 * ContentPadX;

    public static int Px(int logical, double dpi) => (int)Math.Round(logical * dpi / 96.0);

    /// <summary>Initial window size (device pixels) for <paramref name="dpi"/> on a work area of the given size.</summary>
    public static (int W, int H) InitialSize(double dpi, int workW, int workH)
    {
        int w = Math.Min(Px(DefaultWidth, dpi), (int)(workW * 0.9));
        int h = Math.Min(Px(DefaultHeight, dpi), (int)(workH * 0.9));
        var (mw, mh) = MinimumSize(dpi, workW, workH);
        return (Math.Max(w, mw), Math.Max(h, mh));
    }

    /// <summary>Minimum window size, itself limited to the work area so a small screen can still show the window.</summary>
    public static (int W, int H) MinimumSize(double dpi, int workW, int workH) =>
        (Math.Min(Px(MinWidth, dpi), workW), Math.Min(Px(MinHeight, dpi), workH));

    /// <summary>Width available to a row's text for a window of <paramref name="windowW"/> device pixels.</summary>
    public static int ContentWidth(int windowW, double dpi) => Math.Max(0, windowW - Px(NavWidth, dpi) - 2 * Px(ContentPadX, dpi));

    /// <summary>Stacks rows of the given heights (device pixels). Returns each row's top and the content height.</summary>
    public static (int[] Tops, int Total) Stack(IReadOnlyList<int> rowHeights, double dpi, int headerHeight)
    {
        var tops = new int[rowHeights.Count];
        int y = Px(ContentPadY, dpi) + headerHeight;
        int gap = Px(RowGap, dpi);
        for (int i = 0; i < rowHeights.Count; i++)
        {
            if (i > 0) y += gap;
            tops[i] = y;
            y += rowHeights[i];
        }
        return (tops, y + Px(ContentPadY, dpi));
    }

    /// <summary>
    /// 0.7.2: geometry of one Settings page (device pixels), computed headless from measuring functions so it can be
    /// unit-tested without WinForms. <paramref name="measureTitle"/> and the others return a height for a text width.
    /// </summary>
    public static PageGeometry Compose(int clientWidth, double dpi, Func<int, int> measureTitle, Func<int, int> measureLead,
        IReadOnlyList<Func<int, int>> rows, Func<int, int> measureKeys)
    {
        int padX = Px(ContentPadX, dpi), padY = Px(ContentPadY, dpi);
        int w = Math.Max(Px(200, dpi), clientWidth - 2 * padX);
        int th = Math.Max(0, measureTitle(w)), lh = Math.Max(0, measureLead(w));
        var title = new PageRect(padX, padY, w, th);
        var lead = new PageRect(padX, padY + th + Px(4, dpi), w, lh);
        int header = th + Px(4, dpi) + lh + Px(10, dpi);
        var heights = rows.Select(r => Math.Max(0, r(w))).ToList();
        var (tops, total) = Stack(heights, dpi, header);
        var rects = heights.Select((h, i) => new PageRect(padX, tops[i], w, h)).ToArray();
        int kh = Math.Max(0, measureKeys(w));
        var keys = new PageRect(padX, total, w, kh);
        return new PageGeometry(w, title, lead, rects, keys, total + kh + padY);
    }

    /// <summary>True when the content needs the vertical scrollbar in a viewport of <paramref name="viewportH"/>.</summary>
    public static bool NeedsScroll(int contentH, int viewportH) => contentH > viewportH;
}

/// <summary>A rectangle in content coordinates (before the scroll offset).</summary>
public readonly record struct PageRect(int X, int Y, int W, int H)
{
    public int Bottom => Y + H;
}

/// <summary>Result of <see cref="SettingsLayout.Compose"/>.</summary>
public sealed record PageGeometry(int TextWidth, PageRect Title, PageRect Lead, PageRect[] Rows, PageRect Keys, int Total);

/// <summary>
/// 0.7.2 (Settings crash in 0.7.0/0.7.1): WinForms raises Layout from inside a constructor (for example when
/// AutoScroll is set), before the fields a layout pass uses exist. A control's layout handler calls
/// <see cref="TryEnter"/> first: it is refused until <see cref="MarkBuilt"/> and while a pass is already running.
/// </summary>
public sealed class LayoutGate
{
    private bool _built, _running;
    public bool Built => _built;
    public void MarkBuilt() => _built = true;

    public bool TryEnter()
    {
        if (!_built || _running) return false;
        _running = true;
        return true;
    }

    public void Exit() => _running = false;
}
