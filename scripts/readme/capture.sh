#!/usr/bin/env bash
# Renders the README screenshots from the real Potion app (Debug build, macOS), in light and dark mode.
# The app runs a scripted tour with generated sample files and snapshots its own windows (see
# apps/potion-macos/Sources/ReadmeShots.swift); settings go to a throwaway preferences domain.
#   scripts/readme/capture.sh          → docs/images/readme/*.png and demo-*.gif
# Needs the LGPL FFmpeg build (scripts/ffmpeg/build-macos.sh) for the video and audio samples and the GIFs.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
make potion-macos >/dev/null
APP="build/DerivedData/Build/Products/Debug/Potion.app/Contents/MacOS/Potion"
FFMPEG="build/ffmpeg/macos/bin/ffmpeg"
OUT="docs/images/readme"
RAW="$(mktemp -d)"
trap 'rm -rf "$RAW"' EXIT
# Launch-argument overrides apply to this run only and are never saved.
ARGS=(-convertSheet.showAdvanced YES -includeSubfolders YES -notifyWhenDone YES -settings.tab output
      -lastOutput.image "" -lastOutput.audio "" -lastOutput.video "" -lastOutput.docs "")

for theme in light dark; do
  echo "· $theme: onboarding"
  POTION_README_SHOTS="$RAW" POTION_README_THEME=$theme POTION_README_SCENE=onboarding "$APP" -onboardingDone NO "${ARGS[@]}"
  echo "· $theme: main tour"
  POTION_README_SHOTS="$RAW" POTION_README_THEME=$theme POTION_README_SCENE=main "$APP" -onboardingDone YES "${ARGS[@]}"
done

mkdir -p "$OUT"
for f in "$RAW"/*.png; do
  # 1440-px-wide images keep text sharp on Retina screens without bloating the repo.
  sips --resampleWidth 1440 "$f" --out "$OUT/$(basename "$f")" >/dev/null
done

# Animated demo: queue → converting → done, per theme (palette GIF, a few hundred KB).
for theme in light dark; do
  list="$RAW/frames-$theme.txt"
  : > "$list"
  printf "file '%s'\nduration 1.6\n" "$RAW/queue-$theme.png" >> "$list"
  for frame in "$RAW"/frames-"$theme"/converting-*-"$theme".png; do printf "file '%s'\nduration 0.5\n" "$frame" >> "$list"; done
  printf "file '%s'\nduration 2.4\n" "$RAW/done-$theme.png" "$RAW/done-$theme.png" >> "$list"
  "$FFMPEG" -hide_banner -loglevel error -y -f concat -safe 0 -i "$list" \
    -vf "scale=960:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=128:stats_mode=diff[p];[b][p]paletteuse=dither=sierra2_4a" \
    -loop 0 "$OUT/demo-$theme.gif"
done
ls -la "$OUT"
