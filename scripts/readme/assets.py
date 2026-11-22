#!/usr/bin/env python3
"""README graphics generated from the design system (tokens.json and icons/category), in light and dark variants:
category tiles and the download button. Nothing in design-system/ is copied or edited.

  scripts/readme/assets.py   → docs/images/readme/tile-<category>-<theme>.svg, download-<theme>.svg
"""
import json
import pathlib
import re

ROOT = pathlib.Path(__file__).resolve().parents[2]
TOKENS = json.loads((ROOT / "design-system" / "tokens" / "tokens.json").read_text(encoding="utf-8"))
ICONS = ROOT / "design-system" / "icons" / "category"
OUT = ROOT / "docs" / "images" / "readme"


def rgba(hex_color, alpha):
    h = hex_color.lstrip("#")
    r, g, b = (int(h[i:i + 2], 16) for i in (0, 2, 4))
    return f"rgba({r},{g},{b},{alpha})"


def glyph(name):
    """The icon's drawing elements (the <svg> wrapper and its currentColor stroke are replaced)."""
    svg = (ICONS / f"{name}.svg").read_text(encoding="utf-8")
    return re.search(r"<svg[^>]*>(.*)</svg>", svg, re.S).group(1)


def tile(category, theme, size=48):
    brand = TOKENS["brand"]["category"][category]
    tint = rgba(brand["fill"], TOKENS["brand"]["categoryTintAlpha"][theme])
    stroke = brand["textOnLight"] if theme == "light" else brand["textOnDark"]
    radius = round(size * 0.286, 2)   # PotionCategoryTile's corner ratio
    inset = size * 0.215              # glyph at ~57 % of the tile, as in the apps
    scale = (size - 2 * inset) / 24
    return (
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size}" viewBox="0 0 {size} {size}" '
        f'role="img" aria-label="{brand["label"]}">'
        f'<rect width="{size}" height="{size}" rx="{radius}" fill="{tint}"/>'
        f'<g transform="translate({inset} {inset}) scale({scale})" fill="none" stroke="{stroke}" stroke-width="1.75" '
        f'stroke-linecap="round" stroke-linejoin="round">{glyph(category)}</g></svg>\n'
    )


def download_button(theme):
    ink, paper = TOKENS["brand"]["ink"], TOKENS["brand"]["paper"]
    background, text = (ink, paper) if theme == "light" else (paper, ink)
    width, height, radius = 248, 52, TOKENS["radius"]["lg"]
    return (
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}" '
        f'role="img" aria-label="Download Potion for Mac">'
        f'<rect width="{width}" height="{height}" rx="{radius}" fill="{background}"/>'
        f'<g transform="translate(22 14) scale(1)" fill="none" stroke="{text}" stroke-width="1.75" stroke-linecap="round" '
        f'stroke-linejoin="round">{glyph("download")}</g>'
        f'<text x="58" y="33" fill="{text}" font-family="-apple-system, BlinkMacSystemFont, \'Segoe UI\', Helvetica, Arial, sans-serif" '
        f'font-size="17" font-weight="600">Download for Mac</text></svg>\n'
    )


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    for theme in ("light", "dark"):
        for category in ("image", "audio", "video", "docs"):
            (OUT / f"tile-{category}-{theme}.svg").write_text(tile(category, theme), encoding="utf-8")
        (OUT / f"download-{theme}.svg").write_text(download_button(theme), encoding="utf-8")
    print(f"wrote README graphics to {OUT.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
