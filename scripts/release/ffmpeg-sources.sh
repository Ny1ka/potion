#!/usr/bin/env bash
# The LGPL source offer for FFmpeg (spec Part 6.1): every pinned source archive (checked against its SHA-256),
# plus the build scripts and the build's BUILDINFO, in one archive attached to each release.
#   scripts/release/ffmpeg-sources.sh dist/ffmpeg-sources.tar
set -euo pipefail
out="${1:?output .tar}"
root="$(git rev-parse --show-toplevel)"
# shellcheck source=../ffmpeg/versions.sh
source "$root/scripts/ffmpeg/versions.sh"

sha256() { if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
mkdir -p "$stage/ffmpeg-sources/archives"
while IFS= read -r name; do
  url_var="${name}_URL"; sha_var="${name}_SHA256"
  url="${!url_var}"; sha="${!sha_var}"
  base="$(basename "$url")"
  [[ "$base" =~ ^v[0-9] ]] && base="$(echo "$name" | tr '[:upper:]' '[:lower:]')-$base"   # GitHub tag archives: v1.2.3.tar.gz
  file="$stage/ffmpeg-sources/archives/$base"
  curl -fsSL --retry 3 -o "$file" "$url"
  [[ "$(sha256 "$file")" == "$sha" ]] || { echo "Checksum mismatch for $url" >&2; exit 1; }
done < <(grep -oE '^[A-Z0-9]+_URL=' "$root/scripts/ffmpeg/versions.sh" | sed 's/_URL=//')
cp -R "$root/scripts/ffmpeg" "$stage/ffmpeg-sources/build-scripts"
for platform in macos windows; do
  [[ -f "$root/build/ffmpeg/$platform/BUILDINFO.txt" ]] && cp "$root/build/ffmpeg/$platform/BUILDINFO.txt" "$stage/ffmpeg-sources/BUILDINFO-$platform.txt"
done
cat > "$stage/ffmpeg-sources/README.txt" <<'TEXT'
FFmpeg sources for Potion and Potion Dropper

The apps run FFmpeg as separate programs (ffmpeg, ffprobe), built LGPL-only from the archives in archives/
(each checked against the SHA-256 in build-scripts/versions.sh) with the scripts in build-scripts/.
BUILDINFO-*.txt records the exact configuration of the shipped builds. To rebuild:
  build-scripts/build-macos.sh universal      (macOS)
  build-scripts/build-windows.sh              (Windows, cross-compiled with MinGW-w64)
Replace the programs in the app (Contents/Helpers on macOS, the ffmpeg folder on Windows) with your own build.
TEXT
mkdir -p "$(dirname "$out")"
tar -C "$stage" -cf "$out" ffmpeg-sources
echo "✓ $out ($(du -h "$out" | cut -f1))"
