# Progress

Read this first in any new session. Update it at every checkpoint.

## Status

- **Current milestone:** M0 Scaffold
- **Last completed chunk:** M0.2 `core/dotnet` + Potion Windows app + CI
- **Next chunk:** M1.1 Format registry and content detection (`core/spec/formats.json`, both cores)

## Milestones

| Milestone | Status |
|---|---|
| M0 Scaffold | Done |
| M1 Core: formats | Not started |
| M2 Core: conversion engines | Not started |
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

### M1 chunks (planned)

- [ ] M1.1 Format registry + content detection: `core/spec/formats.json`, shared test cases, Swift + C#
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
