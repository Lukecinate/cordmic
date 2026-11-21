#!/usr/bin/env bash
# Sign an unsigned APK with a keystore the operator points at. Never prints
# passwords or the keystore path's contents. Refuses to run if the keystore
# lives inside this git tree.
set -euo pipefail
# no set -x
usage() { echo "usage: sign-apk.sh --in unsigned.apk --out signed.apk --keystore <jks> --name <alias>" >&2; }
IN=""; OUT=""; KS=""; ALIAS=""
while [ $# -gt 0 ]; do
  case "$1" in
    --in) IN=${2:-}; shift 2 ;;
    --out) OUT=${2:-}; shift 2 ;;
    --keystore) KS=${2:-}; shift 2 ;;
    --name|--alias) ALIAS=${2:-}; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) usage; exit 2 ;;
  esac
done
if [ -z "$IN" ] || [ -z "$OUT" ] || [ -z "$KS" ] || [ -z "$ALIAS" ]; then usage; exit 2; fi
if [ ! -f "$IN" ] || [ ! -f "$KS" ]; then echo "error: input or keystore not found" >&2; exit 1; fi
# Refuse a keystore that sits in the checkout (including the shared box repo).
root=$(cd "$(dirname "$0")/../.." && pwd)
ks_abs=$(readlink -f "$KS")
if [ "${ks_abs#"$root"}" != "$ks_abs" ]; then
  echo "error: refusing to use a keystore inside the git working tree" >&2
  exit 1
fi
if [ -z "${CORDMIC_KEYSTORE_PASS:-}" ] || [ -z "${CORDMIC_KEY_PASS:-}" ]; then
  echo "error: set CORDMIC_KEYSTORE_PASS and CORDMIC_KEY_PASS in the environment (they are not printed)" >&2
  exit 1
fi
if ! command -v apksigner >/dev/null 2>&1 && [ -z "${APKSIGNER:-}" ]; then
  echo "error: apksigner not on PATH (or set APKSIGNER)" >&2
  exit 1
fi
tool=${APKSIGNER:-apksigner}
"$tool" sign --ks "$KS" --ks-key-alias "$ALIAS" \
  --ks-pass env:CORDMIC_KEYSTORE_PASS --key-pass env:CORDMIC_KEY_PASS \
  --out "$OUT" "$IN"
"$tool" verify --verbose "$OUT" >/dev/null
echo "ok: signed APK written (certificate details only; no secrets)"
# Print the cert SHA-256 only, via apksigner / keytool if available, without listing aliases' passwords.
if command -v keytool >/dev/null 2>&1; then
  # -list needs the store pass; we accept that keytool may print the fingerprint only.
  keytool -list -keystore "$KS" -alias "$ALIAS" -storepass:env CORDMIC_KEYSTORE_PASS 2>/dev/null \
    | grep -i 'SHA256:' || true
fi
