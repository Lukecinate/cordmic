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
`error_outline` and `settings` in `windows-app/src/CordMic.App/Assets/icons/`.
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

WASAPI playback is CordMic's own COM interop (`IAudioClient3`). NAudio was removed in 0.5.0. Noise suppression,
automatic gain, the noise-floor tracker, the pause expander and the limiter are original CordMic code in `VoiceChain` (C# reference and Kotlin port). No
third-party DSP library is bundled. VB-CABLE (VB-Audio, donationware) is not bundled; when the user asks for it,
the official package is downloaded from VB-Audio and VB-Audio's own setup is started.

| Component | Used by | License |
|---|---|---|
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

## RNNoise

**RNNoise** (noise suppression library and its trained model), used by the Windows app's opt-in Studio noise
reduction (0.6.0 and later).
- Shipped as `rnnoise.dll` next to `CordMic.exe`, with its licence as `RNNOISE-LICENSE.txt`, in the installer and the
  portable zip. Built from
  [xiph/rnnoise](https://gitlab.xiph.org/xiph/rnnoise) commit `70f1d256acd4b34a572f999a05c87bf00b67730d` with model
  `rnnoise_data-0a8755f8…6b37` by [`tools/rnnoise/build.sh`](../tools/rnnoise/build.sh); unmodified source, called
  through P/Invoke. Hashes in [`third_party/rnnoise/SHA256SUMS`](../third_party/rnnoise/SHA256SUMS).
- License: BSD 3-Clause, full text in [`third_party/rnnoise/COPYING`](../third_party/rnnoise/COPYING), reproduced below.

```
Copyright (c) 2007-2017, 2024 Jean-Marc Valin
Copyright (c) 2023 Amazon
Copyright (c) 2017, Mozilla
Copyright (c) 2005-2017, Xiph.Org Foundation
Copyright (c) 2003-2004, Mark Borgerding

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

- Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.

- Redistributions in binary form must reproduce the above copyright
notice, this list of conditions and the following disclaimer in the
documentation and/or other materials provided with the distribution.

- Neither the name of the Xiph.Org Foundation nor the names of its
contributors may be used to endorse or promote products derived from
this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED.  IN NO EVENT SHALL THE FOUNDATION
OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## Evaluation data (not shipped)

The developer regression harness in `tools/harness/` downloads evaluation data at test time; none of it is in the
repository or in any release asset. EARS (CC-BY-NC 4.0) is used for local evaluation only; DEMAND and LibriSpeech
are CC-BY 4.0; the DNSMOS model is MIT; the PESQ package is for research use only. See
[`tools/harness/README.md`](../tools/harness/README.md).
