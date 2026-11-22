# Legal review log

Claude Code is not a lawyer and nothing here is legal advice. This file records decisions, open questions
and items that need review by a qualified lawyer before public release. The full audit and release gate
are completed in M8/M9.

## Decisions

| Date | Decision | Notes |
|---|---|---|
| 2026-11-21 | Project code is licensed under **MIT** (`/LICENSE`). | Compatible with LGPL FFmpeg linked dynamically or run as a separate process, OS frameworks, and GPL tools run as separate executables. AGPL libraries stay excluded. MIT grants no rights to the Potion name or logo; add a trademark note in M8. |
| 2026-11-21 | Bundle an **LGPL-only FFmpeg 9.1** as separate `ffmpeg`/`ffprobe` programs (not linked into the apps). | Built from pinned, checksummed sources by `scripts/ffmpeg/`. No `--enable-gpl`/`--enable-nonfree`, no x264/x265/fdk-aac, no network support. `verify-license.sh` enforces this in CI. Statically includes LAME (LGPL) and Opus, libogg, libvorbis, libvpx, libwebp (BSD) and SVT-AV1 (BSD-3-Clause-Clear + AOMedia Patent License 1.0). |
| 2026-11-21 | Documents use the OS text system (macOS) and the Open XML SDK (MIT, Windows); no LibreOffice or Pandoc. | No new bundled dependency on macOS. Opening HTML/Markdown never fetches remote resources (files never leave the machine, and nothing is requested on the document's behalf). |
| 2026-11-21 | Patent-encumbered encoding goes through the OS. | H.264/HEVC via VideoToolbox, AAC/ALAC via AudioToolbox on macOS (Media Foundation on Windows). FFmpeg's native AAC encoder is disabled. |

## Codec and patent notes (for legal advice)

| Codec in the FFmpeg build | Notes |
|---|---|
| H.264, HEVC, AAC **decoders** (FFmpeg's own) | Needed to read common files; FFmpeg's VideoToolbox path still uses its parsers. Encoding is done by the OS. **Ask a lawyer** whether shipping these decoders needs a license (Via LA / Access Advance pools). |
| MPEG-1/MPEG-2 video, MP2 audio encoders | Core patents believed expired in most countries. Confirm. |
| MPEG-4 Part 2 encoder (used for AVI/3GP outputs) | Most pool patents believed expired by 2024. Confirm. |
| WMV2 / WMA2 encoders (FFmpeg's reverse-engineered implementations) | Microsoft codecs. **Ask a lawyer** about any remaining patents. |
| MP3 (LAME), AC-3 | Patents expired (2017). |
| VP8/VP9 (libvpx), WebP (libwebp), Opus, Vorbis, FLAC | Distributed royalty-free; libvpx and libwebp include Google's patent grants. |
| AV1 / AVIF (SVT-AV1, and ImageIO on newer macOS) | Royalty-free under the AOMedia Patent License. A third-party pool (Sisvel) claims AV1 patents. **Ask a lawyer** whether that matters for a free app. |

## Open questions

| # | Question | Needs lawyer? | Status |
|---|---|---|---|
| 1 | Mac App Store and Microsoft Store distribution for Potion: does shipping an LGPL FFmpeg satisfy the LGPL "user can replace the library" requirement inside a store package? | Yes | Open (decide before M9) |
| 2 | App Sandbox for Potion (macOS): needed for the Mac App Store, but it limits "save next to the original" and "move original to Trash" unless the user grants folder access. | No (product decision) | Open (decide in M3) |

## Items for lawyer review before public release

- Codec patent questions above (FFmpeg decoders for H.264/HEVC/AAC; WMV2/WMA2)
- LGPL source offer for FFmpeg: plan is to attach the exact source archives and build scripts to each release
- Terms of use / EULA (`legal/TERMS.md`, M8)
- Privacy policy (`legal/PRIVACY.md`, M8)
- Potion Dropper source policy (`docs/SOURCES.md`, M5)
