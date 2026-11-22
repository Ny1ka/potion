# Architecture

## Decision: native core per platform, shared spec (2026-11-21)

The spec asks the four apps to share as much processing logic as practical while feeling native. The codec
and document work is done mostly by OS frameworks (AVFoundation/ImageIO/PDFKit on macOS, Media
Foundation/WIC/Windows.Data.Pdf on Windows), and those are different on each platform anyway. So:

```
                 core/spec/  (JSON: format signatures, conversion matrix, presets, test cases)
                   │                                   │
        copied into resources                copied into resources
                   ▼                                   ▼
   core/swift  PotionCore (Swift package)    core/dotnet  Potion.Core (.NET library)
        │              │                          │               │
 potion-macos   dropper-macos              potion-windows   dropper-windows
```

- **Shared once, as data:** what a file is (magic-byte signatures, extensions, category), which conversions
  are allowed and with which limits, preset values (bitrates, resolutions, quality levels).
- **Shared as behavior, via tests:** `core/spec/tests/*.json` holds input → expected output cases. Both
  cores run them, so detection and matrix behavior can't drift between platforms.
- **Written per platform:** engine wrappers around OS frameworks, the job queue (Swift concurrency vs.
  .NET tasks), file-system integration, UI.

Rejected: one Rust core with C bindings to Swift and C#. It shares more code, but adds a toolchain, two
binding layers and harder debugging, while the OS-framework engines would still need per-platform code.

## Conversion engines (macOS, `core/swift/Sources/PotionCore/Engines`)

| Engine | Handles | Built on |
|---|---|---|
| `AudioEngine` | `transcodeAudio`, `extractAudio` | Bundled LGPL `ffmpeg`/`ffprobe` run as separate processes; AAC/ALAC via AudioToolbox |
| `VideoEngine` | `transcodeVideo`, `videoToGif`, `gifToVideo` | Same FFmpeg; H.264/HEVC via VideoToolbox |
| `ImageEngine` | `convertImage`, `imageToPdf` | ImageIO, AppKit (SVG), Core Graphics PDF; FFmpeg only for WebP and the AVIF fallback |
| `DocumentEngine` | `convertDocument`, `documentToPdf` | The macOS text system (`NSAttributedString` readers; RTF/DOC/ODT/HTML writers), TextKit pagination for PDF, Foundation's Markdown parser; our own DOCX and EPUB writers and EPUB reader on a small zip implementation (`Zip.swift`, Compression framework) |
| `PDFEngine` | `pdfToImage`, `pdfToDocument`, merge/split/extract/reorder/compress | PDFKit, Core Graphics, ImageIO; documents written through `DocumentEngine`'s writers |

Apps call `ConversionService`, which picks the engine from the matrix operation and passes the per-category
options (`ConversionSettings`). Every engine checks the matrix first, writes to a hidden temporary file in the destination folder, verifies
the result by opening it again, then moves it to a name that doesn't exist yet (`OutputFiles`). Documents
are read without network access: remote images, stylesheets, frames and scripts are removed from HTML (and
Markdown rendered to HTML) before the system HTML reader sees it (`HTMLResources`).

## Potion Dropper core (`PotionCore/Dropper`, `Potion.Core/Dropper`)

- `SourceRegistry` loads the allowlist (`core/spec/sources.json`) and pairs each source with its adapter. Adapters
  live in `Dropper/SourceAdapters` (the only folder name the source scan allows for them) and are listed in
  `SourceAdapters.builtIn` / `SourceAdapters.BuiltIn`. Loading fails if an adapter has no allowlist entry.
- Links are parsed by our own code (`LinkSyntax`), not the platform URL parser, so both apps normalize, refuse and
  de-duplicate identically (`core/spec/tests/links.json`). The result is a `DropLink`: normalized link, source id,
  format from the file name, and whether the server's answer must confirm it's media.
- `Downloader` runs one download: robots.txt per site, redirects re-checked with `SourceRegistry.redirect`,
  retries, resume, the size limit and the content check (`DownloadRules`), into a hidden `.part` file. It talks to
  the network only through `HTTPTransport` / `IHttpTransport` (one GET, no automatic redirects, cookies or
  credentials), so tests script the server and the apps plug in the OS HTTP stack: `URLSessionTransport` (macOS) and
  `HttpClientTransport` (Windows), both without cookies, credentials, caches or automatic redirects.
  FFmpeg is built without network support and only ever sees local files.

## Project generation

- macOS apps: XcodeGen (`apps/*/project.yml`), so project files are reviewable text and the generated
  `.xcodeproj` isn't committed. `projectFormat: xcode15_0` keeps it readable by Xcode 15.4.
- Windows apps: SDK-style `.csproj` for WinUI 3 / Windows App SDK 2.x on .NET 10 (LTS), in `Potion.slnx`.
  `Potion.Core` targets plain `net10.0` so its tests run on macOS too; Windows-only engine code goes in the
  app or a separate `net10.0-windows` project.

## Identifiers

| | Bundle ID / package name |
|---|---|
| Potion (macOS) | `com.ny1ka.potion` |
| Potion Dropper (macOS) | `com.ny1ka.potion-dropper` |

These can change freely until the first signed release (M9). After that they're permanent.
