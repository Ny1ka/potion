# core/spec

Data shared by both platform cores. Both `core/swift` and `core/dotnet` load these files and run the same
test cases, so macOS and Windows agree on what a file is and what it can become.

Planned contents (M1):

| File | Contents |
|---|---|
| `formats.json` | Every supported format: id, extensions, MIME types, category, magic-byte signatures |
| `matrix.json` | Allowed conversions per input format, with any limitation notes shown before converting |
| `presets.json` | Quality presets (audio bitrates, video resolutions, image quality) |
| `tests/*.json` | Conformance cases: input bytes or name → expected detection or offered outputs |

`design-system/data/formats.json` (extension → category, for colors and glyphs) is the starting point for
`formats.json`. The design-system file stays the source for visual category; this folder adds behavior.
