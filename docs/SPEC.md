# Product spec

Copied from the owner's final project prompt. Prompts 1–2 and Part 3 are reproduced in full, except that
references to the personal add-on (spec Part 6.6) are left out because they must not appear in this repo.
Parts 4–6 (workflow, collaboration, legal) are summarized at the end of this file and in the root
`CLAUDE.md`. **Where Parts 4–6 conflict with Prompts 1–2 or Part 3, Parts 4–6 win.** In particular, Part 6
decides which download sources ship and prefers OS frameworks over bundled libraries.

---

## Context

Whenever I'm editing videos or doing any file editing or stuff like that, I always find that I need to convert files between different formats (MP4 to MP3, MP4 to MOV, DOCX to PDF), and I don't want to use browser converters and such. My plan is to build my own file converter that has 4 separate downloads available.

The goal is to develop **two separate desktop applications**, each with its own macOS and Windows version, resulting in four downloadable applications total.

All four applications should be lightweight, independently installable, privacy-focused, and designed to feel native to their respective operating systems. They should not be browser-based applications or websites wrapped inside desktop windows.

The four downloadable applications are:

1. **Universal File Converter – macOS:** A native Mac application for converting files between different formats, including audio, video, images, documents, and PDFs.
2. **Universal File Converter – Windows:** A native Windows application offering the same functionality as the macOS version.
3. **Media Downloader & Converter – macOS:** A native Mac application that downloads media from supported online video platforms using a URL and converts it into different audio/video formats.
4. **Media Downloader & Converter – Windows:** A native Windows application offering the same functionality as the macOS version.

Each application should be developed as a separate product with its own downloadable installer. The two versions of each product should share as much underlying functionality and processing logic as practical while maintaining a genuinely native operating-system experience.

**Product names:** the Universal File Converter is called **Potion**, and the Media Downloader & Converter is called **Potion Dropper**. A complete design system for both is provided (see Part 4.1).

**How to read this prompt:** Prompts 1 and 2 describe what the apps do. Part 3 covers shared requirements. Parts 4–6 at the end add development workflow, collaboration and legal requirements. Where Parts 4–6 are more specific than, or conflict with, Prompts 1–2 or Part 3, **Parts 4–6 take precedence**. In particular, Part 6 decides which download sources ship in the public apps.

---

# PROMPT 1: Universal File Converter

## 1. Product Overview

Build a lightweight, fully native desktop file converter that allows users to convert virtually any common file type into another compatible format without uploading files to external servers or using browser-based conversion websites.

The application should function as a downloadable desktop utility that integrates naturally into the operating system.

Users should be able to:
- Drag and drop individual files into the application.
- Select multiple files and convert them simultaneously.
- Drag and drop entire folders for batch conversion.
- Convert files between different formats with minimal effort.
- Select output quality and conversion settings where applicable.
- Choose where converted files are saved.
- Optionally delete original files after successful conversion.
- Access conversion functionality directly from the operating system's file manager.

The application should prioritize simplicity, speed, privacy, and reliability.

## 2. Supported File Categories and Formats

Support as many commonly used formats as reasonably possible across the following categories.

### Video Conversion

Support:
- MP4
- MOV
- MKV
- AVI
- WEBM
- WMV
- FLV
- MPEG
- M4V
- 3GP
- TS
- GIF (video-to-animation conversion)

Allow users to convert between compatible video formats and select:
- Resolution
- Frame rate
- Video codec
- Bitrate
- Compression level
- Aspect ratio
- Quality presets

Include options for preserving the original quality where possible or compressing videos to reduce file size.

### Audio Conversion

Support:
- MP3
- WAV
- M4A
- AAC
- FLAC
- OGG
- OPUS
- AIFF
- WMA
- ALAC

Allow users to:
- Extract audio from video files.
- Convert audio between supported formats.
- Adjust bitrate and sample rate.
- Select audio quality presets.
- Preserve metadata and album artwork where supported.

### Image Conversion

Support:
- PNG
- JPG / JPEG
- WEBP
- HEIC / HEIF
- TIFF
- BMP
- GIF
- SVG
- ICO
- AVIF
- RAW formats where supported by available decoders.

Allow users to:
- Convert between image formats.
- Resize images.
- Compress images.
- Change image dimensions.
- Preserve transparency when supported.
- Adjust image quality.
- Rotate and flip images.
- Process entire image folders in batches.

Include an option to preserve original dimensions or specify custom output dimensions.

### Document Conversion

Support common document formats, including:
- DOCX
- DOC
- ODT
- RTF
- TXT
- HTML
- Markdown
- EPUB

Support conversions between compatible formats, including document-to-PDF and PDF-to-document workflows where practical.

Preserve:
- Text formatting
- Headings
- Tables
- Images
- Hyperlinks
- Page layout
- Fonts where available

Document conversions should prioritize formatting accuracy over simply extracting text.

### PDF Conversion

Support:
- PDF to DOCX
- PDF to TXT
- PDF to HTML
- PDF to PNG
- PDF to JPG
- PDF to SVG where practical
- Image formats to PDF
- DOCX to PDF
- Other compatible document-to-PDF conversions.

Include options for:
- Combining multiple PDFs.
- Splitting PDFs.
- Extracting individual pages.
- Reordering pages.
- Compressing PDFs.
- Converting entire folders of images into individual PDFs or one combined PDF.
- Selecting specific pages for conversion.

Preserve searchable text and document structure whenever possible.

### Compatibility and Conversion Matrix

Not every file type can be converted directly into every other file type.

The application should intelligently determine which conversions are supported and display only compatible output options.

For example:
- MP4 to MP3: supported.
- MP4 to MOV: supported.
- DOCX to PDF: supported.
- PNG to JPG: supported.
- PDF to DOCX: supported, with formatting limitations communicated.
- MP3 to PNG: unsupported.

Never present an incompatible conversion as a valid option.

## 3. Batch Conversion and Folder Support

Batch processing is a core feature.

Users should be able to:
- Select multiple files at once.
- Drag and drop an entire folder.
- Automatically detect supported files inside folders.
- Recursively process subfolders when enabled.
- Convert all compatible files into a selected output format.
- Apply the same conversion settings to multiple files.
- Choose whether to preserve the original folder structure.
- Skip unsupported files and report them separately.
- View individual and overall conversion progress.

For example, a user should be able to drag in a folder containing 100 PNG images and convert all of them to JPG without manually selecting each file.

Include options to:
- Convert all files to the same format.
- Preserve each file's original category.
- Automatically select compatible conversions.
- Rename converted files using customizable naming rules.

## 4. File Management and Deletion Options

Include an optional checkbox:

**[ ] Delete original files after successful conversion**

Important behavior:
- This option must be disabled by default.
- Only delete originals after the converted files have been successfully generated and verified.
- Never delete an original if conversion fails.
- Never overwrite an existing file without confirmation or an explicitly enabled overwrite setting.
- Display a confirmation before deleting original files.
- Allow users to select whether originals should be moved to Trash or permanently deleted, with Trash as the default.

Additional functionality:
- Select a custom output directory.
- Save converted files next to their originals.
- Create a separate output folder.
- Automatically organize converted files by type.
- Open the output folder directly in Finder or File Explorer.

## 5. Native Operating System Integration

### macOS Version

Build a genuinely native macOS application using Swift and SwiftUI.

Include:
- Native Finder integration.
- Finder Quick Actions through macOS Services or Shortcuts.
- Right-click context menu functionality where supported.
- A Quick Action such as "Convert" that can process selected files.
- Drag-and-drop support.
- Native file selection dialogs.
- macOS share sheet integration where appropriate.
- Apple Silicon support.
- Background conversion support.
- Menu bar access where useful.

A user should be able to right-click a file in Finder, select Quick Actions, choose Convert, and launch a conversion workflow without manually opening the application.

### Windows Version

Build a genuinely native Windows application using C# and WinUI 3 or another suitable native Windows framework.

Include:
- File Explorer context menu integration.
- A right-click action such as "Convert".
- Drag-and-drop support.
- Native file selection dialogs.
- Windows share integration where practical.
- Background processing.
- Windows notification support.
- Support for modern Windows 10/11 environments, with Windows 11 as the primary target.

A user should be able to right-click a file in File Explorer and access the converter directly.

## 6. Technical Implementation

Use reliable, established media and document processing libraries.

Recommended technologies:

| Function | Technology |
|---|---|
| macOS interface | Swift / SwiftUI |
| Windows interface | C# / WinUI 3 |
| Video processing | FFmpeg |
| Audio processing | FFmpeg |
| Image processing | ImageMagick or equivalent |
| Native image support | Core Image / ImageIO / Windows imaging APIs |
| PDF processing | PDFKit / PDFium / equivalent |
| Document conversion | LibreOffice headless or suitable document libraries |
| Background tasks | Native OS task management |
| File management | Native filesystem APIs |

Additional requirements:
- Bundle or securely manage required dependencies.
- Users should not need to install Homebrew, Python, FFmpeg, LibreOffice, or other dependencies manually.
- Use native processing frameworks whenever practical.
- Avoid unnecessary re-encoding when stream copying or lossless conversion is possible.
- Use multithreading appropriately.
- Limit simultaneous conversions to avoid excessive CPU and memory consumption.
- Implement robust error handling.
- Clean up temporary files automatically.
- Maintain conversion logs locally for troubleshooting.

## 7. Privacy and Security

Privacy is one of the application's primary selling points.

Requirements:
- All file conversion happens locally on the user's computer.
- Files must never be uploaded to external servers.
- Zero bytes of file content should be transmitted to external services.
- No cloud processing.
- No account registration.
- No unnecessary telemetry or analytics.
- No mandatory subscriptions.
- No unnecessary internet connection requirements.
- All temporary files should be stored locally and cleaned up automatically.

The application should operate offline after installation, except for optional dependency or application updates.

## 8. User Interface and Design

Create a clean, modern, minimalist interface that feels like a first-party application on each operating system.

Avoid unnecessary complexity.

The main screen should include:
- A large drag-and-drop area.
- An Add Files button.
- An Add Folder button.
- A list of selected files.
- Automatically detected file types.
- Output format selection.
- Conversion settings.
- Output location selection.
- An optional delete-original checkbox.
- A prominent Convert button.

The application should also have:
- Individual conversion progress bars.
- Overall batch progress.
- Estimated completion time.
- Current file status.
- Success and failure indicators.
- A conversion history page.
- A settings page for default formats and preferences.

Include light and dark mode support.

## 9. Performance and Distribution

The application should be lightweight and efficient.

Requirements:
- Fast application startup.
- Low memory usage when idle.
- Efficient CPU utilization during conversion.
- No unnecessary background processes.
- Reliable processing of large files.
- Support for large batch operations.
- Graceful cancellation and recovery from failed jobs.

Distribution:
- macOS: signed and notarized downloadable DMG.
- Windows: signed downloadable installer (EXE or MSI).
- Provide separate architecture-compatible builds where needed.
- Include all necessary runtime dependencies.
- Provide an uninstall process that removes application files and associated components cleanly.

---

# PROMPT 2: Media Downloader & Converter

## 1. Product Overview

Build a lightweight, fully native desktop media downloader and converter that allows users to download videos and audio from supported online platforms using a URL, then convert the downloaded media into a variety of formats.

The application should eliminate the need to use third-party browser-based media converter websites.

Users should be able to paste a video link, select their preferred quality and output format, click Download, and receive the converted file directly on their computer.

**Public release (GitHub downloads, installers):** supports only sources that permit downloading, such as direct links to media files, sites that offer an official or uploader-enabled download option, and freely licensed or public-domain repositories. Each source is documented in `docs/SOURCES.md` (see Part 6.3).

Platform support should be modular so that additional sources can be supported as extraction tools evolve.

Only download content that users are authorized to download. Do not bypass DRM, authentication, private-content restrictions, or other platform access controls.

## 2. Link Input and Platform Detection

The application should accept URLs from supported platforms.

Include:
- A large URL input field.
- Paste-from-clipboard functionality.
- Automatic platform detection.
- Drag-and-drop URL support.
- URL validation.
- Automatic retrieval of available media information.

After submitting a link, the application should retrieve available metadata, including:
- Video title.
- Creator or channel name when available.
- Thumbnail.
- Duration.
- Available resolutions.
- Available audio formats.
- Available codecs.
- Estimated file size.

Display this information before downloading so users can make an informed choice.

Support videos up to approximately **30 minutes in length by default**.

If a video exceeds the configured duration limit:
- Display a clear warning.
- Prevent downloading by default.
- Explain the restriction.
- Allow the user to change the limit in settings if desired, subject to resource and platform constraints.

Include configurable file size limits to prevent unexpectedly large downloads.

## 3. Video Downloading and Conversion

Support as many useful video output formats as reasonably possible.

Include:
- MP4
- MOV
- MKV
- WEBM
- AVI
- M4V
- MPEG
- WMV where compatible
- Other formats supported by FFmpeg.

Allow users to select:
- 360p
- 480p
- 720p
- 1080p
- 1440p
- 4K when available.

Additional options:
- Original source quality.
- Frame rate selection where supported.
- Video codec selection.
- Bitrate settings.
- Compression presets.
- File size optimization.
- Preserve original quality whenever possible.

Automatically detect available formats and present only valid conversion options.

When the source platform provides separate audio and video streams, retrieve and combine them using the appropriate processing tools.

Avoid unnecessary re-encoding whenever possible to preserve quality and reduce processing time.

## 4. Audio Extraction and Conversion

Allow users to download audio only, without downloading or retaining the full video unnecessarily.

Support:
- MP3
- WAV
- M4A
- AAC
- FLAC
- OGG
- OPUS
- AIFF
- Other compatible formats.

Include:
- Audio bitrate selection.
- Sample rate selection.
- Audio quality presets.
- Metadata preservation.
- Automatic audio extraction.
- Audio-only download mode.

For example, users should be able to paste a supported URL, select MP3, choose their preferred bitrate, and download the audio file directly.

## 5. Quality and Format Selection

Create an intuitive selection interface.

Users should be able to choose between:

**Video mode**
- Download as MP4.
- Download as MOV.
- Download as MKV.
- Download as WEBM.
- Other supported formats.

**Audio mode**
- Extract as MP3.
- Extract as WAV.
- Extract as M4A.
- Extract as FLAC.
- Other supported formats.

Include an automatic mode that selects a reasonable quality and compatible format based on the source.

Display estimated output file sizes where possible.

## 6. Batch Downloads and Queue Management

Allow users to submit multiple video URLs and process them in a queue.

Include:
- Multiple URL input.
- Batch download functionality.
- Individual progress indicators.
- Overall queue progress.
- Pause and resume where supported.
- Cancel individual downloads.
- Cancel all downloads.
- Retry failed downloads.
- Set maximum simultaneous downloads.
- Prioritize or reorder queued downloads.

Allow users to apply the same format and quality settings to an entire batch.

For example, a user should be able to paste 15 links from supported sources and download all of them as MP4 files without processing each link individually.

## 7. Download Management

Include:
- Custom output folder selection.
- Automatic file naming based on video title.
- Custom naming templates.
- Duplicate file detection.
- Existing-file overwrite protection.
- Download history.
- Open downloaded files directly in Finder or File Explorer.
- Open the output folder.
- Automatically clean up temporary files.

Include an optional setting to delete temporary source files after successful conversion.

Do not automatically delete any completed downloads without explicit user permission.

## 8. Native Operating System Integration

### macOS Version

Build a genuinely native macOS application using Swift and SwiftUI.

Include:
- Native macOS interface.
- Apple Silicon support.
- Drag-and-drop URL input.
- Clipboard paste functionality.
- macOS share sheet integration where practical.
- Background download support.
- Menu bar access where useful.
- Native notifications when downloads finish.
- Finder integration for downloaded files.
- Convenient URL handoff from supported applications.

The application should feel like a lightweight native Mac utility rather than a website.

### Windows Version

Build a genuinely native Windows application using C# and WinUI 3 or another suitable native framework.

Include:
- Native Windows interface.
- Drag-and-drop URL input.
- Clipboard integration.
- Windows share integration where practical.
- Background downloading.
- System notifications.
- File Explorer integration.
- Convenient access from the system tray where useful.

The application should feel like a proper Windows desktop utility.

## 9. Technical Implementation

Use established and actively maintained tools for media extraction and processing.

Recommended technologies:

| Function | Technology |
|---|---|
| macOS interface | Swift / SwiftUI |
| Windows interface | C# / WinUI 3 |
| Media extraction | Native download manager + per-source adapters (Part 6.3) |
| Media processing | FFmpeg |
| Metadata extraction | Source adapters / FFprobe |
| Download management | Native backend queue |
| Temporary storage | Local filesystem |
| Background processing | Native OS task management |

Technical requirements:

**Media extraction**
- A native download manager with one adapter per allowlisted source (Part 6.3). No third-party extraction tools are bundled.
- Keep extraction functionality modular.
- Handle platform changes and extraction failures gracefully.
- Provide a mechanism for updating source adapters.
- Never attempt to circumvent DRM, login requirements, or access restrictions.

**Media processing**
- Use FFmpeg for format conversion, stream merging, audio extraction, and compression.
- Use FFprobe to inspect media metadata and available streams.
- Avoid re-encoding if the requested output can be produced through stream copying.
- Handle incompatible codecs and containers appropriately.

**Dependency management**
- Bundle all necessary dependencies.
- Users should not need to install Python, Homebrew, FFmpeg, or command-line utilities themselves.
- Ensure dependencies are compatible with each supported operating system.
- Include appropriate licenses and notices for bundled open-source software.

**Resource management**
- Keep CPU and memory usage low when idle.
- Prevent excessive simultaneous downloads.
- Implement download and processing timeouts.
- Clean up incomplete downloads and temporary files.
- Allow users to cancel jobs safely.

## 10. Privacy and Security

The application should prioritize user privacy.

Requirements:
- All downloading, processing, and conversion should happen locally.
- Downloaded media must never be uploaded to third-party servers.
- No cloud-based media processing.
- No account registration.
- No unnecessary telemetry.
- No mandatory subscriptions.
- No unnecessary collection of personal information.
- No unnecessary internet connections beyond accessing the requested media and optional application/dependency updates.

Clearly communicate that downloading and converting content must comply with applicable copyright laws and platform terms.

The application should never advertise itself as bypassing restrictions or downloading private or protected content.

## 11. User Interface and Design

Create a modern, minimalist desktop interface that feels native to the operating system.

The primary screen should include:
- A prominent URL input field.
- A Paste button.
- A Download/Analyze button.
- A video preview thumbnail.
- Video title and duration.
- Platform identification.
- Video/audio mode selection.
- Output format selection.
- Resolution and quality selection.
- Output directory selection.
- A prominent Download button.

After a download starts, display:
- Download progress.
- Conversion progress.
- Download speed.
- Estimated time remaining.
- Current processing stage.
- Estimated output file size.
- Completion status.

Include:
- Download queue.
- Download history.
- Settings.
- Light and dark mode.
- Clear error messages.

Keep the interface simple enough that someone unfamiliar with media conversion can use it immediately.

## 12. Performance and Distribution

The application should be lightweight and optimized for modern desktop hardware.

Requirements:
- Fast startup.
- Low idle resource usage.
- Efficient downloading.
- Efficient local conversion.
- Background operation.
- Stable handling of large files.
- Reliable batch processing.
- Safe cancellation and recovery.

Distribution:
- macOS: signed and notarized downloadable DMG.
- Windows: signed downloadable EXE or MSI installer.
- Bundle all required dependencies.
- Include a clean installation and uninstallation process.
- Support automatic or manual application updates.

---

# 3. Shared Development Requirements Across All Four Applications

## 1. Four Separate Downloadable Applications

The final deliverables must be four independently downloadable and installable applications:

| Application | Operating System | Primary Function |
|---|---|---|
| Universal File Converter | macOS | Local file conversion |
| Universal File Converter | Windows | Local file conversion |
| Media Downloader & Converter | macOS | Online media downloading and conversion |
| Media Downloader & Converter | Windows | Online media downloading and conversion |

Do not combine the two products into one application.

Each product should have its own branding, application identity, installer, settings, and release package.

The two operating-system versions of each product should share functionality and design language, but each should use native interfaces and system integrations.

## 2. Shared Codebase and Architecture

Where practical, develop shared processing components that can be reused across operating systems.

For example:
- Shared FFmpeg processing logic.
- Shared conversion configuration.
- Shared format detection.
- Shared error handling.
- Shared queue management.
- Shared metadata processing.
- Shared application settings schemas.

However, do not sacrifice native functionality just to force a single codebase.

Use Swift and SwiftUI for the macOS interfaces and C# with WinUI 3 for Windows interfaces.

The architecture should make it straightforward to update one shared processing component without rebuilding unrelated application functionality.

## 3. Branding and User Experience

Both applications should feel like products from the same software family.

Use:
- Consistent branding.
- Similar visual language.
- Minimalist design.
- Clear typography.
- Simple navigation.
- Light and dark modes.
- Consistent icons.
- Consistent terminology.

However, respect the native design conventions of macOS and Windows.

Avoid unnecessary animations, excessive settings, advertisements, and distracting interface elements.

## 4. Licensing and Legal Compliance

Use properly licensed open-source dependencies.

Ensure that:
- All bundled software complies with its respective licenses.
- Required attribution and license notices are included.
- Distribution complies with FFmpeg licensing requirements and the licenses of all other bundled components.
- Platform access restrictions are respected.
- The applications do not bypass DRM or authentication controls.

The detailed legal, licensing and distribution requirements are in Part 6, which takes precedence over this section.

## 5. Testing and Quality Assurance

Test all four applications independently.

Include:
- Unit tests.
- Integration tests.
- File format compatibility tests.
- Batch processing tests.
- Large file tests.
- Memory usage tests.
- Error recovery tests.
- Installation and uninstallation tests.
- Native operating-system integration tests.

Test on:
- Apple Silicon Macs.
- Supported Intel Macs if included in the macOS support target.
- Windows 10/11 systems according to the declared compatibility target.
- Different display sizes and scaling settings.

Verify that converted files actually open and function correctly in common media players and document applications.

## 6. Final Deliverables

The completed project should include:

**Application downloads**
1. Universal File Converter for macOS (`.dmg`)
2. Universal File Converter for Windows (`.exe` or `.msi`)
3. Media Downloader & Converter for macOS (`.dmg`)
4. Media Downloader & Converter for Windows (`.exe` or `.msi`)

**Development deliverables**
- Complete source code.
- Organized project structure.
- Build and installation instructions.
- Dependency and licensing documentation.
- README for each application.
- Testing documentation.
- Release and packaging scripts.
- Application icons and branding assets.

**Important:** Do not stop at producing mockups, prototypes, or a partially functional interface. The objective is to deliver four fully functional desktop applications that can be installed and used independently.

Prioritize getting the core functionality working first, then improve the interface, system integration, performance, and additional features.

The finished products should be lightweight, reliable, privacy-focused alternatives to browser-based file converters and media downloading websites.

---

# Part 4: Development instructions (summary)

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
- **M7.5 Personal module:** a generic, Debug-only external-adapter loading point in the public app; everything
  else is handled outside this repository per the owner's spec.
- **M8 Legal and compliance pass:** third-party notices from shipped dependencies, license audit, in-app
  notices, About windows, `LEGAL_REVIEW.md`.
- **M9 Packaging and V1 release:** Developer ID signing and notarization (DMG), Windows code signing
  (MSIX and/or signed installer), versioning, release notes, tag `v1.0.0`. Signing certificates and
  notarization credentials stay on the owner's machine or in protected CI secrets, never in the repo.

## Phase 2 (after V1; don't start until the owner says "Start Phase 2")

Trusted collaborators, a protected `main` branch with CODEOWNERS, PR template, security settings,
`CONTRIBUTING.md`, and automated Claude review of PRs from anyone other than the owner. Keep the repo
compatible: CI checks with stable job names and a CODEOWNERS-ready structure.
