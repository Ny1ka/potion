# 04 · Typography

## Families

| Use | macOS | Windows |
|---|---|---|
| All UI text | System (SF Pro) via text styles | Segoe UI Variable via WinUI text styles |
| Extensions / format badges | System monospaced (SF Mono) | Cascadia Mono, falling back to Consolas |
| Brand moments only | Space Grotesk Bold / Medium | Space Grotesk Bold / Medium |

Space Grotesk is allowed only for: the onboarding hero headline, the About window product name, empty marketing surfaces, and the website. It never appears in controls, menus, lists, dialogs or body copy. The logo wordmark is already outlined, so never typeset it.

## Roles

| Role | macOS | Windows | Weight | Notes |
|---|---|---|---|---|
| `brandDisplay` | Space Grotesk 40 | Space Grotesk 40 | 700 | tracking −0.03em, line height 1.05 |
| `title` | `.title3` semibold (17) | `SubtitleTextBlockStyle` (20) | 600 | Sheet/dialog titles, section titles in Settings |
| `sectionHeader` | `.headline` (13) | `BodyStrongTextBlockStyle` (14) | 600 | "Output", "Advanced" groups |
| `body` | `.body` (13) | `BodyTextBlockStyle` (14) | 400 | Default |
| `secondary` | `.subheadline` (11) | `CaptionTextBlockStyle` (12) | 400 | File metadata "MOV · 48.2 MB" in `textSecondary` |
| `caption` | `.caption` (10) | `CaptionTextBlockStyle` (12) | 400 | Helper text in `textTertiary` |
| `formatBadge` | mono 10 | mono 11 | 600 | UPPERCASE, tracking +0.04em |
| `fileName` | `.body` medium (13) | Body, SemiBold (14) | 500 | Truncate in the middle |

Swift: `PotionFont.title`, `.sectionHeader`, `.body`, `.secondary`, `.caption`, `.fileName`, `.formatBadge`, `PotionFont.brand(_:)`.
WinUI: use the built-in styles named above, `PotionMonoFontFamily`, `PotionBrandBoldFontFamily`.

Always use the text style instead of a fixed point size, so the text follows the user's system size settings (Windows Settings › Accessibility › Text size; macOS sidebar/list size preferences).

## Details

- **File names truncate in the middle** ("Interview_fin…_EXPORT.mov") so the extension stays visible. Use `.truncationMode(.middle)` on macOS and `TextTrimming` plus a custom middle-ellipsis converter on Windows (WinUI only trims at the end natively).
- **Tabular digits** for progress percentages, sizes, durations and counts: `.monospacedDigit()` on macOS, `Typography.NumeralAlignment="Tabular"` on Windows (Segoe UI Variable supports it; check the attached property in your Windows App SDK version).
- **Capitalization:** macOS buttons and menus use Title Case, Windows uses Sentence case (see `01-brand.md`). Format names are always uppercase (MP4, HEIC), except brand-cased names like "Markdown".
- No all-caps labels besides format badges. Never fake bold.

## Registering the brand font

**macOS:** add both `.ttf` files to the app bundle (e.g. `Resources/Fonts/`) and set Info.plist `ATSApplicationFontsPath` = `Fonts`. Reference them by PostScript name: `SpaceGrotesk-Bold`, `SpaceGrotesk-Medium`.

**Windows:** the fonts are copied into `Assets/Fonts/`. Reference them as:
- Bold: `ms-appx:///Assets/Fonts/SpaceGrotesk-Bold.ttf#Space Grotesk`
- Medium: `ms-appx:///Assets/Fonts/SpaceGrotesk-Medium.ttf#Space Grotesk Medium`

(The family names really do differ, because the static Medium file declares its own family.) Both are already defined in `PotionTheme.xaml`.
