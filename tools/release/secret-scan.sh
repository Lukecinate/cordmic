#!/usr/bin/env bash
# Heuristic secret scan for the CordMic tree. Fails the build if a private key
# or a well-known token prefix is committed. Does not print matched secret
# values — only the file and a rule name.
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
cd "$root"
fail=0

# Skip bulky or generated trees that are never a place for secrets we author.
excludes=(
  ':!**/bin/**' ':!**/obj/**' ':!**/build/**' ':!**/dist/**'
  ':!**/.gradle/**' ':!**/node_modules/**'
)

# git grep -I skips binary. We report the path only.
scan() {
  local rule="$1" pattern="$2"
  local hits
  hits=$(git grep -I -l -E "$pattern" -- "${excludes[@]}" 2>/dev/null || true)
  if [ -n "$hits" ]; then
    echo "secret-scan: rule '$rule' matched:" >&2
    printf '%s\n' "$hits" | sed 's/^/  /' >&2
    fail=1
  fi
}

if ! git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  echo "secret-scan: not a git work tree, walking files with grep" >&2
  # Fallback used only if someone runs this outside git (should not happen in CI).
  if grep -R -I -l -E '-----BEGIN ([A-Z0-9]+ )?PRIVATE KEY-----' . >/dev/null 2>&1; then
    echo "secret-scan: rule 'pem-private-key' matched (names suppressed in fallback)" >&2
    fail=1
  fi
else
  scan pem-private-key '-----BEGIN ([A-Z0-9]+ )?PRIVATE KEY-----'
  scan openssh-private-key '-----BEGIN OPENSSH PRIVATE KEY-----'
  scan github-pat 'gh[pousr]_[A-Za-z0-9]{20,}'
  scan github-fine-grained 'github_pat_[A-Za-z0-9_]{20,}'
  scan aws-access-key-id 'AKIA[0-9A-Z]{16}'
  scan slack-token 'xox[aboprs]-[A-Za-z0-9-]{10,}'
  # PKCS#12 / Java keystores must not be committed. The tools/keystore folder may
  # contain only documentation and scripts, never the .jks/.p12 itself.
  while IFS= read -r f; do
    echo "secret-scan: rule 'keystore-or-pfx' matched: $f" >&2
    fail=1
  done < <(git ls-files | grep -E '\.(jks|p12|pfx|pem)$' || true)
fi

if [ "$fail" -ne 0 ]; then
  echo "secret-scan failed; remove the files or replace real secrets with placeholders" >&2
  exit 1
fi
echo "ok: secret-scan found no private keys, token prefixes, or keystore files"
