#!/usr/bin/env bash
# Build CordMic for Windows (works on Linux, macOS or WSL): tests -> win-x64 single-file publish -> NSIS installer.
# Needs: .NET 8 SDK (dotnet on PATH), NSIS 3 (makensis), python3 (interop test).
set -euo pipefail
cd "$(dirname "$0")"
VERSION=0.4.2
dotnet test tests/CordMic.Tests -c Release
rm -rf publish
dotnet publish src/CordMic.App/CordMic.App.csproj -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -o publish
(cd installer && makensis -V2 -DVERSION=$VERSION cordmic.nsi)
ls -la ../dist/CordMic-Setup-$VERSION.exe
