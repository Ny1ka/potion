# Progress

Read this first in any new session. Update it at every checkpoint.

## Status

- **Current milestone:** M9 Packaging and V1 release: macOS beta first (M2–M8 done)
- **Last completed chunk:** M9.1 Versioning, macOS beta release workflow, DMGs, FFmpeg source bundle, release gate
- **Next chunk:** run the first macOS beta build; polish from beta feedback (Windows installer after real-PC testing)

## Milestones

| Milestone | Status |
|---|---|
| M0 Scaffold | Done |
| M1 Core: formats | Done |
| M2 Core: conversion engines | Done (macOS M2.0–M2.6, Windows M2.7a–d) |
| M3 Potion macOS UI | Done (Quick Action extension deferred to M9, see below) |
| M4 Potion Windows UI | Done (Win11 top-level Explorer menu deferred to M9, see below) |
| M5 Dropper core | Done |
| M6 Dropper macOS UI | Done (Share extension deferred to M9, see M6.3) |
| M7 Dropper Windows UI | Done (share target deferred to M9) |
| M7.5 Personal experimental module (private repo) | Done (public part: the Debug-only loading point) |
| M8 Legal and compliance pass | Done (lawyer review items in LEGAL_REVIEW) |
| M9 Packaging and V1 release | In progress (macOS beta first; owner's plan in LEGAL_REVIEW) |

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

## Notes for later chunks

- Done in M5.3b: the Dropper macOS app sets `NSAppTransportSecurity` › `NSAllowsArbitraryLoads`, so plain
  `http://` direct links aren't blocked by App Transport Security.
- Done in M6.1: conversion errors take the product name, so Dropper says "Potion Dropper".
- M6/M7: direct links carry no thumbnail or duration, so the media card shows the category tile and the duration
  limit is applied after downloading (core/spec/README.md, "Jobs"). Sources that publish metadata can fill both.
- M6/M7: the design system's segmented control puts a 20-pt category tile in each segment; macOS segmented pickers
  only take images, so the segments use the category's SF Symbol with the label.

## Decisions

- License: MIT (see `docs/LEGAL_REVIEW.md`).
- Architecture: native core per platform + shared JSON spec in `core/spec` (see `docs/ARCHITECTURE.md`).
- Checkpoints: Claude commits and pushes, then waits for "continue".
- Dropper app scaffolds (`apps/dropper-*`) are created at the start of M5 rather than in M0, so M0–M4 stay
  focused on Potion.

### M9 chunks (owner's plan, 2026-11-22: unsigned beta first, buy nothing yet, macOS before Windows)

- [x] M9.1 `VERSION` (0.9.0) as the single version, written into every project by `scripts/release/version.sh`
      (checked in CI and the release workflow); `.github/workflows/release.yml` (run by hand): Release-only build from
      a clean checkout, release gate checks (FFmpeg license, versions, notices, source and artifact scans), unsigned
      DMGs for both macOS apps (`scripts/release/make-dmg.sh`), the FFmpeg LGPL source bundle
      (`scripts/release/ffmpeg-sources.sh`: every pinned archive, checksum-verified, plus build scripts and
      BUILDINFO), notices, Terms, Privacy, `docs/BETA.md` and SHA256SUMS, uploaded as a 30-day workflow artifact
      (not a public release); release gate checklist in LEGAL_REVIEW
- [ ] M9.2 Beta round on macOS: fixes and polish from testers (incl. the About splash animation)
- [ ] M9.3 Windows: test both apps on a real PC, trim the Windows App SDK to the components the apps use, EXE
      installer (tool to be chosen and license-checked), Windows beta
- [ ] M9.4 When the owner decides: Developer ID signing and notarization, Windows signing, `v1.0.0`

### M8 chunks

- [x] M8.1 `legal/third-party.json` (21 components: FFmpeg and its 7 libraries, zlib and the MinGW-w64 runtime on
      Windows, Space Grotesk, the .NET runtime, the Windows SDK projection, the Windows App SDK with WebView2 and the
      Windows ML runtime, Open XML SDK, Html Agility Pack, PdfPig, PDFsharp/MigraDoc, Microsoft .NET libraries; which
      apps ship each), license texts in `legal/licenses` (from source archives, packages and repositories at the
      shipped tags; scanned clean), `scripts/legal/notices.py` generating `legal/THIRD_PARTY_NOTICES.md` and checking
      it in CI (up to date; FFmpeg versions match `versions.sh`; every package a Windows Release build ships is
      listed). License audit and new lawyer items in LEGAL_REVIEW
- [x] M8.2 `legal/PRIVACY.md` (what each app does and doesn't collect, from the code: Potion makes no network
      connections; Dropper contacts only the links you give it and their `robots.txt`, honest User-Agent, no cookies;
      local settings and the optional, clearable history; no analytics, telemetry or crash reporting),
      `legal/TERMS.md` (responsibility for files and downloads, no warranty, limitation of liability, third-party
      components with protection for their licensors as the Windows App SDK license requires, trademarks, takedown
      requests, changes), contact nyikabusiness@gmail.com, `legal/LICENSE` (copy of `/LICENSE`, checked in CI)
- [x] M8.3 About windows in all four apps (shared code in `apps/shared/macos/Legal.swift` and
      `apps/shared/windows/Legal.cs`): mark at 96, product name in the brand face at 28, version, copyright, privacy
      line (Potion: the spec Part 6.2 note; Dropper: its privacy line plus the download note), Terms / Privacy /
      Acknowledgements opening in-app viewers (plain text, so license texts stay exact; the 1 MB of notices scrolls
      fine), FFmpeg/LGPL attribution with a link to the sources and build scripts, source code and contact. The
      legal files are bundled from `legal/` (macOS resources; a `Legal` folder on Windows). macOS: About replaces the
      app-menu item, Help lists the documents. Windows: About, Terms and Privacy in the command bar's overflow menu.
      Terms at first launch: Potion macOS onboarding now has the spec Part 6.2 note word for word plus "By
      continuing, you agree to the Terms of Use and Privacy Policy"; Potion Windows gets a one-time first-run dialog
      with the same; both Dropper acknowledgments add the agreement line. The About window shows the static mark (the
      splash animation in 06-components.md is left for later polish)

### M7.5

- [x] Generic, Debug-only external source loading point in both cores and both Dropper apps (`#if DEBUG`): source
      folders outside the repo with `source.json` + a program run as a separate process (JSON-line protocol; its
      file must be inside the download folder, then the usual content check, duration limit and conversion); links a
      source handles are queued without Fetch; window title badge "PERSONAL BUILD: EXPERIMENTAL SOURCES ENABLED" and
      the acknowledgment's extra line (asked again) when a source is loaded. Release builds contain none of it
      (checked locally on the macOS Release app and the Release `Potion.Core.dll`); `forbidden-scan.sh --artifact`
      now fails on loader symbols. 3 tests per core with a script standing in for an external program. Everything
      else is handled outside this repository

### M7 chunks

- [x] M7.1 `DropQueue` in Potion.Core (C#, same behavior as Swift: parallel limit, no duplicate links, Stop keeps a
      resumable partial file and Retry continues it, Remove, Clear finished, reorder, overall progress, events;
      bindable `DropQueueItem` with the macOS status wording; `DropText.Detail`; 7 tests on a UI-like thread).
      Windows app: first-run acknowledgment `ContentDialog` (Continue enabled by "I understand"; Quit exits), link bar
      per 06-components.md (40-px sunken well, Fetch inside, "Paste link" when the clipboard holds a link, error
      caption and border), loading skeleton, media card, batch card ("Download all"), Video/Audio as a two-button
      segmented control with 20-px category tiles, Format and Quality/Bitrate, queue list (tile, name, status,
      category-colored progress, Stop / Retry / Show in folder, right-click menu with Copy link and Remove, drag to
      reorder), header with overall progress, Stop all, Clear finished; history recorded for M7.2's window
- [x] M7.2 Settings window (same options and keys as macOS: notifications, downloads at once, keep a history and
      Clear history, save folder, keep the original, naming template with an example, largest download, length
      limit), Download history window (Ctrl+H; Show in folder, Copy link, Remove, Clear history with confirmation),
      notifications with Show in folder when a batch ends in the background, taskbar progress, links dropped on the
      window, confirmation before closing mid-download. Deferred: Windows share target (needs package identity, M9);
      the optional tray icon (off by default per 06-components.md, so not in V1)

### M6 chunks

- [x] M6.1 Core: error messages take the product name (`ConversionError.message(product:)`,
      `ConversionException.MessageFor`), `Downloader.inspect` / `InspectAsync` (same checks as a download, reads only
      the first bytes), `DropChoices` (modes, offered formats, default; 6 shared cases). macOS app: first-run
      acknowledgment sheet (spec Part 6.3.5 text, "I understand" checkbox, Continue / Quit), link bar per
      06-components.md (sunken well, Fetch inside, "Paste Link" chip when the clipboard holds a link, error caption
      and border), media card (16:9 well with the category tile, name, format · size · "Direct link from <site>"),
      loading skeleton, Video/Audio segmented control, Format (with "as downloaded"), Quality / Bitrate, Download with
      progress, speed and time left, Stop, "Saved …" with Show in Finder; saves to Downloads
- [x] M6.2 `DropQueue` in PotionCore (up to 4 at once, default 2; skips links already waiting or downloading; Stop
      keeps a resumable partial file and Retry continues it; Remove deletes it; Clear Finished; reorder; overall
      progress; per-item and per-batch callbacks; 7 tests). macOS: Download adds to the queue and clears the link bar;
      several pasted links show a batch card ("15 links", one mode and format for all, "As downloaded" option,
      Download All); queue list with category tile, name, status (bytes, speed, time left, converting %, saved name or
      error), per-row progress in the category color, Stop / Retry / Show in Finder buttons, context menu with Remove,
      drag to reorder; header with "2 of 5 · 40%", overall bar, Stop All, Clear Finished; Download History window
      (⌘Y; finished and failed downloads, Show in Finder, Copy Link, Remove, Clear History with confirmation);
      notifications when a batch ends in the background (permission asked at the first download); Dock badge with
      downloads left; quitting mid-download asks first
- [x] M6.3 Settings window (General: notifications, downloads at once, keep a history, Clear History; Output: save
      folder, keep the original, naming template with a live example; Advanced: largest download 1–32 GB or no limit,
      length limit on/off and 5–240 minutes) applied to downloads queued afterwards (each queued item keeps its
      folder); links dropped on the window are fetched (accent outline while dragging); Services › "Download with
      Potion Dropper" for a link selected in any app. Deferred: a Share extension (needs real signing, M9, like
      Potion's Quick Action); dropping on the Dock icon (would mean declaring http/https URL handling, which offers
      Dropper as a browser); the optional menu bar extra (off by default per 06-components.md, so not in V1); About
      with Terms/Privacy/Acknowledgements (M8, for both apps)

### M5 chunks

- [x] M5.1 Links and the source allowlist: `core/spec/sources.json` (one source: direct media links; limits, honest
      User-Agent, error messages), `docs/SOURCES.md` (policy and the `direct` entry), `core/spec/tests/links.json`
      (58 link + 7 paste cases). Swift and C#: our own link parser (normalizes scheme, host incl. Punycode and IPv6,
      port, percent-encoding; refuses credentials, non-web schemes, playlists, non-media files), finding links in
      pasted text (punctuation, duplicates, 100-link limit), `SourceRegistry` with adapters in `SourceAdapters`
      folders (refuses to load an adapter missing from sources.json; tests check every source has a SOURCES.md
      entry with the same review date)
- [x] M5.2a Download rules (Swift + C#, `core/spec/tests/downloads.json`: 52 server answers, 10 content checks,
      22 file names, 7 robots statuses, 20 robots files): what to do with each status (redirects, retries with
      `Retry-After`, resume restarts, access errors), content-type groups and the size limit, the content check that
      every body passes before conversion, safe file names from `Content-Disposition` or the link, robots.txt
      parsing and matching (RFC 9309) for the `PotionDropper` token, download error messages
- [x] M5.2b Downloader logic (Swift + C#) behind an `HTTPTransport` / `IHttpTransport` seam: honest User-Agent,
      `Accept-Encoding: identity`, robots.txt once per site (redirects followed, 5xx blocks), redirects resolved per
      RFC 3986 and re-checked against the allowlist (15 shared cases in `links.json`), retries with Retry-After,
      size limit before and while streaming, the content check on every body, hidden `.potion-dropper-*.part` file,
      resume with Range/If-Range (strong ETag or Last-Modified; 200 or 416 start over), cancel keeps a resumable
      partial file. 14 tests per core with a scripted transport
- [x] M5.2c Real transports: `URLSessionTransport` (ephemeral session per request; no cookie storage or cookie
      headers, no credential storage, no cache; redirects handed back; sign-in challenges refused so a 401 comes back
      as is; idle timeout) and `HttpClientTransport` (SocketsHttpHandler: no auto-redirect, no cookies, no
      credentials, no decompression; header and per-read idle timeouts). Loopback test servers (Network.framework,
      TcpListener) with 8 tests per core: streaming, our headers, redirects not followed, cookies not stored or sent,
      401 passed through, timeout, connection refused, Downloader end to end (robots.txt, redirect, content check)
      and resume after a dropped connection
- [x] M5.3a Dropper jobs (Swift + C#): `DropJobRunner` (download → ffprobe → duration limit, default 30 minutes,
      and copy-protection check → keep or convert with the regular engines → unique final name; hidden work folder
      kept only for resumable failures), naming templates (`{title}`, `{site}`, `{date}`, `{format}`; 13 shared
      cases), `DropHistory` (JSON, newest first, 500 entries, remove and clear), `MediaConverting` /
      `IMediaConverter` with `FFmpegMediaConverter` (C#, audio/video only). 8 job tests per core incl. a real FFmpeg
      run (WAV link → MP3)
- [x] M5.3b Dropper app scaffolds: `apps/dropper-macos` (XcodeGen, SwiftUI; `com.ny1ka.potion-dropper`; Dropper
      icon and colors from the design system; FFmpeg bundled like Potion; `NSAllowsArbitraryLoads` for `http://`
      direct links) and `apps/dropper-windows/PotionDropper` (WinUI 3, unpackaged; Potion.Core only; FFmpeg folder),
      both with the link bar from 05-layout.md (Paste, Fetch, Return/Enter): Fetch checks the links against the
      allowlist and shows the format and site or the error message. CI builds Debug and Release of both and scans
      the Release apps; `Potion.slnx` and the Makefile (`make dropper-macos`, `make run-dropper-macos`) include them

### M4 chunks

- [x] M4.1 `Potion.Core.App` (platform-neutral, tested on macOS): `FileCollector` (C#, same rules as Swift),
      `QueueModel`/`QueueItem` (bindable; last-used format per category in `%LOCALAPPDATA%\Potion\settings.json`;
      banner wording as on macOS), `DropState`, File Explorer-style sizes (5 tests). WinUI window: drop zone with
      valid/invalid drag states, Add files (Ctrl+O) / Add folder, files from the command line, queue rows (tile, name,
      details, format menu grouped by category, status with icon), Delete, right-click Change format / Show in folder /
      Remove, InfoBar for files that can't be converted, footer with Clear. Built by CI only (no local Windows)
- [x] M4.2 Queue runner in `Potion.Core.App` (same behavior as macOS: two at a time, adjustable 1–4, per-row and
      overall progress, Stop, Retry, summary, Recycle Bin only after a verified save; 5 tests on a UI-like thread).
      Windows app: ConversionService plugged in, FFmpeg copied into an `ffmpeg` folder next to Potion.exe (required for
      Release), footer with Save to (same folder / recent / Choose folder…), Move originals to Recycle Bin, Convert all
      (Ctrl+Enter), progress + Stop (Esc), "12 files converted · Show in folder", taskbar progress (error state after
      failures), Retry and Show in folder for converted files, confirmation before closing mid-batch
- [x] M4.3 `OptionsKind`/`OptionsText` in Potion.Core.App (which controls, summary line, notes, page hints; same as
      macOS; 3 tests) and per-row `ConversionSettings` passed to the engines. Windows `ConvertDialog` (ContentDialog:
      header, Convert to with "Each file's current format", notes, Advanced expander remembered, Recycle Bin choice),
      opened by double-click, Options… in the context menu or Ctrl+I; footer "Convert all to" for same-kind queues
- [x] M4.4 Settings window (General: subfolders, notifications, File Explorer menu; Output: save location, Recycle
      Bin, default format per file type from `DefaultFormats`; Advanced: files at once, tools present), completion
      notifications with Show in folder (Windows App SDK, unpackaged registration) when Potion is in the background,
      "Convert with Potion" in File Explorer's classic menu (per-user registry, toggled in Settings), single instance
      (`Program.cs`: a second launch hands its files to the running window)

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
          Markdown (our `MarkdownRenderer` → HTML, like macOS) and EPUB (spine order, images from the book, DRM check). 7 tests,
          run on every platform
    - [x] M2.7d-2b readers for DOCX (Open XML SDK 3.5.1: heading styles/outline levels, lists with numbering,
          tables, hyperlinks incl. HYPERLINK fields, images, run formatting, page setup; encrypted files →
          copy-protected), ODT (content/styles XML: headings, lists with start values, tables, links, images,
          automatic styles, page layout; password-protected → copy-protected) and RTF (own parser: formatting,
          fonts, colors, Unicode/code page, lists, tables, hyperlink fields, PNG/JPEG pictures, page size). 6 tests
    - [x] M2.7d-3a writers for TXT, Markdown, HTML (XHTML-capable, images embedded) and EPUB 3 (nav from headings,
          images as files), and `DocumentEngine` (C#: matrix check, temp file, verify, unique name). PDFsharp-MigraDoc
          6.5.0 and PdfPig 0.1.16 added (PDFsharp later removed as AGPL, see Decisions). `make test-core-dotnet` now
          scans every .NET dependency. 5 tests
    - [x] M2.7d-3b writers for DOCX (same structure as the macOS writer: heading styles, hyperlinks, per-list
          numbering with start values, tables, images, page setup, no author), RTF (font/color tables, list table
          with \ls/\listtext, \outlinelevel headings, tables, hyperlink fields, PNG/JPEG pictures, 7-bit output) and
          ODT (automatic styles, heading styles, list styles with start values, tables, inline images, links, page
          layout). Each round-trips through our readers (3 tests); RTF reader now reads \outlinelevel headings and
          ODT nested lists keep their own type
    - [x] M2.7d-3c document → PDF with PDFsharp/MigraDoc **6.2.4 pinned** (last MIT release; owner's choice
          2026-11-22): page size and margins, headings with PDF bookmarks, alignment, styled text, clickable links,
          list markers with their own numbers, bordered tables, images scaled to fit, code, page breaks; `PdfFonts`
          finds system TrueType fonts on Windows/macOS/Linux with fallbacks. Verified with PdfPig (2 tests)
    - [x] M2.7d-4a `PageSelection` in C# (passes core/spec/tests/pages.json), `PdfTools`: merge, split, extract,
          reorder, compress (best stream compression; images aren't recompressed on Windows) with PDFsharp 6.2.4,
          and PDF → DOCX/TXT/HTML via PdfPig text in reading order (page breaks between pages, page selection).
          Any encrypted PDF (password or owner restrictions) gets the copy-protected message. 5 tests
    - [x] M2.7d-4b `WindowsPdfEngine` (Potion.Core.Windows): PDF → PNG/JPG/SVG with Windows.Data.Pdf (resolution,
          pixel cap, page selection), pictures of scanned pages for PDF → document, images → PDF (WIC decode,
          PDFsharp pages at 72 dpi); `ConversionService` routes every matrix operation to its Windows engine.
          Shared `PdfPresets`, `PdfOptions`, `ConversionSettings` in Potion.Core. 3 Windows-only tests (CI)

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
  HtmlAgilityPack (MIT), PdfPig (Apache-2.0); Windows.Data.Pdf for rendering. PDFsharp/MigraDoc were removed on
  2026-11-22: version 6.5.0 is AGPL-3.0-only (MIT up to 6.2.4); the owner chose to pin **6.2.4** (frozen, MIT), with
  System.Security.Cryptography.Pkcs pinned to 11.0.0 over its vulnerable 8.0.1. Never upgrade PDFsharp/MigraDoc past
  6.2.4 without checking the license. Always read a package's license from its nuspec before adding it. Our own
  code for Markdown, RTF, ODT, EPUB, TXT. Each goes into THIRD_PARTY_NOTICES (M8).
  Markdig was tried and dropped (2026-11-22): its media-links extension embeds the names of video sites the source
  policy blocks, and CI's release scan caught them in the Windows build. Only a package reference was ever
  committed (no blocked strings in the repo or any published artifact). `MarkdownRenderer.cs` replaces it.
  Every new dependency must pass `scripts/forbidden-scan.sh --artifact` on its DLLs before it's added.
- **DOC on Windows (decided 2026-11-21):** macOS only for now (no free, reliable .doc reader/writer on Windows).
  formats.json marks it `"platforms": ["macos"]`; the Windows app never offers it. Revisit if a good library appears.
- **Windows File Explorer menu (2026-11-22):** Windows 11 shows third-party commands in its top-level menu only for
  apps with package identity (MSIX or a sparse package, both need signing). Until M9, Potion adds "Convert with
  Potion" to the classic menu (Show more options on Windows 11, top level on Windows 10) via per-user registry keys.
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
- Windows documents: table column alignment from Markdown/HTML isn't carried over yet; strikethrough isn't drawn
  in PDFs (MigraDoc has no strikethrough); CJK text in PDFs needs a CJK font, which the resolver doesn't look for yet.
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
