# 05 · Layout

## Spacing (4-pt grid)

| Token | px | Typical use |
|---|---|---|
| x0.5 (`xHalf`) | 2 | Badge vertical padding |
| x1 | 4 | Icon-to-label inside a chip |
| x2 | 8 | Between related controls |
| x3 | 12 | Tile ↔ text in a row; field label ↔ control |
| x4 | 16 | Container padding (sheets, cards, rows' horizontal inset) |
| x5 | 20 | Window content inset on macOS |
| x6 | 24 | Window content inset on Windows; between groups |
| x8 | 32 | Between major sections |
| x10–x16 | 40–64 | Onboarding / About only |

Swift `PotionSpacing.x4`, XAML `{StaticResource PotionSpace4}`, C# `PotionSpacing.X4`.

## Radius

| Token | Value | Use |
|---|---|---|
| xs | 4 | Format badges, checkboxes you draw |
| sm | 6 macOS / 4 Windows | Custom controls that sit next to stock ones (match the platform) |
| md | 8 | Category tiles (28 px), cards, thumbnails, URL field |
| lg | 12 | Drop zone, custom sheets/popovers on macOS (WinUI overlays use its own 8) |
| xl | 16 | Onboarding panels |
| iconTileRatio | 0.225 | App-icon-like tiles (side × 0.225) |

Category tile radius scales with size: 0.286 × side (8 at 28, ~11 at 40). macOS uses `.continuous` corners.

## Sizes

| Element | Value |
|---|---|
| Category tile | 20 (dense lists) · **28 (default rows)** · 40 (convert sheet header) · 64 (empty/success states) |
| Glyph inside tile | 57% of tile (12 · 16 · 22 · 34) |
| Queue row height | 44 compact · **56 regular** |
| Progress bar (per row) | 4 tall, radius 2 |
| Drop zone min height | 220 |
| Convert sheet width | 460 |
| Min hit target | 28 macOS · 32 Windows (40 for touch-friendly toolbar buttons) |
| Main window | macOS 720 × 520 (min 560 × 420) · Windows 820 × 580 (min 600 × 440) |

## Screen layouts

### Potion: main window

```
┌──────────────────────────────────────────────────────────────┐
│ ● ● ●   Potion                         [+ Add] [⚙︎]        │  toolbar (unified, compact)
├──────────────────────────────────────────────────────────────┤
│                                                              │
│   EMPTY:  Drop zone (fills window, inset x5 / x6)            │
│                                                              │
│   WITH FILES:                                                │
│   ┌──────────────────────────────────────────────────────┐   │
│   │ [tile] Interview.mov           →  [MP4 ▾]   62% ▓▓░  │   │  queue rows (56)
│   │        MOV · 48.2 MB                                 │   │
│   │ [tile] voice.opus              →  [MP3 ▾]   ✓ Done   │   │
│   └──────────────────────────────────────────────────────┘   │
│   (the whole list stays a drop target: drop more to append)  │
├──────────────────────────────────────────────────────────────┤
│ Save to: [Same folder as original ▾]  ☐ Move originals to    │  footer bar
│                                Trash   [Clear] [Convert All] │
└──────────────────────────────────────────────────────────────┘
```

- During a batch, the footer shows overall progress ("Converting 3 of 12…" + accent progress bar) and **Convert All** becomes **Stop**.
- **Batch "convert all to…" shortcut:** when every row shares a category, a "Convert all to [MP3 ▾]" popup appears in the footer.
- Rows support multi-select. Delete/Backspace removes rows, and the context menu has Change Format, Show in Finder and Remove.

### Potion: convert sheet (single file, file-manager entry point)

Matches the existing mock: 460 wide, padding x4.

```
[tile 40] Interview.opus                    ← fileName + secondary "Audio · 2.1 MB"
          Convert to   [MP3            ▾]   ← label aligned to text column (indent 52)
          › Advanced                        ← disclosure: bitrate, sample rate, etc.
────────────────────────────────────────────
☐ Move original to Trash        [Cancel] [Convert]
```

macOS: a small standalone window or sheet with Return = Convert and Esc = Cancel. Windows: a `ContentDialog` with Primary = "Convert" and Close = "Cancel".

### Potion Dropper: main window

```
┌──────────────────────────────────────────────────────────────┐
│ [🔗  Paste a video link                       ] [Fetch]      │  URL bar (sunken well, radius md)
├──────────────────────────────────────────────────────────────┤
│ [thumb 16:9 96×54]  Title of the media                       │  media card (after fetch)
│                     12:48 · 1080p available                  │
│ [◉ Video | ○ Audio]   Format [MP4 ▾]   Quality [1080p ▾]     │  output options
├──────────────────────────────────────────────────────────────┤
│ Downloads                                                    │  queue (same row component as Potion)
│ [tile] Title…        Downloading 40% ▓▓▓░░   [✕]            │
└──────────────────────────────────────────────────────────────┘
```

Video/Audio is a segmented control. Each segment carries its small category tile (20). Selection uses the system accent, not the category color.

### Settings

A standard platform Settings window. macOS: `Settings` scene with a tabbed toolbar (General, Output, Advanced). Windows: a single page with `SettingsCard`-style rows (Community Toolkit) or plain grouped `StackPanel`s. No brand color here.

## Resizing

- Rows stretch. The file name column takes all free space and truncates in the middle.
- Below 640 px wide (Windows) or at min width (macOS), the format popup and status stack under the file name.
- The drop zone keeps a min height of 220 and is otherwise flexible.
