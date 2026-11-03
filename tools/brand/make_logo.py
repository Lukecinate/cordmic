#!/usr/bin/env python3
"""CordMic logo: rebuilds the "CM" monogram (plug notch in the M) as clean geometry and exports every
icon/logo asset in standard formats (.png, .ico, .bmp for the NSIS installer).

The geometry was measured from the approved concept (1024 px artboard, mark spans x 211-813, y 304-710).
No vector or custom formats: Pillow draws the shapes at 4x supersampling and downsamples.

    python3 tools/brand/make_logo.py            # writes into the repo
    python3 tools/brand/make_logo.py --sheet    # also writes contact sheets to /tmp/cordmic-logo
"""
import os, sys, pathlib
from PIL import Image, ImageDraw, ImageFont

R = pathlib.Path(__file__).resolve().parents[2]
TILE = (0x00, 0x7A, 0x7F)            # brand tile, sampled from the concept (also the app accent)
WHITE = (255, 255, 255)
STATES = {                            # tray: tile colour per state, white monogram
    'streaming': TILE,
    'waiting': (0x6B, 0x71, 0x7A),
    'warning': (0xB0, 0x6E, 0x00),
    'error': (0xB3, 0x26, 0x1E),
}
SS = 4                                # supersampling of the 1024 artboard
A = 1024 * SS


def _mask(notch=True, weight=0):
    """White-mark coverage (L) on the 1024*SS artboard. weight>0 thickens strokes (artboard px) for tiny sizes."""
    k = SS
    m = Image.new('L', (A, A), 0)
    d = ImageDraw.Draw(m)
    s = lambda *v: [round(x * k) for x in v]
    w = weight
    cx, cy, Ro, Ri = 414, 507, 203, 124 - w      # C: half ring, opening right
    d.ellipse(s(cx - Ro, cy - Ro, cx + Ro, cy + Ro), fill=255)
    d.ellipse(s(cx - Ri, cy - Ri, cx + Ri, cy + Ri), fill=0)
    d.rectangle(s(cx, 0, 1024, 1024), fill=0)
    d.rectangle(s(cx, 304, 486 + w, 383 + w), fill=255)       # C top terminal
    d.rectangle(s(cx, 631 - w, 645, 710), fill=255)           # C base, runs into the M
    # M: left stem, 45-degree diagonals meeting at the valley, tall right side
    vx, vy = 656.5, 460.5 + w
    d.polygon([tuple(s(510 - w, 384)), tuple(s(580, 384)), tuple(s(vx, vy)), tuple(s(813.5, 304)),
               tuple(s(813.5, 710)), tuple(s(510 - w, 710))], fill=255)
    d.rectangle(s(486 + w, 631 - w, 520, 710), fill=255)
    if notch:   # plug-shaped cut: two prongs, round body, neck down to the baseline
        nx, nr = 659, 57.5
        d.rounded_rectangle(s(601.5 + w, 522 + w, 716.5 - w, 622), radius=9 * k, fill=0)
        d.ellipse(s(nx - nr + w, 620 - nr + w, nx + nr - w, 620 + nr - w), fill=0)
        d.rectangle(s(645.5 + w / 2, 620, 672.5 - w / 2, 712), fill=0)
        d.polygon([tuple(s(nx - 62 - w, 500)), tuple(s(nx + 62 + w, 500)), tuple(s(nx, 562 + w))], fill=255)
    return m


BBOX = (211, 304, 814, 710)   # artboard coords of the mark


def mark(width, color=WHITE, notch=True, weight=0, pad=0):
    """Tight-cropped mark of a given pixel width on transparency."""
    mk = _mask(notch, weight).crop(tuple(round(v * SS) for v in BBOX))
    h = round(width * mk.height / mk.width)
    a = mk.resize((width, h), Image.LANCZOS)
    im = Image.new('RGBA', (width + 2 * pad, h + 2 * pad), color + (0,))
    solid = Image.new('RGBA', (width, h), color + (255,)); solid.putalpha(a)
    im.alpha_composite(solid, (pad, pad))
    return im


def tile(size, bg=TILE, fg=WHITE, frac=None, radius=0.16, shape='round', notch=None, weight=None):
    """Square app icon: rounded tile + centred mark. Small sizes get a larger, simplified mark."""
    if frac is None:
        frac = 0.84 if size <= 16 else 0.80 if size <= 24 else 0.70 if size <= 48 else 0.62
    if notch is None:
        notch = size >= 32
    if weight is None:
        weight = 10 if size <= 24 else 0
    S = size * SS
    im = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    if shape == 'circle':
        d.ellipse((0, 0, S - 1, S - 1), fill=bg + (255,))
    elif shape == 'square':
        d.rectangle((0, 0, S - 1, S - 1), fill=bg + (255,))
    else:
        d.rounded_rectangle((0, 0, S - 1, S - 1), radius=round(S * radius), fill=bg + (255,))
    mw = round(S * frac)
    mk = mark(mw, fg, notch, weight)
    im.alpha_composite(mk, ((S - mk.width) // 2, (S - mk.height) // 2))
    return im.resize((size, size), Image.LANCZOS)


def save_ico(path, **kw):
    sizes = [16, 24, 32, 48, 64, 128, 256]
    imgs = {s: tile(s, **kw) for s in sizes}
    imgs[256].save(path, format='ICO', sizes=[(s, s) for s in sizes],
                   append_images=[imgs[s] for s in sizes[:-1]])


def font(px, weight='SemiBold'):
    p = R / f'windows-app/src/CordMic.App/Assets/fonts/PlusJakartaSans-{weight}.ttf'
    return ImageFont.truetype(str(p), px)


def main():
    docs = R / 'docs/assets'; docs.mkdir(parents=True, exist_ok=True)
    res = R / 'android/app/src/main/res'
    win = R / 'windows-app/src/CordMic.App/Assets'
    nsis = R / 'windows-app/installer/art'; nsis.mkdir(parents=True, exist_ok=True)

    # ---- docs
    tile(512).save(docs / 'logo.png', optimize=True)
    tile(512, radius=0, shape='square').save(docs / 'play-store-512.png', optimize=True)   # Play masks corners itself
    mark(1024, TILE).save(docs / 'logo-mark.png', optimize=True)
    mark(1024, WHITE).save(docs / 'logo-mark-white.png', optimize=True)
    full = Image.new('RGBA', (1024, 1024), (0, 0, 0, 0))
    m = mark(round(603 / 1024 * 1024), WHITE); full.alpha_composite(m, (211, 304))
    full.save(docs / 'logo-mark-1024.png', optimize=True)
    # social preview 1280x640
    sp = Image.new('RGB', (1280, 640), (0xF3, 0xF4, 0xF6)); d = ImageDraw.Draw(sp)
    f1, f2 = font(104), font(34, 'Regular')
    t1, t2 = 'CordMic', 'Your Android phone as a PC microphone, over USB.'
    tw = max(d.textlength(t1, font=f1), d.textlength(t2, font=f2))
    x0 = round((1280 - (200 + 48 + tw)) / 2)
    ic = tile(200); sp.paste(ic, (x0, 220), ic)
    d.text((x0 + 248, 236), t1, font=f1, fill=(0x16, 0x18, 0x1C))
    d.text((x0 + 252, 368), t2, font=f2, fill=(0x55, 0x5B, 0x65))
    sp.save(docs / 'social-preview.png', optimize=True)

    # ---- Windows: app/installer icon + tray states, header logo PNG for the wizard
    save_ico(win / 'cordmic.ico')
    for name, col in STATES.items():
        save_ico(win / f'tray-{name}.ico', bg=col)
    tile(96).save(win / 'logo.png', optimize=True)

    # ---- NSIS bitmaps (BMP is what MUI2 accepts): welcome/finish 164x314, header 150x57
    wb = Image.new('RGB', (164, 314), TILE); w = mark(112, WHITE); wb.paste(w, ((164 - w.width) // 2, 120), w)
    wb.save(nsis / 'welcome.bmp')
    hb = Image.new('RGB', (150, 57), WHITE); t = tile(40); hb.paste(t, (150 - 40 - 8, 8), t)
    hb.save(nsis / 'header.bmp')

    # ---- Android
    dens = [('mdpi', 1), ('hdpi', 1.5), ('xhdpi', 2), ('xxhdpi', 3), ('xxxhdpi', 4)]
    for dn, f in dens:
        px = round(48 * f)
        mp = res / f'mipmap-{dn}'; mp.mkdir(exist_ok=True)
        # adaptive foreground: 108dp canvas, mark 52dp wide (bbox diagonal 63dp < 66dp safe circle)
        fpx = round(108 * f); fg = Image.new('RGBA', (fpx, fpx), (0, 0, 0, 0))
        mk = mark(round(52 * f), WHITE); fg.alpha_composite(mk, ((fpx - mk.width) // 2, (fpx - mk.height) // 2))
        fg.save(mp / 'ic_launcher_foreground.png', optimize=True)
        # no legacy ic_launcher*.png: minSdk is 29, so the adaptive icon (mipmap-anydpi) is always used
        dr = res / f'drawable-{dn}'; dr.mkdir(exist_ok=True)
        # notification small icon: white-on-transparent, 24dp box, 22dp wide mark (simplified below xhdpi)
        npx = round(24 * f); n = Image.new('RGBA', (npx, npx), (255, 255, 255, 0))
        nm = mark(round(22 * f), WHITE, notch=f >= 2, weight=0 if f >= 2 else 10)
        n.alpha_composite(nm, ((npx - nm.width) // 2, (npx - nm.height) // 2))
        n.save(dr / 'ic_notification.png', optimize=True)
        # in-app brand mark for the toolbar: 28dp tile
        tile(round(28 * f), notch=f >= 1.5, frac=0.72).save(dr / 'ic_brand.png', optimize=True)

    if '--sheet' in sys.argv:
        sheet()
    print('ok')


def sheet():
    out = pathlib.Path('/tmp/cordmic-logo'); out.mkdir(exist_ok=True)
    sizes = [16, 24, 32, 48, 64]
    rows = [('app (auto)', {}), ('notch, no thicken', dict(notch=True, weight=0)),
            ('no notch, thick', dict(notch=False, weight=10))] + \
           [(f'tray {n}', dict(bg=c)) for n, c in STATES.items()]
    for bgname, bgc in [('light', (243, 244, 246)), ('dark', (32, 32, 32))]:
        W = 20 + sum(s * 4 + 20 for s in sizes) + 200; H = 20 + len(rows) * (64 * 4 + 20)
        sh = Image.new('RGB', (W, H), bgc); d = ImageDraw.Draw(sh)
        y = 20
        for label, kw in rows:
            d.text((10, y), label, fill=(128, 128, 128))
            x = 200
            for s in sizes:
                im = tile(s, **kw)
                c = Image.new('RGB', (s, s), bgc); c.paste(im, (0, 0), im)
                sh.paste(c.resize((s * 4, s * 4), Image.NEAREST), (x, y))   # nearest: shows real pixels
                sh.paste(c, (x, y + s * 4 + 2))
                x += s * 4 + 20
            y += 64 * 4 + 20
        sh.save(out / f'sheet-{bgname}.png')


if __name__ == '__main__':
    main()
