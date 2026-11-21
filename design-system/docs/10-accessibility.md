# 10 · Accessibility

Target: WCAG 2.2 AA, plus each platform's own accessibility conventions.

## Contrast (verified)

| Pair | Dark (on #17141F) | Light (on #FFFFFF) |
|---|---|---|
| textPrimary | 18.16 | 18.16 |
| textSecondary | 8.65 | 8.88 |
| textTertiary | 6.21 | 5.60 |
| Image text | 9.76 | 5.05 |
| Audio text | 8.90 | 5.00 |
| Video text | 5.76 | 4.97 |
| Docs text | 4.71 | 4.96 |
| success | 9.68 | 4.99 |
| warning | 11.51 | 5.52 |
| error | 6.54 | 5.57 |
| Glyph (ink) on each category fill | 4.71–9.76 | same |

Category **glyphs on tints** (the standard tile) are non-text graphics, so the requirement is 3:1. Measured: Image 6.80 / 4.60, Audio 6.33 / 4.54, Video 4.55 / 4.21, Docs 3.79 / 4.21 (dark / light). All pass. Never put body text on a tint; put text on `surface`, where the 4.5:1 numbers above apply. `border` hairlines are decorative. Anything that marks a control's boundary (drop zone, input) uses `borderStrong` or a native control. If you change a color, run `python3 tools/build.py` and check the numbers in `preview/index.html`.

## Never color alone

- A category always shows its glyph, and the row always shows the extension/format text.
- Status = icon + word ("✓ Done", "! Failed").
- Drag-over states also change the label text, not just the border color.

## Keyboard

- Everything is reachable with Tab / arrow keys. On macOS, respect Full Keyboard Access.
- Shortcuts: Add Files ⌘O / Ctrl+O. Convert All ⌘↩ / Ctrl+Enter. Remove ⌫ / Delete. Settings ⌘, / Ctrl+,. Paste link in Dropper ⌘V / Ctrl+V while the window is focused, with no field needed.
- Use the platform focus ring (system accent). Never remove it, and never draw a category-colored focus.
- The drop zone's "Choose Files…" button is the keyboard alternative to dragging.

## Screen readers (VoiceOver, Narrator)

- Queue row as one element: "Interview.mov, Video, 48.2 megabytes, converting to MP4, 62 percent." Actions: Change format, Remove, Show in Finder.
- Progress: announce at 25% steps, and on completion or failure (`AccessibilityNotification.Announcement` / `RaiseNotificationEvent`). Don't announce every percent.
- Icon-only buttons get labels ("Add files", "Settings", "Remove"). Decorative marks are hidden.
- Drop zone label: "Drop files or folders here, or choose files."

## Text size

- Use text styles, never fixed sizes, so system text scaling flows through.
- At 200% Windows text size, rows grow in height. Format popup and status wrap under the file name (`05-layout.md` › Resizing).

## Motion

See `08-motion.md` › Reduce motion. The splash never loops in the apps.

## High contrast / Increase contrast

- Windows High Contrast: Potion brushes map to system colors (generated). Tiles become outlined. Test with the Aquatic and Desert themes.
- macOS Increase Contrast: custom borders switch to `borderStrong`. System colors handle the rest.
