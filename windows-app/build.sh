#!/usr/bin/env bash
# Build CordMic for Windows (works on Linux, macOS or WSL): tests -> win-x64 single-file publish -> NSIS installer.
# Needs: .NET 8 SDK (dotnet on PATH), NSIS 3 (makensis), python3 (interop test, portable zip).
# Output: ../dist/CordMic-Setup-<ver>.exe and ../dist/CordMic-Portable-<ver>.zip (0.7.0+: the zip carries portable.txt,
# which tells the in-app updater to update that folder in place).
set -euo pipefail
cd "$(dirname "$0")"
VERSION=$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' src/CordMic.App/CordMic.App.csproj | head -1)
# 0.6.2: stamp the commit that is actually being built ("<version>+<commit>" in the log). A build with uncommitted
# changes is refused (it would carry the previous commit's hash) unless CORDMIC_ALLOW_DIRTY=1, which marks it "-dirty".
REV=$(git rev-parse --short=12 HEAD 2>/dev/null || echo unknown)
if [ -n "$(git status --porcelain --untracked-files=no 2>/dev/null)" ]; then
  if [ "${CORDMIC_ALLOW_DIRTY:-0}" != "1" ]; then
    echo "error: uncommitted changes; commit first or set CORDMIC_ALLOW_DIRTY=1" >&2; exit 1
  fi
  REV="$REV-dirty"
fi
dotnet test tests/CordMic.Tests -c Release
rm -rf publish
dotnet publish src/CordMic.App/CordMic.App.csproj -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:SourceRevisionId=$REV -o publish
(cd installer && makensis -V2 -DVERSION=$VERSION cordmic.nsi)
# Portable zip: the same publish folder plus the portable marker, files at the zip root (the updater expects
# CordMic.exe there and the asset name CordMic-Portable-<ver>.zip).
printf 'CordMic portable copy. Keep this file: it tells CordMic to update this folder in place.\r\n' > publish/portable.txt
python3 - "$VERSION" <<'PY'
import os, sys, zipfile
ver = sys.argv[1]
out = os.path.join("..", "dist", f"CordMic-Portable-{ver}.zip")
os.makedirs(os.path.dirname(out), exist_ok=True)
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for name in sorted(os.listdir("publish")):
        z.write(os.path.join("publish", name), name)
PY
ls -la ../dist/CordMic-Setup-$VERSION.exe ../dist/CordMic-Portable-$VERSION.zip
echo "built CordMic $VERSION+$REV"
