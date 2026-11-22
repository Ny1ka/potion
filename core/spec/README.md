# core/spec

Data shared by both platform cores. `core/swift` and `core/dotnet` load these files and run the same test
cases, so macOS and Windows agree on what a file is and what it can become.

| File | Contents |
|---|---|
| `formats.json` | Every supported format (id, display name, category, extensions, MIME types) and the content-detection rules |
| `tests/detection.json` | Conformance cases for detection. Both cores must pass all of them |
| `matrix.json` | Allowed conversions, the engine operation for each, and limitation notes shown before converting |
| `tests/matrix.json` | Conformance cases for the matrix |
| `presets.json` | Audio, video, image and PDF presets, encoders and per-format settings |
| `tests/pages.json` | Conformance cases for page selections ("1-3, 5") used by the PDF tools |
| `sources.json` | Potion Dropper's source allowlist, link limits, User-Agent and link error messages |
| `tests/links.json` | Conformance cases for link parsing, normalization and source matching |

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

### Platforms

A format may list `platforms` (e.g. `"platforms": ["macos"]` on DOC, which has no reader or writer on Windows).
Detection still recognizes it everywhere, so the app can explain why it can't convert the file, but the matrix
built for another platform leaves out every conversion from or to it. Formats without the field work everywhere.

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

## matrix.json

Only conversions the matrix lists are ever offered. It's written as **routes** instead of a 48 × 48 table.

| Field | Meaning |
|---|---|
| `groups` | Named lists of format ids, referenced as `@name` |
| `routes[].from`, `routes[].to` | Lists of references: a format id (`mp4`), a group (`@audioOut`) or every format in a category (`#video`) |
| `routes[].operation` | What the engine does: `transcodeVideo`, `videoToGif`, `extractAudio`, `transcodeAudio`, `convertImage`, `imageToPdf`, `gifToVideo`, `convertDocument`, `documentToPdf`, `pdfToDocument`, `pdfToImage` |
| `routes[].sameFormat` | `true` allows a format to convert to itself (re-encode, resize, compress). Default `false` |
| `routes[].note` | Id of a limitation in `notes`, shown before converting |
| `notes` | User-facing text for each note id (follows the design system's voice rules) |

### Rules

1. **Routes with an `operation` create conversions** for every `from` × `to` pair (skipping `from == to`
   unless `sameFormat`). Two routes may not give the same pair different operations; loading fails.
2. **Every route's `note` is attached** to the pairs it covers that step 1 created, in route order, without
   duplicates. Routes with only a `note` annotate; they never create a conversion.
3. **Outputs for one input** are listed with the input's own category first, then the rest, each in
   `formats.json` order.
4. **Outputs for a batch**: an output qualifies when every input either converts to it or is already in that
   format (those files are skipped), and at least one input actually converts. Same ordering, using the first
   input's category.

## presets.json

`audio.presets` are the quality choices shown to users (High 320 kbps, Standard 192 kbps, Small 128 kbps).
`audio.formats.<id>` says how to write each audio output with FFmpeg:

| Field | Meaning |
|---|---|
| `muxer` | FFmpeg output format (`-f`), e.g. `ipod` for M4A/ALAC, `adts` for raw AAC |
| `encoder` | FFmpeg encoder per platform (`macos`, `windows`) with a `default` fallback. Patent-encumbered codecs use the OS encoder (`aac_at`, `aac_mf`) |
| `highBitDepthEncoder` | Used instead when the source has more than 16 bits per sample (WAV, AIFF) |
| `bitrates` | Allowed bitrates in kbps for lossy formats. A preset or requested bitrate snaps to the nearest one (ties go lower) |
| `sampleRates` | Allowed output sample rates. The source rate is kept when allowed, otherwise snapped to the nearest |
| `lossless` | No bitrate option |
| `artwork` | The container can keep cover art (copied when converting audio, never taken from video frames) |

`video` describes video outputs:

| Field | Meaning |
|---|---|
| `presets` | Original quality (copy streams when nothing changes, otherwise match the source bitrate), High, Standard, Small (bits per pixel used to size the bitrate) |
| `codecs.<id>` | Display `name`, encoder per platform, `efficiency` (bitrate multiplier vs. H.264), container `tag`, `standardFrameRates` (MPEG-2) |
| `audioCodecs.<id>` | Encoder and bitrate used when a container needs its audio re-encoded |
| `formats.<id>` | `muxer`, offered `videoCodecs` (first is the default), `audioCodec`, source codecs that can be copied (`copyVideo`, `copyAudio`, `*` = any), `faststart` |
| `gif.<preset>` | Frame rate and maximum width for video → GIF |

Rules both engines follow: never upscale; only lower the frame rate; aspect ratios letterbox instead of
stretching; copy audio whenever the container accepts it; use the display size of rotated (portrait) video.

`image` describes image outputs: quality `presets` (0–1, for the `lossy` formats), which formats keep
`transparency` (others are composited onto white), `icoMaxSize` (icons are square, use a standard size up to
256 px, and are never enlarged), and `pdfDefaultDPI` (page size when an image has no DPI).

`pdf` holds the PDF tools' settings: `renderDPI` (default and offered resolutions for PDF → image),
`maxRenderPixels` (longest side of a rendered page; higher resolutions are scaled down to fit) and
`compression.levels` (Standard recompresses images as JPEG; Smallest also reduces them to screen resolution).

## Page selections (tests/pages.json)

Users type pages as `1-3, 5`. Items are separated by commas or semicolons, spaces don't matter, and empty
items are skipped. An item is a page (`5`), a range (`1-3`, also with an en dash), `4-` (to the last page) or
`-2` (from the first). The result is 1-based pages in the order typed, without duplicates, so the same syntax
serves "extract pages" and "reorder". An empty selection means every page. Pages outside the document,
reversed ranges and anything else are errors.

## Links and sources (sources.json, tests/links.json)

Potion Dropper downloads only from allowlisted sources (`docs/SOURCES.md`). `sources.json` lists them in matching
order; each `id` must have an adapter in both cores (in a `SourceAdapters` folder) and a section in
`docs/SOURCES.md` with the same review date. Both cores refuse to load with an adapter that isn't listed.

| Field | Meaning |
|---|---|
| `userAgent` | Sent with every request; `{version}` is the app version. Honest, never a browser's |
| `limits.maxLinkLength` | Longest normalized link accepted |
| `limits.maxLinksPerPaste` | Links kept from one paste or drop; the rest are counted as `overLimit` |
| `playlistExtensions` | Streaming playlists and manifests, refused |
| `sources[]` | `id`, `name`, the `categories` it provides, and the `reviewed` date |
| `messages` | User-facing text for each link error |

Both cores parse links with their own code (not the platform URL parser), so they agree byte for byte.

### Parsing one link

1. Trim whitespace and one pair of surrounding `< >`. Empty → `notALink`.
2. Scheme: text before `://` if it's a valid scheme (a letter, then letters, digits, `+ - .`), lowercased.
   Without `://`, a link that starts with an obvious host (an ASCII name with a dot and a letters-only last label,
   or a dotted IPv4 address, optionally `:port`) gets `https`. Otherwise `scheme:` → `unsupportedScheme`, anything
   else → `notALink`. Only `http` and `https` are accepted (`unsupportedScheme`).
3. The authority runs to the first `/`, `?` or `#`. An `@` in it → `credentials`.
4. Host: `[IPv6]` (hex digits, `:`, `.`; lowercased) or a name: one trailing dot removed, lowercased, NFC,
   non-ASCII labels in Punycode (`xn--`), each label 1–63 characters of letters, digits, `-`, `_` without a leading
   or trailing hyphen, 253 characters at most. Otherwise `invalidHost`.
5. Port: digits, 1–65535 (`invalidPort`); empty is ignored; the scheme's default (80, 443) is dropped.
6. The fragment is dropped. Path (at least `/`) and query keep valid `%XX` escapes and the characters
   `A–Z a–z 0–9 - . _ ~ ! $ & ' ( ) * + , ; = : @ /` (plus `?` in the query); everything else is encoded as UTF-8
   `%XX` with uppercase hex, including a `%` that doesn't start an escape. An empty query is dropped.
7. A normalized link longer than `maxLinkLength` → `tooLong`.
8. The file name is the last path segment, percent-decoded; its extension follows the detection rules (lowercase,
   text after the last dot, none when the only dot is first). A playlist extension → `playlist`.
9. Each source's adapter is asked in order; the first that answers decides. The direct-links adapter answers every
   link: a video or audio extension is accepted as that format; a non-text format of another category → `notMedia`;
   anything else is accepted with `needsCheck` (the download decides from the server's answer). No adapter answers
   → `unsupportedSite`.

### Finding links in pasted text

Split on whitespace; remove leading `< ( [ { " ' “ ‘ «` and trailing `> ) ] } " ' . , ; : ! ? ” ’ »`. A word is
a link candidate if it contains `://` or starts with an obvious host (step 2); other words are ignored. Candidates
that fail are reported with their error; good links are kept in order without duplicates (compared after
normalizing), up to `maxLinksPerPaste`.
