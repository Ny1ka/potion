# Potion Design System · v1.1.0

The visual system for **Potion** (file converter) and **Potion Dropper** (media downloader + converter) on macOS and Windows.

**Start here:** open `preview/index.html` in a browser to see every logo, color, type role, icon and component mockup in light and dark. Then read `CLAUDE.md`.

## The idea in one paragraph

The mark is an Erlenmeyer flask: a cone filled with four flat bands under a plain neck and rim. Each band is one file category: **Image** (teal), **Audio** (orange), **Video** (pink), **Docs** (violet). Those four colors run through the product and always mean the same thing: what kind of file this is. Everything else stays quiet and native. The apps use the system font, the system accent color, system materials and stock controls, so Potion feels like it came with the OS. Potion Dropper is the sibling app. Its mark is a dropper holding only the two colors it outputs, video and audio.

## What's inside

```
CLAUDE.md                     Instructions for Claude Code (rules, integration, checklist)
README.md                     This file
CHANGELOG.md                  What changed between versions (1.1.0: new flask logo)
tokens/
  tokens.json                 SOURCE OF TRUTH: colors, type, spacing, radius, sizes, motion, elevation
  tokens.css                  Generated CSS custom properties (light/dark/[data-theme])
docs/
  01-brand.md                 Names, concept, voice and copy, platform wording
  02-logo.md                  Construction, variants, clear space, minimum sizes, misuse, app icons
  03-color.md                 Category roles, neutrals, system color mapping, accent and status rules
  04-typography.md            System type ramp per platform, brand font, numerals, truncation
  05-layout.md                Spacing, radius, window sizes, screen layouts
  06-components.md            Every component: anatomy, sizes, states, platform control to use
  07-iconography.md           SF Symbols / Segoe Fluent mapping, custom glyph style
  08-motion.md                Durations, easing, transitions, splash timeline, reduce motion
  09-platforms.md             macOS and Windows integration specifics
  10-accessibility.md         Contrast table, keyboard, screen readers, high contrast
logos/
  svg/                        Marks (dark/light/neutral/small/mono), wordmark, lockups, app icon masters
  png/                        Raster exports (256/512/1024 marks, @2x lockups, 1024 app icons)
icons/category/               24 px stroke glyphs: image, audio, video, docs, link, folder, download, convert, check, error
macos/
  Potion/Assets.xcassets      Drop-in catalog: AppIcon, colorsets, LogoMark, MenuBarIcon
  Potion/AppIcon.icns
  PotionDropper/…             Same for Potion Dropper
  Shared/PotionDesign.swift   SwiftUI tokens + PotionCategoryTile, PotionFormatBadge, PotionMark
windows/
  Potion/Assets/              MSIX logo set, Potion.ico, tray icons, Fonts/
  PotionDropper/Assets/       Same for Potion Dropper
  Shared/PotionTheme.xaml     WinUI ResourceDictionary (Dark/Light/HighContrast)
  Shared/PotionDesign.cs      C# tokens (category glyphs, spacing, motion)
fonts/                        Space Grotesk Medium/Bold (.ttf + .woff2) + OFL license
data/formats.json             File extension → category (color + glyph)
splash/splash.html            Reference build-up animation
preview/index.html            Visual overview of the whole system
tools/build.py                Regenerates every derived file from tokens.json
```

## Core values at a glance

| | Value |
|---|---|
| Image | fill `#2DD4BF` · text on light `#197C70` |
| Audio | fill `#FF9F43` · text on light `#B15600` |
| Video | fill `#FF4F7B` · text on light `#E00038` |
| Docs | fill `#7C6CFF` · text on light `#6553FF` |
| Ink | `#17141F` (also the glyph color on any category fill) |
| Brand background | `#0E0C14` |
| UI font | SF Pro (macOS), Segoe UI Variable (Windows) |
| Brand font | Space Grotesk 700, tracking −0.05em (wordmark) |
| Accent | System accent color, always |
| Spacing | 4-pt grid |
| Radius | 4 · 6 (mac controls) / 4 (Windows controls) · 8 · 12 · 16 |

## Regenerating

```bash
pip install fonttools pillow       # plus librsvg (rsvg-convert) and ImageMagick
python3 tools/build.py
```

## Licenses

Space Grotesk is © The Space Grotesk Project Authors, under the SIL Open Font License 1.1 (`fonts/OFL-SpaceGrotesk.txt`). You can bundle it with the apps. Everything else in this folder belongs to the Potion project.
