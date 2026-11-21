# core/spec

Data shared by both platform cores. `core/swift` and `core/dotnet` load these files and run the same test
cases, so macOS and Windows agree on what a file is and what it can become.

| File | Contents |
|---|---|
| `formats.json` | Every supported format (id, display name, category, extensions, MIME types) and the content-detection rules |
| `tests/detection.json` | Conformance cases for detection. Both cores must pass all of them |
| `matrix.json` | Allowed conversions per input format, with limitation notes (M1.2) |
| `presets.json` | Quality presets (M2) |

The Swift package can't read files outside its own folder, so `core/swift/Sources/PotionCore/Resources/`
holds byte-for-byte copies. Run `make sync-spec` after editing anything here. A test in both cores fails if
the copies drift. The .NET library embeds the files from this folder directly.

`design-system/data/formats.json` (extension → category, for colors and glyphs) must agree with the
categories here. It stays the source for visuals; this folder adds behavior.

## formats.json

### Formats

| Field | Meaning |
|---|---|
| `id` | Stable lowercase id used everywhere (matrix, settings, tests) |
| `name` | Text for format badges and menus ("MP4", "Markdown") |
| `category` | `image`, `audio`, `video` or `docs`. Decides the tile color and glyph |
| `extensions` | Lowercase, without the dot. The **first** one is used for output files. When two formats share an extension (`m4a` for M4A and ALAC), the format listed first in the file owns it for extension lookup |
| `mimeTypes` | For share sheets, pasteboards and file pickers |
| `text` | `true` for text formats (TXT, Markdown, HTML, SVG, RTF). Default `false` |
| `raw` | `true` for camera RAW formats. Default `false` |

### Detection algorithm

Inputs: the first `headerSize` bytes of the file (fewer if the file is shorter) and the file name. The
extension is the text after the last `.`, lowercased; a name whose only dot is the first character
(`.png`) has no extension.

1. **Empty file** → not supported (`method: none`).
2. **Is it text?** The header is text if it starts with a UTF-16 byte-order mark (`FF FE` or `FE FF`), or if
   it contains no bytes in `00–08`, `0E–1A` or `1C–1F` (tab, newlines, form feed and escape are allowed).
3. **Text with a text extension** → the extension's format (`method: extension`). For text, the user's
   extension is the best signal: a `.md` file containing `<svg>` is still Markdown.
4. **Content rules**, in file order. A rule matches when all its conditions hold. The first match wins
   (`method: content`). If the rule has `extensionHint` and the file's extension format is listed there,
   return the extension's format instead (TIFF-structured RAW files, ID3-tagged FLAC, generic MP4 brands
   in a `.mov`).
5. **Extension fallback**: if the extension maps to a format and that format's `text` flag equals the
   header's textness, return it (`method: extension`). A text file named `.ts` is TypeScript, not video.
6. Otherwise → not supported (`method: none`).

`mismatch` is true when a format was found, the file has an extension, and that extension isn't in the
found format's `extensions`.

### Rule conditions

| Condition | Matches when |
|---|---|
| `{ "offset": n, "hex": "…" }` | The bytes at `offset` equal the hex bytes |
| `{ "offset": n, "hex": "…", "mask": "…" }` | `(byte & mask) == (hex & mask)` for each byte |
| `{ "offset": n, "ascii": "…" }` | The bytes at `offset` equal the ASCII text |
| `{ "contains": { "ascii" or "hex": "…" }, "within": n }` | The value appears in the first `n` bytes. Add `"ignoreCase": true` for ASCII A–Z case-insensitivity |
| `{ "extension": ["…"] }` | The file's extension maps to one of these format ids |

A condition that reads past the end of the header doesn't match.
