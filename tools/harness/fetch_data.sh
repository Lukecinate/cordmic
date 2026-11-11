#!/usr/bin/env bash
# Download the evaluation data for the CordMic regression harness into the cache (default tools/harness/.cache,
# or $CORDMIC_HARNESS_DATA). Nothing here is committed: .cache is git-ignored.
#   EARS (talkers)      CC-BY-NC 4.0  local evaluation only; never commit or redistribute clips or derived audio
#   DEMAND (noise)      CC-BY 4.0
#   LibriSpeech dev     CC-BY 4.0
#   DNSMOS P.835 model  MIT (microsoft/DNS-Challenge)
#   RNNoise reference   BSD-3 (built from the pinned commit for rnnoise_demo / rnnoise_vad)
# Needs: git, curl, unzip, tar, sha256sum, a C compiler (cc or `zig cc`), and the Python packages in requirements.txt.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=${CORDMIC_HARNESS_DATA:-$HERE/.cache}
CC=${CC:-cc}
mkdir -p "$ROOT"/ext/ears "$ROOT"/ext/demand "$ROOT"/data/libri "$ROOT"/results
cd "$ROOT"

for n in 001 002 003 004 006 007 008 009 010 011 012 013 014 016; do
  [ -d ext/ears/p$n ] && continue
  curl -fsSL -o /tmp/ears_p$n.zip "https://github.com/facebookresearch/ears_dataset/releases/download/dataset/p$n.zip"
  unzip -q -o -j /tmp/ears_p$n.zip "p$n/rainbow_0[1-5]_regular.wav" "p$n/rainbow_0[1-5]_whisper.wav" -d ext/ears/p$n
  rm -f /tmp/ears_p$n.zip; echo "EARS p$n"
done

for e in STRAFFIC SCAFE OOFFICE DKITCHEN TBUS PSTATION; do
  [ -f ext/demand/$e/ch01.wav ] && continue
  curl -fsSL -o /tmp/demand_$e.zip "https://zenodo.org/records/1227121/files/${e}_48k.zip"
  mkdir -p ext/demand/$e && unzip -q -o -j /tmp/demand_$e.zip "$e/ch01.wav" -d ext/demand/$e
  rm -f /tmp/demand_$e.zip; echo "DEMAND $e"
done

if [ ! -f data/libri/M3_2086-149214-0002.flac ]; then
  curl -fsSL -o /tmp/dev-clean.tar.gz https://www.openslr.org/resources/12/dev-clean.tar.gz
  while read -r tag id; do
    spk=${id%%-*}; rest=${id#*-}; ch=${rest%%-*}
    tar -xzf /tmp/dev-clean.tar.gz -O "LibriSpeech/dev-clean/$spk/$ch/$id.flac" > "data/libri/${tag}_$id.flac"
  done <<'LIST'
F0 1462-170138-0000
F1 1673-143396-0000
F2 1988-147956-0000
F3 1993-147149-0003
M0 1272-128104-0011
M1 174-168635-0002
M2 2078-142845-0006
M3 2086-149214-0002
LIST
  rm -f /tmp/dev-clean.tar.gz; echo "LibriSpeech 8 clips"
fi

[ -f ext/sig_bak_ovr.onnx ] || curl -fsSL -o ext/sig_bak_ovr.onnx \
  https://github.com/microsoft/DNS-Challenge/raw/master/DNSMOS/DNSMOS/sig_bak_ovr.onnx

if [ ! -x ext/rnnoise/rnnoise_vad ]; then
  # Same pinned source and model as tools/rnnoise/build.sh (which writes the shipped binaries; not run here).
  COMMIT=70f1d256acd4b34a572f999a05c87bf00b67730d
  MODEL=0a8755f8e2d834eff6a54714ecc7d75f9932e845df35f8b59bc52a7cfe6e8b37
  S=ext/rnnoise/src-tree
  [ -d $S ] || git clone -q https://gitlab.xiph.org/xiph/rnnoise.git $S
  (cd $S && git checkout -q $COMMIT && if [ ! -f src/rnnoise_data.c ]; then
     curl -fsSLO https://media.xiph.org/rnnoise/models/rnnoise_data-$MODEL.tar.gz
     echo "$MODEL  rnnoise_data-$MODEL.tar.gz" | sha256sum -c - && tar xzf rnnoise_data-$MODEL.tar.gz; fi)
  SRC=""; for f in denoise rnn pitch kiss_fft celt_lpc nnet nnet_default parse_lpcnet_weights rnnoise_data rnnoise_tables; do SRC="$SRC $S/src/$f.c"; done
  $CC -O2 -I$S/include -I$S/src -o ext/rnnoise/rnnoise_demo $S/examples/rnnoise_demo.c $SRC -lm
  $CC -O2 -I$S/include -I$S/src -o ext/rnnoise/rnnoise_vad "$HERE/rnnoise_vad.c" $SRC -lm
  echo "RNNoise reference tools built"
fi
echo "data ready in $ROOT"
