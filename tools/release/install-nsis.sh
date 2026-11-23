#!/usr/bin/env bash
# Install official NSIS 3.13 (portable zip) after verifying SHA-256.
# Used on GitHub Actions windows-2022 runners. Not a Windows driver or
# signing-policy change. Local Linux packaging continues to use distro
# nsis via the package manager only as a last resort; CI uses this pinned official zip (3.13, CVE-2026-42171).
set -euo pipefail

NSIS_VERSION="3.13"
NSIS_SHA256="ba63dffc4410ee89193e1cb5a41989991bd77c61068da17e3156d136b7b0b3d8"
NSIS_URL="https://downloads.sourceforge.net/project/nsis/NSIS%203/${NSIS_VERSION}/nsis-${NSIS_VERSION}.zip"

TMP="${RUNNER_TEMP:-${TMPDIR:-/tmp}}/cordmic-nsis-pin"
mkdir -p "$TMP"
ZIP="$TMP/nsis-${NSIS_VERSION}.zip"

echo "Downloading official NSIS ${NSIS_VERSION} zip"
# Retry: SourceForge occasionally serves a short interstitial.
curl -L --retry 8 --retry-delay 2 --fail -A "CordMic-CI" -o "$ZIP" "$NSIS_URL"

got=""
if command -v sha256sum >/dev/null 2>&1; then
  echo "${NSIS_SHA256}  ${ZIP}" | sha256sum -c -
  got="$NSIS_SHA256"
else
  got="$(python3 -c "import hashlib,sys; print(hashlib.sha256(open(sys.argv[1],'rb').read()).hexdigest())" "$ZIP")"
  if [ "$got" != "$NSIS_SHA256" ]; then
    echo "NSIS SHA-256 mismatch: got $got want $NSIS_SHA256" >&2
    exit 1
  fi
  echo "NSIS SHA-256 OK $got"
fi

DEST="$TMP/nsis"
rm -rf "$DEST"
mkdir -p "$DEST"
python3 -c "import zipfile,sys; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])" "$ZIP" "$DEST"
BIN="$DEST/nsis-${NSIS_VERSION}"
if [ ! -f "$BIN/makensis.exe" ] && [ ! -f "$BIN/makensis" ]; then
  echo "makensis missing after extract under $BIN" >&2
  ls -la "$BIN" >&2 || true
  exit 1
fi

if [ -n "${GITHUB_PATH:-}" ]; then
  echo "$BIN" >> "$GITHUB_PATH"
fi
echo "NSIS_BIN=$BIN"
echo "NSIS_VERSION=$NSIS_VERSION"
echo "NSIS_SHA256=$NSIS_SHA256"
