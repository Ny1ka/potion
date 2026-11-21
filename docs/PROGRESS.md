# Progress

Read this first in any new session. Update it at every checkpoint.

## Status

- **Current milestone:** M1 Core: formats
- **Last completed chunk:** M1.1 Format registry + content detection (both cores)
- **Next chunk:** M1.2 Conversion matrix (`core/spec/matrix.json`, both cores)
- **Progress to V1:** 8% (see weights below). First point where the owner can convert real files in an app:
  Potion for macOS after M3.3 (convert sheet), at about 37%.

## Milestones

Weights estimate each milestone's share of the work to V1 (sum 100). Progress = finished milestones plus
the finished share of the current one (by chunks). Report it at every checkpoint.

| Milestone | Weight | Status |
|---|---|---|
| M0 Scaffold | 5 | Done |
| M1 Core: formats | 5 | In progress (M1.1 done) |
| M2 Core: conversion engines | 20 | Not started |
| M3 Potion macOS UI | 15 | Not started |
| M4 Potion Windows UI | 15 | Not started |
| M5 Dropper core | 8 | Not started |
| M6 Dropper macOS UI | 7 | Not started |
| M7 Dropper Windows UI | 7 | Not started |
| M7.5 Personal experimental module (private repo) | 3 | Not started |
| M8 Legal and compliance pass | 5 | Not started |
| M9 Packaging and V1 release | 10 | Not started |

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
- [ ] M1.2 Conversion matrix: `core/spec/matrix.json` with limitation notes, Swift + C#, shared tests

## Decisions

- License: MIT (see `docs/LEGAL_REVIEW.md`).
- Architecture: native core per platform + shared JSON spec in `core/spec` (see `docs/ARCHITECTURE.md`).
- Checkpoints: Claude commits and pushes, then waits for "continue".
- Dropper app scaffolds (`apps/dropper-*`) are created at the start of M5 rather than in M0, so M0–M4 stay
  focused on Potion.

## Decisions coming up (from the final spec)

- **M2.1/M2.2, FFmpeg:** Prompt 1 §6 lists formats AVFoundation and Media Foundation can't write (MKV, WEBM,
  AVI, WMV, FLV, OGG, OPUS, WMA) and says users must never install dependencies themselves. Plan: OS
  frameworks first; bundle an LGPL-only FFmpeg (no x264/x265; H.264/HEVC/AAC through the OS encoders via
  `h264_videotoolbox` / `h264_mf`) for everything else. Confirm with the owner before adding it (spec 6.1.1).
- **M2.4, document engine:** DOC/DOCX/ODT/RTF/EPUB ↔ PDF with layout fidelity. Options: LibreOffice headless
  as a separate process (MPL, large install: needs owner approval per 6.1.1), Pandoc as a separate executable
  (GPL; weaker layout fidelity), or OS-native paths only (macOS NSAttributedString/WebKit printing; limited
  on Windows). Present the trade-off to the owner at M2.4.
- **macOS architectures:** Release builds are universal (Apple Silicon + Intel) unless the owner drops Intel.

## Known issues / TODO

- The M0 window shows the static empty-state drop zone only. Dropping files does nothing until M3.1.
- No Windows machine locally: the WinUI app is verified only in CI until the owner runs it on a PC. The .NET SDK
  (10.0) is installed in `~/.dotnet` so `make test-core-dotnet` runs the shared C# core on macOS.
- Windows M0 window: the min-size uses `OverlappedPresenter.PreferredMinimumWidth/Height` (physical pixels,
  scaled by DPI); check it on a real PC in M4.1. The mono mark in the empty state is added in M4.1.
- Local Xcode is 15.4; keep `projectFormat: xcode15_0` in `project.yml` files.
- Open product decision for M3: App Sandbox on or off (see `docs/LEGAL_REVIEW.md` #2).
- Design-system conflict (needs owner decision before M3.1): dark `borderStrong` (#3A3547) is tuned for the
  brand background (#0E0C14), but `09-platforms.md` puts the drop zone on the system window background
  (~#323232 in dark mode), where the dashed border is ~1.1:1 and effectively invisible. Light mode is fine.
- The owner's prompt files (`universal-file-converter-prompt-*.txt`, `universal-converter-*prompt*.md`) are
  gitignored: they describe the personal add-on and must never be committed. Per spec 6.6.5 the final prompt
  belongs in the private repo; `docs/SPEC.md` is the public copy (add-on references removed).
- Screenshots: Screen Recording is granted to VS Code, so Claude can capture app windows to check UI work.
