#!/usr/bin/env bash
# Write SHA256SUMS for the files passed as arguments, or every regular file in a directory.
set -euo pipefail
if [ $# -lt 1 ]; then
  echo "usage: hash-assets.sh <file-or-dir>..." >&2
  exit 2
fi
tmp=$(mktemp)
trap 'rm -f "$tmp"' EXIT
for arg in "$@"; do
  if [ -d "$arg" ]; then
    find "$arg" -maxdepth 1 -type f -printf '%f\n' | sort | while read -r name; do
      (cd "$arg" && sha256sum -- "$name")
    done >>"$tmp"
  elif [ -f "$arg" ]; then
    sha256sum -- "$(basename "$arg")" >>"$tmp" 2>/dev/null || (cd "$(dirname "$arg")" && sha256sum -- "$(basename "$arg")" >>"$tmp")
  else
    echo "error: not a file or directory: $arg" >&2
    exit 1
  fi
done
cat "$tmp"
