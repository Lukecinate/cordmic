#!/usr/bin/env bash
# Rebuild the RNNoise native libraries shipped with CordMic (third_party/rnnoise/<rid>/).
# RNNoise is BSD-3-Clause (Xiph.Org / Jean-Marc Valin / Amazon / Mozilla); see third_party/rnnoise/COPYING.
# Needs: git, curl, sha256sum and zig 0.13 (used only as a C cross-compiler, so no MinGW install is needed).
#   tools/rnnoise/build.sh [path/to/zig]
# The source and the model are pinned; the build is plain C (no RTCD), SSE2 baseline for x86-64.
set -euo pipefail
ZIG=${1:-zig}
COMMIT=70f1d256acd4b34a572f999a05c87bf00b67730d
MODEL=0a8755f8e2d834eff6a54714ecc7d75f9932e845df35f8b59bc52a7cfe6e8b37
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
WORK=${RNNOISE_WORK:-$(mktemp -d)}
OUT=$ROOT/third_party/rnnoise
cd "$WORK"
if [ ! -d rnnoise ]; then git clone -q https://gitlab.xiph.org/xiph/rnnoise.git rnnoise; fi
cd rnnoise && git checkout -q $COMMIT
if [ ! -f src/rnnoise_data.c ]; then
  curl -sSLO https://media.xiph.org/rnnoise/models/rnnoise_data-$MODEL.tar.gz
  echo "$MODEL  rnnoise_data-$MODEL.tar.gz" | sha256sum -c -
  tar xzf rnnoise_data-$MODEL.tar.gz
fi
SRC="src/denoise.c src/rnn.c src/pitch.c src/kiss_fft.c src/celt_lpc.c src/nnet.c src/nnet_default.c src/parse_lpcnet_weights.c src/rnnoise_data.c src/rnnoise_tables.c"
FLAGS="-O2 -fno-sanitize=undefined -Iinclude -Isrc -DRNNOISE_BUILD -DDLL_EXPORT -std=gnu99"
mkdir -p "$OUT/win-x64" "$OUT/linux-x64"
"$ZIG" cc -target x86_64-windows-gnu $FLAGS -shared -s -o "$OUT/win-x64/rnnoise.dll" $SRC -lm
"$ZIG" cc -target x86_64-linux-gnu.2.17 $FLAGS -fPIC -shared -s -o "$OUT/linux-x64/librnnoise.so" $SRC -lm
rm -f "$OUT"/win-x64/*.lib "$OUT"/win-x64/*.pdb
cp COPYING "$OUT/COPYING"
(cd "$OUT" && sha256sum win-x64/rnnoise.dll linux-x64/librnnoise.so > SHA256SUMS && cat SHA256SUMS)
