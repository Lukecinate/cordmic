#!/usr/bin/env python3
"""Draw labelled HTML/PNG *renders* of the CordMic 1.1.1 main (connect) window.

WinForms cannot run on Linux, so these are not screenshots. Geometry follows MainForm
(ClientSize 392x456, header 56, orb 148 at y=64, meter + gain under the status).
Every image carries a "Render" label.

    python3 tools/render/render_main.py out/ [--chrome /usr/bin/google-chrome] [--scales 1,1.5,2]
"""
import html, pathlib, subprocess, sys

HERE = pathlib.Path(__file__).resolve().parent
FONTS = HERE.parents[1] / "windows-app/src/CordMic.App/Assets/fonts"
CLIENT_W, CLIENT_H = 392, 456
LEVEL_DB = -18
LEVEL_FILL = (LEVEL_DB - (-60)) / 60

COPY = {
    "en": dict(title="CordMic", state="Streaming from your phone", detail="Pixel phone  ·  22 ms",
               level="Level · −18 dB", gain="Mic gain", gainVal="0.0 dB", settings="Settings", lang="EN"),
    "id": dict(title="CordMic", state="Streaming dari HP Anda", detail="Pixel phone  ·  22 ms",
               level="Level · −18 dB", gain="Gain mic", gainVal="0.0 dB", settings="Pengaturan", lang="ID"),
}

KNOB = """
<svg class="knob" viewBox="0 0 44 44" width="44" height="44" aria-hidden="true">
  <circle cx="22" cy="22" r="17" fill="none" stroke="#C9CDD3" stroke-width="3" stroke-linecap="round"
          stroke-dasharray="80.1 26.7" transform="rotate(135 22 22)"/>
  <line x1="22" y1="11" x2="22" y2="6" stroke="#16181C" stroke-width="1" stroke-linecap="round"/>
  <line x1="22" y1="22" x2="22" y2="7" stroke="#16181C" stroke-width="2" stroke-linecap="round"/>
  <circle cx="22" cy="22" r="3.5" fill="#16181C"/>
</svg>
"""

CSS = """
@font-face { font-family: PJS; src: url('%(fonts)s/PlusJakartaSans-Regular.ttf'); font-weight: 400; }
@font-face { font-family: PJS; src: url('%(fonts)s/PlusJakartaSans-SemiBold.ttf'); font-weight: 600; }
* { box-sizing: border-box; }
body { margin: 0; background: #DADDE2; font-family: PJS, 'Segoe UI', sans-serif; color: #16181C; }
.tag { font: 600 12px PJS, sans-serif; color: #555B65; padding: 10px 16px 6px; letter-spacing: .02em; }
.win { margin: 0 16px 16px; width: %(w)dpx; background: #F3F4F6; border: 1px solid #9AA0A8;
       display: flex; flex-direction: column; overflow: hidden; }
.titlebar { height: 32px; display: flex; align-items: center; padding: 0 12px; font-size: 12px; color: #16181C;
            border-bottom: 1px solid #E3E5E9; flex: none; background: #F3F4F6; }
.titlebar .sp { flex: 1 } .titlebar .ctl { width: 36px; text-align: center; color: #555B65; }
.client { width: %(cw)dpx; height: %(ch)dpx; position: relative; background: #F3F4F6; }
.header { position: absolute; left: 0; top: 0; width: 100%%; height: 56px; }
.logo { position: absolute; left: 24px; top: 14px; width: 28px; height: 28px; border-radius: 6px; background: #007A7F; }
.brand { position: absolute; left: 60px; top: 16px; font-size: 16px; font-weight: 600; line-height: 24px; }
.gear { position: absolute; right: 16px; top: 10px; width: 36px; height: 36px; color: #555B65;
        display: flex; align-items: center; justify-content: center; font-size: 18px; }
.orb { position: absolute; left: 50%%; top: 64px; width: 148px; height: 148px; margin-left: -74px;
       border-radius: 50%%; background: #D9EEEF; border: 3px solid #007A7F; }
.orb .core { position: absolute; inset: 28px; border-radius: 50%%; background: #007A7F; }
.state { position: absolute; left: 32px; right: 32px; top: 228px; height: 28px; text-align: center;
         font-size: 16px; font-weight: 600; line-height: 28px; }
.detail { position: absolute; left: 24px; right: 24px; top: 258px; height: 36px; text-align: center;
          font-size: 14px; color: #555B65; line-height: 20px; }
.llab { position: absolute; left: 24px; right: 24px; top: 304px; height: 16px; font-size: 12px; color: #555B65; }
.meter { position: absolute; left: 24px; right: 24px; top: 324px; height: 10px; background: #EBEDF0; border-radius: 5px; overflow: hidden; }
.meter i { display: block; height: 100%%; width: %(fill)s%%; background: #007A7F; border-radius: 5px; }
.glab { position: absolute; left: 24px; right: 24px; top: 342px; height: 16px; font-size: 12px; color: #555B65; }
.gain { position: absolute; left: 24px; right: 24px; top: 360px; height: 48px; display: flex; align-items: center; gap: 12px; }
.gain-col { flex: 1; min-width: 0; position: relative; height: 48px; }
.gain-val { position: absolute; right: 0; top: 0; color: #007A7F; font-weight: 600; font-size: 12px; }
.gain-track { position: absolute; left: 0; right: 0; top: 22px; height: 4px; background: #C9CDD3; }
.gain-track i { position: absolute; left: 0; width: 50%%; height: 4px; background: #007A7F; }
.gain-track b { position: absolute; left: calc(50%% - 5px); top: -7px; width: 10px; height: 18px; background: #007A7F; }
.link { position: absolute; left: 0; right: 0; bottom: 12px; text-align: center; color: #007A7F; font-size: 14px; }
"""

def page_html(lang, scale):
    c = COPY[lang]
    css = CSS % dict(fonts=FONTS.as_uri(), w=CLIENT_W, cw=CLIENT_W, ch=CLIENT_H, fill=f"{LEVEL_FILL * 100:.1f}")
    tag = (f'Render (not a Windows screenshot) · CordMic 1.1.1 main window · {c["lang"]} · '
           f'{int(scale * 100)}% scale · client {CLIENT_W}×{CLIENT_H} logical px')
    return f"""<!doctype html><meta charset="utf-8"><style>{css}</style>
<div class="tag">{html.escape(tag)}</div>
<div class="win">
  <div class="titlebar">{html.escape(c['title'])}<span class="sp"></span><span class="ctl">–</span><span class="ctl">☐</span><span class="ctl">✕</span></div>
  <div class="client">
    <div class="header"><div class="logo"></div><div class="brand">CordMic</div><div class="gear">⚙</div></div>
    <div class="orb"><div class="core"></div></div>
    <div class="state">{html.escape(c['state'])}</div>
    <div class="detail">{html.escape(c['detail'])}</div>
    <div class="llab">{html.escape(c['level'])}</div>
    <div class="meter"><i></i></div>
    <div class="glab">{html.escape(c['gain'])}</div>
    <div class="gain">{KNOB}<div class="gain-col"><div class="gain-val">{html.escape(c['gainVal'])}</div>
      <div class="gain-track"><i></i><b></b></div></div></div>
    <div class="link">{html.escape(c['settings'])}</div>
  </div>
</div>
"""

def chrome_shot(chrome, html_path, png_path, vw, vh, scale):
    subprocess.run([
        chrome, "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-sandbox",
        f"--window-size={vw},{vh}",
        f"--force-device-scale-factor={scale}",
        f"--screenshot={png_path.resolve()}",
        html_path.resolve().as_uri(),
    ], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

def main():
    a = sys.argv[1:]
    if not a:
        print(__doc__); return 64
    out = pathlib.Path(a[0]); out.mkdir(parents=True, exist_ok=True)
    chrome = "/usr/bin/google-chrome"
    scales = [1.0, 1.5, 2.0]
    if "--chrome" in a: chrome = a[a.index("--chrome") + 1]
    if "--scales" in a: scales = [float(x) for x in a[a.index("--scales") + 1].split(",")]
    vw, vh = CLIENT_W + 34, CLIENT_H + 32 + 34 + 32
    for lang in ("en", "id"):
        for scale in scales:
            name = f"main-{lang}-{int(scale * 100)}"
            hp = out / (name + ".html")
            hp.write_text(page_html(lang, scale), encoding="utf-8")
            chrome_shot(chrome, hp, out / (name + ".png"), vw, vh, scale)
            print(name)
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
