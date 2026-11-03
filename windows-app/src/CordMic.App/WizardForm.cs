using System.Diagnostics;
using CordMic.Core;

namespace CordMic.App;

/// <summary>
/// First-run guide, three steps, one task per screen. Live status rows poll the controller every 500 ms,
/// so the user just follows the instructions and watches the dot turn green.
/// </summary>
internal sealed class WizardForm : Form
{
    private const string CableUrl = "https://vb-audio.com/Cable/";
    private const int ContentWidth = 600;

    private readonly AppController _app;
    private readonly Label _stepLabel;
    private readonly Panel _content;
    private readonly Button _back, _next;
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 500 };
    private int _step;
    private DateTime _lastRecheck = DateTime.MinValue;

    // live widgets (rebuilt per step)
    private Dot? _dot;
    private Label? _status;
    private LevelMeter? _meter;

    public bool Finished { get; private set; }

    public WizardForm(AppController app)
    {
        _app = app;
        Text = L.T("wiz.title");
        Icon = Theme.AppIcon();
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        ClientSize = new Size(680, 540);
        MinimumSize = new Size(600, 480);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Surface;
        ForeColor = Theme.Ink;
        Font = Theme.Body;
        MaximizeBox = false;

        // header
        var header = new Panel { Dock = DockStyle.Top, Height = 56, Padding = new Padding(32, 0, 32, 0), BackColor = Theme.Surface };
        var brand = new Label { Text = "CordMic", Font = Theme.Brand, AutoSize = true, Location = new Point(32, 18), ForeColor = Theme.Ink };
        _stepLabel = new Label { Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        header.Controls.Add(brand);
        header.Controls.Add(_stepLabel);
        header.Resize += (_, _) => _stepLabel.Location = new Point(header.ClientSize.Width - 32 - _stepLabel.Width, 21);
        header.Paint += (_, e) => { using var p = new Pen(Theme.Line); e.Graphics.DrawLine(p, 0, header.Height - 1, header.Width, header.Height - 1); };

        // footer
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 68, BackColor = Theme.Surface };
        footer.Paint += (_, e) => { using var p = new Pen(Theme.Line); e.Graphics.DrawLine(p, 0, 0, footer.Width, 0); };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, 14, 28, 0), WrapContents = false,
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

        _content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(40, 28, 40, 12), AutoScroll = true, BackColor = Theme.Surface };

        Controls.Add(_content);
        Controls.Add(header);
        Controls.Add(footer);
        _content.BringToFront();

        _poll.Tick += (_, _) => Poll();
        Load += (_, _) => { GoTo(_app.CableName is null ? 0 : 1); _poll.Start(); };
        FormClosed += (_, _) => _poll.Dispose();
    }

    // ---------------------------------------------------------------- steps

    private void GoTo(int step)
    {
        _step = Math.Clamp(step, 0, 2);
        _stepLabel.Text = L.T("wiz.step", _step + 1);
        _stepLabel.Location = new Point(_stepLabel.Parent!.ClientSize.Width - 32 - _stepLabel.PreferredWidth, 21);
        _back.Visible = _step > 0;
        _next.Text = _step == 2 ? L.T("wiz.finish") : L.T("wiz.next");
        _dot = null; _status = null; _meter = null;

        var col = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Location = new Point(40, 28), BackColor = Theme.Surface,
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
        col.Controls.Add(Theme.Text(L.T("wiz.cable.title"), Theme.Title, maxWidth: ContentWidth));
        col.Controls.Add(Theme.Text(
            L.T("wiz.cable.body"),
            maxWidth: ContentWidth));
        col.Controls.Add(StatusRow());
        var open = Theme.Secondary(L.T("wiz.cable.open"));
        open.Margin = new Padding(0, 6, 0, 16);
        open.Click += (_, _) => OpenUrl(CableUrl);
        col.Controls.Add(open);
        col.Controls.Add(Steps(
            L.T("wiz.cable.s1"), L.T("wiz.cable.s2"), L.T("wiz.cable.s3")));
        col.Controls.Add(Theme.Text(L.T("wiz.cable.other"), Theme.Small, Theme.Muted, ContentWidth));
    }

    private void BuildPhone(FlowLayoutPanel col)
    {
        col.Controls.Add(Theme.Text(L.T("wiz.phone.title"), Theme.Title, maxWidth: ContentWidth));
        col.Controls.Add(Steps(
            L.T("wiz.phone.s1"), L.T("wiz.phone.s2"), L.T("wiz.phone.s3")));
        col.Controls.Add(Note(L.T("wiz.phone.note")));
        col.Controls.Add(StatusRow());
        _meter = new LevelMeter();
        col.Controls.Add(_meter);
    }

    private void BuildApps(FlowLayoutPanel col)
    {
        col.Controls.Add(Theme.Text(L.T("wiz.apps.title"), Theme.Title, maxWidth: ContentWidth));
        col.Controls.Add(Steps(
            L.T("wiz.apps.s1"), L.T("wiz.apps.s2"), L.T("wiz.apps.s3")));
        var sound = Theme.Secondary(L.T("wiz.apps.sound"));
        sound.Margin = new Padding(0, 0, 0, 14);
        sound.Click += (_, _) => Run("control.exe", "mmsys.cpl,,1");
        col.Controls.Add(sound);
        col.Controls.Add(Note(L.T("wiz.apps.note")));
        col.Controls.Add(StatusRow());
        _meter = new LevelMeter();
        col.Controls.Add(_meter);
    }

    // ---------------------------------------------------------------- live status

    private void Poll()
    {
        if (_step == 0 && DateTime.UtcNow - _lastRecheck > TimeSpan.FromSeconds(2))
        {
            _lastRecheck = DateTime.UtcNow;
            _app.RecheckDevices();
        }
        if (_status is null || _dot is null) return;
        var st = _app.Status;
        var s = _app.Stats;
        bool connected = s?.Peer is not null;
        switch (_step)
        {
            case 0:
                if (_app.CableName is { } cable)
                    SetStatus(Theme.Ok, L.T("wiz.cable.ok", cable));
                else
                    SetStatus(Theme.Warn, L.T("wiz.cable.missing"));
                _next.Text = _app.CableName is null ? L.T("wiz.skip") : L.T("wiz.next");
                break;
            case 1:
                if (connected)
                    SetStatus(Theme.Ok, s!.SecondsSinceAudio < 1.5 ? L.T("wiz.phone.ok") : L.T("wiz.phone.silent"));
                else if (_app.Adapter is { } a)
                    SetStatus(Theme.Wait, L.T("wiz.phone.detected", a.PhoneIp));
                else
                    SetStatus(Theme.Wait, L.T("wiz.phone.waiting"));
                break;
            default:
                var color = st.Health switch { Health.Ok => Theme.Ok, Health.Warn => Theme.Warn, Health.Error => Theme.Bad, _ => Theme.Wait };
                SetStatus(color, $"{st.Title}. {st.Detail}");
                break;
        }
        if (_meter is not null) _meter.Dbfs = connected && s!.SecondsSinceAudio < 1.0 ? s.LevelDbfs : -90;
    }

    private void SetStatus(Color c, string text)
    {
        if (_dot!.DotColor != c) _dot.DotColor = c;
        if (_status!.Text != text) _status.Text = text;
    }

    // ---------------------------------------------------------------- building blocks

    private Control StatusRow()
    {
        var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 4, 0, 8) };
        _dot = new Dot();
        _status = Theme.Text("…", Theme.Strong, maxWidth: ContentWidth - 24);
        _status.Margin = new Padding(0, 2, 0, 0);
        row.Controls.Add(_dot);
        row.Controls.Add(_status);
        return row;
    }

    private static Control Steps(params string[] items)
    {
        var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 4, 0, 10) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int i = 0; i < items.Length; i++)
        {
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(new Label { Text = $"{i + 1}.", Font = Theme.Strong, ForeColor = Theme.Accent, AutoSize = true, Margin = new Padding(0, 0, 0, 10) }, 0, i);
            t.Controls.Add(Theme.Text(items[i], maxWidth: ContentWidth - 32), 1, i);
        }
        return t;
    }

    private static Control Note(string text)
    {
        var p = new Panel { BackColor = Theme.Note, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(14, 12, 14, 4), Margin = new Padding(0, 4, 0, 14) };
        var l = Theme.Text(text, maxWidth: ContentWidth - 28);
        l.BackColor = Theme.Note;
        p.Controls.Add(l);
        return p;
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
