#!/usr/bin/env bash
# Fail if the Gate H checklist is missing or still has an unchecked item.
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
file=${1:-"$root/docs/release-checklists/0.8.0.md"}
if [ ! -f "$file" ]; then
  echo "error: Gate H checklist not found: $file" >&2
  echo "the checklist is added in milestone M8; publish stays blocked until it exists and is ticked" >&2
  exit 2
fi
# Ignore example / documentation checkboxes inside backticks if any; count Markdown tasks.
unchecked=$(grep -E '^\s*- \[ \]' "$file" || true)
if [ -n "$unchecked" ]; then
  echo "error: Gate H still has unchecked items:" >&2
  printf '%s\n' "$unchecked" >&2
  exit 1
fi
if ! grep -Eq '^\s*- \[[xX]\]' "$file"; then
  echo "error: Gate H has no ticked items (empty or malformed checklist)" >&2
  exit 1
fi
echo "ok: Gate H has no unchecked items"
