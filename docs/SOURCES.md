# Potion Dropper sources

Potion Dropper downloads only from the sources listed here (spec Part 6.3). Each source is one adapter module in a
`SourceAdapters` folder, plus an entry in `core/spec/sources.json`. Both cores refuse to start with an adapter that
isn't in `sources.json`, and tests in both cores fail when a `sources.json` entry has no section here or the review
dates differ.

**No entry, no adapter, no download.** To add a source:

1. Read the site's terms of service and download policy. The source qualifies only if the terms or an official
   feature allow downloading (for example a download option the uploader enabled, or a freely licensed or
   public-domain collection), or if it serves the user's own exports through official tools.
2. Add a section below with every field filled in. "Approved by" must be the owner.
3. Add the source to `core/spec/sources.json` (same id and review date), then write the adapter in both cores.
4. If the source provides license information, show it on the media card (spec Part 6.3.7).

The major video and social platforms excluded by the spec are never added to this repository, its builds or its
releases (see the root `CLAUDE.md`, "Source policy"). Requests to add them go to the owner with the warnings from
spec Part 6.3.3.

## Rules for every source

- Only what the user is allowed to download: no DRM or encryption bypass, no deciphering meant to defeat access
  controls, no login, paywall, age-gate or geo-block bypass, no browser cookies, no livestreams or premium
  content the user hasn't bought for offline use.
- An honest User-Agent (`PotionDropper/<version> (+https://github.com/Ny1ka/potion)`, from `sources.json`); no
  browser impersonation. Respect rate limits (`429`/`503` with `Retry-After`) and robots rules.
- Links that contain a user name or password are refused, so Potion Dropper never handles credentials.
- An unsupported site gets one neutral message: "Potion Dropper can't download from this site." No workarounds.
- Before the first download the user accepts the acknowledgment in spec Part 6.3.5.

## Direct media links

- Source id: `direct`
- Adapter: `DirectLinkAdapter` in `core/swift/Sources/PotionCore/Dropper/SourceAdapters` and
  `core/dotnet/Potion.Core/Dropper/SourceAdapters`
- Site: any web address whose response is itself an audio or video file
- What's downloaded: the single file at that address, exactly as the server sends it, then converted locally if
  the user picks another format
- Terms or policy that permits it: spec Part 6.3.1, first item: "direct links to media files (the URL points at an
  actual `.mp4`/`.mp3`/… file served without login or protection)". The server publishes the file at that address
  for anyone to fetch, and the user confirms the acknowledgment that they have the right to download it
- Limits: `http`/`https` only; no credentials in the link; no streaming playlists (`.m3u8`, `.mpd` and similar); the
  response must be an audio or video file (checked from the server's answer and the file's content, not only the
  extension); no login, cookies or protected content; a server that asks for authentication (`401`/`403`) gets
  reported, never retried with credentials
- Reviewed: 2026-11-22 (Claude, against spec Part 6.3)
- Approved by: the owner, who lists direct media links in the default V1 scope (spec Part 6.3.1)

How links are matched (details in `core/spec/README.md`, "Links"): a file name with an audio or video extension is
accepted as that format; other known file types (images, PDFs, documents) are refused as "not an audio or video
file"; anything else (a page, `/download?id=4`) is accepted only if the server's answer is an audio or video file.
