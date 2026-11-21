#!/usr/bin/env bash
# forbidden-scan.sh: keeps excluded-platform code out of the public repo and its builds.
#
# The public repo, builds and installers must contain no support, names or tooling for
# the video platforms excluded by the Dropper source policy (CLAUDE.md, "Source policy").
# Used by .githooks/pre-commit (--staged) and CI (--tree, --artifact).
#
# Usage:
#   scripts/forbidden-scan.sh --staged          scan files staged for commit
#   scripts/forbidden-scan.sh --tree            scan every tracked file in the working tree
#   scripts/forbidden-scan.sh --artifact PATH   scan a built app bundle / folder, binaries included
#
# Exit code 0 = clean, 1 = something was found.
set -euo pipefail

# Terms are written with character classes so this file never matches itself.
TERMS='you[t]ube|yout[u]\.be|y[t][-_]dlp|tik[t]ok|insta[g]ram'
# Any path with a directory named exactly "experimental" or "Adapters" (any case).
# Public Dropper source code uses "SourceAdapters" instead.
PATHS='(^|/)(experimenta[l]|adapter[s])(/|$)'
# Files allowed to state, in plain words, that a platform is unsupported.
ALLOW_FILES=("docs/SOURCES.md")

found=0
report() { printf '  \033[31m✗\033[0m %s\n' "$1" >&2; found=1; }

is_allowed() {
  local f="$1" a
  for a in "${ALLOW_FILES[@]}"; do [[ "$f" == "$a" ]] && return 0; done
  return 1
}

check_path() {
  if printf '%s\n' "$1" | grep -qiE "$PATHS"; then report "$1: path is under an experimental/adapters folder"; fi
}

mode="${1:-}"
case "$mode" in
  --staged)
    while IFS= read -r -d '' f; do
      check_path "$f"
      is_allowed "$f" && continue
      if git show ":$f" 2>/dev/null | grep -aiqE "$TERMS"; then
        report "$f: contains a blocked term"
      fi
    done < <(git diff --cached --name-only --diff-filter=ACMR -z)
    ;;
  --tree)
    while IFS= read -r -d '' f; do
      check_path "$f"
      is_allowed "$f" && continue
      [[ -f "$f" ]] || continue
      if grep -aiqE "$TERMS" -- "$f"; then report "$f: contains a blocked term"; fi
    done < <(git ls-files -z)
    ;;
  --artifact)
    target="${2:?--artifact needs a path}"
    [[ -e "$target" ]] || { echo "No such path: $target" >&2; exit 2; }
    while IFS= read -r -d '' f; do
      rel="${f#"$target"/}"
      check_path "$rel"
      if grep -aiqE "$TERMS" -- "$f"; then report "$rel: contains a blocked term"; fi
    done < <(find "$target" -type f -print0)
    ;;
  *)
    sed -n '2,15p' "$0" >&2
    exit 2
    ;;
esac

if [[ $found -ne 0 ]]; then
  cat >&2 <<'EOF'

Blocked by the source policy in CLAUDE.md. This content must not enter the public repo.
If it was already committed, rewrite history before pushing; anything already pushed
counts as published. Tell the project owner.
EOF
  exit 1
fi
exit 0
