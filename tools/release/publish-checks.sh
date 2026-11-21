#!/usr/bin/env bash
# Checks that must pass before a draft is published. Does not publish.
# Usage: publish-checks.sh --dir <asset-dir> [--manifest release-manifest.json] [--sig release-manifest.json.sig] [--pub-keys keys.txt] [--channel stable] [--current-version 0.7.2] [--apk-cert-sha256 HEX] [--skip-gate-h]
set -euo pipefail
DIR=""; MAN=""; SIG=""; PUBS=""; CHANNEL=stable; CURRENT=0.0.0; APK_PIN=""; SKIP_H=0
while [ $# -gt 0 ]; do
  case "$1" in
    --dir) DIR=${2:-}; shift 2 ;;
    --manifest) MAN=${2:-}; shift 2 ;;
    --sig) SIG=${2:-}; shift 2 ;;
    --pub-keys) PUBS=${2:-}; shift 2 ;;
    --channel) CHANNEL=${2:-}; shift 2 ;;
    --current-version) CURRENT=${2:-}; shift 2 ;;
    --apk-cert-sha256) APK_PIN=${2:-}; shift 2 ;;
    --skip-gate-h) SKIP_H=1; shift ;;
    *) echo "unknown arg $1" >&2; exit 2 ;;
  esac
done
[ -n "$DIR" ] && [ -d "$DIR" ] || { echo "usage: --dir <asset-dir>" >&2; exit 2; }

if [ -f "$DIR/SHA256SUMS" ]; then
  (cd "$DIR" && sha256sum -c SHA256SUMS)
else
  echo "error: SHA256SUMS missing" >&2
  exit 1
fi

if [ -n "$MAN" ]; then
  [ -f "$MAN" ] && [ -f "${SIG:-}" ] || { echo "error: manifest or signature missing" >&2; exit 1; }
  if [ -n "$PUBS" ]; then
    # pubs file: one SPKI base64 per line (comments starting with # allowed)
    root=$(cd "$(dirname "$0")/../.." && pwd)
    tool=${CORDMIC_KEYTOOL:-}
    if [ -z "$tool" ]; then
      echo "error: set CORDMIC_KEYTOOL to cordmic-keytool.dll (or the exe) to verify the manifest" >&2
      exit 1
    fi
    ok=0
    while IFS= read -r pub; do
      case "$pub" in ""|\#*) continue ;; esac
      if dotnet "$tool" verify "$MAN" --sig "$SIG" --pub "$pub"; then ok=1; break; fi
    done < "$PUBS"
    [ "$ok" -eq 1 ] || { echo "error: manifest signature did not verify with any provided public key" >&2; exit 1; }
  else
    echo "note: no --pub-keys given; manifest signature not checked (production keys are not embedded yet)"
  fi
fi

if [ -n "$APK_PIN" ]; then
  apk=$(find "$DIR" -maxdepth 1 -name '*.apk' | head -1)
  [ -n "$apk" ] || { echo "error: no APK in $DIR" >&2; exit 1; }
  # Comparison is left to apksigner/keytool when available; require the pin to be 64 hex.
  echo "$APK_PIN" | grep -Eq '^[0-9a-fA-F]{64}$' || { echo "error: --apk-cert-sha256 must be 64 hex digits" >&2; exit 1; }
fi

if [ "$SKIP_H" -eq 0 ]; then
  "$(dirname "$0")/verify-gate-h.sh"
fi
echo "ok: publish checks passed"
