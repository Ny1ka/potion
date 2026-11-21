# 08 · Motion

Motion confirms what happened. It never decorates. Most of it should come for free from native controls.

## Tokens

| Duration | ms | Use |
|---|---|---|
| fast | 120 | Hover, drag-over highlight, checkmark appear, reduce-motion fades |
| base | 200 | Row insert/remove, disclosure expand, segmented change |
| slow | 320 | Sheet / dialog content, onboarding panel change |
| splashOnce | 1300 | Full build-up animation |

| Easing | Cubic bezier | Use |
|---|---|---|
| standard | (0.2, 0.8, 0.2, 1) | Default for everything entering or changing |
| emphasized | (0.3, 1.3, 0.5, 1), slight overshoot | Neck drop in the splash; success checkmark scale |
| exit | (0.4, 0, 1, 1) | Things leaving (row removed, toast dismissed) |

Swift: `PotionMotion.standard()`, `.emphasized()`, `.exit()`. WinUI: `PotionMotion.Standard` etc. (control points for `CubicBezierEasingFunction`), or the built-in theme transitions (`AddDeleteThemeTransition`, `EntranceThemeTransition`), which are preferred for lists.

## Specific transitions

| Event | Motion |
|---|---|
| Files dropped | Rows fade + rise 8 px, staggered 30 ms per row (max 10 rows animated, the rest appear instantly), `base`, standard |
| Drag enters window | Drop-zone border/fill crossfade to accent, `fast` |
| Row progress | Bar width animates linearly with real progress. Never fake progress. |
| Row completes | Percentage crossfades to check + "Done", check scales 0.6 → 1 with emphasized, `fast` |
| Row removed | Fade + collapse height, `base`, exit |
| Batch completes | Footer text crossfade only |
| Advanced disclosure | Native disclosure animation |
| Theme change | None (instant) |

## Splash / build-up animation

Reference: `splash/splash.html`. Used in **onboarding panel 1** and the **About window**. Don't use it as a launch splash screen: the apps should open straight to a usable window.

| t (ms) | Element | Change |
|---|---|---|
| 0 → 420 | Docs band (bottom) | translateY 16 → 0, opacity 0 → 1, standard |
| 150 → 570 | Video band | same |
| 300 → 720 | Audio band | same |
| 450 → 870 | Image band (top) | same |
| 600 → 900 | Neck | translateY −12 → 0, opacity 0 → 1, emphasized |
| 750 → 1150 | Wordmark / tagline | translateY 10 → 0, opacity 0 → 1, standard |

Bands are clipped by the bulb circle, so they appear to fill the flask from below. Total ≈ 1.3 s, plays once.

Implementation:
- **SwiftUI:** draw with `PotionMark`-style shapes; animate each band's offset/opacity with `.animation(PotionMotion.standard(0.42).delay(…))`, triggered `onAppear`.
- **WinUI:** a `Canvas` with an elliptical `Clip` and four `Rectangle`s; animate with `Storyboard` (`DoubleAnimation` on `TranslateTransform.Y` and `Opacity`, `KeySpline` from the easing tokens) or Composition `Vector3KeyFrameAnimation`.

## Reduce motion

When macOS "Reduce motion" (`accessibilityDisplayShouldReduceMotion`) or Windows "Animation effects" off (`UISettings.AnimationsEnabled == false`) is set:
- Replace movement with a `fast` opacity crossfade, or show the end state immediately.
- Splash: show the finished mark and wordmark immediately.
- Skeleton shimmer: static blocks.
- Progress bars still update; they are information, not decoration.
