#!/usr/bin/env bash
# Encrypt a keystore to two destinations. The original is not moved or modified.
# Passphrase is read from the terminal, not from argv, and is not printed.
set -euo pipefail
# no set -x
IN=""; OUT1=""; OUT2=""
while [ $# -gt 0 ]; do
  case "$1" in
    --in) IN=${2:-}; shift 2 ;;
    --out1) OUT1=${2:-}; shift 2 ;;
    --out2) OUT2=${2:-}; shift 2 ;;
    *) echo "usage: backup-keystore.sh --in <keystore> --out1 <a.enc> --out2 <b.enc>" >&2; exit 2 ;;
  esac
done
if [ -z "$IN" ] || [ -z "$OUT1" ] || [ -z "$OUT2" ]; then
  echo "usage: backup-keystore.sh --in <keystore> --out1 <a.enc> --out2 <b.enc>" >&2
  exit 2
fi
if [ ! -f "$IN" ]; then echo "error: input not found" >&2; exit 1; fi
root=$(cd "$(dirname "$0")/../.." && pwd)
in_abs=$(readlink -f "$IN")
if [ "${in_abs#"$root"}" != "$in_abs" ]; then
  echo "error: refusing to read a keystore from inside the git working tree" >&2
  exit 1
fi
for o in "$OUT1" "$OUT2"; do
  if [ -e "$o" ]; then echo "error: refusing to overwrite $o" >&2; exit 1; fi
  mkdir -p "$(dirname "$o")"
done
if [ "$OUT1" = "$OUT2" ]; then echo "error: the two copies must be different paths" >&2; exit 1; fi
if ! command -v openssl >/dev/null 2>&1; then echo "error: openssl is required" >&2; exit 1; fi

# Read passphrase twice, hidden.
read_pw() {
  # shellcheck disable=SC2162
  if [ -t 0 ]; then
    printf '%s' "$1" >&2
    stty -echo
    IFS= read pw
    stty echo
    printf '\n' >&2
    printf '%s' "$pw"
  else
    echo "error: run this on a terminal so the passphrase is not logged" >&2
    exit 1
  fi
}
pw1=$(read_pw "encryption passphrase: ")
pw2=$(read_pw "again: ")
if [ "$pw1" != "$pw2" ]; then echo "error: passphrases did not match" >&2; exit 1; fi
if [ ${#pw1} -lt 12 ]; then echo "error: use a longer passphrase (at least 12 characters)" >&2; exit 1; fi

encrypt() {
  local dest=$1
  umask 077
  openssl enc -aes-256-cbc -pbkdf2 -iter 600000 -salt -in "$IN" -out "$dest" -pass stdin <<<"$pw1"
}

encrypt "$OUT1"
encrypt "$OUT2"
# Confirm both ciphertexts exist and are non-empty; do not compare them (salt makes them differ).
if [ ! -s "$OUT1" ] || [ ! -s "$OUT2" ]; then echo "error: encrypted copy is empty" >&2; exit 1; fi
echo "ok: two encrypted copies written. The original was not moved."
echo "run restore-keystore-test.sh on each copy."
