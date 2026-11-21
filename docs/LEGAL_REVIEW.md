# Legal review log

Claude Code is not a lawyer and nothing here is legal advice. This file records decisions, open questions
and items that need review by a qualified lawyer before public release. The full audit and release gate
are completed in M8/M9.

## Decisions

| Date | Decision | Notes |
|---|---|---|
| 2026-11-21 | Project code is licensed under **MIT** (`/LICENSE`). | Compatible with LGPL FFmpeg linked dynamically or run as a separate process, OS frameworks, and GPL tools run as separate executables. AGPL libraries stay excluded. MIT grants no rights to the Potion name or logo; add a trademark note in M8. |

## Open questions

| # | Question | Needs lawyer? | Status |
|---|---|---|---|
| 1 | Mac App Store and Microsoft Store distribution for Potion: does shipping an LGPL FFmpeg satisfy the LGPL "user can replace the library" requirement inside a store package? | Yes | Open (decide before M9) |
| 2 | App Sandbox for Potion (macOS): needed for the Mac App Store, but it limits "save next to the original" and "move original to Trash" unless the user grants folder access. | No (product decision) | Open (decide in M3) |

## Items for lawyer review before public release

- Terms of use / EULA (`legal/TERMS.md`, M8)
- Privacy policy (`legal/PRIVACY.md`, M8)
- Potion Dropper source policy (`docs/SOURCES.md`, M5)
