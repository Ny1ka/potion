#!/usr/bin/env bash
# Packs a built .app into a compressed DMG with an Applications shortcut (macOS only; uses hdiutil).
#   scripts/release/make-dmg.sh "build/.../Potion.app" "dist/Potion-0.9.0.dmg" "Potion"
# The DMG holds the app exactly as built; signing and notarization (when configured) happen before this step.
set -euo pipefail
app="${1:?app path}"; out="${2:?output .dmg}"; volume="${3:?volume name}"
[[ -d "$app" ]] || { echo "No app at $app" >&2; exit 1; }

stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
ditto "$app" "$stage/$(basename "$app")"
ln -s /Applications "$stage/Applications"
mkdir -p "$(dirname "$out")"
rm -f "$out"
hdiutil create -volname "$volume" -srcfolder "$stage" -fs HFS+ -format UDZO -imagekey zlib-level=9 -ov "$out" >/dev/null
hdiutil verify "$out" >/dev/null
echo "✓ $out ($(du -h "$out" | cut -f1))"
