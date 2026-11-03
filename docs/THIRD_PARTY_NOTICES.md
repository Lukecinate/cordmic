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
and `expand_more` in `android/app/src/main/res/drawable-*dpi/` (plus a white copy of `mic` as `ic_notification`), and `mic`, `usb`, `search`,
`error_outline` in `windows-app/src/CordMic.App/Assets/icons/`.
- License: Apache License 2.0 (https://www.apache.org/licenses/LICENSE-2.0).
- Source: https://github.com/google/material-design-icons and https://fonts.google.com/icons.
- The files are PNG renders of the original glyphs. Where a state colour is needed, the Windows app recolours the
  glyph's alpha mask at runtime; the shapes are not changed.

**CordMic app icon** (Android launcher, `cordmic.ico`, tray icons, `docs/cordmic-icon.png`): the Material Icons
Round `mic` glyph (Apache 2.0, as above) in white on a teal (`#0B6B5D`) rounded square or circle. Tray icon
colours: teal = streaming, grey = waiting, amber = needs attention, red = error. Delivered as standard files only:
Android mipmap PNGs + adaptive-icon XML, Windows multi-size `.ico` (16/24/32/48/256 px), PNG for docs.

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

## Not included

[VB-CABLE](https://vb-audio.com/Cable/) is a separate product by VB-Audio. CordMic does not include or redistribute
it; the setup guide links to the official download page.
