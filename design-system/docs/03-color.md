# 03 · Color

Two kinds of color: the four **category colors**, which carry meaning, and **everything else**, which stays native and neutral.

## Category colors

| Category | `fill` | `textOnDark` | `textOnLight` | `onFill` (glyph on fill) |
|---|---|---|---|---|
| Image | `#2DD4BF` | `#2DD4BF` (9.76:1) | `#197C70` (5.05:1) | `#17141F` (9.76:1) |
| Audio | `#FF9F43` | `#FF9F43` (8.90:1) | `#B15600` (5.00:1) | `#17141F` (8.90:1) |
| Video | `#FF4F7B` | `#FF4F7B` (5.76:1) | `#E00038` (4.97:1) | `#17141F` (5.76:1) |
| Docs | `#7C6CFF` | `#7C6CFF` (4.71:1) | `#6553FF` (4.96:1) | `#17141F` (4.71:1) |

Ratios are measured against `surface` (#17141F dark, #FFFFFF light). All four pass WCAG AA for text.

**Roles** (generated names in brackets):
- **fill** [`CategoryVideoFill`, `PotionCategoryVideoFillBrush`]: logo bands, solid category tiles, per-file progress bar fill. Same in both themes.
- **text** [`CategoryVideoText`, `PotionCategoryVideoTextBrush`]: a glyph or label drawn in the category color on a neutral surface. Resolves to `textOnDark` or `textOnLight` automatically.
- **tint** [`CategoryVideoTint`, `PotionCategoryVideoTintBrush`]: background of the standard category tile. A solid pre-mix of the fill over `surface` (18% dark, 14% light). Put a `text`-colored glyph on top.
- **onFill** [`CategoryVideoOnFill`]: glyph on a solid `fill` (always ink).

**Allowed:** category tiles, file-type chips, per-row progress fill, segmented-control icons in Dropper (Video/Audio), logo, marketing illustrations that explain categories.
**Not allowed:** buttons, links, selection, focus, success/warning/error, backgrounds of whole regions, decorative accents, charts unrelated to file types.

Category comes from the input file's extension (`data/formats.json`). In a row that converts MOV → MP3, the tile is Video (the input). The output format shows as a neutral badge.

## Accent

The accent is the **system accent color**. That's `Color.accentColor` / `NSColor.controlAccentColor` on macOS, and `AccentFillColorDefaultBrush` and the related resources on Windows. Use it for primary buttons, selection, focus ring, links, toggles, the overall batch progress bar and drag-over highlights. Potion has no brand accent of its own. The only fallback (`accent.fallback`: white on dark, ink on light) is for the website and marketing.

## Neutrals

For **custom-drawn** surfaces: drop zone, category tiles, onboarding, About window, splash, website.

| Token | Dark | Light | Use |
|---|---|---|---|
| background | `#0E0C14` | `#F7F6FA` | Brand canvas (onboarding, About, web) |
| surface | `#17141F` | `#FFFFFF` | Cards, sheets you draw yourself |
| surfaceRaised | `#211D2B` | `#EFEDF3` | Badges, inputs, segmented track |
| surfaceSunken | `#0B0A10` | `#ECEAF1` | URL field well (Dropper) |
| border | `#2E2939` | `#E4E1EA` | Hairlines, row separators (decorative) |
| borderStrong | `#3A3547` | `#C9C4D3` | Drop zone dashed border, input borders |
| textPrimary | `#FFFFFF` | `#17141F` | |
| textSecondary | `#B6B0C4` | `#4D475A` | Metadata (size, format) |
| textTertiary | `#9A94A8` | `#6B6577` | Captions, placeholders, "Waiting" |
| textDisabled | `#5E5869` | `#A8A3B3` | Disabled text only (exempt from contrast) |

### System vs Potion neutrals

For window chrome and stock controls, **use system colors**. They adapt to vibrancy, Mica, Increase Contrast and High Contrast, which fixed hex values can't do.

| Need | macOS (SwiftUI / AppKit) | Windows (WinUI resource) | Potion token (custom views only) |
|---|---|---|---|
| Window background | system window background / `.background` material | `SolidBackgroundFillColorBaseBrush` or Mica | background |
| Primary text | `.primary` / `labelColor` | `TextFillColorPrimaryBrush` | textPrimary |
| Secondary text | `.secondary` / `secondaryLabelColor` | `TextFillColorSecondaryBrush` | textSecondary |
| Tertiary text | `tertiaryLabelColor` | `TextFillColorTertiaryBrush` | textTertiary |
| Separator | `Divider()` / `separatorColor` | `DividerStrokeColorDefaultBrush` | border |
| Card | `.regularMaterial` or `controlBackgroundColor` | `CardBackgroundFillColorDefaultBrush` | surface |

Rule of thumb: if a stock control or container draws it, use the system color. If you draw it yourself (drop zone, tiles, badges, onboarding), use the Potion token.

## Status

| | Dark | Light | Pair with |
|---|---|---|---|
| success | `#3DD68C` | `#12804A` | checkmark glyph + "Done" |
| warning | `#FFC53D` | `#8F5F00` | warning glyph + reason |
| error | `#FF6B6B` | `#C62A2F` | error glyph + reason + action |

Where the platform has a status control, use it and keep its colors (WinUI `InfoBar`, the system notification styles). Error red and Video pink are both warm, so status must always carry an icon and text.

## Themes

- Follow the system appearance by default. Offer a Settings override (System / Light / Dark) only if it costs nothing; it's not required.
- **Windows High Contrast:** `PotionTheme.xaml` maps every Potion brush to system colors. Category tiles become outline-only, using system text color for both the glyph and the 1-px border. Don't force brand colors in High Contrast.
- **macOS Increase Contrast:** system colors adapt on their own. Custom borders should switch from `border` to `borderStrong`. Check `accessibilityDisplayShouldIncreaseContrast`.
