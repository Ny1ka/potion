# Progress

Read this first in any new session. Update it at every checkpoint.

## Status

- **Current milestone:** M0 Scaffold
- **Last completed chunk:** M0.1 Repo scaffold + Potion macOS app builds
- **Next chunk:** M0.2 Potion Windows app scaffold (WinUI 3) + `core/dotnet`

## Milestones

| Milestone | Status |
|---|---|
| M0 Scaffold | In progress (M0.1 done) |
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
- [ ] M0.2 `core/dotnet` + Potion Windows app (WinUI 3) that builds
- [ ] M0.3 CI: GitHub Actions builds and tests on macOS and Windows runners, runs the source scan

## Decisions

- License: MIT (see `docs/LEGAL_REVIEW.md`).
- Architecture: native core per platform + shared JSON spec in `core/spec` (see `docs/ARCHITECTURE.md`).
- Checkpoints: Claude commits and pushes, then waits for "continue".
- Dropper app scaffolds (`apps/dropper-*`) are created at the start of M5 rather than in M0, so M0–M4 stay
  focused on Potion.

## Known issues / TODO

- `docs/SPEC.md` is incomplete: the pasted spec stops partway through Prompt 1 §3 (Batch Conversion), and
  Prompt 2 (Potion Dropper) is missing. Get the rest from the owner before M3 (Potion UI) and M5 (Dropper).
- The M0 window shows the static empty-state drop zone only. Dropping files does nothing until M3.1.
- No Windows machine locally: Windows builds are verified only in CI until the owner tests on a PC.
- Local Xcode is 15.4; keep `projectFormat: xcode15_0` in `project.yml` files.
- Open product decision for M3: App Sandbox on or off (see `docs/LEGAL_REVIEW.md` #2).
