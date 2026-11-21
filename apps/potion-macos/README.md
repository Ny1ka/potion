# Potion for macOS

SwiftUI app, macOS 13+. The Xcode project is generated from `project.yml` by XcodeGen
and isn't committed.

```bash
make potion-macos-project   # generate apps/potion-macos/Potion.xcodeproj, then open it in Xcode
make potion-macos           # command-line Debug build
make run-potion-macos       # build and launch
```

## What comes from where

- `Sources/`: app code.
- `../../design-system/macos/Shared/PotionDesign.swift`, `../../design-system/macos/Potion/Assets.xcassets`
  and the Space Grotesk fonts are referenced in place from the design system (never copied or edited).
  Regenerate them with `python3 design-system/tools/build.py`.
- `Resources/Brand.xcassets/MarkMono.imageset/mark-mono.svg` is an unmodified copy of
  `design-system/logos/svg/potion-mark-mono-black.svg`, used as a template image for the empty
  state and the Quick Action. The generated catalog doesn't include a mono mark, so if the design
  system's mark changes, copy the file again.
- `PotionCore` (`../../core/swift`) is linked as a local Swift package.
