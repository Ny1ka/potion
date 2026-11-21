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
