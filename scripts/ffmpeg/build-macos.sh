#!/usr/bin/env bash
# Builds the LGPL-only FFmpeg that Potion ships on macOS: `ffmpeg` and `ffprobe` as standalone programs,
# with LAME (MP3), Opus, Vorbis, libvpx (VP8/VP9), libwebp and SVT-AV1 (AVIF) linked in statically, plus Apple's VideoToolbox and
# AudioToolbox for H.264/HEVC/AAC. No GPL or non-free code, no network support.
#
# Usage: scripts/ffmpeg/build-macos.sh [arm64|x86_64|universal]   (default: universal)
# Output: build/ffmpeg/macos/{bin/ffmpeg, bin/ffprobe, licenses/, BUILDINFO.txt}
# Needs: Xcode command line tools, pkg-config, cmake, nasm (for x86_64).
set -euo pipefail

ROOT="$(git rev-parse --show-toplevel)"
source "$ROOT/scripts/ffmpeg/versions.sh"
TARGET="${1:-universal}"
# FFmpeg embeds its configure paths in the binary, so build outside the home folder (no user name inside).
WORK="${POTION_FFMPEG_WORK:-${TMPDIR:-/tmp}/potion-ffmpeg-work}"
OUT="$ROOT/build/ffmpeg/macos"
MIN_MACOS=13.0
JOBS="$(sysctl -n hw.ncpu)"
HOST_ARCH="$(uname -m)"

log() { printf '\n\033[1m==> %s\033[0m\n' "$*"; }

fetch() { # name url sha256 → extracts into $WORK/src/<name>
  local url=$2 sha=$3
  local file="$WORK/src/$(basename "$url")"
  mkdir -p "$WORK/src"
  [[ -f "$file" ]] || curl -sSfL --retry 3 -o "$file" "$url"
  echo "$sha  $file" | shasum -a 256 -c --quiet - || { echo "Checksum mismatch for $file" >&2; exit 1; }
}

unpack() { # archive-basename dest-dir → fresh source tree for one arch
  local archive="$WORK/src/$1" dest=$2
  rm -rf "$dest" && mkdir -p "$dest"
  tar -xf "$archive" -C "$dest" --strip-components 1
}

build_arch() {
  local arch=$1
  local prefix="$WORK/$arch/prefix" srcs="$WORK/$arch/src"
  local host; [[ $arch == arm64 ]] && host=aarch64-apple-darwin || host=x86_64-apple-darwin
  local cflags="-arch $arch -mmacosx-version-min=$MIN_MACOS -O2"
  mkdir -p "$prefix" "$srcs"

  # Only our own prefix is visible to pkg-config, so Homebrew libraries can never leak into the build.
  export PKG_CONFIG_LIBDIR="$prefix/lib/pkgconfig" PKG_CONFIG_PATH="$prefix/lib/pkgconfig"
  local autoconf=(--host="$host" --prefix="$prefix" --disable-shared --enable-static)

  log "[$arch] LAME $LAME_VERSION"
  unpack "$(basename "$LAME_URL")" "$srcs/lame"
  (cd "$srcs/lame"
   sed -i '' '/lame_init_old/d' include/libmp3lame.sym   # symbol listed but not built; breaks linking on macOS
   CFLAGS="$cflags" ./configure "${autoconf[@]}" --disable-frontend --disable-decoder --disable-dependency-tracking >/dev/null
   make -j"$JOBS" >/dev/null && make install >/dev/null)

  log "[$arch] libogg $OGG_VERSION"
  unpack "$(basename "$OGG_URL")" "$srcs/ogg"
  (cd "$srcs/ogg" && CFLAGS="$cflags" ./configure "${autoconf[@]}" >/dev/null && make -j"$JOBS" >/dev/null && make install >/dev/null)

  log "[$arch] libvorbis $VORBIS_VERSION"
  unpack "$(basename "$VORBIS_URL")" "$srcs/vorbis"
  (cd "$srcs/vorbis"
   sed -i '' 's/-force_cpusubtype_ALL//g' configure   # flag rejected by current clang
   CFLAGS="$cflags" ./configure "${autoconf[@]}" --with-ogg="$prefix" --disable-oggtest >/dev/null
   make -j"$JOBS" >/dev/null && make install >/dev/null)

  log "[$arch] Opus $OPUS_VERSION"
  unpack "$(basename "$OPUS_URL")" "$srcs/opus"
  (cd "$srcs/opus" && CFLAGS="$cflags" ./configure "${autoconf[@]}" --disable-doc --disable-extra-programs >/dev/null \
   && make -j"$JOBS" >/dev/null && make install >/dev/null)

  log "[$arch] libvpx $VPX_VERSION"
  unpack "libvpx-$VPX_VERSION.tar.gz" "$srcs/vpx"
  (cd "$srcs/vpx"
   # Newest Darwin target this libvpx knows for the architecture.
   local vpx_target
   vpx_target="$( (./configure --help || true) | grep -oE "${arch}-darwin[0-9]+-gcc" | sort -u -V | tail -1)"
   [[ -n "$vpx_target" ]] || { echo "libvpx has no Darwin target for $arch" >&2; exit 1; }
   ./configure --target="$vpx_target" --prefix="$prefix" --disable-shared --enable-static --enable-pic \
     --disable-examples --disable-tools --disable-docs --disable-unit-tests --enable-vp8 --enable-vp9 \
     --extra-cflags="-mmacosx-version-min=$MIN_MACOS" >/dev/null
   make -j"$JOBS" >/dev/null && make install >/dev/null)

  log "[$arch] libwebp $WEBP_VERSION"
  unpack "$(basename "$WEBP_URL")" "$srcs/webp"
  (cd "$srcs/webp" && CFLAGS="$cflags" ./configure "${autoconf[@]}" --enable-libwebpmux \
     --disable-gif --disable-jpeg --disable-png --disable-tiff --disable-sdl --disable-gl --disable-wic >/dev/null \
   && make -j"$JOBS" >/dev/null && make install >/dev/null)

  log "[$arch] SVT-AV1 $SVTAV1_VERSION"
  unpack "$(basename "$SVTAV1_URL")" "$srcs/svtav1"
  (cd "$srcs/svtav1"
   cmake -S . -B build -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF -DBUILD_APPS=OFF -DBUILD_TESTING=OFF \
     -DCMAKE_INSTALL_PREFIX="$prefix" -DCMAKE_INSTALL_LIBDIR=lib -DCMAKE_OSX_ARCHITECTURES="$arch" \
     -DCMAKE_OSX_DEPLOYMENT_TARGET="$MIN_MACOS" -DCMAKE_SYSTEM_NAME=Darwin -DCMAKE_SYSTEM_PROCESSOR="$arch" >/dev/null
   cmake --build build -j "$JOBS" >/dev/null && cmake --install build >/dev/null)

  log "[$arch] FFmpeg $FFMPEG_VERSION"
  unpack "$(basename "$FFMPEG_URL")" "$srcs/ffmpeg"
  local cross=(); [[ $arch != "$HOST_ARCH" ]] && cross=(--enable-cross-compile)
  (cd "$srcs/ffmpeg"
   # ${cross[@]+…}: macOS ships bash 3.2, where an empty array counts as unbound under `set -u`.
   ./configure --prefix="$prefix" --arch="$arch" --target-os=darwin ${cross[@]+"${cross[@]}"} \
     --cc="clang -arch $arch" --extra-cflags="$cflags -I$prefix/include" \
     --extra-ldflags="-arch $arch -mmacosx-version-min=$MIN_MACOS -L$prefix/lib" --extra-libs="-liconv" \
     --pkg-config-flags=--static \
     --disable-gpl --disable-nonfree --disable-version3 \
     --disable-autodetect --disable-network --disable-ffplay --disable-doc --disable-debug \
     --enable-static --disable-shared --enable-pthreads \
     --enable-videotoolbox --enable-audiotoolbox --enable-zlib --enable-bzlib --enable-iconv \
     --enable-libmp3lame --enable-libopus --enable-libvorbis --enable-libvpx --enable-libwebp --enable-libsvtav1 \
     --disable-encoder=aac >/dev/null   # AAC is encoded by the OS (aac_at), per the codec-patent policy
   make -j"$JOBS" >/dev/null && make install >/dev/null)
}

case "$TARGET" in
  arm64|x86_64) ARCHS=("$TARGET") ;;
  universal) ARCHS=(arm64 x86_64) ;;
  *) echo "Usage: $0 [arm64|x86_64|universal]" >&2; exit 2 ;;
esac

log "Checking sources"
fetch ffmpeg "$FFMPEG_URL" "$FFMPEG_SHA256"
fetch lame "$LAME_URL" "$LAME_SHA256"
fetch opus "$OPUS_URL" "$OPUS_SHA256"
fetch ogg "$OGG_URL" "$OGG_SHA256"
fetch vorbis "$VORBIS_URL" "$VORBIS_SHA256"
fetch webp "$WEBP_URL" "$WEBP_SHA256"
fetch svtav1 "$SVTAV1_URL" "$SVTAV1_SHA256"
[[ -f "$WORK/src/libvpx-$VPX_VERSION.tar.gz" ]] || curl -sSfL --retry 3 -o "$WORK/src/libvpx-$VPX_VERSION.tar.gz" "$VPX_URL"
echo "$VPX_SHA256  $WORK/src/libvpx-$VPX_VERSION.tar.gz" | shasum -a 256 -c --quiet -

for arch in "${ARCHS[@]}"; do build_arch "$arch"; done

log "Assembling $OUT"
rm -rf "$OUT" && mkdir -p "$OUT/bin" "$OUT/licenses"
for tool in ffmpeg ffprobe; do
  inputs=(); for arch in "${ARCHS[@]}"; do inputs+=("$WORK/$arch/prefix/bin/$tool"); done
  if [[ ${#inputs[@]} -gt 1 ]]; then lipo -create "${inputs[@]}" -output "$OUT/bin/$tool"; else cp "${inputs[0]}" "$OUT/bin/$tool"; fi
  strip -x "$OUT/bin/$tool"
done

src="$WORK/${ARCHS[0]}/src"
cp "$src/ffmpeg/COPYING.LGPLv2.1" "$OUT/licenses/FFmpeg-COPYING.LGPLv2.1"
cp "$src/ffmpeg/LICENSE.md" "$OUT/licenses/FFmpeg-LICENSE.md"
cp "$src/lame/COPYING" "$OUT/licenses/LAME-COPYING"
cp "$src/opus/COPYING" "$OUT/licenses/Opus-COPYING"
cp "$src/ogg/COPYING" "$OUT/licenses/libogg-COPYING"
cp "$src/vorbis/COPYING" "$OUT/licenses/libvorbis-COPYING"
cp "$src/vpx/LICENSE" "$OUT/licenses/libvpx-LICENSE"
cp "$src/vpx/PATENTS" "$OUT/licenses/libvpx-PATENTS"
cp "$src/webp/COPYING" "$OUT/licenses/libwebp-COPYING"
cp "$src/webp/PATENTS" "$OUT/licenses/libwebp-PATENTS"
cp "$src/svtav1/LICENSE.md" "$OUT/licenses/SVT-AV1-LICENSE.md"
cp "$src/svtav1/PATENTS.md" "$OUT/licenses/SVT-AV1-PATENTS.md"

{
  echo "Potion FFmpeg build for macOS ($TARGET), built $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "Minimum macOS: $MIN_MACOS"
  echo
  echo "Sources (SHA-256):"
  echo "  FFmpeg $FFMPEG_VERSION     $FFMPEG_SHA256  $FFMPEG_URL"
  echo "  LAME $LAME_VERSION       $LAME_SHA256  $LAME_URL"
  echo "  Opus $OPUS_VERSION       $OPUS_SHA256  $OPUS_URL"
  echo "  libogg $OGG_VERSION     $OGG_SHA256  $OGG_URL"
  echo "  libvorbis $VORBIS_VERSION  $VORBIS_SHA256  $VORBIS_URL"
  echo "  libvpx $VPX_VERSION    $VPX_SHA256  $VPX_URL"
  echo "  libwebp $WEBP_VERSION   $WEBP_SHA256  $WEBP_URL"
  echo "  SVT-AV1 $SVTAV1_VERSION   $SVTAV1_SHA256  $SVTAV1_URL"
  echo
  "$OUT/bin/ffmpeg" -hide_banner -version
} > "$OUT/BUILDINFO.txt"

"$ROOT/scripts/ffmpeg/verify-license.sh" "$OUT/bin"
log "Done: $OUT"
