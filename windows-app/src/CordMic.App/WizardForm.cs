using System.Diagnostics;
using System.Globalization;
using CordMic.Core;

namespace CordMic.App;

/// <summary>
/// First-run guide in the "Quiet utility" layout: a status card on top (one glance tells you whether the
/// phone is acting as your mic), the steps in a second card, one primary action in the footer.
/// Live rows poll the controller every 500 ms. Numbers are only shown when they are measured.
/// </summary>
internal sealed class WizardForm : Form
{
    private const string CableUrl = "https://vb-audio.com/Cable/";
    private const int ContentWidth = 600;
    private const int Inner = ContentWidth - 40;

    private readonly AppController _app;
    private readonly Label _stepLabel;
    private readonly Panel _content;
    private readonly Button _back, _next;
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 500 };
    private int _step;
    private DateTime _lastRecheck = DateTime.MinValue;

    // live widgets (rebuilt per step)
    private StateBadge? _badge;
    private Label? _stateTitle, _stateMsg;
    private LevelMeter? _meter;
    private Control? _statsRow;
    private Label? _latency, _rtt, _loss;

    public bool Finished { get; private set; }

    public WizardForm(AppController app)
    {
        _app = app;
        Text = L.T("wiz.title");
        Icon = Theme.AppIcon();
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        ClientSize = new Size(680, 720);
        MinimumSize = new Size(620, 520);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Background;
        ForeColor = Theme.Ink;
        Font = Theme.Body;
        MaximizeBox = false;

        // header: brand left, step counter right
        var header = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Theme.Background };
        var brand = new Label { Text = "CordMic", Font = Theme.Brand, AutoSize = true, Location = new Point(40, 18), ForeColor = Theme.Ink };
        _stepLabel = new Label { Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        header.Controls.Add(brand);
        header.Controls.Add(_stepLabel);
        header.Resize += (_, _) => PlaceStepLabel();

        // footer: one primary action, Back is secondary
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 72, BackColor = Theme.Surface };
        footer.Paint += (_, e) => { using var p = new Pen(Theme.Line); e.Graphics.DrawLine(p, 0, 0, footer.Width, 0); };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, 16, 36, 0), WrapContents = false,
            BackColor = Theme.Surface,
        };
        _next = Theme.Primary(L.T("wiz.next"));
        _back = Theme.Secondary(L.T("wiz.back"));
        _next.Margin = new Padding(8, 0, 0, 0);
        buttons.Controls.Add(_next);
        buttons.Controls.Add(_back);
        footer.Controls.Add(buttons);
        _next.Click += (_, _) => { if (_step == 2) { Finished = true; Close(); } else GoTo(_step + 1); };
        _back.Click += (_, _) => GoTo(_step - 1);
        AcceptButton = _next;

        _content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(40, 8, 40, 16), AutoScroll = true, BackColor = Theme.Background };

        Controls.Add(_content);
        Controls.Add(header);
        Controls.Add(footer);
        _content.BringToFront();

        _poll.Tick += (_, _) => Poll();
        Load += (_, _) => { GoTo(_app.CableName is null ? 0 : 1); _poll.Start(); };
        FormClosed += (_, _) => _poll.Dispose();
    }

    private void PlaceStepLabel()
    {
        if (_stepLabel.Parent is { } p) _stepLabel.Location = new Point(p.ClientSize.Width - 40 - _stepLabel.PreferredWidth, 21);
    }

    // ---------------------------------------------------------------- steps

    private void GoTo(int step)
    {
        _step = Math.Clamp(step, 0, 2);
        _stepLabel.Text = L.T("wiz.step", _step + 1);
        PlaceStepLabel();
        _back.Visible = _step > 0;
        _next.Text = _step == 2 ? L.T("wiz.finish") : L.T("wiz.next");
        _badge = null; _stateTitle = _stateMsg = null; _meter = null; _statsRow = null; _latency = _rtt = _loss = null;

        var col = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Location = new Point(40, 8), BackColor = Theme.Background,
        };
        switch (_step)
        {
            case 0: BuildCable(col); break;
            case 1: BuildPhone(col); break;
            default: BuildApps(col); break;
        }
        _content.SuspendLayout();
        foreach (Control c in _content.Controls.Cast<Control>().ToList()) c.Dispose();
        _content.Controls.Clear();
        _content.Controls.Add(col);
        _content.ResumeLayout();
        Poll();
        _next.Focus();
    }

    private void BuildCable(FlowLayoutPanel col)
    {
        col.Controls.Add(Heading(L.T("wiz.cable.title")));
        col.Controls.Add(StatusCard(live: false));
        var card = StepsCard(L.T("wiz.cable.body"), L.T("wiz.cable.s1"), L.T("wiz.cable.s2"), L.T("wiz.cable.s3"));
        card.Controls.Add(Action(L.T("wiz.cable.open"), () => OpenUrl(CableUrl)));
        card.Controls.Add(Theme.Text(L.T("wiz.cable.other"), Theme.Small, Theme.Muted, Inner));
        col.Controls.Add(card);
    }

    private void BuildPhone(FlowLayoutPanel col)
    {
        col.Controls.Add(Heading(L.T("wiz.phone.title")));
        col.Controls.Add(StatusCard(live: true));
        var card = StepsCard(null, L.T("wiz.phone.s1"), L.T("wiz.phone.s2"), L.T("wiz.phone.s3"));
        card.Controls.Add(Theme.Text(L.T("wiz.phone.note"), Theme.Small, Theme.Muted, Inner));
        col.Controls.Add(card);
    }

    private void BuildApps(FlowLayoutPanel col)
    {
        col.Controls.Add(Heading(L.T("wiz.apps.title")));
        col.Controls.Add(StatusCard(live: true));
        var card = StepsCard(null, L.T("wiz.apps.s1"), L.T("wiz.apps.s2"), L.T("wiz.apps.s3"));
        card.Controls.Add(Action(L.T("wiz.apps.sound"), () => Run("control.exe", "mmsys.cpl,,1")));
        card.Controls.Add(Theme.Text(L.T("wiz.apps.note"), Theme.Small, Theme.Muted, Inner));
        col.Controls.Add(card);
    }

    // ---------------------------------------------------------------- live status

    private void Poll()
    {
        if (_step == 0 && DateTime.UtcNow - _lastRecheck > TimeSpan.FromSeconds(2))
        {
            _lastRecheck = DateTime.UtcNow;
            _app.RecheckDevices();
        }
        if (_badge is null) return;
        var st = _app.Status;
        var s = _app.Stats;
        bool connected = s?.Peer is not null;
        bool audio = connected && s!.SecondsSinceAudio < 1.5;
        switch (_step)
        {
            case 0:
                if (_app.CableName is { } cable)
                    SetStatus(Health.Ok, "mic", L.T("wiz.cable.okTitle"), L.T("wiz.cable.ok", cable));
                else
                    SetStatus(Health.Warn, "error_outline", L.T("status.noCable.title"), L.T("wiz.cable.missing"));
                _next.Text = _app.CableName is null ? L.T("wiz.skip") : L.T("wiz.next");
                break;
            case 1:
                if (connected)
                    SetStatus(audio ? Health.Ok : Health.Warn, audio ? "mic" : "error_outline",
                        audio ? L.T("status.connected.title") : L.T("status.silent.title"),
                        audio ? L.T("wiz.phone.ok") : L.T("wiz.phone.silent"));
                else if (_app.Adapter is { } a)
                    SetStatus(Health.Waiting, "search", L.T("status.detected.title"), L.T("wiz.phone.detected", a.PhoneIp));
                else
                    SetStatus(Health.Waiting, "usb", L.T("status.waiting.title"), L.T("wiz.phone.waiting"));
                break;
            default:
                var glyph = st.Health switch
                {
                    Health.Ok => "mic",
                    Health.Waiting => _app.Adapter is null ? "usb" : "search",
                    _ => "error_outline",
                };
                SetStatus(st.Health, glyph, st.Title, st.Detail);
                break;
        }
        if (_meter is not null) _meter.Dbfs = connected && s!.SecondsSinceAudio < 1.0 ? s.LevelDbfs : -90;
        if (_statsRow is not null)
        {
            // Only measured values; hidden while nothing is connected.
            _statsRow.Visible = connected;
            if (connected)
            {
                var inv = CultureInfo.InvariantCulture;
                SetText(_latency!, _app.EstLatencyMs() is { } lat ? $"~{lat.ToString("F0", inv)} ms" : "–");
                SetText(_rtt!, s!.RttMs is { } r ? $"{r.ToString("F1", inv)} ms" : "–");
                SetText(_loss!, $"{_app.RecentLossPct.ToString("F1", inv)} %");
            }
        }
    }

    private void SetStatus(Health h, string glyph, string title, string msg)
    {
        _badge!.Set(h, glyph);
        SetText(_stateTitle!, title);
        SetText(_stateMsg!, msg);
        _stateTitle!.AccessibleName = title + ". " + msg;
    }

    private static void SetText(Label l, string t) { if (l.Text != t) l.Text = t; }

    // ---------------------------------------------------------------- building blocks

    private static Label Heading(string text)
    {
        var l = Theme.Text(text, Theme.Title, maxWidth: ContentWidth);
        l.Margin = new Padding(0, 4, 0, 14);
        l.AccessibleRole = AccessibleRole.StaticText;
        return l;
    }

    /// <summary>Hero card: state badge + plain-language title + one next step; on live steps also level and stats.</summary>
    private Card StatusCard(bool live)
    {
        var card = new Card(ContentWidth);
        var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 0, 0, 8), BackColor = Theme.Surface };
        _badge = new StateBadge();
        var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty, BackColor = Theme.Surface };
        _stateTitle = Theme.Text("…", Theme.StateTitle, maxWidth: Inner - 52);
        _stateTitle.Margin = new Padding(0, 0, 0, 4);
        _stateTitle.AccessibleRole = AccessibleRole.Alert;
        _stateMsg = Theme.Text("", Theme.Body, Theme.Muted, Inner - 52);
        text.Controls.Add(_stateTitle);
        text.Controls.Add(_stateMsg);
        row.Controls.Add(_badge);
        row.Controls.Add(text);
        card.Controls.Add(row);
        if (!live) return card;

        var lvl = Theme.Text(L.T("wiz.level"), Theme.Small, Theme.Muted, Inner);
        lvl.Margin = new Padding(0, 4, 0, 0);
        card.Controls.Add(lvl);
        _meter = new LevelMeter { Width = Inner, AccessibleName = L.T("wiz.level") };
        card.Controls.Add(_meter);

        var stats = new TableLayoutPanel { ColumnCount = 3, RowCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Width = Inner, Margin = new Padding(0, 0, 0, 8), BackColor = Theme.Surface, Visible = false };
        for (int i = 0; i < 3; i++) stats.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Inner / 3f));
        Label Val() { var l = Theme.Text("–", Theme.StatValue, maxWidth: Inner / 3); l.Margin = new Padding(0, 2, 0, 0); return l; }
        Label Cap(string k) { var l = Theme.Text(L.T(k), Theme.Small, Theme.Muted, Inner / 3); l.Margin = Padding.Empty; return l; }
        _latency = Val(); _rtt = Val(); _loss = Val();
        stats.Controls.Add(Cap("wiz.stat.latency"), 0, 0); stats.Controls.Add(_latency, 0, 1);
        stats.Controls.Add(Cap("wiz.stat.rtt"), 1, 0); stats.Controls.Add(_rtt, 1, 1);
        stats.Controls.Add(Cap("wiz.stat.loss"), 2, 0); stats.Controls.Add(_loss, 2, 1);
        _statsRow = stats;
        card.Controls.Add(stats);
        return card;
    }

    private static Card StepsCard(string? intro, params string[] items)
    {
        var card = new Card(ContentWidth);
        if (intro is not null) card.Controls.Add(Theme.Text(intro, maxWidth: Inner));
        var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 2, 0, 4), BackColor = Theme.Surface };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int i = 0; i < items.Length; i++)
        {
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(new Label { Text = $"{i + 1}", Font = Theme.Strong, ForeColor = Theme.Accent, AutoSize = true, Margin = new Padding(0, 0, 0, 12), BackColor = Theme.Surface }, 0, i);
            var l = Theme.Text(items[i], maxWidth: Inner - 34);
            l.Margin = new Padding(0, 0, 0, 12);
            t.Controls.Add(l, 1, i);
        }
        card.Controls.Add(t);
        return card;
    }

    private static Button Action(string text, Action onClick)
    {
        var b = Theme.Secondary(text);
        b.Margin = new Padding(0, 0, 0, 14);
        b.Click += (_, _) => onClick();
        return b;
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception) { }
    }

    private static void Run(string exe, string args)
    {
        try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true }); } catch (Exception) { }
    }
}
