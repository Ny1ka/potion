# 02 · Logo

## Construction (100 × 100 grid)

**Potion mark: Erlenmeyer flask** (v1.1, replaces the round-bulb mark from v1.0)

| Part | Geometry |
|---|---|
| Flask silhouette (clip) | path `M41 14 H59 V38 L81 80 Q84 88 75 88 H25 Q16 88 19 80 L41 38 Z`: a straight neck (x 41–59) opening into a cone with softly rounded bottom corners |
| Rim | rect x 37, y 9, w 26, h 7, corner radius 3.5 |
| Neck fill | everything inside the silhouette above y 38 (the neck and the top of the shoulder) is the neck color |
| Bands | four horizontal bands inside the silhouette with 2-unit gaps: y 40–50 (Image), 52–62 (Audio), 64–74 (Video), 76–88 (Docs) |
| Band order | Image, Audio, Video, Docs (top → bottom). Fixed. |

The triangle silhouette is what makes the mark read as a lab flask. Never round the cone into a bulb: the round-bottom version read as a grenade and is retired.

**Potion Dropper mark**

| Part | Geometry |
|---|---|
| Bulb (rubber top) | path `M38 32 V24 A12 12 0 0 1 62 24 V32 Z` |
| Upper band (audio) | rect x 42, y 34.5, w 16, h 18 |
| Lower band + tip (video) | path `M42 55 H58 V64 L50 75 L42 64 Z` |
| Drop (video) | circle (50, 85), r 6 |

Both marks are flat: no outlines, gradients, shadows or highlights. The neck and rim (Potion) and the rubber bulb (Dropper) take the "neck" color, which is white on dark, ink on light, or the neutral `#8F88A2` when the background is unknown.

**Small variants** (`*-mark-small*.svg`) exist for rendering at **32 px and below**. They use a wider neck, a taller rim, a slightly wider cone and no gaps between the colored bands. Silhouette: `M39 13 H61 V36 L84 79 Q87 89 77 89 H23 Q13 89 16 79 L39 36 Z`. The mono small variants widen the gaps so the stripes don't blur together. Pick the variant by final pixel size, not point size: 16 pt @2x = 32 px, so use small.

## Files (`logos/svg/`)

| File | Use on |
|---|---|
| `potion-mark.svg` | Dark backgrounds (white neck) |
| `potion-mark-on-light.svg` | Light backgrounds (ink neck) |
| `potion-mark-neutral.svg` | Unknown background (gray neck), e.g. Windows .ico |
| `potion-mark-small.svg`, `potion-mark-small-on-light.svg` | ≤ 32 px |
| `potion-mark-mono-black.svg` / `-mono-white.svg` | One-color contexts (print, embossing, watermark) |
| `potion-mark-mono-small-black.svg` | macOS menu bar template, Windows tray (≤ 32 px) |
| `potion-mark-mono-currentColor.svg` | Web/HTML, where it inherits the text color (e.g. empty-state illustration) |
| `potion-wordmark-on-dark.svg` / `-on-light.svg` | Wordmark alone (outlined Space Grotesk Bold, tracking −0.05em) |
| `potion-lockup-on-dark.svg` / `-on-light.svg` | Mark + wordmark, horizontal |
| `potion-appicon-macos.svg` (+ `-small`) | macOS app icon master (1024 grid) |
| `dropper-*` | Same set for Potion Dropper, plus `dropper-lockup-*` with "potion **dropper**" |

PNG exports are in `logos/png/`.

## Lockup

- Mark height = 90% of the word's ink height, vertically centered on the x-height.
- Gap between mark and word = 0.30 × font size.
- In the Dropper lockup, "dropper" is Space Grotesk Medium in `textTertiary`, separated from "potion" by 0.24 × font size.
- Use the lockup files as-is. Never re-typeset the wordmark.

## Clear space and minimum size

- **Clear space:** keep a margin equal to the neck width (18 units, 18% of the mark's box) on every side. For the lockup, measure it at lockup scale.
- **Minimum size:** mark 16 px (use the small variant), lockup 96 px wide.

## Misuse

Don't:
- reorder, recolor or add bands, or use a gradient between bands
- draw an outline around the flask, or add a cork, bubbles, sparkles or highlights
- round the cone into a sphere or bulb (the retired v1.0 shape, which read as a grenade)
- tilt the flask, or add liquid splashes or pouring
- rotate, skew or squash the mark, or place it on a busy photo
- use a category color for the neck
- put the white-neck mark on a light background, or the ink-neck mark on a dark one
- set "potion" in a live font as the logo

## App icons

**macOS** (`macos/<App>/Assets.xcassets/AppIcon.appiconset`, generated):
- 1024 canvas, rounded-square body 824 × 824 at (100, 100), continuous-corner squircle, fill `#17141F`, 3 px inner stroke white at 8%, drop shadow (y 12, blur 14, black 32%).
- Mark scaled to 72% of the body. At 16 px and 32 px final size: small mark at 84%, no shadow.
- Sizes included: 16, 32, 128, 256, 512 at @1x and @2x.

**Windows** (`windows/<App>/Assets`, generated):
- Freestanding glyph with no plate, matching Windows 11 icon style.
- `Square44x44Logo.targetsize-N_altform-unplated.png` has a white neck (dark taskbar). `…_altform-lightunplated.png` has an ink neck (light taskbar). The plain `targetsize` and `scale` assets use the neutral neck.
- `<App>.ico` (16–256, neutral neck) for the window icon and unpackaged builds.
- Tiles (`Square150x150`, `Wide310x150`) put the glyph at 62% of tile height. `SplashScreen` uses a white neck. Set the manifest splash `BackgroundColor` to `#0E0C14`.

**Menu bar / tray:** `MenuBarIcon.imageset` is a template image, so macOS tints it for you. On Windows, swap between `Tray-dark-taskbar.ico` and `Tray-light-taskbar.ico` when the system theme changes.
