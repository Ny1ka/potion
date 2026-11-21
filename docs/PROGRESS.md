# Progress

Read this first in any new session. Update it at every checkpoint.

## Status

- **Current milestone:** M2 Core: conversion engines
- **Last completed chunk:** M2.3 Image engine (Swift)
- **Next chunk:** M2.4 Documents (engine choice needed from the owner)

## Milestones

| Milestone | Status |
|---|---|
| M0 Scaffold | Done |
| M1 Core: formats | Done |
| M2 Core: conversion engines | In progress (M2.0–M2.3 done) |
| M3 Potion macOS UI | Not started |
| M4 Potion Windows UI | Not started |
| M5 Dropper core | Not started |
| M6 Dropper macOS UI | Not started |
| M7 Dropper Windows UI | Not started |
| M7.5 Personal experimental module (private repo) | Not started |
| M8 Legal and compliance pass | Not started |
| M9 Packaging and V1 release | Not started |

### M0 chunks

- [x] M0.1 Repo structure, `.gitignore`, root `CLAUDE.md`, design system in `/design-system`, pre-commit
      scan hook, PotionCore Swift package, Potion macOS app that builds and launches
- [x] M0.2 `core/dotnet` (Potion.Core + xUnit tests), Potion Windows app (WinUI 3, unpackaged), `Potion.slnx`,
      and CI (`.github/workflows/ci.yml`): source scan, macOS and Windows build + tests, Release-app scans.
      (Merged the planned M0.3 into M0.2: the Windows app can only be verified by CI.)

### M1 chunks

- [x] M1.1 Format registry + content detection: `core/spec/formats.json` (48 formats, 72 rules),
      `core/spec/tests/detection.json` (82 shared cases), Swift + C# implementations. Also checked by hand
      against 42 real files generated with ffmpeg/sips/textutil (all correct in both cores)
- [x] M1.2 Conversion matrix: `core/spec/matrix.json` (20 routes, 11 notes), `core/spec/tests/matrix.json`
      (45 shared cases incl. every spec example), Swift + C#. Batches skip files already in the target format.

## Decisions

- License: MIT (see `docs/LEGAL_REVIEW.md`).
- Architecture: native core per platform + shared JSON spec in `core/spec` (see `docs/ARCHITECTURE.md`).
- Checkpoints: Claude commits and pushes, then waits for "continue".
- Dropper app scaffolds (`apps/dropper-*`) are created at the start of M5 rather than in M0, so M0–M4 stay
  focused on Potion.

### M2 chunks

- [x] M2.0 LGPL-only FFmpeg 9.1 build for macOS (`scripts/ffmpeg/`): universal `ffmpeg` + `ffprobe`, pinned
      checksummed sources, license verification in CI, cached between runs
- [x] M2.1 Audio engine (Swift): FFmpeg locator, process runner with progress and cancellation, ffprobe media
      info, copy-protection check, verified save without overwriting, `core/spec/presets.json`,
      `transcodeAudio` + `extractAudio`. 11 end-to-end tests with generated samples
- [x] M2.2 Video engine (Swift): `transcodeVideo` (all 11 containers, stream copy when possible, resolution,
      frame rate, codec, bitrate, aspect ratio, rotation-aware), `videoToGif` (palette), `gifToVideo`. 13 tests
- [x] M2.3 Image engine (Swift): ImageIO decode (incl. RAW, upright via EXIF), SVG via AppKit, resize (fit,
      percent, exact), rotate/flip, white background for JPG/BMP, metadata kept, ICO, WebP + AVIF fallback via
      FFmpeg (libwebp, SVT-AV1 added to the build), images → PDF (single or combined). 12 tests
- [ ] M2.4 Documents: `convertDocument`, `documentToPdf` (engine choice below)
- [ ] M2.5 PDF tools: merge, split, extract, reorder, compress, `pdfToImage`, `pdfToDocument` (PDFKit)
- [ ] M2.6 Windows: FFmpeg build for Windows + the same engines in Potion.Core (C#), tested in CI

## Decisions coming up (from the final spec)

- **FFmpeg (decided 2026-11-21):** OS frameworks first; LGPL-only FFmpeg as separate programs for
  everything else. See `scripts/ffmpeg/README.md` and `docs/LEGAL_REVIEW.md`.
- **M2.4, document engine (OWNER DECISION NEEDED before starting M2.4):** DOC/DOCX/ODT/RTF/EPUB/HTML/MD/TXT
  conversions and documents → PDF, with layout fidelity. Options presented to the owner on 2026-11-21:
  1. **Native first (recommended to start):** macOS uses NSAttributedString (reads/writes TXT, RTF, HTML, DOC,
     DOCX, ODT; reads Markdown) and paginates to PDF with Core Text; EPUB is built/read as zipped XHTML; PDF →
     text/DOCX/HTML uses PDFKit's text. Windows uses the Open XML SDK (MIT) plus a small PDF writer. Small and
     offline, but complex layouts (columns, floating images, headers/footers, tracked changes) are simplified.
  2. **Bundle LibreOffice** (MPL, run as a separate process): best fidelity, but adds roughly 300–600 MB per
     installer. Needs owner approval (spec 6.1.1).
  3. **Native first + optional LibreOffice download** offered the first time a document needs higher fidelity
     (allowed by Prompt 1 §7 "optional dependency updates"; still needs approval for the size and the network use).
  Pandoc (GPL, separate executable) is a weaker fit: good for Markdown/HTML/DOCX/EPUB but no PDF without LaTeX.
- **macOS architectures:** Release builds are universal (Apple Silicon + Intel) unless the owner drops Intel.

## Known issues / TODO

- The M0 window shows the static empty-state drop zone only. Dropping files does nothing until M3.1.
- No Windows machine locally: the WinUI app is verified only in CI until the owner runs it on a PC. The .NET SDK
  (10.0) is installed in `~/.dotnet` so `make test-core-dotnet` runs the shared C# core on macOS.
- Windows M0 window: the min-size uses `OverlappedPresenter.PreferredMinimumWidth/Height` (physical pixels,
  scaled by DPI); check it on a real PC in M4.1. The mono mark in the empty state is added in M4.1.
- Local Xcode is 15.4; keep `projectFormat: xcode15_0` in `project.yml` files.
- Engine tests need the FFmpeg build (`scripts/ffmpeg/build-macos.sh`); they skip locally without it and fail
  in CI (`POTION_REQUIRE_FFMPEG=1`). The app doesn't bundle `ffmpeg`/`ffprobe` yet (M3: `Contents/Helpers`).
- Images: animated GIF/WebP inputs convert their first frame only. SVG rendering relies on AppKit and
  needs a recent macOS (it works on 26; check 13–15 before release, or fall back with a clear error). AVIF via
  the FFmpeg fallback drops transparency.
- Video: 10-bit/HDR sources are converted to 8-bit SDR without tone mapping (colors can look washed out).
  Subtitles are dropped. Revisit after M3.
- Copy-protection detection covers FairPlay/CENC sample entries and FFmpeg's DRM errors; protected WMA/WMV
  needs a real protected sample to confirm (none can be generated). Revisit in M8.
- Open product decision for M3: App Sandbox on or off (see `docs/LEGAL_REVIEW.md` #2).
- Design-system conflict (needs owner decision before M3.1): dark `borderStrong` (#3A3547) is tuned for the
  brand background (#0E0C14), but `09-platforms.md` puts the drop zone on the system window background
  (~#323232 in dark mode), where the dashed border is ~1.1:1 and effectively invisible. Light mode is fine.
- The owner's prompt files (`universal-file-converter-prompt-*.txt`, `universal-converter-*prompt*.md`) are
  gitignored: they describe the personal add-on and must never be committed. Per spec 6.6.5 the final prompt
  belongs in the private repo; `docs/SPEC.md` is the public copy (add-on references removed).
- Screenshots: Screen Recording is granted to VS Code, so Claude can capture app windows to check UI work.
