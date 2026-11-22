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
| 2026-11-22 | **PDFsharp/MigraDoc removed.** Version 6.5.0 (the one added on 2026-11-21 in commit 7c60572) is licensed AGPL-3.0-only with a commercial license key; versions up to 6.2.4 are MIT. AGPL is not allowed in this project. | Only a package reference was ever committed; no PDFsharp code is in the repository and nothing was released. Removed the same day it was noticed. Which PDF writer to use on Windows is an open owner decision (see Open questions #3). Licenses of every other added package were re-checked from their nuspec files: PdfPig Apache-2.0; HtmlAgilityPack, Open XML SDK MIT. |
| 2026-11-21 | Windows documents use permissive libraries (bundled, offline): Open XML SDK (MIT), HtmlAgilityPack (MIT), PdfPig (Apache-2.0). PDFsharp + MigraDoc were planned as MIT but turned out to be AGPL in the current version (see 2026-11-22). | Owner's choice. (Markdig was dropped on 2026-11-22: it embeds names the source policy blocks; Potion renders Markdown itself.) Each needs its license text and version in THIRD_PARTY_NOTICES (M8); Apache-2.0 also needs any NOTICE file reproduced. No copyleft. DOC stays macOS-only. |
| 2026-11-21 | Protected PDFs: Potion never removes or works around PDF passwords or permissions. | A PDF that needs a password is refused. One with owner restrictions converts only where its permissions allow (copying → text, printing → images) and is never rewritten by merge/split/extract/reorder/compress, since the new file would lose the restrictions. Same rule for DRM EPUBs and encrypted Office files. |
| 2026-11-21 | Patent-encumbered encoding goes through the OS. | H.264/HEVC via VideoToolbox, AAC/ALAC via AudioToolbox on macOS; `h264_mf`/`hevc_mf`/`aac_mf` (Media Foundation) on Windows. FFmpeg's native AAC encoder is disabled. |
| 2026-11-22 | **Potion Dropper V1 source allowlist: direct media links only** (`docs/SOURCES.md`, `core/spec/sources.json`). | The one source in spec Part 6.3.1's default scope that needs no per-site terms review. Links with a user name or password are refused (Dropper never handles credentials); streaming playlists (`.m3u8`, `.mpd`) are refused (they can carry encrypted segments and are usually a site's player, not a published file); a link without a media extension is accepted only if the server answers with an audio or video file. More sites (for example freely licensed collections) need a SOURCES.md entry with the site's terms URL and the owner's approval. |
| 2026-11-22 | Potion Dropper follows robots.txt (RFC 9309) for every download, although each one is a single file the user asked for, not crawling. | Spec Part 6.3.8. Product token `PotionDropper`; a site that disallows the path gets "This site asks apps not to download this file, so Potion Dropper won't." A robots.txt that fails with a server error blocks downloads from that site for the session (the RFC's rule for crawlers); a missing one (4xx) allows them. Every body is also checked by content before conversion, and 401/403 answers are reported, never retried with credentials. |
| 2026-11-22 | Potion Dropper for macOS shows the first-run acknowledgment (spec Part 6.3.5, text word for word) before anything can be fetched or downloaded: "I understand" must be ticked to continue; the other choice quits. | Stored as a version number in the app's preferences, so a changed text asks again. Unsupported sites get the neutral message from `sources.json`. The Windows app gets the same in M7. |
| 2026-11-22 | Personal experimental adapters are kept outside this repository. The public Dropper apps have only a generic external source loading point, compiled into Debug builds only. | Owner's decision and responsibility (spec Part 6.6). Release builds compile the loader out; the Release artifact scan fails if any loader symbol or the personal-build badge is found. A Debug build that loads an external source shows "PERSONAL BUILD: EXPERIMENTAL SOURCES ENABLED" in its title and asks for the acknowledgment again with an extra line. Personal builds are never published. |
| 2026-11-21 | Windows FFmpeg: same LGPL-only configuration, cross-compiled with MinGW-w64, plus zlib (zlib License) linked statically. | The MinGW-w64 runtime and libgcc are linked statically; their licenses (MinGW-w64 runtime: ZPL/public domain mix; libgcc: GPL with the GCC Runtime Library Exception) allow this for any program. List them in THIRD_PARTY_NOTICES (M8). |

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
| 3 | PDF writing on Windows (documents → PDF, merge/split): pin PDFsharp/MigraDoc 6.2.4 (last MIT release, frozen), or build on PdfPig's PDF builder (Apache-2.0, maintained) with our own layout. | No (product decision; both licenses are permissive) | Decided 2026-11-22: pin 6.2.4 (MIT). Do not upgrade past it without re-checking the license |
| 2 | App Sandbox for Potion (macOS): needed for the Mac App Store, but it limits "save next to the original" and "move original to Trash" unless the user grants folder access. | No (product decision) | Decided 2026-11-21: off for V1 (DMG). Revisit for the Mac App Store |

## Items for lawyer review before public release

- Codec patent questions above (FFmpeg decoders for H.264/HEVC/AAC; WMV2/WMA2)
- LGPL source offer for FFmpeg: plan is to attach the exact source archives and build scripts to each release
- Terms of use / EULA (`legal/TERMS.md`, M8)
- Privacy policy (`legal/PRIVACY.md`, M8)
- Potion Dropper source policy (`docs/SOURCES.md`, M5)
