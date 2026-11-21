# Product spec

Copied from the owner's project prompt (v3). Legal rules and the source policy are summarized in the root
`CLAUDE.md`. **This copy is incomplete:** the pasted prompt stopped partway through Prompt 1 §3, and Prompt 2
(Potion Dropper) is missing. Add them here when the owner provides them.

## Context

Whenever I'm editing videos or doing any file editing, I need to convert files between formats (MP4 to MP3,
MP4 to MOV, DOCX to PDF), and I don't want to use browser converters. The goal is two desktop applications,
each with a macOS and a Windows version: four downloadable apps in total. All four are lightweight,
independently installable, privacy-focused and native to their OS. They are not browser-based applications
or websites wrapped in desktop windows.

1. **Potion (macOS):** native Mac app for converting audio, video, images, documents and PDFs.
2. **Potion (Windows):** native Windows app with the same functionality.
3. **Potion Dropper (macOS):** downloads media from supported sources by URL and converts it into audio/video formats.
4. **Potion Dropper (Windows):** the same on Windows.

The two versions of each product share as much underlying functionality and processing logic as practical
while keeping a genuinely native experience.

---

# Prompt 1: Potion (Universal File Converter)

## 1. Product overview

A lightweight, fully native desktop file converter that converts virtually any common file type into another
compatible format without uploading files to external servers.

Users can:
- Drag and drop individual files into the application.
- Select multiple files and convert them simultaneously.
- Drag and drop entire folders for batch conversion.
- Convert files between different formats with minimal effort.
- Select output quality and conversion settings where applicable.
- Choose where converted files are saved.
- Optionally delete original files after successful conversion.
- Access conversion functionality directly from the operating system's file manager.

Priorities: simplicity, speed, privacy, reliability.

## 2. Supported file categories and formats

### Video

MP4, MOV, MKV, AVI, WEBM, WMV, FLV, MPEG, M4V, 3GP, TS, GIF (video-to-animation).

Users can select: resolution, frame rate, video codec, bitrate, compression level, aspect ratio, quality
presets. Options to preserve original quality where possible or compress to reduce file size.

### Audio

MP3, WAV, M4A, AAC, FLAC, OGG, OPUS, AIFF, WMA, ALAC.

Users can: extract audio from video files; convert between audio formats; adjust bitrate and sample rate;
select quality presets; preserve metadata and album artwork where supported.

### Image

PNG, JPG/JPEG, WEBP, HEIC/HEIF, TIFF, BMP, GIF, SVG, ICO, AVIF, RAW formats where decoders are available.

Users can: convert between formats; resize; compress; change dimensions; preserve transparency when
supported; adjust quality; rotate and flip; process entire image folders in batches. Option to keep original
dimensions or set custom output dimensions.

### Documents

DOCX, DOC, ODT, RTF, TXT, HTML, Markdown, EPUB.

Conversions between compatible formats, including document-to-PDF and PDF-to-document where practical.
Preserve text formatting, headings, tables, images, hyperlinks, page layout and fonts where available.
Formatting accuracy takes priority over simply extracting text.

### PDF

- PDF to DOCX, TXT, HTML, PNG, JPG, and SVG where practical
- Image formats to PDF; DOCX and other compatible documents to PDF

Options: combine multiple PDFs; split PDFs; extract individual pages; reorder pages; compress PDFs; convert
folders of images into individual PDFs or one combined PDF; select specific pages for conversion. Preserve
searchable text and document structure whenever possible.

### Compatibility and conversion matrix

Not every type converts to every other type. The app determines which conversions are supported and shows
only compatible output options. Examples:

| Conversion | Supported |
|---|---|
| MP4 → MP3 | Yes |
| MP4 → MOV | Yes |
| DOCX → PDF | Yes |
| PNG → JPG | Yes |
| PDF → DOCX | Yes, with formatting limitations communicated |
| MP3 → PNG | No |

Never present an incompatible conversion as a valid option.

## 3. Batch conversion and folder support

Batch processing is a core feature. Users can:
- Select multiple files at once.
- Drag and drop an entire folder.
- Automatically detect supported files inside folders.
- Recursively process subfolders when enabled.
- Convert all compatible files … *(the pasted prompt ends here)*

---

# Development instructions (summary of Part 3)

- Names: **Potion** (converter), **Potion Dropper** (downloader). Used everywhere: bundle names, window
  titles, installers, About windows, docs.
- Follow `design-system/CLAUDE.md` exactly. Don't invent visual values that aren't in `tokens/tokens.json`.
- Quality bar per chunk: tests for all core logic; UI chunks include a manual test script; no placeholder
  copy or fake data; errors shown in plain language; files never leave the machine. The only network access
  is Dropper fetching a URL the user entered, plus optional update checks if the owner approves.

## Checkpoint format

Print at the end of every chunk:

```
==================== CHECKPOINT: COMMIT NOW ====================
Milestone / chunk: M2.1 Audio conversion engine
What changed:      <3–6 bullet summary>
Files:             <list of added/changed files>
How to test:       <exact steps to run or click to verify it works>
Build / tests:     <command run + result>
Suggested commit:  feat(core): add audio conversion via engine wrapper
Commands:
  git add -A
  git commit -m "feat(core): add audio conversion via engine wrapper"
  git push
Next chunk:        M2.2 Video conversion engine
================================================================
```

At the end of every milestone, also say to push and merge, and suggest a git tag for releases (e.g. `v0.3.0`).

## Milestones

- **M0 Scaffold:** repo structure, `.gitignore`, root `CLAUDE.md`, design system, empty app projects that build
  and launch on each platform, CI that builds and runs tests on macOS and Windows runners.
- **M1 Core: formats:** file type detection by content, conversion matrix as data, unit tests.
- **M2 Core: conversion engines:** one chunk each for audio, video, image, documents and PDF tools (merge,
  split, extract, reorder, compress), each with tests using generated sample files.
- **M3 Potion macOS UI:** window + drop zone, queue, convert sheet, Advanced options, Settings, Finder Quick
  Action, notifications and Dock progress.
- **M4 Potion Windows UI:** the same, with the File Explorer context menu and taskbar progress.
- **M5 Dropper core:** URL validation, source-adapter system with its allowlist, download manager, hand-off to
  the conversion core.
- **M6 Dropper macOS UI**, then **M7 Dropper Windows UI.**
- **M7.5 Personal module:** handled outside this repository per the owner's spec.
- **M8 Legal and compliance pass:** third-party notices from shipped dependencies, license audit, in-app
  notices, About windows, `LEGAL_REVIEW.md`.
- **M9 Packaging and V1 release:** Developer ID signing and notarization (DMG), Windows code signing
  (MSIX and/or signed installer), versioning, release notes, tag `v1.0.0`. Signing certificates and
  notarization credentials stay on the owner's machine or in protected CI secrets, never in the repo.

## Phase 2 (after V1; don't start until the owner says "Start Phase 2")

Trusted collaborators, a protected `main` branch with CODEOWNERS, PR template, security settings,
`CONTRIBUTING.md`, and automated Claude review of PRs from anyone other than the owner. Keep the repo
compatible: CI checks with stable job names and a CODEOWNERS-ready structure.
