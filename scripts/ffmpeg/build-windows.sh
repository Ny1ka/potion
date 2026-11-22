#!/usr/bin/env bash
# Builds the LGPL-only FFmpeg that Potion ships on Windows: `ffmpeg.exe` and `ffprobe.exe` as standalone
# programs, cross-compiled with MinGW-w64 on Linux (CI) or macOS. LAME, Opus, Vorbis, libvpx, libwebp, SVT-AV1
# and zlib are linked in statically, plus Windows Media Foundation for H.264/HEVC/AAC encoding. No GPL or
# non-free code, no network support, and no MinGW runtime DLLs (the programs need only Windows system DLLs).
#
# Usage: scripts/ffmpeg/build-windows.sh            (x86_64; Windows on Arm runs it under emulation)
# Output: build/ffmpeg/windows/{bin/ffmpeg.exe, bin/ffprobe.exe, licenses/, BUILDINFO.txt}
# Needs: x86_64-w64-mingw32 GCC toolchain, pkg-config, cmake, nasm, make.
# The full license check runs the .exe, so it happens on Windows (CI): scripts/ffmpeg/verify-license.sh.
set -euo pipefail

ROOT="$(git rev-parse --show-toplevel)"
source "$ROOT/scripts/ffmpeg/versions.sh"
# FFmpeg embeds its configure paths in the binary, so build outside the home folder (no user name inside).
WORK="${POTION_FFMPEG_WORK:-${TMPDIR:-/tmp}/potion-ffmpeg-windows-work}"
OUT="$ROOT/build/ffmpeg/windows"
CROSS=x86_64-w64-mingw32
JOBS="$( (nproc || sysctl -n hw.ncpu) 2>/dev/null)"

log() { printf '\n\033[1m==> %s\033[0m\n' "$*"; }
sha256() { if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }

fetch() { # url sha256 [file name]
  local url=$1 sha=$2 file="$WORK/src/${3:-$(basename "$1")}"
  mkdir -p "$WORK/src"
  [[ -f "$file" ]] || curl -sSfL --retry 3 -o "$file" "$url"
  [[ "$(sha256 "$file")" == "$sha" ]] || { echo "Checksum mismatch for $file" >&2; exit 1; }
}

unpack() { # archive-basename dest-dir
  rm -rf "$2" && mkdir -p "$2"
  tar -xf "$WORK/src/$1" -C "$2" --strip-components 1
}

command -v "$CROSS-gcc" >/dev/null || { echo "Missing $CROSS-gcc (MinGW-w64)" >&2; exit 1; }

log "Checking sources"
fetch "$FFMPEG_URL" "$FFMPEG_SHA256"
fetch "$LAME_URL" "$LAME_SHA256"
fetch "$OPUS_URL" "$OPUS_SHA256"
fetch "$OGG_URL" "$OGG_SHA256"
fetch "$VORBIS_URL" "$VORBIS_SHA256"
fetch "$WEBP_URL" "$WEBP_SHA256"
fetch "$SVTAV1_URL" "$SVTAV1_SHA256"
fetch "$ZLIB_URL" "$ZLIB_SHA256"
fetch "$VPX_URL" "$VPX_SHA256" "libvpx-$VPX_VERSION.tar.gz"

prefix="$WORK/x86_64/prefix" srcs="$WORK/x86_64/src"
rm -rf "$WORK/x86_64" && mkdir -p "$prefix" "$srcs"
# Only our own prefix is visible to pkg-config, so host libraries can never leak into the build.
export PKG_CONFIG_LIBDIR="$prefix/lib/pkgconfig" PKG_CONFIG_PATH="$prefix/lib/pkgconfig"
export CC="$CROSS-gcc" CXX="$CROSS-g++" AR="$CROSS-ar" RANLIB="$CROSS-ranlib" STRIP="$CROSS-strip"
cflags="-O2"
autoconf=(--host="$CROSS" --prefix="$prefix" --disable-shared --enable-static)

log "zlib $ZLIB_VERSION"
unpack "$(basename "$ZLIB_URL")" "$srcs/zlib"
(cd "$srcs/zlib" && make -f win32/Makefile.gcc PREFIX="$CROSS-" -j"$JOBS" libz.a >/dev/null \
 && make -f win32/Makefile.gcc install BINARY_PATH="$prefix/bin" INCLUDE_PATH="$prefix/include" LIBRARY_PATH="$prefix/lib" \
      SHARED_MODE=0 PREFIX="$CROSS-" >/dev/null)
mkdir -p "$prefix/lib/pkgconfig"
printf 'prefix=%s\nlibdir=${prefix}/lib\nincludedir=${prefix}/include\nName: zlib\nDescription: zlib\nVersion: %s\nLibs: -L${libdir} -lz\nCflags: -I${includedir}\n' \
  "$prefix" "$ZLIB_VERSION" > "$prefix/lib/pkgconfig/zlib.pc"

log "LAME $LAME_VERSION"
unpack "$(basename "$LAME_URL")" "$srcs/lame"
(cd "$srcs/lame"
 sed -i.orig '/lame_init_old/d' include/libmp3lame.sym   # symbol listed but not built
 CFLAGS="$cflags" ./configure "${autoconf[@]}" --disable-frontend --disable-decoder --disable-dependency-tracking >/dev/null
 make -j"$JOBS" >/dev/null && make install >/dev/null)

log "libogg $OGG_VERSION"
unpack "$(basename "$OGG_URL")" "$srcs/ogg"
(cd "$srcs/ogg" && CFLAGS="$cflags" ./configure "${autoconf[@]}" >/dev/null && make -j"$JOBS" >/dev/null && make install >/dev/null)

log "libvorbis $VORBIS_VERSION"
unpack "$(basename "$VORBIS_URL")" "$srcs/vorbis"
(cd "$srcs/vorbis" && CFLAGS="$cflags" ./configure "${autoconf[@]}" --with-ogg="$prefix" --disable-oggtest >/dev/null \
 && make -j"$JOBS" >/dev/null && make install >/dev/null)

log "Opus $OPUS_VERSION"
unpack "$(basename "$OPUS_URL")" "$srcs/opus"
# _FORTIFY_SOURCE would need libssp at link time; off for a static Windows build.
(cd "$srcs/opus" && CFLAGS="$cflags -U_FORTIFY_SOURCE -D_FORTIFY_SOURCE=0" ./configure "${autoconf[@]}" \
   --disable-doc --disable-extra-programs >/dev/null && make -j"$JOBS" >/dev/null && make install >/dev/null)

log "libvpx $VPX_VERSION"
unpack "libvpx-$VPX_VERSION.tar.gz" "$srcs/vpx"
(cd "$srcs/vpx"
 CROSS="$CROSS-" ./configure --target=x86_64-win64-gcc --prefix="$prefix" --disable-shared --enable-static \
   --disable-examples --disable-tools --disable-docs --disable-unit-tests --enable-vp8 --enable-vp9 >/dev/null
 CROSS="$CROSS-" make -j"$JOBS" >/dev/null && make install >/dev/null)

log "libwebp $WEBP_VERSION"
unpack "$(basename "$WEBP_URL")" "$srcs/webp"
(cd "$srcs/webp" && CFLAGS="$cflags" ./configure "${autoconf[@]}" --enable-libwebpmux \
   --disable-gif --disable-jpeg --disable-png --disable-tiff --disable-sdl --disable-gl --disable-wic >/dev/null \
 && make -j"$JOBS" >/dev/null && make install >/dev/null)

log "SVT-AV1 $SVTAV1_VERSION"
unpack "$(basename "$SVTAV1_URL")" "$srcs/svtav1"
(cd "$srcs/svtav1"
 cmake -S . -B build -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF -DBUILD_APPS=OFF -DBUILD_TESTING=OFF \
   -DCMAKE_INSTALL_PREFIX="$prefix" -DCMAKE_INSTALL_LIBDIR=lib \
   -DCMAKE_SYSTEM_NAME=Windows -DCMAKE_SYSTEM_PROCESSOR=x86_64 \
   -DCMAKE_C_COMPILER="$CROSS-gcc" -DCMAKE_CXX_COMPILER="$CROSS-g++" -DCMAKE_RC_COMPILER="$CROSS-windres" \
   -DCMAKE_ASM_NASM_COMPILER=nasm >/dev/null
 cmake --build build -j "$JOBS" >/dev/null && cmake --install build >/dev/null)

log "FFmpeg $FFMPEG_VERSION"
unpack "$(basename "$FFMPEG_URL")" "$srcs/ffmpeg"
(cd "$srcs/ffmpeg"
 ./configure --prefix="$prefix" --arch=x86_64 --target-os=mingw32 --cross-prefix="$CROSS-" --enable-cross-compile \
   --pkg-config=pkg-config --pkg-config-flags=--static \
   --extra-cflags="$cflags -I$prefix/include" --extra-ldflags="-L$prefix/lib -static -static-libgcc" \
   --disable-gpl --disable-nonfree --disable-version3 \
   --disable-autodetect --disable-network --disable-ffplay --disable-doc --disable-debug \
   --disable-indevs --disable-outdevs \
   --enable-static --disable-shared --enable-w32threads \
   --enable-mediafoundation --enable-zlib \
   --enable-libmp3lame --enable-libopus --enable-libvorbis --enable-libvpx --enable-libwebp --enable-libsvtav1 \
   --disable-encoder=aac >/dev/null   # AAC is encoded by the OS (aac_mf), per the codec-patent policy
 make -j"$JOBS" >/dev/null && make install >/dev/null)

log "Assembling $OUT"
rm -rf "$OUT" && mkdir -p "$OUT/bin" "$OUT/licenses"
for tool in ffmpeg ffprobe; do
  cp "$prefix/bin/$tool.exe" "$OUT/bin/$tool.exe"
  "$CROSS-strip" "$OUT/bin/$tool.exe"
done

src="$srcs"
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
cp "$src/zlib/LICENSE" "$OUT/licenses/zlib-LICENSE"

{
  echo "Potion FFmpeg build for Windows (x86_64), built $(date -u +%Y-%m-%dT%H:%M:%SZ) with $("$CROSS-gcc" --version | head -1)"
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
  echo "  zlib $ZLIB_VERSION     $ZLIB_SHA256  $ZLIB_URL"
  echo
  echo "Configuration:"
  grep -E '^#define (FFMPEG_CONFIGURATION|FFMPEG_LICENSE)' "$src/ffmpeg/config.h"
} > "$OUT/BUILDINFO.txt"

"$ROOT/scripts/ffmpeg/verify-windows-build.sh" "$OUT" "$src/ffmpeg"
log "Done: $OUT"
