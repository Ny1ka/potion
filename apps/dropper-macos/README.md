# Potion Dropper for macOS

SwiftUI app, macOS 13+. The Xcode project is generated from `project.yml` by XcodeGen and isn't committed.

```bash
make dropper-macos-project   # generate "apps/dropper-macos/Potion Dropper.xcodeproj", then open it in Xcode
make dropper-macos           # command-line Debug build
make run-dropper-macos       # build and launch
```

Status (M6 done): first-run acknowledgment, link bar, Fetch with the media card, batches of links, Video/Audio output
options, the download queue (Stop, Retry with resume, reorder), Download History (⌘Y), notifications, the Dock badge,
Settings (folder, naming template, size and length limits, downloads at once, history), dropped links and
Services › "Download with Potion Dropper". About with Terms and Privacy comes in M8; a Share extension in M9.

## What comes from where

- `Sources/`: app code. `Sources/Info.plist` is written by XcodeGen from `project.yml`.
- `../../design-system/macos/Shared/PotionDesign.swift`, `../../design-system/macos/PotionDropper/Assets.xcassets`
  and the Space Grotesk fonts are referenced in place from the design system (never copied or edited).
- `PotionCore` (`../../core/swift`) is linked as a local Swift package: link parsing, the source allowlist, the
  downloader and the conversion engines.
- The LGPL FFmpeg build (`scripts/ffmpeg/`) is copied into `Contents/Helpers` by a build script (required for
  Release builds), the same way as Potion.

## Notes

- Bundle id `com.ny1ka.potion-dropper`. Distributed directly (Developer ID, notarized DMG), never through the Mac
  App Store (spec Part 6.1.7).
- `NSAllowsArbitraryLoads` is on because direct media links may be plain `http://`. The app only fetches links
  the user entered and the redirects they lead to, each checked against the allowlist.
