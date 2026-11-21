# FFmpeg for Potion

Potion and Potion Dropper ship FFmpeg as **separate programs** (`ffmpeg`, `ffprobe`) that the apps run as
child processes. They are never linked into the apps. These scripts build them from pinned, checksummed
sources.

| File | Purpose |
|---|---|
| `versions.sh` | Source versions, URLs and SHA-256 checksums |
| `build-macos.sh` | Builds universal (Apple Silicon + Intel) `ffmpeg` and `ffprobe` into `build/ffmpeg/macos/` |
| `verify-license.sh` | Fails unless a build is LGPL-only, has no network support, has no native AAC encoder and links only system libraries |

```bash
brew install nasm pkg-config        # build tools only; nothing from Homebrew ships
scripts/ffmpeg/build-macos.sh       # about 6 minutes on a recent Mac; CI caches the result
```

## What's in the build

- **FFmpeg 9.1, LGPL 2.1 or later.** Configured with `--disable-gpl --disable-nonfree --disable-version3`
  and `--disable-network` (the converter never needs the network).
- **Statically linked into the FFmpeg programs:** LAME (MP3, LGPL), Opus, libogg, libvorbis and libvpx
  (VP8/VP9) (all BSD).
- **OS codecs for patent-encumbered formats** (spec Part 6.1.2): H.264 and HEVC through VideoToolbox, AAC and
  ALAC through AudioToolbox. FFmpeg's own AAC encoder is left out.
- `build/ffmpeg/macos/licenses/` has every license text and `BUILDINFO.txt` lists the exact sources.

## LGPL obligations (completed in M8/M9)

Ship the license texts and credit FFmpeg in the About window, offer the exact corresponding source (the
pinned archives plus these scripts), and keep `ffmpeg`/`ffprobe` as replaceable files inside the app bundle.

## Not built yet

- Windows: built or sourced in M2 when the Windows engines start (MSYS2/MinGW cross-build, same configuration).
- AV1 encode/decode (SVT-AV1, dav1d): add if AVIF or AV1 video needs FFmpeg rather than the OS.
- Size: each universal binary is about 54 MB before DMG compression. Trimming unused components is an M9 task.
