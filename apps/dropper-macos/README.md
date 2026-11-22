# Potion Dropper for macOS

SwiftUI app, macOS 13+. The Xcode project is generated from `project.yml` by XcodeGen and isn't committed.

```bash
make dropper-macos-project   # generate "apps/dropper-macos/Potion Dropper.xcodeproj", then open it in Xcode
make dropper-macos           # command-line Debug build
make run-dropper-macos       # build and launch
```

Status (M5): the window has the link bar; Fetch checks links against the source allowlist (`docs/SOURCES.md`).
The media card, output options, download queue and Settings come in M6.

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
