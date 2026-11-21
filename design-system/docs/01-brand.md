# 01 · Brand

## Names

| Context | Potion | Potion Dropper |
|---|---|---|
| Product name in text, menus, About, installer | Potion | Potion Dropper |
| Wordmark (logo only) | `potion` (lowercase, outlined art) | `potion` + `dropper` (second word in tertiary color) |
| macOS bundle display name | Potion | Potion Dropper |
| macOS bundle id (suggested) | `com.<you>.potion` | `com.<you>.potion-dropper` |
| Windows executable / package display name | Potion | Potion Dropper |

- Write the name in Title Case in running text: "Potion", never "POTION", "potion" or "PotionApp".
- Lowercase belongs only to the wordmark artwork.
- Short form for the second app is "Dropper". Use it only after the full name has already appeared on the same screen.

## Concept

A potion transforms whatever goes into it. The flask holds four layers, one per kind of file the app understands:

| Band (top → bottom) | Category | Formats (examples) |
|---|---|---|
| Teal | Image | PNG, JPG, HEIC, WEBP, TIFF, SVG, RAW |
| Orange | Audio | MP3, WAV, FLAC, M4A, OPUS |
| Pink | Video | MP4, MOV, MKV, WEBM |
| Violet | Docs | PDF, DOCX, EPUB, TXT, Markdown |

The brand is two things: the mark, and the strict meaning of those four colors. Everything else defers to the operating system.

## Personality

- **Quiet:** a utility that feels like it came with the OS. No mascots, no gradients, no confetti.
- **Precise:** says exactly what will happen ("Converts to MP3 at 192 kbps"), never vague ("Optimizing your media…").
- **Private by default:** files never leave the machine. Say so once, plainly. Don't build fear-based copy around it.

## Voice and copy

Plain, short, confident. Write in second person only when needed, and never use exclamation marks.

| Situation | Write | Don't write |
|---|---|---|
| Empty state | Drop files or folders | Get started by dragging your amazing files here! |
| Primary action | Convert / Convert 12 Files (macOS) · Convert 12 files (Windows) | Start Magic |
| In progress | Converting 3 of 12… | Brewing your potion… |
| Done | 12 files converted. Show in Finder | All done!!! 🎉 |
| Partial failure | 11 converted, 1 failed. Review | Oops! Something went wrong |
| Error detail | Can't read "clip.mkv". The file may be damaged. | Error code 0x80004005 |
| Incompatible choice | MP3 can't be converted to PNG. | Invalid selection |
| Privacy line (onboarding, About) | Everything converts on this computer. Nothing is uploaded. | Military-grade privacy! |
| Dropper fetch | Fetching video info… | Hang tight! |

Rules:
- Use the ellipsis character `…` for in-progress states and for commands that open a dialog ("Choose Files…").
- Put file names in curly quotes: "Interview.mov".
- Numbers: "12 files", "1 file". Sizes in base-10 units with one decimal: 48.2 MB.
- Puns ("brewing", "potion master") are off-limits in the UI. They wear thin on the 50th conversion.

## Platform wording

| Concept | macOS | Windows |
|---|---|---|
| Deleted files go to | Trash ("Move original to Trash") | Recycle Bin ("Move original to Recycle Bin") |
| File manager | Finder ("Show in Finder") | File Explorer ("Show in folder") |
| Preferences | Settings… (⌘,) | Settings |
| Button / menu capitalization | Title Case ("Choose Files…", "Convert All") | Sentence case ("Choose files…", "Convert all") |
| Quit | Quit Potion (⌘Q) | Exit (in tray menu) |
| Context menu entry | Quick Actions › Convert with Potion | Convert with Potion |
