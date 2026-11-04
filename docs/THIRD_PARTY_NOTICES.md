# Third-party notices

CordMic itself is MIT-licensed (see [LICENSE](../LICENSE)). It ships or builds on the following third-party work.

## Fonts

**Plus Jakarta Sans** (Regular 400, Medium 500, SemiBold 600)
- Copyright 2020 The Plus Jakarta Sans Project Authors (https://github.com/tokotype/PlusJakartaSans)
- License: SIL Open Font License 1.1, full text in [`third_party/fonts/PlusJakartaSans/OFL.txt`](../third_party/fonts/PlusJakartaSans/OFL.txt)
  (also shipped next to the embedded copies in `windows-app/src/CordMic.App/Assets/fonts/`).
- Source: [google/fonts `ofl/plusjakartasans`](https://github.com/google/fonts/tree/main/ofl/plusjakartasans).
  Static instances were cut from the variable font with `fontTools.varLib.instancer`; the font is not renamed,
  not modified otherwise and not sold on its own.
- Used in: Android app (`android/app/src/main/res/font/`), Windows app (embedded resource, falls back to Segoe UI).

## Icons

**Material Icons, Round style** by Google: glyphs `mic`, `stop`, `usb`, `search`, `error_outline`
and `expand_more` in `android/app/src/main/res/drawable-*dpi/`, and `mic`, `usb`, `search`,
`error_outline` in `windows-app/src/CordMic.App/Assets/icons/`.
- License: Apache License 2.0 (https://www.apache.org/licenses/LICENSE-2.0).
- Source: https://github.com/google/material-design-icons and https://fonts.google.com/icons.
- The files are PNG renders of the original glyphs. Where a state colour is needed, the Windows app recolours the
  glyph's alpha mask at runtime; the shapes are not changed.

## Logo

**CordMic logo** (the "CM" monogram with a plug-shaped notch in the M, white on a teal `#007A7F` tile): an original
CordMic asset generated for this project, not taken from a third party. The shapes are rebuilt as clean geometry by
[`tools/brand/make_logo.py`](../tools/brand/make_logo.py) (Pillow), which writes every file from one source:
- Android: adaptive launcher icon (`mipmap-*/ic_launcher_foreground.png` + `mipmap-anydpi/*.xml`, solid
  background), white-on-transparent notification icon (`drawable-*/ic_notification.png`) and app-bar logo
  (`drawable-*/ic_brand.png`).
- Windows: multi-size `.ico` (16/24/32/48/64/128/256 px) for the app and installer, four tray-state `.ico` files
  (grey = waiting, teal = streaming, amber = needs attention, red = error), `Assets/logo.png` for the setup guide
  header, and the installer header/welcome bitmaps (`windows-app/installer/art/*.bmp`).
- Docs: `docs/assets/logo.png`, `logo-mark.png`, `logo-mark-white.png`, `logo-mark-1024.png`, `play-store-512.png`
  and `social-preview.png` (the social preview uses Plus Jakarta Sans, see above).

At 16 and 24 px the plug notch is left out and the strokes are slightly heavier so the monogram stays readable.
The logo is covered by the project's MIT license.

## Libraries

| Component | Used by | License |
|---|---|---|
| [NAudio](https://github.com/naudio/NAudio) 2.2.1 (`NAudio.Core`, `NAudio.Wasapi`) | Windows app (WASAPI output) | MIT |
| .NET 8 runtime and Windows Forms (self-contained publish) | Windows app | MIT |
| AndroidX (AppCompat, Core KTX, ConstraintLayout) | Android app | Apache 2.0 |
| Material Components for Android | Android app | Apache 2.0 |
| Kotlin standard library | Android app | Apache 2.0 |
| JUnit 4, Robolectric (tests only, not shipped) | Android unit tests and screenshot renders | EPL 1.0 / MIT |
| xUnit (tests only, not shipped) | .NET tests | Apache 2.0 |
| NSIS (build tool, not a runtime dependency) | Windows installer | zlib/libpng |

Python reference receiver (`windows-receiver/`, optional, installed with `pip install -r requirements.txt`; not bundled
in the installer or releases):

| Component | License |
|---|---|
| [sounddevice](https://github.com/spatialaudio/python-sounddevice) (uses PortAudio) | MIT |
| [pystray](https://github.com/moses-palmer/pystray) | LGPL-3.0 |
| [Pillow](https://github.com/python-pillow/Pillow) | MIT-CMU (HPND) |

## Not included

[VB-CABLE](https://vb-audio.com/Cable/) is a separate product by VB-Audio. CordMic does not include or redistribute
it; the setup guide links to the official download page.
