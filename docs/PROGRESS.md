# Progress

Read this first in any new session. Update it at every checkpoint.

## Status

- **Current milestone:** M2.7 Windows engines, then M4 Potion Windows UI (M3 Potion macOS UI is done)
- **Last completed chunk:** M2.7d-2b DOCX, ODT and RTF readers (C#)
- **Next chunk:** M2.7d-3 Windows document writers + document → PDF (MigraDoc)

## Milestones

| Milestone | Status |
|---|---|
| M0 Scaffold | Done |
| M1 Core: formats | Done |
| M2 Core: conversion engines | macOS done (M2.0–M2.6); M2.7 Windows runs before M4 |
| M3 Potion macOS UI | Done (Quick Action extension deferred to M9, see below) |
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
- [x] M2.4 Documents I (Swift): `DocumentEngine` for `convertDocument` + `documentToPdf` with the macOS text
      system. Reads TXT (UTF-8/UTF-16/Windows Latin 1), RTF, DOC, DOCX, ODT, HTML (remote resources removed
      first, so nothing is fetched) and Markdown (Foundation parser → HTML). Writes RTF, DOC, ODT, DOCX (system
      writer for now), self-contained HTML (images embedded), Markdown and TXT (headings, lists, tables kept)
      and PDF (TextKit pages, the document's page size or Letter/A4 by region, searchable text, clickable
      links, oversized images scaled to fit). Encrypted Word files → copy-protected message. 20 tests
- [x] M2.5 Documents II (Swift): zip reader/writer (stored + deflate via the Compression framework, CRC
      checked, 512 MB per-entry cap), our own DOCX writer (heading styles, real hyperlinks, numbered/bulleted
      lists, tables, images, page size and margins, no author metadata), EPUB reader (spine order, linked CSS
      and images inlined) and EPUB 3 writer (nav from headings, images as files), copy-protected message for
      DRM EPUBs (Adobe/LCP; font obfuscation is fine), password-protected ODT and zip-encrypted files. 11 tests
- [x] M2.6 PDF tools (Swift, PDFKit): `PDFEngine` with merge, split (every N pages), extract and reorder
      (shared page-selection syntax, `core/spec/tests/pages.json`), compress (JPEG images; "Smallest" also
      screen resolution; macOS 13.4+; refuses to save a copy that isn't smaller), `pdfToImage` (PNG/JPG at a
      chosen DPI, rotation-aware, size-capped; SVG wraps the rendered page), `pdfToDocument` (DOCX/TXT/HTML
      from PDFKit's text, page breaks in DOCX, pages without text become pictures). Password-protected PDFs
      and permission-restricted ones get the copy-protected message where the permission is missing. 14 tests
- M2.7 Windows engines (moved to run right before M4, owner 2026-11-21), split into:
  - [x] M2.7a LGPL FFmpeg for Windows: `scripts/ffmpeg/build-windows.sh` cross-compiles x86_64 `ffmpeg.exe` and
        `ffprobe.exe` with MinGW-w64 (same libraries plus zlib; Media Foundation for H.264/HEVC/AAC; no capture
        devices; static runtime, so only Windows system DLLs). `verify-windows-build.sh` checks it without running
        it; CI's new "Windows FFmpeg build" job (Linux) caches it and the Windows job runs `verify-license.sh` on
        the real .exe
  - [x] M2.7b Potion.Core (C#): `ConversionException` (same wording as Swift), `FFmpegTools`, `ProcessRunner`
        (argument lists, kills the process tree on cancel), `MediaProbe`, `OutputFiles`, presets from the embedded
        presets.json with per-platform encoders, `AudioEngine` and `VideoEngine` ported from Swift. 18 tests (8 unit,
        10 end-to-end; they use the macOS FFmpeg locally and the Windows build in CI; the Media Foundation test
        runs only where Media Foundation exists)
  - [x] M2.7c Images on Windows: `Potion.Core.Windows` (net10.0-windows) with `WicImageEngine`: WIC decode (EXIF
        orientation, sRGB) and encode (PNG, JPEG, BMP, GIF, TIFF, HEIC), FFmpeg for WebP/AVIF output and as a decode
        fallback, ICO writer, and "install … from the Microsoft Store" messages when HEIF/AV1/WebP/RAW codecs are
        missing. Pixel work (resize, rotate, flip, flatten, icon canvas) is platform-neutral in Potion.Core (6 tests
        here); 6 WIC tests run in the Windows CI job. Images → PDF on Windows comes with M2.7d
  - M2.7d Documents and PDF on Windows (owner chose permissive libraries, 2026-11-21):
    - [x] M2.7d-1 `platforms` in formats.json (DOC is macOS-only); both matrices are built per platform; shared
          cases can name a platform (5 new cases)
    - [x] M2.7d-2a `Potion.Core.Documents`: format-neutral `Document` model (headings, paragraphs with alignment and
          quotes, lists, tables, code, rules, page breaks, styled text runs with links, images) and readers for TXT
          (UTF-8/UTF-16/Windows-1252), HTML (HtmlAgilityPack; nothing fetched, only data: and local images),
          Markdown (Markdig → HTML, like macOS) and EPUB (spine order, images from the book, DRM check). 7 tests,
          run on every platform
    - [x] M2.7d-2b readers for DOCX (Open XML SDK 3.5.1: heading styles/outline levels, lists with numbering,
          tables, hyperlinks incl. HYPERLINK fields, images, run formatting, page setup; encrypted files →
          copy-protected), ODT (content/styles XML: headings, lists with start values, tables, links, images,
          automatic styles, page layout; password-protected → copy-protected) and RTF (own parser: formatting,
          fonts, colors, Unicode/code page, lists, tables, hyperlink fields, PNG/JPEG pictures, page size). 6 tests
    - [ ] M2.7d-3 Writers for the same formats + document → PDF (PDFsharp/MigraDoc)
    - [ ] M2.7d-4 PDF tools and conversions (PDFsharp, PdfPig for text, Windows.Data.Pdf for images), images → PDF

### M3 chunks

- [x] M3.1 Window, drop zone and queue: single `Window` scene, drop zone with valid/invalid drag states,
      Add Files (toolbar, ⌘O, Choose Files…), files opened with Potion (Dock icon, Open With) join the queue,
      `FileCollector` in PotionCore (folders, subfolders, hidden files/packages skipped, duplicates, 5,000
      limit; 3 tests), queue rows (tile, middle-truncated name, format · size, compatible formats grouped by
      category, last-used format per category, status), multi-select, Delete, context menu (Change Format,
      Show in Finder, Remove), banner for files that can't be converted, footer with Clear
- [x] M3.2 Converting from the queue: `ConversionService` in PotionCore routes every matrix operation to its
      engine (4 tests). The app converts two files at a time with per-row progress (category fill), Stop
      (FFmpeg processes are terminated, rows show Canceled, nothing partial is left), Retry, Convert All (⌘↩),
      Save to (next to the original, recent folders, Choose Folder…), Move originals to Trash (only after the
      converted copy is verified and saved), overall progress and "6 files converted · Show in Finder".
      ffmpeg/ffprobe are copied into `Contents/Helpers` and signed by a build phase (required for Release).
      Checked by hand in the app: 6 kinds of conversion, Stop on a 1080p video, Trash, narrow-window footer
- [x] M3.3 Convert sheet (double-click a row, Options… ⌘I, or the context menu; one or several rows) with
      the matrix's limitation notes and Advanced options from presets.json: video (quality or custom bitrate,
      resolution, frame rate, codec, aspect ratio), GIF quality, audio (quality or bitrate, sample rate),
      images (size, quality, rotate, flip), PDF (pages with live validation, resolution, JPG quality); a summary
      line of what will happen; Advanced stays open once opened. Footer "Convert all to" when every row is
      the same kind of file. Codec display names added to presets.json
- [x] M3.4 Settings window (⌘, or the toolbar gear): General (include subfolders, notifications), Output (save
      location, Move originals to Trash, default format per file type), Advanced (1–4 files at once, audio/video
      tools present). File menu: Add Files… ⌘O, Options… ⌘I, Convert All ⌘↩, Stop ⌘., Show in Finder ⌘⇧R.
      Dock badge with files left; a notification with Show when a batch ends in the background (permission is
      asked when the first batch starts, not at launch)
- [x] M3.5 Finder › Services › Convert with Potion (adds the files and opens the convert sheet; "Each File's
      Current Format" for mixed selections), first-launch onboarding sheet (build-up animation with Reduce
      Motion fallback, category tiles, privacy line), quitting when the window closes, and a confirmation
      before quitting mid-batch

## Decisions coming up (from the final spec)

- **FFmpeg (decided 2026-11-21):** OS frameworks first; LGPL-only FFmpeg as separate programs for
  everything else. See `scripts/ffmpeg/README.md` and `docs/LEGAL_REVIEW.md`.
- **Document engine (decided 2026-11-21):** native first (option 1 of 3). macOS uses the system text
  system; Windows will use the Open XML SDK (MIT) plus a small PDF writer. No LibreOffice; revisit only if
  real-world fidelity is not good enough, and only with the owner's approval (spec 6.1.1).
- **App Sandbox (decided 2026-11-21):** off for V1, which ships as a notarized DMG. Hardened Runtime stays on.
  Revisit for a Mac App Store version.
- **Drop zone contrast (decided 2026-11-21):** dark `borderStrong` lightened from #3A3547 to #8A849C (3.6:1 on
  the macOS window background) in `design-system/tokens/tokens.json` and regenerated. Regenerating with a
  different librsvg/ImageMagick re-renders logo PNGs with tiny pixel differences; only text outputs were
  committed because the images don't use this token.
- **Finder integration (2026-11-21):** the design asks for an Action Extension under Quick Actions. macOS only
  loads sandboxed, properly signed app extensions, and local builds are ad-hoc signed until M9, so V1-dev uses a
  Finder Service (Services › Convert with Potion). Add the Action Extension in M9 with Developer ID signing,
  then switch the onboarding text to "Quick Actions › Convert with Potion" (01-brand.md wording).
- **Windows documents (decided 2026-11-21):** permissive libraries, all bundled and offline: Open XML SDK (MIT),
  Markdig (BSD-2-Clause), HtmlAgilityPack (MIT), PDFsharp + MigraDoc (MIT), PdfPig (Apache-2.0); Windows.Data.Pdf
  for rendering. Our own code for RTF, ODT, EPUB, TXT. Each goes into THIRD_PARTY_NOTICES (M8).
- **DOC on Windows (decided 2026-11-21):** macOS only for now (no free, reliable .doc reader/writer on Windows).
  formats.json marks it `"platforms": ["macos"]`; the Windows app never offers it. Revisit if a good library appears.
- **macOS architectures:** Release builds are universal (Apple Silicon + Intel) unless the owner drops Intel.

## Known issues / TODO

- No Windows machine locally: the WinUI app is verified only in CI until the owner runs it on a PC. The .NET SDK
  (10.0) is installed in `~/.dotnet` so `make test-core-dotnet` runs the shared C# core on macOS.
- Windows M0 window: the min-size uses `OverlappedPresenter.PreferredMinimumWidth/Height` (physical pixels,
  scaled by DPI); check it on a real PC in M4.1. The mono mark in the empty state is added in M4.1.
- Local Xcode is 15.4; keep `projectFormat: xcode15_0` in `project.yml` files.
- Engine tests need the FFmpeg build (`scripts/ffmpeg/build-macos.sh`); they skip locally without it and fail
  in CI (`POTION_REQUIRE_FFMPEG=1`). The app bundles it in `Contents/Helpers` (Debug builds without it still
  run, but audio/video report the tools as missing). The universal binaries add ~130 MB to the app; an
  FFmpeg build trimmed to the formats Potion uses (M9) would shrink that.
- Images: animated GIF/WebP inputs convert their first frame only. SVG rendering relies on AppKit and
  needs a recent macOS (it works on 26; check 13–15 before release, or fall back with a clear error). AVIF via
  the FFmpeg fallback drops transparency.
- Video: 10-bit/HDR sources are converted to 8-bit SDR without tone mapping (colors can look washed out).
  Subtitles are dropped. Revisit after M3.
- Copy-protection detection covers FairPlay/CENC sample entries and FFmpeg's DRM errors; protected WMA/WMV
  needs a real protected sample to confirm (none can be generated). Revisit in M8.
- Windows images: SVG input isn't supported yet (macOS uses AppKit; Windows needs Direct2D's SVG renderer, M2.7d or
  M4). Metadata (EXIF/GPS) isn't copied to outputs yet. Animated GIF/WebP use the first frame, as on macOS.
- PDF: SVG output holds a picture of each page (no vector paths or selectable text). PDF → document keeps
  each line as its own paragraph (no reflowing into paragraphs or columns yet) and doesn't run OCR on scans.
  Merge/split drop the PDF outline (bookmarks) of the originals. Compression needs macOS 13.4.
- Documents: the system ODT, RTF and DOC writers drop images. The system DOCX *reader* ignores hyperlinks,
  so links in Word files become plain text when converted to other formats. Imported HTML/Markdown lists
  show numbers without a period ("3" not "3.") and block quotes lose their indent in PDF/DOCX output.
  Headings in Word/ODT files without heading styles are guessed from big bold text. EPUB output is one
  chapter. Our DOCX and EPUB files pass our structure tests but haven't been opened in Microsoft Word,
  Apple Books or an EPUB validator yet: owner check after M3 (or add epubcheck to CI if it's worth it).
- The owner's prompt files (`universal-file-converter-prompt-*.txt`, `universal-converter-*prompt*.md`) are
  gitignored: they describe the personal add-on and must never be committed. Per spec 6.6.5 the final prompt
  belongs in the private repo; `docs/SPEC.md` is the public copy (add-on references removed).
- macOS UI: drag-over states (valid/invalid), the grouped format menus and the footer "Convert all to" menu
  are built to the design docs but weren't captured in screenshots (they need a real drag or click). Check
  them when trying the app. The convert sheet has no size estimate yet ("about 120 MB" in 06-components).
  The completion notification and Dock badge weren't triggered in testing (the first notification shows the
  owner a permission prompt); check them on the first real batch.
- Screenshots: Screen Recording is granted to VS Code, so Claude can capture app windows to check UI work.
