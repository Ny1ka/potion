#!/usr/bin/env bash
# One-time setup for a fresh clone on macOS. Safe to re-run.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

git config core.hooksPath .githooks
chmod +x .githooks/* scripts/*.sh
echo "✓ Git hooks enabled (.githooks)"

missing=0
need() {
  if command -v "$1" >/dev/null 2>&1; then echo "✓ $1"; else echo "✗ $1 not found. $2"; missing=1; fi
}
need xcodebuild "Install Xcode from the App Store, then run: xcode-select --install"
need swift      "Comes with Xcode."
need xcodegen   "Install with: brew install xcodegen"

[[ $missing -eq 0 ]] && echo "Ready. Try: make test-core && make potion-macos" || exit 1
