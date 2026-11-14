#!/usr/bin/env python3
"""Draw labelled HTML/PNG *renders* of the CordMic 0.7.0 Settings window.

WinForms cannot run on Linux, so these are not screenshots. They are drawn from the same catalog, strings and
layout constants the app uses (dumped by `CordMic.Harness settings-json en|id`), with the app's colours, fonts
(Plus Jakarta Sans, point sizes converted at 96 DPI) and geometry (nav 184, padding 28/22, row gap 14, toggle
46x26, buttons 36 high). Every image carries a "Render" label.

    dotnet run --project windows-app/src/CordMic.Harness -c Release -- settings-json en > en.json
    python3 tools/render/render_settings.py en.json out/ [--chrome /usr/bin/google-chrome] [--scales 1,1.5,2]

Sample values (phone name, versions, progress) are placeholders; no addresses are shown.
"""
import html, json, os, pathlib, subprocess, sys

HERE = pathlib.Path(__file__).resolve().parent
FONTS = HERE.parents[1] / "windows-app/src/CordMic.App/Assets/fonts"

SAMPLE = {
    "phone": ("Connected: Pixel phone", "USB tethering · about 22 ms"),
    "output": "Automatic: CABLE Input (VB-Audio Virtual Cable)",
    "language": 0, "jitter": 1, "outMode": 0,
}

CSS = """
@font-face { font-family: PJS; src: url('%(fonts)s/PlusJakartaSans-Regular.ttf'); font-weight: 400; }
@font-face { font-family: PJS; src: url('%(fonts)s/PlusJakartaSans-SemiBold.ttf'); font-weight: 600; }
* { box-sizing: border-box; }
body { margin: 0; background: #DADDE2; font-family: PJS, 'Segoe UI', sans-serif; color: #16181C; }
.tag { font: 600 12px PJS, sans-serif; color: #555B65; padding: 10px 16px 6px; letter-spacing: .02em; }
.win { margin: 0 16px 16px; width: %(w)dpx; height: %(h)dpx; background: #fff; border: 1px solid #9AA0A8;
       display: flex; flex-direction: column; overflow: hidden; }
.titlebar { height: 32px; display: flex; align-items: center; padding: 0 12px; font-size: 12px; color: #16181C;
            border-bottom: 1px solid #E3E5E9; flex: none; }
.titlebar .sp { flex: 1 } .titlebar .ctl { width: 36px; text-align: center; color: #555B65; }
.body { flex: 1; display: flex; min-height: 0; }
.nav { width: %(nav)dpx; background: #F3F4F6; border-right: 1px solid #E3E5E9; padding: 14px 10px; position: relative; flex: none; }
.nav .it { height: 38px; margin-bottom: 2px; border-radius: 6px; display: flex; align-items: center; padding-left: 12px;
           font-size: 14px; position: relative; }
.nav .it.sel { background: #fff; font-weight: 600; }
.nav .it.sel::before { content: ''; position: absolute; left: 0; top: 8px; bottom: 8px; width: 3px; background: #007A7F; }
.nav .dot { position: absolute; right: 14px; width: 9px; height: 9px; border-radius: 50%%; background: #007A7F; }
.nav .ver { position: absolute; left: 22px; bottom: 12px; font-size: 12px; color: #555B65; line-height: 1.35; }
.page { flex: 1; overflow-y: auto; padding: %(py)dpx %(px)dpx; }
.page h1 { font-size: 21px; font-weight: 600; margin: 0 0 4px; }
.lead { font-size: 14px; color: #555B65; margin-bottom: 10px; }
.row { padding: %(gap2)dpx 0; border-top: 1px solid #E3E5E9; }
.row:first-of-type { border-top: 0; }
.t { font-size: 14px; font-weight: 600; }
.h { font-size: 12px; color: #555B65; margin-top: 2px; line-height: 1.4; }
.v { font-size: 14px; margin-top: 4px; }
.flex { display: flex; gap: 16px; align-items: flex-start; } .grow { flex: 1; }
.sw { width: 46px; height: 26px; flex: none; position: relative; }
.sw i { position: absolute; inset: 3px; border-radius: 10px; background: #C9CDD3; }
.sw.on i { background: #007A7F; }
.sw b { position: absolute; top: 6px; left: 6px; width: 14px; height: 14px; border-radius: 50%%; background: #fff; }
.sw.on b { left: 26px; }
.combo { margin-top: 6px; max-width: 420px; height: 30px; border: 1px solid #C9CDD3; border-radius: 3px; font-size: 14px;
         display: flex; align-items: center; padding: 0 8px; justify-content: space-between; }
.combo::after { content: '⌄'; color: #555B65; }
.seg { display: flex; gap: 8px; margin-top: 8px; flex-wrap: wrap; }
.seg span { width: 170px; height: 36px; border: 1px solid #C9CDD3; display: flex; align-items: center; justify-content: center; font-size: 14px; }
.seg span.on { background: #D9EEEF; border-color: #007A7F; font-weight: 600; }
.radio { margin-top: 8px; }
.radio .o { display: flex; gap: 8px; align-items: center; font-size: 14px; margin-top: 6px; }
.radio .o i { width: 13px; height: 13px; border: 1px solid #555B65; border-radius: 50%%; display: inline-block; position: relative; }
.radio .o.on i::after { content: ''; position: absolute; inset: 2px; border-radius: 50%%; background: #16181C; }
.radio .o.on { font-weight: 600; }
.radio .oh { font-size: 12px; color: #555B65; margin-left: 24px; line-height: 1.4; }
.slider { display: flex; align-items: center; justify-content: space-between; }
.slider .val { color: #007A7F; font-weight: 600; font-size: 14px; }
.track { margin-top: 12px; max-width: 440px; height: 4px; background: #C9CDD3; position: relative; margin-bottom: 14px; }
.track b { position: absolute; left: 40%%; top: -8px; width: 10px; height: 20px; background: #007A7F; }
.btn { display: inline-flex; align-items: center; justify-content: center; height: 36px; min-width: 112px; padding: 0 16px;
       border: 1px solid #C9CDD3; font-size: 14px; background: #fff; }
.btn.pri { background: #007A7F; border-color: #007A7F; color: #fff; font-weight: 600; }
.link { color: #007A7F; font-size: 14px; }
.card { border: 2px solid #007A7F; border-radius: 8px; padding: 18px; }
.card .top { display: flex; justify-content: space-between; } .card .meta { font-size: 12px; color: #555B65; }
.card .st { font-size: 14px; margin-top: 6px; }
.steps { display: flex; gap: 18px; margin-top: 10px; font-size: 12px; color: #555B65; align-items: center; }
.steps span { display: inline-flex; gap: 6px; align-items: center; }
.steps span i { width: 10px; height: 10px; border-radius: 50%%; border: 1px solid #C9CDD3; display: inline-block; }
.steps span.d { color: #16181C; } .steps span.d i { background: #007A7F; border-color: #007A7F; }
.steps span.a { color: #16181C; font-weight: 600; } .steps span.a i { background: #D9EEEF; border-color: #007A7F; }
.card .acts { margin-top: 14px; display: flex; gap: 10px; align-items: center; }
.card .note { font-size: 12px; color: #555B65; margin-top: 12px; line-height: 1.4; }
.keys { font-size: 12px; color: #555B65; padding-top: 14px; }
"""


def e(s):
    return html.escape(s or "")


def row_html(it, S):
    k, lab, hp = it["kind"], e(it["label"]), it.get("help")
    help_ = f'<div class="h">{e(hp)}</div>' if hp else ""
    i = it["id"]
    if k == "Status":
        a, b = SAMPLE["phone"]
        return f'<div class="t">{lab}</div><div class="v">{e(a)}</div><div class="h">{e(b)}</div>{help_}'
    if k == "Toggle":
        on = i in ("pairing", "startup", "autoVolume", "clarity", "autoUpdate")
        extra = ""
        if i == "pairing":
            extra = f'<div class="v" style="margin-top:8px">{e(S["set.pairing.code"].replace("{0}", "482 913"))}</div>'
        return (f'<div class="flex"><div class="grow"><div class="t">{lab}</div>{help_}{extra}</div>'
                f'<div class="sw{" on" if on else ""}"><i></i><b></b></div></div>')
    if k == "Choice":
        opts = it.get("options") or []
        cur = SAMPLE["output"] if i == "output" else (opts[SAMPLE.get(i, 0)] if opts else "")
        return f'<div class="t">{lab}</div><div class="combo">{e(cur)}</div>{help_}'
    if k == "Slider":
        return (f'<div class="slider"><span class="t">{lab}</span><span class="val">{e(S["ui.gainValue"].replace("{0}", "9"))}</span></div>'
                f'<div class="track"><b></b></div>{help_}')
    if k == "Segmented":
        segs = "".join(f'<span class="{"on" if n == 1 else ""}">{e(o)}</span>' for n, o in enumerate(it["options"]))
        now = e(S["set.delay.now"].replace("{0}", "38"))
        return f'<div class="t">{lab}</div>{help_}<div class="seg">{segs}</div><div class="h" style="color:#16181C;margin-top:8px">{now}</div>'
    if k == "Radio":
        ops = ""
        for n, (o, oh) in enumerate(zip(it["options"], it.get("optionHelp") or [""] * len(it["options"]))):
            ops += f'<div class="o{" on" if n == 1 else ""}"><i></i>{e(o)}</div><div class="oh">{e(oh)}</div>'
        return f'<div class="t">{lab}</div>{help_}<div class="radio">{ops}</div>'
    if k == "Button":
        return f'<span class="btn">{lab}</span>{help_}'
    if k == "Link":
        return f'<span class="link">{lab}{" ↗" if i in ("releasePage", "getCable") else ""}</span>{help_}'
    if k == "Note":
        return f'<div class="t">{lab}</div><div class="v" style="color:#555B65">Pixel phone · Android 15 · 48 kHz</div>'
    if k == "UpdateCard":
        steps = [S["set.update.step.download"], S["set.update.step.verify"], S["set.update.step.install"], S["set.update.step.restart"]]
        st = "".join(f'<span class="{"d" if n < 2 else ("a" if n == 2 else "")}"><i></i>{e(x)}</span>' for n, x in enumerate(steps))
        meta = S["set.update.lastChecked"].replace("{0}", S["set.update.minutesAgo"].replace("{0}", "3"))
        return (f'<div class="card"><div class="top"><span class="t">{e(S["set.update.card"])}</span><span class="meta">{e(meta)}</span></div>'
                f'<div class="st">{e(S["set.update.ready"].replace("{0}", "0.7.1"))}</div><div class="steps">{st}</div>'
                f'<div class="acts"><span class="btn pri">{e(S["set.update.install"])}</span></div>'
                f'<div class="note">{e(S["set.update.noteInstalled"])}</div></div>')
    return f'<div class="t">{lab}</div>{help_}'


SKIP_WHEN_SAMPLE = {"installCable"}  # hidden while a virtual cable is found (the sample state)


def page_html(d, page, w, h, scale):
    L = d["layout"]; S = d["strings"]
    nav = "".join(
        f'<div class="it{" sel" if p["id"] == page["id"] else ""}">{e(p["title"])}'
        f'{"<span class=dot></span>" if p["id"] == "Updates" else ""}</div>' for p in d["pages"])
    rows = "".join(f'<div class="row">{row_html(it, S)}</div>' for it in page["items"] if it["id"] not in SKIP_WHEN_SAMPLE)
    css = CSS % dict(fonts=FONTS.as_uri(), w=w, h=h, nav=L["NavWidth"], px=L["ContentPadX"], py=L["ContentPadY"], gap2=L["RowGap"] // 2 + 4)
    tag = f'Render (not a Windows screenshot) · CordMic 0.7.0 Settings · {page["title"]} · {d["lang"].upper()} · {int(scale * 100)}% scale · window {w}×{h} logical px'
    ver = e(S["set.version"].replace("{0}", "0.7.0"))
    return f"""<!doctype html><meta charset="utf-8"><style>{css}</style>
<div class="tag">{e(tag)}</div>
<div class="win"><div class="titlebar">{e(S["set.title"])} – {e(page["title"])}<span class="sp"></span><span class="ctl">–</span><span class="ctl">☐</span><span class="ctl">✕</span></div>
<div class="body"><div class="nav">{nav}<div class="ver">{ver}</div></div>
<div class="page"><h1>{e(page["title"])}</h1><div class="lead">{e(page["lead"])}</div>{rows}<div class="keys">{e(S["set.keys"])}</div></div></div></div>"""


def main():
    a = sys.argv[1:]
    if len(a) < 2:
        print(__doc__); return 64
    src, out = pathlib.Path(a[0]), pathlib.Path(a[1])
    chrome = "/usr/bin/google-chrome"; scales = [1.0, 1.5, 2.0]; size = None
    if "--chrome" in a: chrome = a[a.index("--chrome") + 1]
    if "--scales" in a: scales = [float(x) for x in a[a.index("--scales") + 1].split(",")]
    d = json.loads(src.read_text(encoding="utf-8"))
    out.mkdir(parents=True, exist_ok=True)
    L = d["layout"]
    for page in d["pages"]:
        for scale in scales:
            # Same rule as SettingsLayout.InitialSize: default logical size, clamped to a 1366x728 work area.
            w = min(L["DefaultWidth"], int(1366 * 0.9 / 1)); h = min(L["DefaultHeight"], int(728 * 0.9))
            name = f'settings-{d["lang"]}-{page["id"].lower()}-{int(scale * 100)}'
            hp = out / (name + ".html")
            hp.write_text(page_html(d, page, w, h, scale), encoding="utf-8")
            vw, vh = w + 34, h + 34 + 32
            subprocess.run([chrome, "--headless=new", "--no-sandbox", "--disable-gpu", "--hide-scrollbars",
                            f"--force-device-scale-factor={scale}", f"--window-size={vw},{vh}",
                            f"--screenshot={out / (name + '.png')}", hp.as_uri()],
                           check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    return 0


if __name__ == "__main__":
    sys.exit(main())
