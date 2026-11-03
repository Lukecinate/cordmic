#!/usr/bin/env bash
# Cross-build CordMic for Windows on Linux: tests -> win-x64 single-file publish -> NSIS installer.
# Needs: .NET 8 SDK (dotnet on PATH), NSIS 3 (makensis), python3 (interop test).
set -euo pipefail
cd "$(dirname "$0")"
VERSION=0.3.0
dotnet test tests/CordMic.Tests -c Release
rm -rf publish
dotnet publish src/CordMic.App/CordMic.App.csproj -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -o publish
(cd installer && makensis -V2 -DVERSION=$VERSION cordmic.nsi)
ls -la ../dist/CordMic-Setup-$VERSION.exe
