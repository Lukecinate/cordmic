#!/usr/bin/env bash
# Decrypt an encrypted keystore into a temporary directory, compare SHA-256 with
# the original, print only ok/fail, and delete the temporary file. The original
# is never overwritten.
set -euo pipefail
# no set -x
ENC=""; ORIG=""
while [ $# -gt 0 ]; do
  case "$1" in
    --enc) ENC=${2:-}; shift 2 ;;
    --original) ORIG=${2:-}; shift 2 ;;
    *) echo "usage: restore-keystore-test.sh --enc <file.enc> --original <keystore>" >&2; exit 2 ;;
  esac
done
if [ -z "$ENC" ] || [ -z "$ORIG" ]; then
  echo "usage: restore-keystore-test.sh --enc <file.enc> --original <keystore>" >&2
  exit 2
fi
if [ ! -f "$ENC" ] || [ ! -f "$ORIG" ]; then echo "error: file not found" >&2; exit 1; fi
if ! command -v openssl >/dev/null 2>&1; then echo "error: openssl is required" >&2; exit 1; fi
if [ -t 0 ]; then
  printf 'encryption passphrase: ' >&2
  stty -echo
  IFS= read -r pw
  stty echo
  printf '\n' >&2
else
  echo "error: run this on a terminal so the passphrase is not logged" >&2
  exit 1
fi
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
umask 077
if ! openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000 -in "$ENC" -out "$tmp/restored" -pass stdin <<<"$pw"; then
  echo "fail: decrypt"
  exit 1
fi
orig_hash=$(sha256sum -- "$ORIG" | awk '{print $1}')
got_hash=$(sha256sum -- "$tmp/restored" | awk '{print $1}')
if [ "$orig_hash" != "$got_hash" ]; then
  echo "fail: hash mismatch"
  exit 1
fi
echo "ok"
