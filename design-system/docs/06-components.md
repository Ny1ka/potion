# 06 · Components

Each entry lists anatomy, tokens, states and which native control to use. Mockups for most of these are in `preview/index.html` → Components.

---

## Category tile

The atom of the system: a rounded square showing what kind of file this is.

- Size 28 (default). Other sizes: 20, 40, 64. Radius 0.286 × size, continuous corners on macOS.
- Background `Category<X>Tint`, glyph `Category<X>Text`, glyph = 57% of tile, semibold weight.
- Glyph: SF Symbol / Segoe Fluent glyph from `07-iconography.md`.
- Solid variant (rare, e.g. onboarding illustration): background `fill`, glyph `onFill`.
- High Contrast: transparent background, 1-px border and glyph in system text color.
- Accessibility: label = category name ("Video"). If the row already says the format, hide the tile from screen readers instead.

Swift: `PotionCategoryTile(.video)`. WinUI: a `Border` (CornerRadius 8, Background `PotionCategoryVideoTintBrush`) containing a `FontIcon` (Glyph from `PotionCategoryInfo.Glyph`, Foreground `PotionCategoryVideoTextBrush`, FontSize 16).

## Format badge

Uppercase extension in a pill: `MP4`.
- Mono 10/11 semibold, tracking +0.04em, color `textSecondary`, background `surfaceRaised`, radius xs, padding 2 × 6.
- Always neutral, even for the output format. Category color belongs to the tile only.

Swift: `PotionFormatBadge("mp4")`.

## Drop zone (empty state)

- Fills the content area minus the window inset; min height 220.
- 1.5-px dashed border `borderStrong`, radius lg (12), transparent fill.
- Centered stack, gap 6: mono Potion mark 48 px in `textTertiary`, then "Drop files or folders" (body, semibold), then "Video, audio, images, documents and PDFs" (secondary), then a gap of 10 and a **Choose Files…** button (standard bordered button).
- **Drag over (valid):** border becomes solid 2 px system accent, background = accent at 8% opacity, label changes to "Drop to add 12 files". Animation: `fast`, standard easing.
- **Drag over (nothing supported):** border `error`, label "These files can't be converted", no fill.
- Folders are accepted. If "Include subfolders" is on, say so in the caption.
- The whole window, including a non-empty queue, is a drop target. Once the queue has items, highlight the list itself.

## Queue row

```
[tile 28] fileName                         →  [format popup]   [status]
          secondary: "MOV · 48.2 MB"
          [per-row progress, 4 px]  (only while converting)
```

- Height 56 (44 compact), horizontal inset x4, tile ↔ text x3, separators `border` 1 px, or the native list separators.
- Arrow glyph `convert` 14 px in `textTertiary`.
- Format popup: a stock popup button / `ComboBox` listing **only compatible formats**, grouped by category with headers. Default is the last-used output format for that category.
- **Status column (right, 56–72 wide, right-aligned, tabular digits):**
  - Waiting: "Waiting" in `textTertiary`
  - Converting: "62%". Progress bar under the text column, filled with the row's category `fill` on a `surfaceRaised` track.
  - Done: check glyph + "Done" in `success`. The row action becomes "Show in Finder" / "Show in folder" (on hover, or as a context menu item).
  - Failed: error glyph + "Failed" in `error`. The secondary line swaps to the reason ("Can't read this file"). Offer "Retry".
  - Canceled: "Canceled" in `textTertiary`
- Hover: a subtle row highlight from the native list. Selected: the native selection, which uses the system accent.
- Reduce motion: progress still updates, but rows appear and disappear without sliding.

## Format picker (Advanced options)

Revealed by the "Advanced" disclosure in the convert sheet, or in a row's popover. Use native form layout (macOS `Form` with `.grouped` style; WinUI `StackPanel` rows with label + control).

| Category | Controls |
|---|---|
| Video | Resolution (Original, 4K, 1080p, 720p, 480p, Custom…), Frame rate, Codec, Quality preset (High / Balanced / Small file), Bitrate (shown only with Custom), Aspect ratio |
| Audio | Quality preset, Bitrate, Sample rate, Keep metadata & artwork (toggle) |
| Image | Size (Original / Custom W × H with lock), Quality slider, Keep transparency (toggle), Rotate / Flip buttons |
| Docs / PDF | Pages (All / Range…), Merge into one PDF (toggle), Compression |

- Above the controls, show one plain line of what will happen: "MP4 · H.264 · 1080p · about 120 MB".
- Formats with known limits show an inline caption, e.g. "PDF to DOCX keeps text and images. Complex layouts may shift." Use the `caption` role, not a warning color.

## Convert sheet / dialog

Specified in `05-layout.md`. Notes:
- Title row: tile 40 + file name + secondary meta. For several files: "12 files" with a stacked preview of up to 3 tiles.
- Primary button "Convert" (system accent, default button). Secondary "Cancel".
- "Move original to Trash/Recycle Bin" checkbox, unchecked by default, remembered between uses.

## Footer bar (batch)

- Height 52, top separator, padding x4.
- Left: "Save to" popup (Same folder as original / Choose Folder… / recent folders) and the "Move originals to Trash" checkbox.
- Right: **Clear** (bordered) and **Convert All** (accent, default).
- While converting: an accent progress bar (overall) with "Converting 3 of 12…" and **Stop**.

## Completion feedback

- In-app: the footer shows "12 files converted · Show in Finder" for 6 s, then returns to idle.
- When the app isn't frontmost, send a system notification with the same text plus a "Show" action.
- macOS: Dock badge with the remaining count during a batch. Windows: taskbar progress via `ITaskbarList3` (normal/error states).
- No confetti and no sound beyond the system notification sound.

## Errors

- Per-file problems stay in the row. Batch problems use the platform banner: a macOS inline banner above the list (a custom view with error glyph and `surfaceRaised` background) or a WinUI `InfoBar` (Severity Error).
- Write what happened and what to do: "Not enough space on 'Macintosh HD'. Free up 2.1 GB or choose another folder."

## Dropper: URL bar

- Sunken well: background `surfaceSunken`, 1-px `borderStrong`, radius md, height 36 (macOS) / 40 (Windows), leading `link` glyph in `textTertiary`.
- Placeholder "Paste a video link". On focus, if the clipboard holds a URL, offer a one-click "Paste link" chip inside the field.
- **Fetch** = accent button inside the trailing edge. Return triggers it.
- Invalid link: the error caption below the field reads "That link doesn't point to a supported video." The border turns `error`.

## Dropper: media card

- 16:9 thumbnail 96 × 54, radius 6. Title (fileName role, two lines max, end truncation), meta "12:48 · 1080p available" (secondary).
- Loading: thumbnail and lines as `surfaceRaised` skeleton blocks. Use a shimmer only if reduce motion is off.

## Dropper: output mode

- Segmented control with two segments: [tile-20 Video] and [tile-20 Audio]. Selection follows the native control (accent). Video shows Format + Quality, Audio shows Format + Bitrate.

## Menu bar extra (macOS) / tray icon (Windows)

Optional. Off by default, toggled in Settings.
- Icon: the template / tray `.ico` from the generated assets.
- Menu: "Convert Files…", "Open Potion", recent conversions (up to 5, each with its tile), separator, "Settings…", "Quit Potion" (macOS) / "Exit" (Windows).

## File manager integration

- **macOS:** a Quick Action ("Convert with Potion") via a Finder Action extension or Services. Its menu icon is the mono small mark. Choosing it opens the convert sheet pre-filled.
- **Windows:** an Explorer context menu entry "Convert with Potion" (`IExplorerCommand`, packaged with sparse/MSIX for the Windows 11 top-level menu). Icon: `Potion.ico`.

## Onboarding (first launch only)

One window, three short panels, skippable:
1. Splash animation (`08-motion.md`) + `brandDisplay` "potion" + "Any file in. The format you need out."
2. The four category tiles at 64 with labels, plus "Drop anything. Potion shows you what it can become."
3. The privacy line, "Add Quick Action" / "Add to context menu" toggle, and **Get Started**.

Background `background` token, centered content, max width 480.

## About window

Mark at 96 (LogoMark asset), product name in `brandDisplay` at 28, version in caption, privacy line, links. Plays the splash animation once on open (reduce motion: static).
