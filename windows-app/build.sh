#!/usr/bin/env bash
# Build CordMic for Windows (works on Linux, macOS or WSL): tests -> win-x64 single-file publish -> NSIS installer.
# Needs: .NET 8 SDK (dotnet on PATH), NSIS 3 (makensis), python3 (interop test).
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
ls -la ../dist/CordMic-Setup-$VERSION.exe
echo "built CordMic $VERSION+$REV"
