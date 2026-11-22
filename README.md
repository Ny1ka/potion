# Potion

Convert video, audio, images, documents and PDFs on your own computer. Nothing is uploaded.

## Download

**[Download Potion for Mac](https://github.com/Ny1ka/potion/releases/latest)**: macOS 13 or later, Apple silicon
and Intel.

Open the DMG and drag Potion onto Applications. This early version isn't signed by Apple yet, so the first time you
open it: on macOS 15 or later, click **Done**, then **System Settings › Privacy & Security › Open Anyway**; on
macOS 13 or 14, Control-click Potion in Applications and choose **Open**. The
[release notes](https://github.com/Ny1ka/potion/releases/latest) have the details.

## What Potion does

- Drop files or folders, pick a format, convert. Only conversions that work are offered, and lossy ones say what
  they'll change first.
- **Video** (MP4, MOV, MKV, WEBM, AVI…), **audio** (MP3, WAV, M4A, FLAC, OGG, OPUS…), **images** (PNG, JPG, WEBP,
  HEIC, AVIF…, plus SVG and camera RAW files), **documents** (DOCX, DOC, ODT, RTF, TXT, HTML, Markdown, EPUB) and
  **PDF** (to images or documents, merge, split, extract, reorder, compress).
- Converted files never replace anything. Originals can go to the Trash after a successful conversion.
- Finder integration: **Services › Convert with Potion**.
- Private by design: no network connections, no accounts, no analytics.

## Apps

| App | Status |
|---|---|
| Potion for Mac | Available: [latest release](https://github.com/Ny1ka/potion/releases/latest) |
| Potion for Windows | Built; released after testing on Windows PCs |
| Potion Dropper | Experimental and still in development. Not available as a download |

Potion Dropper is a companion app I'm still developing. It downloads a file from a link you're allowed to download
from, then converts it. It isn't released.

## Building from source (macOS)

Requires Xcode 15+ and [XcodeGen](https://github.com/yonaskolb/XcodeGen) (`brew install xcodegen`). Audio and video
conversion needs the LGPL FFmpeg build from `scripts/ffmpeg/`.

```bash
make setup                          # enables git hooks, checks tools
scripts/ffmpeg/build-macos.sh       # builds the LGPL FFmpeg into build/ffmpeg/macos
make test                           # core tests on both platforms' cores
make run-potion-macos               # builds and launches Potion
```

## Repository

| Path | Contents |
|---|---|
| `apps/` | The app projects (macOS: SwiftUI; Windows: WinUI 3) |
| `core/` | Shared processing logic and the cross-platform spec both cores are tested against |
| `design-system/` | Logos, colors, typography, components and generated platform assets |
| `docs/` | Architecture, progress, legal review, release notes |
| `legal/` | Terms, privacy policy, third-party notices and license texts |

## License

MIT, see `LICENSE`. The Potion and Potion Dropper names and logos aren't covered by the license.
[Terms of Use](legal/TERMS.md) · [Privacy Policy](legal/PRIVACY.md) ·
[Third-party notices](legal/THIRD_PARTY_NOTICES.md) · Contact: nyikabusiness@gmail.com
