# 07 · Iconography

## Principle

Use the platform icon font first: **SF Symbols** on macOS, **Segoe Fluent Icons** on Windows. They match system text weight, scale with text size and look native. The custom set in `icons/category/` is for the website, the preview, and any glyph the platforms don't have.

## Mapping

| Meaning | SF Symbol | Segoe Fluent Icons (code point) | Custom SVG |
|---|---|---|---|
| Image (category) | `photo` | Photo `E91B` | `image.svg` |
| Audio (category) | `waveform` | Audio `E8D6` | `audio.svg` |
| Video (category) | `film` | Video `E714` | `video.svg` |
| Docs (category) | `doc.text` | Document `E8A5` | `docs.svg` |
| Add files | `plus` | Add `E710` | n/a |
| Folder | `folder` | Folder `E8B7` | `folder.svg` |
| Convert / arrow | `arrow.right` | Forward `E72A` | `convert.svg` |
| Done | `checkmark` / `checkmark.circle.fill` | CheckMark `E73E` / Completed `E930` | `check.svg` |
| Failed | `exclamationmark.circle` | Error `E783` | `error.svg` |
| Retry | `arrow.clockwise` | Refresh `E72C` | n/a |
| Stop / cancel | `stop.fill` / `xmark` | Stop `E71A` / Cancel `E711` | n/a |
| Remove from list | `minus.circle` / `trash` | Remove `E738` / Delete `E74D` | n/a |
| Show in Finder / folder | `folder` (usually a text button, no icon) | OpenFolderHorizontal `ED25` | n/a |
| Settings | `gearshape` | Setting `E713` | n/a |
| Link (Dropper) | `link` | Link `E71B` | `link.svg` |
| Paste | `doc.on.clipboard` | Paste `E77F` | n/a |
| Download (Dropper) | `arrow.down.circle` | Download `E896` | `download.svg` |

Code points come from the Segoe Fluent Icons reference. Check each against Microsoft's current glyph list when you implement, and fix this table if one has moved. The category glyphs are also generated in `PotionDesign.cs` (`PotionCategoryInfo.Glyph`).

## Sizes and weight

| Context | Size | Weight |
|---|---|---|
| Inside category tile 20 / 28 / 40 / 64 | 12 / 16 / 22 / 34 | semibold |
| Toolbar | system default (macOS ~15 pt; WinUI 16) | regular |
| Inline with body text | match text style (`.imageScale(.medium)`) | regular |
| Row arrow | 14 | regular, `textTertiary` |

## Custom glyph style

When a glyph has to be drawn: 24 × 24 grid, 2-px safe padding, stroke 1.75, round caps and joins, no fills (except small dots), corner radius ~3 on rectangles. Match `icons/category/*.svg`. Glyphs use `currentColor`.

## Logo as an icon

The mono Potion mark (`potion-mark-mono-*`) is the only brand glyph. Use it for the menu bar, tray, Quick Action and empty state. Never use it as a generic "convert" icon inside the UI.
