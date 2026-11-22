#!/usr/bin/env bash
# Fails unless the FFmpeg build in <bin-dir> is LGPL-only, has no network support, and (on macOS) links
# only system libraries. Run by build-macos.sh and by CI before anything ships.
# Usage: scripts/ffmpeg/verify-license.sh <bin-dir>
set -euo pipefail
BIN="${1:?usage: verify-license.sh <bin-dir>}"
FFMPEG="$BIN/ffmpeg"; [[ -x "$FFMPEG" ]] || FFMPEG="$BIN/ffmpeg.exe"
fail() { echo "✗ $*" >&2; exit 1; }

# Windows builds print CRLF line endings; drop the carriage returns so the same checks work everywhere.
config="$("$FFMPEG" -hide_banner -buildconf | tr -d '\r')"
# The license text is wrapped across lines; flatten it before searching.
license="$("$FFMPEG" -hide_banner -L | tr -d '\r' | tr -s '\n ' '  ')"

grep -q -- "--enable-gpl" <<<"$config" && fail "built with --enable-gpl"
grep -q -- "--enable-nonfree" <<<"$config" && fail "built with --enable-nonfree"
grep -q "GNU Lesser General Public License" <<<"$license" || fail "license is not LGPL"
grep -q -- "--disable-network" <<<"$config" || fail "network support is enabled"
# Patent-encumbered formats are encoded by the OS (spec Part 6.1.2), so FFmpeg's own AAC encoder must be absent.
"$FFMPEG" -hide_banner -encoders 2>/dev/null | tr -d '\r' | grep -qE '^ A[A-Z.]{5} aac ' && fail "FFmpeg's native AAC encoder is included"
for lib in libx264 libx265 libfdk-aac libxvid librubberband libvidstab; do
  grep -q -- "--enable-$lib" <<<"$config" && fail "GPL or non-free library enabled: $lib"
done

if [[ "$(uname)" == Darwin ]]; then
  for tool in "$BIN/ffmpeg" "$BIN/ffprobe"; do
    deps="$(otool -L "$tool" | grep -E '^[[:space:]]' | awk '{print $1}' | sort -u)"  # indented lines are libraries; fat binaries add per-arch headers
    while read -r dep; do
      [[ -z "$dep" || "$dep" == /usr/lib/* || "$dep" == /System/Library/* ]] || fail "$(basename "$tool") links a non-system library: $dep"
    done <<<"$deps"
  done
fi

echo "✓ FFmpeg build is LGPL-only, has no network support and links only system libraries"
