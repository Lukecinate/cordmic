#!/usr/bin/env bash
# Fail if a GitHub Actions workflow uses a floating tag or a short SHA.
# Every "uses:" must be owner/name@<40 lowercase hex> (or owner/name/path@sha).
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
fail=0
shopt -s nullglob
files=("$root"/.github/workflows/*.{yml,yaml})
if [ ${#files[@]} -eq 0 ]; then
  echo "error: no workflows under .github/workflows" >&2
  exit 1
fi
# Allow only 40-hex pins. Comments after the SHA are fine.
pattern='uses:[[:space:]]*([^#[:space:]]+)'
while IFS= read -r line; do
  file=${line%%:*}; rest=${line#*:}
  uses=$(printf '%s\n' "$rest" | sed -n 's/.*uses:[[:space:]]*//p' | sed 's/[[:space:]]*#.*//;s/[[:space:]]*$//')
  [ -n "$uses" ] || continue
  case "$uses" in
    ./*) continue ;;  # local action
  esac
  sha=${uses##*@}
  if ! printf '%s' "$sha" | grep -Eq '^[0-9a-f]{40}$'; then
    echo "unpinned uses: $uses  ($file)" >&2
    fail=1
  fi
done < <(grep -n "uses:" "$root"/.github/workflows/*.{yml,yaml} 2>/dev/null || true)
if [ "$fail" -ne 0 ]; then
  echo "every marketplace action must be pinned to a 40-character commit SHA" >&2
  exit 1
fi
echo "ok: all workflow uses: pins are 40-character SHAs"
