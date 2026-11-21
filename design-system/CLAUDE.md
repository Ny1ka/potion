# CLAUDE.md: Potion Design System

You are building the visual layer for two products, each shipped as a native macOS app and a native Windows app (four installers total):

| Product | What it does | macOS target | Windows target |
|---|---|---|---|
| **Potion** | Local file converter (video, audio, image, docs, PDF) | `Potion.app` | `Potion.exe` / MSIX |
| **Potion Dropper** | Download media from a link, then convert it | `Potion Dropper.app` | `PotionDropper.exe` / MSIX |

This folder is the single source of truth for how they look and feel. Functional behavior (conversion matrix, codecs, download logic) lives in the product spec, not here.

## Read in this order

1. `README.md`: map of every file
2. `docs/01-brand.md` … `docs/10-accessibility.md`: rules (short, specific)
3. `preview/index.html`: open it in a browser to see everything rendered, including component mockups in both themes
4. `tokens/tokens.json`: every value. Never invent a color, size, radius or duration that isn't here.

## Non-negotiables

1. **Native first.** SwiftUI (plus AppKit where needed) on macOS, WinUI 3 / Windows App SDK on Windows. No web views, Electron or cross-platform UI kits. Standard controls should look like the OS's own controls.
2. **System font and system accent.** UI text uses SF Pro / Segoe UI Variable through the platform text styles. Primary buttons, selection, focus rings, links and overall progress use the user's **system accent color**. Space Grotesk is only for the wordmark and brand moments (onboarding hero, About title).
3. **Category colors identify file types and do nothing else.** Image `#2DD4BF`, Audio `#FF9F43`, Video `#FF4F7B`, Docs `#7C6CFF` (the four logo bands, top to bottom). Use them for category tiles, per-file progress fills and the logo. Never use them for buttons, links, focus, warnings or decoration.
4. **Never color alone.** A category color always comes with its glyph and visible text (extension or label). Status always comes with an icon plus words.
5. **Light, dark and high contrast all work.** Use the generated colorsets / ThemeDictionaries, which already carry light and dark values. On light surfaces category *text and glyphs* switch to their darker `textOnLight` variants. Never put a raw fill color on text.
6. **Don't redraw the logo.** Use the files in `logos/` or the generated asset catalogs. At 32 px and below use the `-small` variants. The band order (image, audio, video, docs) is fixed.
7. **Respect reduce motion.** Every animation has a no-motion fallback (`docs/08-motion.md`).
8. **Platform words.** macOS: Trash, Finder, Settings, Title Case buttons and menus. Windows: Recycle Bin, File Explorer, Settings, Sentence case buttons and menus. See `docs/01-brand.md`.

## Integrating the assets

**macOS (each app):**
- Copy `macos/<App>/Assets.xcassets` into the Xcode target, or merge its contents into the existing catalog. It contains `AppIcon`, every colorset (`PotionSurface`, `CategoryVideoText`, …), `LogoMark` (vector, light/dark) and `MenuBarIcon` (template).
- Add `macos/Shared/PotionDesign.swift` to both targets. It gives you `PotionColor`, `PotionCategory`, `PotionSpacing`, `PotionRadius`, `PotionSize`, `PotionFont`, `PotionMotion` and the reference views `PotionCategoryTile`, `PotionFormatBadge` and `PotionMark`.
- Brand font: add `fonts/SpaceGrotesk-Bold.ttf` and `fonts/SpaceGrotesk-Medium.ttf` to the bundle and set `ATSApplicationFontsPath` in Info.plist. PostScript names are `SpaceGrotesk-Bold` and `SpaceGrotesk-Medium`.
- `macos/<App>/AppIcon.icns` exists for builds that don't use the asset catalog.
- Deployment target: macOS 13+.

**Windows (each app):**
- Copy `windows/<App>/Assets/*` into the project's `Assets/` folder. It has the MSIX logo set with `targetsize`, `altform-unplated` and `altform-lightunplated` variants, `<App>.ico` for unpackaged builds and the window icon, `Tray-dark-taskbar.ico` / `Tray-light-taskbar.ico`, and `Fonts/`.
- Merge `windows/Shared/PotionTheme.xaml` in `App.xaml` and add `windows/Shared/PotionDesign.cs`.
- Brush keys follow `Potion<Token>Brush` and `PotionCategory<Name>{Fill|Text|Tint|OnFill}Brush`. For the accent, use the WinUI system resources (`AccentFillColorDefaultBrush`, …).
- Set the window backdrop to Mica and extend content into the title bar (`docs/09-platforms.md`).

**Shared data:** `data/formats.json` maps file extensions to categories, which decides tile color and glyph. Load it, or port it into each app.

## When something isn't specified

- Use the platform default. A stock control beats a custom one.
- Take spacing from the 4-pt scale, radius from `radius`, and a color from the semantic set. If none fits, ask instead of adding a new value.
- New icon? Find the SF Symbol / Segoe Fluent glyph first (`docs/07-iconography.md`). Only draw a custom one in the 24-px stroke style of `icons/category/`.
- Keep Potion and Potion Dropper visually identical except for the mark, name and Dropper-specific screens.

## Changing tokens

Edit `tokens/tokens.json`, then run `python3 tools/build.py`. That regenerates CSS, Swift, XAML, C#, colorsets, logos, the app icons for both platforms and the preview. It needs Python 3.9+, `fontTools`, `Pillow`, `rsvg-convert` (librsvg) and ImageMagick. Never hand-edit generated files: each has a "Generated by" header.

## Done checklist for any screen

- [ ] Looks right in light and dark, and with Windows High Contrast on
- [ ] Every interactive element is keyboard reachable with a visible focus ring
- [ ] Every icon-only control has an accessibility label
- [ ] Category colors appear only on file-type identity
- [ ] No hard-coded hex values in views; tokens only
- [ ] Long file names truncate in the middle; numbers use tabular digits
- [ ] Reduce motion respected
- [ ] Platform wording (Trash vs Recycle Bin, Title Case vs Sentence case)
