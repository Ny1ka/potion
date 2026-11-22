#!/usr/bin/env bash
# Static checks on the Windows FFmpeg build, for machines that can't run .exe files (the cross-compiling host):
# LGPL-only configuration, no network, no native AAC encoder, and only Windows system DLLs imported (so no MinGW
# runtime DLLs and no third-party libraries ship next to it). The runtime check, verify-license.sh, runs on Windows.
# Usage: scripts/ffmpeg/verify-windows-build.sh <build/ffmpeg/windows> <ffmpeg source dir with config.h>
set -euo pipefail
OUT="${1:?usage: verify-windows-build.sh <out-dir> <ffmpeg-src-dir>}"
SRC="${2:?usage: verify-windows-build.sh <out-dir> <ffmpeg-src-dir>}"
OBJDUMP="${OBJDUMP:-x86_64-w64-mingw32-objdump}"
fail() { echo "✗ $*" >&2; exit 1; }

config="$SRC/config.h"
components="$SRC/config_components.h"
grep -q '^#define FFMPEG_LICENSE "LGPL' "$config" || fail "license is not LGPL"
grep -q '^#define CONFIG_GPL 0' "$config" || fail "built with GPL code"
grep -q '^#define CONFIG_NONFREE 0' "$config" || fail "built with non-free code"
grep -q -- '--disable-network' "$config" || fail "network support is enabled"
grep -q '^#define CONFIG_AAC_ENCODER 0' "$components" || fail "FFmpeg's native AAC encoder is included"
grep -q '^#define CONFIG_AAC_MF_ENCODER 1' "$components" || fail "Media Foundation AAC encoder (aac_mf) is missing"
for lib in libx264 libx265 libfdk-aac libxvid librubberband libvidstab; do
  grep -q -- "--enable-$lib" "$config" && fail "GPL or non-free library enabled: $lib"
done

# Windows system DLLs only. Anything else (libwinpthread-1.dll, libgcc_s_seh-1.dll, zlib1.dll…) would have to ship
# alongside and means the static link failed.
allowed='^(kernel32|user32|advapi32|gdi32|ole32|oleaut32|shell32|shlwapi|psapi|bcrypt|ws2_32|secur32|crypt32|mfplat|mf|mfreadwrite|propsys|msvcrt|ucrtbase|api-ms-win-[a-z0-9-]+)\.dll$'
for tool in ffmpeg ffprobe; do
  exe="$OUT/bin/$tool.exe"
  [[ -f "$exe" ]] || fail "$exe is missing"
  while read -r dll; do
    [[ -z "$dll" ]] && continue
    lower="$(tr '[:upper:]' '[:lower:]' <<<"$dll")"
    [[ "$lower" =~ $allowed ]] || fail "$tool.exe imports a non-system DLL: $dll"
  done < <("$OBJDUMP" -p "$exe" | awk '/DLL Name:/ {print $3}' | sort -u)
done

echo "✓ Windows FFmpeg build is LGPL-only, has no network support and imports only Windows system DLLs"
