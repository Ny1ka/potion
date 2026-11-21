#!/usr/bin/env bash
# Copies the shared spec files into the Swift package, which can't read files outside its folder.
#   scripts/sync-spec.sh          copy
#   scripts/sync-spec.sh --check  exit 1 if any copy differs (used by CI)
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
SRC=core/spec
DST=core/swift/Sources/PotionCore/Resources
FILES=(formats.json matrix.json)

status=0
mkdir -p "$DST"
for f in "${FILES[@]}"; do
  if [[ "${1:-}" == "--check" ]]; then
    if ! cmp -s "$SRC/$f" "$DST/$f"; then echo "✗ $DST/$f is out of date. Run: make sync-spec" >&2; status=1; fi
  else
    cp "$SRC/$f" "$DST/$f"
  fi
done
exit $status
