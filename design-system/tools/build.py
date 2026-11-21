#!/usr/bin/env python3
"""
Potion design system generator.

Reads tokens/tokens.json and (re)generates every derived file:
  tokens/tokens.css                      CSS custom properties (web, preview, marketing)
  logos/svg/*.svg, logos/png/*.png       marks, wordmarks, lockups, app icon masters
  icons/category/*.svg                   category + utility glyphs
  macos/<App>/Assets.xcassets            AppIcon, colorsets, LogoMark, MenuBarIcon (drop-in for Xcode)
  macos/<App>/AppIcon.icns               for non-Xcode builds
  macos/Shared/PotionDesign.swift        SwiftUI tokens + reference views
  windows/<App>/Assets/*                 .ico, MSIX logo assets, tray icons
  windows/Shared/PotionTheme.xaml        WinUI 3 ResourceDictionary (Dark/Light/HighContrast)
  windows/Shared/PotionDesign.cs         C# tokens
  data/formats.json                      extension -> category map
  splash/splash.html                     reference animation
  preview/index.html                     visual overview of the whole system

Requirements: Python 3.9+, fontTools, Pillow, rsvg-convert (librsvg), ImageMagick `convert`.
Usage:  python3 tools/build.py
"""
import json, math, shutil, subprocess
from pathlib import Path

from fontTools.ttLib import TTFont
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from fontTools.pens.boundsPen import BoundsPen
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
T = json.loads((ROOT / "tokens/tokens.json").read_text())
B = T["brand"]
CAT = B["category"]
ORDER = sorted(CAT, key=lambda k: CAT[k]["order"])  # top band -> bottom band: image, audio, video, docs
MODES = ("dark", "light")
INK = B["ink"]


# ---------------------------------------------------------------- color math
def rgba(h):
    h = h.lstrip("#")
    r, g, b = (int(h[i:i + 2], 16) for i in (0, 2, 4))
    a = int(h[6:8], 16) / 255 if len(h) == 8 else 1.0
    return r, g, b, a


def to_hex(r, g, b):
    return "#%02X%02X%02X" % (round(r), round(g), round(b))


def mix(fg, bg, alpha):
    f, b = rgba(fg), rgba(bg)
    return to_hex(*(f[i] * alpha + b[i] * (1 - alpha) for i in range(3)))


def luminance(h):
    def ch(c):
        c /= 255
        return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4
    r, g, b, _ = rgba(h)
    return 0.2126 * ch(r) + 0.7152 * ch(g) + 0.0722 * ch(b)


def contrast(a, b):
    la, lb = sorted((luminance(a), luminance(b)), reverse=True)
    return (la + 0.05) / (lb + 0.05)


def cap(s):
    return s[0].upper() + s[1:]


def tint(cat, mode):
    return mix(CAT[cat]["fill"], T["color"][mode]["surface"], B["categoryTintAlpha"][mode])


def cat_text(cat, mode):
    return CAT[cat]["textOnDark" if mode == "dark" else "textOnLight"]


# ---------------------------------------------------------------- io helpers
def write(rel, text):
    p = ROOT / rel
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(text)
    return p


def write_json(rel, obj):
    return write(rel, json.dumps(obj, indent=2) + "\n")


def render(svg_path, png_path, w, h=None):
    png_path.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run(["rsvg-convert", "-w", str(w), "-h", str(h or w), "-o", str(png_path), str(svg_path)], check=True)


def svg_doc(inner, view_box="0 0 100 100", title=None, extra=""):
    t = f"<title>{title}</title>" if title else ""
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="{view_box}" role="img"{extra}>'
            f"{t}{inner}</svg>\n")


# ---------------------------------------------------------------- mark geometry (100 x 100 box)
def potion_inner(neck, bands, small=False, uid="p"):
    """bands: 4 colors top->bottom, or 1 color for a one-color (mono) mark."""
    mono = len(set(bands)) == 1
    if len(bands) == 1:
        bands = bands * 4
    if not small:
        nx, ny, nw, nh, cy, r = 43, 12, 14, 18, 60, 28
        ys, bh = [32, 46.5, 61, 75.5], 12.5
    else:
        nx, ny, nw, nh, cy, r = 40, 6, 20, 20, 60, 30
        ys, bh = ([30, 47, 64, 81], 12) if mono else ([30, 45, 60, 75], 15)
    rects = "".join(f'<rect x="{50 - r - 2}" y="{y}" width="{2 * r + 4}" height="{bh}" fill="{c}"/>' for y, c in zip(ys, bands))
    return (f'<defs><clipPath id="{uid}"><circle cx="50" cy="{cy}" r="{r}"/></clipPath></defs>'
            f'<rect x="{nx}" y="{ny}" width="{nw}" height="{nh}" rx="3" fill="{neck}"/>'
            f'<g clip-path="url(#{uid})">{rects}</g>')


def dropper_inner(neck, band, lower, small=False, mono=False):
    if not small:
        return (f'<path d="M38 32 V24 A12 12 0 0 1 62 24 V32 Z" fill="{neck}"/>'
                f'<rect x="42" y="34.5" width="16" height="18" fill="{band}"/>'
                f'<path d="M42 55 H58 V64 L50 75 L42 64 Z" fill="{lower}"/>'
                f'<circle cx="50" cy="85" r="6" fill="{lower}"/>')
    if mono:
        return (f'<path d="M34 30 V22 A16 16 0 0 1 66 22 V30 Z" fill="{neck}"/>'
                f'<rect x="38" y="35" width="24" height="18" fill="{band}"/>'
                f'<path d="M38 58 H62 V66 L50 80 L38 66 Z" fill="{lower}"/>'
                f'<circle cx="50" cy="90" r="8" fill="{lower}"/>')
    return (f'<path d="M34 30 V22 A16 16 0 0 1 66 22 V30 Z" fill="{neck}"/>'
            f'<rect x="38" y="34" width="24" height="20" fill="{band}"/>'
            f'<path d="M38 54 H62 V64 L50 80 L38 64 Z" fill="{lower}"/>'
            f'<circle cx="50" cy="90" r="8" fill="{lower}"/>')


BANDS = [CAT[k]["fill"] for k in ORDER]
VIDEO, AUDIO = CAT["video"]["fill"], CAT["audio"]["fill"]


def mark(product, neck, small=False, mono_color=None, uid="m"):
    if product == "potion":
        return potion_inner(neck if not mono_color else mono_color,
                            [mono_color] if mono_color else BANDS, small, uid)
    if mono_color:
        return dropper_inner(mono_color, mono_color, mono_color, small, mono=True)
    return dropper_inner(neck, AUDIO, VIDEO, small)


# visible bounds of each mark inside the 100 box (x, y, w, h) — used for lockups
TRIM = {"potion": (22, 12, 56, 76), "dropper": (38, 12, 24, 79)}


# ---------------------------------------------------------------- wordmark outlines
FONT_BOLD = ROOT / "fonts/SpaceGrotesk-Bold.ttf"
FONT_MED = ROOT / "fonts/SpaceGrotesk-Medium.ttf"


def text_path(text, font_path, size=100.0, tracking_em=0.0, x0=0.0, y0=0.0):
    f = TTFont(str(font_path))
    gs, cmap, upm = f.getGlyphSet(), f.getBestCmap(), f["head"].unitsPerEm
    s = size / upm
    x, parts = x0, []
    bp = BoundsPen(gs)
    for i, ch in enumerate(text):
        g = gs[cmap[ord(ch)]]
        tr = (s, 0, 0, -s, x, y0)
        pen = SVGPathPen(gs)
        g.draw(TransformPen(pen, tr))
        parts.append(pen.getCommands())
        g.draw(TransformPen(bp, tr))
        x += g.width * s + (tracking_em * size if i < len(text) - 1 else 0)
    return " ".join(parts), bp.bounds, x - x0


def wordmark_svg(color, title="Potion"):
    d, (x1, y1, x2, y2), _ = text_path("potion", FONT_BOLD, 100, -0.05)
    pad = 0
    vb = f"{x1 - pad:.2f} {y1 - pad:.2f} {x2 - x1 + 2 * pad:.2f} {y2 - y1 + 2 * pad:.2f}"
    return svg_doc(f'<path d="{d}" fill="{color}"/>', vb, title)


def lockup_svg(product, mode):
    word = "#FFFFFF" if mode == "dark" else INK
    sub = T["color"][mode]["textTertiary"]
    neck = B["neck"]["onDark" if mode == "dark" else "onLight"]
    F = 100.0
    _, (bx1, by1, bx2, by2), _ = text_path("potion", FONT_BOLD, F, -0.05)
    ink_h = by2 - by1
    xh = TTFont(str(FONT_BOLD))["OS/2"].sxHeight * F / 1000
    tx, ty, tw, th = TRIM[product]
    mark_h = 0.9 * ink_h                    # mark = 90% of the word's ink height,
    mark_top = -xh / 2 - mark_h / 2         # centered on the x-height
    scale = mark_h / th
    mark_w = tw * scale
    gap = 0.30 * F
    mark_g = (f'<g transform="translate({-tx * scale:.3f} {mark_top - ty * scale:.3f}) scale({scale:.4f})">'
              f'{mark(product, neck, uid="lk")}</g>')
    text_x = mark_w + gap - bx1
    d1, b1, adv1 = text_path("potion", FONT_BOLD, F, -0.05, x0=text_x)
    parts = f'<path d="{d1}" fill="{word}"/>'
    right = b1[2]
    if product == "dropper":
        d2, b2, _ = text_path("dropper", FONT_MED, F, -0.03, x0=text_x + adv1 + 0.24 * F)
        parts += f'<path d="{d2}" fill="{sub}"/>'
        right = b2[2]
        by1, by2 = min(by1, b2[1]), max(by2, b2[3])
    top, bottom = min(by1, mark_top), max(by2, mark_top + mark_h)
    vb = f"0 {top:.2f} {right:.2f} {bottom - top:.2f}"
    title = "Potion" if product == "potion" else "Potion Dropper"
    return svg_doc(mark_g + parts, vb, title)


# ---------------------------------------------------------------- app icon (macOS 1024 grid)
def squircle(cx, cy, half, n=5.0, steps=360):
    pts = []
    for i in range(steps):
        t = 2 * math.pi * i / steps
        c, s = math.cos(t), math.sin(t)
        x = cx + half * math.copysign(abs(c) ** (2 / n), c)
        y = cy + half * math.copysign(abs(s) ** (2 / n), s)
        pts.append(f"{x:.2f} {y:.2f}")
    return "M" + " L".join(pts) + " Z"


def macos_icon_svg(product, small=False):
    tile = B["iconTile"]
    body = squircle(512, 512, 412)
    shadow = ("" if small else
              '<filter id="sh" x="-20%" y="-20%" width="140%" height="150%">'
              '<feGaussianBlur in="SourceAlpha" stdDeviation="14"/><feOffset dy="12" result="b"/>'
              '<feFlood flood-color="#000" flood-opacity="0.32"/><feComposite in2="b" operator="in"/>'
              '<feMerge><feMergeNode/><feMergeNode in="SourceGraphic"/></feMerge></filter>')
    box = (0.84 if small else 0.72) * 824
    off = 100 + (824 - box) / 2
    s = box / 100
    filt = "" if small else ' filter="url(#sh)"'
    inner = (f"<defs>{shadow}</defs>"
             f'<path d="{body}" fill="{tile["fill"]}"{filt}/>'
             + ("" if small else f'<path d="{body}" fill="none" stroke="{tile["stroke"]}" stroke-opacity="{tile["strokeAlpha"]}" stroke-width="3"/>')
             + f'<g transform="translate({off:.2f} {off:.2f}) scale({s:.4f})">{mark(product, B["neck"]["onDark"], small, uid="ai")}</g>')
    return svg_doc(inner, "0 0 1024 1024", ("Potion" if product == "potion" else "Potion Dropper") + " app icon")


# ---------------------------------------------------------------- glyphs (24 grid, stroke)
GLYPHS = {
    "image":    '<rect x="3" y="4" width="18" height="16" rx="3"/><circle cx="9" cy="9.5" r="1.75"/><path d="M3.5 17 9 12l4 3.5 3-2.5 4.5 4"/>',
    "audio":    '<path d="M4 10v4M8 7v10M12 4v16M16 8v8M20 10.5v3"/>',
    "video":    '<rect x="3" y="5" width="18" height="14" rx="3"/><path d="M10 9.5v5l4.5-2.5z"/>',
    "docs":     '<path d="M7 3h7l5 5v11a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z"/><path d="M14 3v5h5M8.5 13h7M8.5 16.5H13"/>',
    "link":     '<path d="M10 14a4 4 0 0 0 5.66 0l3-3a4 4 0 0 0-5.66-5.66l-1 1"/><path d="M14 10a4 4 0 0 0-5.66 0l-3 3a4 4 0 0 0 5.66 5.66l1-1"/>',
    "folder":   '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>',
    "download": '<path d="M12 4v11M7 10.5l5 5 5-5M5 20h14"/>',
    "convert":  '<path d="M5 12h14M14 7l5 5-5 5"/>',
    "check":    '<path d="M5 12.5l4.5 4.5L19 7.5"/>',
    "error":    '<circle cx="12" cy="12" r="9"/><path d="M12 7.5v5.5M12 16.5v.01"/>',
}


def glyph_svg(name, color="currentColor", size=24):
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="{size}" height="{size}" fill="none" '
            f'stroke="{color}" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">'
            f"{GLYPHS[name]}</svg>")


SF_SYMBOL = {"image": "photo", "audio": "waveform", "video": "film", "docs": "doc.text"}
FLUENT = {"image": "\\uE91B", "audio": "\\uE8D6", "video": "\\uE714", "docs": "\\uE8A5"}


# ================================================================= build steps
def build_logos():
    out = {}
    svg = ROOT / "logos/svg"
    for product in ("potion", "dropper"):
        name = "Potion" if product == "potion" else "Potion Dropper"
        variants = {
            f"{product}-mark.svg": mark(product, B["neck"]["onDark"]),
            f"{product}-mark-on-light.svg": mark(product, B["neck"]["onLight"]),
            f"{product}-mark-neutral.svg": mark(product, B["neck"]["neutral"]),
            f"{product}-mark-small.svg": mark(product, B["neck"]["onDark"], small=True),
            f"{product}-mark-small-on-light.svg": mark(product, B["neck"]["onLight"], small=True),
            f"{product}-mark-mono-black.svg": mark(product, None, mono_color="#000000"),
            f"{product}-mark-mono-white.svg": mark(product, None, mono_color="#FFFFFF"),
            f"{product}-mark-mono-small-black.svg": mark(product, None, small=True, mono_color="#000000"),
            f"{product}-mark-mono-currentColor.svg": mark(product, None, mono_color="currentColor"),
        }
        for fn, inner in variants.items():
            out[fn] = write(f"logos/svg/{fn}", svg_doc(inner, title=f"{name} logo"))
        for mode in MODES:
            fn = f"{product}-lockup-{'on-dark' if mode == 'dark' else 'on-light'}.svg"
            out[fn] = write(f"logos/svg/{fn}", lockup_svg(product, mode))
        write(f"logos/svg/{product}-appicon-macos.svg", macos_icon_svg(product))
        write(f"logos/svg/{product}-appicon-macos-small.svg", macos_icon_svg(product, small=True))
    write("logos/svg/potion-wordmark-on-dark.svg", wordmark_svg("#FFFFFF"))
    write("logos/svg/potion-wordmark-on-light.svg", wordmark_svg(INK))

    png = ROOT / "logos/png"
    for product in ("potion", "dropper"):
        for v in ("mark", "mark-on-light"):
            for s in (256, 512, 1024):
                render(svg / f"{product}-{v}.svg", png / f"{product}-{v}-{s}.png", s)
        for v in ("on-dark", "on-light"):
            src = svg / f"{product}-lockup-{v}.svg"
            w, h = [float(x) for x in src.read_text().split('viewBox="')[1].split('"')[0].split()[2:]]
            render(src, png / f"{product}-lockup-{v}@2x.png", 1200 if product == "potion" else 1600,
                   round((1200 if product == "potion" else 1600) * h / w))
        render(svg / f"{product}-appicon-macos.svg", png / f"{product}-appicon-1024.png", 1024)


def build_glyphs():
    for n in GLYPHS:
        write(f"icons/category/{n}.svg", glyph_svg(n) + "\n")


def build_css():
    lines = ["/* Generated by tools/build.py from tokens/tokens.json. Do not edit by hand. */",
             "@font-face{font-family:'Space Grotesk';font-weight:500;font-display:swap;src:url('../fonts/SpaceGrotesk-Medium.woff2') format('woff2');}",
             "@font-face{font-family:'Space Grotesk';font-weight:700;font-display:swap;src:url('../fonts/SpaceGrotesk-Bold.woff2') format('woff2');}",
             ""]

    def block(mode):
        c = T["color"][mode]
        v = [f"  --potion-{k}: {c[k]};" for k in c]
        v.append(f"  --potion-accent: {T['color']['accent']['fallback'][mode]};")
        for k in ORDER:
            v.append(f"  --potion-{k}-text: {cat_text(k, mode)};")
            v.append(f"  --potion-{k}-tint: {tint(k, mode)};")
        v.append(f"  --potion-neck: {B['neck']['onDark' if mode == 'dark' else 'onLight']};")
        sh = T["elevation"][mode]["popover"].split()
        v.append(f"  --potion-shadow-popover: {' '.join(x + 'px' for x in sh[:4])} {sh[4]};")
        return v

    lines.append(":root{")
    for k in ORDER:
        lines.append(f"  --potion-{k}-fill: {CAT[k]['fill']};")
        lines.append(f"  --potion-{k}-on-fill: {CAT[k]['onFill']};")
    lines.append(f"  --potion-ink: {INK};")
    for k, v in T["spacing"].items():
        lines.append(f"  --potion-space-{k.replace('.', '_')}: {v}px;")
    r = T["radius"]
    for k in ("xs", "md", "lg", "xl", "full"):
        lines.append(f"  --potion-radius-{k}: {r[k]}px;")
    lines.append(f"  --potion-radius-sm: {r['sm']['macos']}px;")
    for k, v in T["motion"]["duration"].items():
        lines.append(f"  --potion-duration-{k}: {v}ms;")
    for k, v in T["motion"]["easing"].items():
        lines.append(f"  --potion-ease-{k}: cubic-bezier({', '.join(str(x) for x in v)});")
    lines += ["  --potion-font-ui: -apple-system, BlinkMacSystemFont, 'Segoe UI Variable Text', 'Segoe UI', system-ui, sans-serif;",
              "  --potion-font-mono: ui-monospace, 'SF Mono', 'Cascadia Mono', Consolas, monospace;",
              "  --potion-font-brand: 'Space Grotesk', var(--potion-font-ui);",
              "  color-scheme: light dark;"]
    lines += block("light")
    lines.append("}")
    lines.append("@media (prefers-color-scheme: dark){:root:not([data-theme=\"light\"]){")
    lines += block("dark")
    lines.append("}}")
    lines.append("[data-theme=\"dark\"]{")
    lines += block("dark")
    lines.append("}")
    lines.append("[data-theme=\"light\"]{")
    lines += block("light")
    lines.append("}")
    lines.append("@media (prefers-reduced-motion: reduce){:root{--potion-duration-fast:0ms;--potion-duration-base:0ms;--potion-duration-slow:0ms;}}")
    write("tokens/tokens.css", "\n".join(lines) + "\n")


# ---------------------------------------------------------------- macOS
def colorset(hex_light, hex_dark=None):
    def comp(h):
        r, g, b, a = rgba(h)
        return {"color-space": "srgb", "components": {"red": f"0x{r:02X}", "green": f"0x{g:02X}", "blue": f"0x{b:02X}", "alpha": f"{a:.3f}"}}
    colors = [{"idiom": "universal", "color": comp(hex_light)}]
    if hex_dark and hex_dark != hex_light:
        colors.append({"idiom": "universal", "appearances": [{"appearance": "luminosity", "value": "dark"}], "color": comp(hex_dark)})
    return {"colors": colors, "info": {"author": "xcode", "version": 1}}


def semantic_colors():
    """name -> (light, dark)"""
    out = {}
    L, D = T["color"]["light"], T["color"]["dark"]
    for k in L:
        out["Potion" + cap(k)] = (L[k], D[k])
    out["PotionInk"] = (INK, INK)
    for k in ORDER:
        n = "Category" + cap(k)
        out[n + "Fill"] = (CAT[k]["fill"], CAT[k]["fill"])
        out[n + "Text"] = (CAT[k]["textOnLight"], CAT[k]["textOnDark"])
        out[n + "Tint"] = (tint(k, "light"), tint(k, "dark"))
        out[n + "OnFill"] = (CAT[k]["onFill"], CAT[k]["onFill"])
    return out


INFO = {"info": {"author": "xcode", "version": 1}}


def build_macos():
    for product, app in (("potion", "Potion"), ("dropper", "PotionDropper")):
        cat_dir = ROOT / f"macos/{app}/Assets.xcassets"
        if cat_dir.exists():
            shutil.rmtree(cat_dir)
        write_json(f"macos/{app}/Assets.xcassets/Contents.json", INFO)
        # App icon
        ai = cat_dir / "AppIcon.appiconset"
        big = ROOT / f"logos/svg/{product}-appicon-macos.svg"
        small = ROOT / f"logos/svg/{product}-appicon-macos-small.svg"
        images = []
        for pt in (16, 32, 128, 256, 512):
            for scale in (1, 2):
                px = pt * scale
                fn = f"icon_{pt}x{pt}{'' if scale == 1 else '@2x'}.png"
                render(small if px <= 32 else big, ai / fn, px)
                images.append({"size": f"{pt}x{pt}", "idiom": "mac", "filename": fn, "scale": f"{scale}x"})
        write_json(f"macos/{app}/Assets.xcassets/AppIcon.appiconset/Contents.json", {"images": images, **INFO})
        # .icns for non-Xcode builds
        base = Image.open(ai / "icon_512x512@2x.png")
        extra = [Image.open(ai / f) for f in ("icon_16x16.png", "icon_32x32.png", "icon_32x32@2x.png", "icon_128x128.png", "icon_256x256.png", "icon_512x512.png")]
        base.save(ROOT / f"macos/{app}/AppIcon.icns", append_images=extra)
        # Colors
        for name, (lt, dk) in semantic_colors().items():
            write_json(f"macos/{app}/Assets.xcassets/{name}.colorset/Contents.json", colorset(lt, dk))
        # Logo mark (vector, light/dark appearances)
        lm = f"macos/{app}/Assets.xcassets/LogoMark.imageset"
        (ROOT / lm).mkdir(parents=True, exist_ok=True)
        shutil.copy(ROOT / f"logos/svg/{product}-mark-on-light.svg", ROOT / lm / "mark-on-light.svg")
        shutil.copy(ROOT / f"logos/svg/{product}-mark.svg", ROOT / lm / "mark-on-dark.svg")
        write_json(f"{lm}/Contents.json", {
            "images": [{"idiom": "universal", "filename": "mark-on-light.svg"},
                       {"idiom": "universal", "filename": "mark-on-dark.svg", "appearances": [{"appearance": "luminosity", "value": "dark"}]}],
            **INFO, "properties": {"preserves-vector-representation": True}})
        # Menu bar template
        mb = ROOT / f"macos/{app}/Assets.xcassets/MenuBarIcon.imageset"
        msrc = ROOT / f"logos/svg/{product}-mark-mono-small-black.svg"
        render(msrc, mb / "menubar.png", 18)
        render(msrc, mb / "menubar@2x.png", 36)
        write_json(f"macos/{app}/Assets.xcassets/MenuBarIcon.imageset/Contents.json", {
            "images": [{"idiom": "universal", "filename": "menubar.png", "scale": "1x"},
                       {"idiom": "universal", "filename": "menubar@2x.png", "scale": "2x"},
                       {"idiom": "universal", "scale": "3x"}],
            **INFO, "properties": {"template-rendering-intent": "template"}})
    write("macos/Shared/PotionDesign.swift", swift_source())


def swift_source():
    sp = T["spacing"]
    sz = T["size"]
    mo = T["motion"]
    colors = "\n".join(f'    public static let {k[0].lower() + k[1:]} = Color("Potion{cap(k)}")' for k in T["color"]["light"])
    cases = ", ".join(ORDER)
    label = "\n".join(f'        case .{k}: return "{CAT[k]["label"]}"' for k in ORDER)
    sym = "\n".join(f'        case .{k}: return "{SF_SYMBOL[k]}"' for k in ORDER)
    space = "\n".join(f"    public static let x{k.replace('0.5', 'Half').replace('.', '_')}: CGFloat = {v}" for k, v in sp.items())
    ease = mo["easing"]
    return f'''// PotionDesign.swift
// Generated by tools/build.py from tokens/tokens.json. Do not edit by hand.
// Requires the colorsets in Assets.xcassets (generated alongside). macOS 13+.

import SwiftUI

// MARK: - Semantic colors (light/dark resolved by the asset catalog)

public enum PotionColor {{
{colors}
    public static let ink = Color("PotionInk")
    /// Interactive accent is always the user's system accent color.
    public static let accent = Color.accentColor
}}

// MARK: - File categories (the four logo bands)

public enum PotionCategory: String, CaseIterable, Identifiable {{
    case {cases}

    public var id: String {{ rawValue }}

    public var label: String {{
        switch self {{
{label}
        }}
    }}

    /// SF Symbol used as the category glyph.
    public var sfSymbol: String {{
        switch self {{
{sym}
        }}
    }}

    private var assetName: String {{ "Category" + rawValue.prefix(1).uppercased() + String(rawValue.dropFirst()) }}

    /// Solid brand color. Use for logo bands, solid tiles, per-file progress fill.
    public var fill: Color {{ Color(assetName + "Fill") }}
    /// Accessible text/glyph color for the current appearance (>= 4.5:1 on surfaces).
    public var text: Color {{ Color(assetName + "Text") }}
    /// Subtle solid background for category tiles and chips.
    public var tint: Color {{ Color(assetName + "Tint") }}
    /// Glyph color when drawn on top of `fill`.
    public var onFill: Color {{ Color(assetName + "OnFill") }}
}}

// MARK: - Spacing (4 pt grid; xN = N * 4)

public enum PotionSpacing {{
{space}
}}

// MARK: - Radius

public enum PotionRadius {{
    public static let xs: CGFloat = {T["radius"]["xs"]}
    public static let sm: CGFloat = {T["radius"]["sm"]["macos"]}
    public static let md: CGFloat = {T["radius"]["md"]}
    public static let lg: CGFloat = {T["radius"]["lg"]}
    public static let xl: CGFloat = {T["radius"]["xl"]}
    /// App-icon style tiles: corner radius = side * ratio.
    public static let iconTileRatio: CGFloat = {T["radius"]["iconTileRatio"]}
}}

// MARK: - Sizes

public enum PotionSize {{
    public static let categoryTileSmall: CGFloat = {sz["categoryTile"]["sm"]}
    public static let categoryTile: CGFloat = {sz["categoryTile"]["md"]}
    public static let categoryTileLarge: CGFloat = {sz["categoryTile"]["lg"]}
    public static let categoryTileXL: CGFloat = {sz["categoryTile"]["xl"]}
    public static let queueRowCompact: CGFloat = {sz["queueRowHeight"]["compact"]}
    public static let queueRow: CGFloat = {sz["queueRowHeight"]["regular"]}
    public static let progressBar: CGFloat = {sz["progressBarHeight"]["macos"]}
    public static let dropZoneMinHeight: CGFloat = {sz["dropZoneMinHeight"]}
    public static let convertSheetWidth: CGFloat = {sz["convertSheetWidth"]}
    public static let windowDefault = CGSize(width: {sz["window"]["macos"]["default"][0]}, height: {sz["window"]["macos"]["default"][1]})
    public static let windowMin = CGSize(width: {sz["window"]["macos"]["min"][0]}, height: {sz["window"]["macos"]["min"][1]})
}}

// MARK: - Typography

public enum PotionFont {{
    public static let title = Font.title3.weight(.semibold)
    public static let sectionHeader = Font.headline
    public static let body = Font.body
    public static let secondary = Font.subheadline
    public static let caption = Font.caption
    public static let fileName = Font.body.weight(.medium)
    public static let formatBadge = Font.system(size: {T["typography"]["roles"]["formatBadge"]["size"]["macos"]}, weight: .semibold, design: .monospaced)
    /// Brand display face. Register fonts/SpaceGrotesk-*.ttf in Info.plist (ATSApplicationFontsPath) first.
    public static func brand(_ size: CGFloat = {T["typography"]["roles"]["brandDisplay"]["size"]["macos"]}, bold: Bool = true) -> Font {{
        .custom(bold ? "SpaceGrotesk-Bold" : "SpaceGrotesk-Medium", size: size)
    }}
}}

// MARK: - Motion

public enum PotionMotion {{
    public static let fast: Double = {mo["duration"]["fast"] / 1000}
    public static let base: Double = {mo["duration"]["base"] / 1000}
    public static let slow: Double = {mo["duration"]["slow"] / 1000}
    public static let splashOnce: Double = {mo["duration"]["splashOnce"] / 1000}

    public static func standard(_ duration: Double = base) -> Animation {{
        .timingCurve({", ".join(str(x) for x in ease["standard"])}, duration: duration)
    }}
    public static func emphasized(_ duration: Double = slow) -> Animation {{
        .timingCurve({", ".join(str(x) for x in ease["emphasized"])}, duration: duration)
    }}
    public static func exit(_ duration: Double = fast) -> Animation {{
        .timingCurve({", ".join(str(x) for x in ease["exit"])}, duration: duration)
    }}
}}

// MARK: - Reference views

/// Rounded tile showing a file category glyph. Always paired with visible text (file name / extension) nearby.
public struct PotionCategoryTile: View {{
    public let category: PotionCategory
    public var size: CGFloat

    public init(_ category: PotionCategory, size: CGFloat = PotionSize.categoryTile) {{
        self.category = category
        self.size = size
    }}

    public var body: some View {{
        RoundedRectangle(cornerRadius: size * 0.286, style: .continuous)
            .fill(category.tint)
            .frame(width: size, height: size)
            .overlay(
                Image(systemName: category.sfSymbol)
                    .font(.system(size: size * 0.57, weight: .semibold))
                    .foregroundStyle(category.text)
            )
            .accessibilityElement()
            .accessibilityLabel(Text(category.label))
    }}
}}

/// Uppercase file-extension pill, e.g. MP4.
public struct PotionFormatBadge: View {{
    public let fileExtension: String

    public init(_ fileExtension: String) {{ self.fileExtension = fileExtension }}

    public var body: some View {{
        Text(fileExtension.uppercased())
            .font(PotionFont.formatBadge)
            .tracking(0.4)
            .foregroundStyle(PotionColor.textSecondary)
            .padding(.horizontal, 6)
            .padding(.vertical, 2)
            .background(RoundedRectangle(cornerRadius: PotionRadius.xs, style: .continuous).fill(PotionColor.surfaceRaised))
    }}
}}

/// The logo mark drawn natively (no image asset needed). Matches logos/svg/potion-mark*.svg.
public struct PotionMark: View {{
    public var neck: Color

    public init(neck: Color = .primary) {{ self.neck = neck }}

    public var body: some View {{
        Canvas {{ context, size in
            let s = min(size.width, size.height) / 100
            let ox = (size.width - 100 * s) / 2
            let oy = (size.height - 100 * s) / 2
            func r(_ x: CGFloat, _ y: CGFloat, _ w: CGFloat, _ h: CGFloat) -> CGRect {{
                CGRect(x: ox + x * s, y: oy + y * s, width: w * s, height: h * s)
            }}
            context.fill(Path(roundedRect: r(43, 12, 14, 18), cornerRadius: 3 * s), with: .color(neck))
            let bulb = Path(ellipseIn: r(22, 32, 56, 56))
            context.clip(to: bulb)
            let bands: [(CGFloat, PotionCategory)] = [(32, .image), (46.5, .audio), (61, .video), (75.5, .docs)]
            for (y, category) in bands {{
                context.fill(Path(r(20, y, 60, 12.5)), with: .color(category.fill))
            }}
        }}
        .aspectRatio(1, contentMode: .fit)
        .accessibilityLabel(Text("Potion"))
    }}
}}
'''


# ---------------------------------------------------------------- Windows
def argb(h):
    r, g, b, a = rgba(h)
    return "#%02X%02X%02X%02X" % (round(a * 255), r, g, b)


def build_windows():
    tmp = ROOT / "build-tmp"
    tmp.mkdir(exist_ok=True)

    def glyph_file(product, neck_key, small, mono=None, scale_box=1.0, canvas=(100, 100), name="g"):
        """Return path to an SVG with the mark centered on a canvas, mark occupying scale_box of canvas height."""
        cw, ch = canvas
        size = ch * scale_box
        s = size / 100
        ox, oy = (cw - size) / 2, (ch - size) / 2
        neck = B["neck"].get(neck_key, neck_key)
        inner = f'<g transform="translate({ox:.2f} {oy:.2f}) scale({s:.4f})">{mark(product, neck, small, mono_color=mono, uid="w")}</g>'
        p = tmp / f"{name}.svg"
        p.write_text(svg_doc(inner, f"0 0 {cw} {ch}"))
        return p

    for product, app in (("potion", "Potion"), ("dropper", "PotionDropper")):
        out = ROOT / f"windows/{app}/Assets"
        if out.exists():
            shutil.rmtree(out)
        out.mkdir(parents=True)
        # .ico (neutral neck so it reads on light and dark taskbars)
        pngs = []
        for px in (16, 20, 24, 32, 40, 48, 64, 96, 256):
            p = tmp / f"{product}-ico-{px}.png"
            render(glyph_file(product, "neutral", px <= 32, name=f"ico{px}"), p, px)
            pngs.append(str(p))
        subprocess.run(["convert", *pngs, str(out / f"{app}.ico")], check=True)
        # MSIX Square44x44 (app list, taskbar, Start)
        for sc, px in ((100, 44), (125, 55), (150, 66), (200, 88), (400, 176)):
            render(glyph_file(product, "neutral", False, name="s44"), out / f"Square44x44Logo.scale-{sc}.png", px)
        for ts in (16, 24, 32, 48, 256):
            small = ts <= 32
            render(glyph_file(product, "neutral", small, name="t1"), out / f"Square44x44Logo.targetsize-{ts}.png", ts)
            render(glyph_file(product, "onDark", small, name="t2"), out / f"Square44x44Logo.targetsize-{ts}_altform-unplated.png", ts)
            render(glyph_file(product, "onLight", small, name="t3"), out / f"Square44x44Logo.targetsize-{ts}_altform-lightunplated.png", ts)
        for sc in (100, 200):
            k = sc / 100
            render(glyph_file(product, "neutral", False, scale_box=0.62, name="s150"), out / f"Square150x150Logo.scale-{sc}.png", int(150 * k))
            render(glyph_file(product, "neutral", False, scale_box=0.62, canvas=(310, 150), name="w310"), out / f"Wide310x150Logo.scale-{sc}.png", int(310 * k), int(150 * k))
            render(glyph_file(product, "neutral", False, name="store"), out / f"StoreLogo.scale-{sc}.png", int(50 * k))
            render(glyph_file(product, "onDark", False, scale_box=0.5, canvas=(620, 300), name="splash"), out / f"SplashScreen.scale-{sc}.png", int(620 * k), int(300 * k))
        # Tray icons (one-color, theme specific)
        for theme, color in (("dark-taskbar", "#FFFFFF"), ("light-taskbar", "#000000")):
            pngs = []
            for px in (16, 20, 24, 32):
                p = tmp / f"tray-{theme}-{px}.png"
                render(glyph_file(product, color, True, mono=color, name=f"tray{px}"), p, px)
                pngs.append(str(p))
            subprocess.run(["convert", *pngs, str(out / f"Tray-{theme}.ico")], check=True)
        # Fonts for brand moments
        (out / "Fonts").mkdir()
        for f in ("SpaceGrotesk-Medium.ttf", "SpaceGrotesk-Bold.ttf"):
            shutil.copy(ROOT / "fonts" / f, out / "Fonts" / f)
    shutil.rmtree(tmp)
    write("windows/Shared/PotionTheme.xaml", xaml_source())
    write("windows/Shared/PotionDesign.cs", cs_source())


def xaml_source():
    def theme(mode):
        rows = []
        for k, v in T["color"][mode].items():
            n = "Potion" + cap(k)
            rows.append(f'            <Color x:Key="{n}Color">{argb(v)}</Color>')
            rows.append(f'            <SolidColorBrush x:Key="{n}Brush" Color="{argb(v)}"/>')
        for k in ORDER:
            n = "PotionCategory" + cap(k)
            rows.append(f'            <SolidColorBrush x:Key="{n}TextBrush" Color="{argb(cat_text(k, mode))}"/>')
            rows.append(f'            <SolidColorBrush x:Key="{n}TintBrush" Color="{argb(tint(k, mode))}"/>')
        return "\n".join(rows)

    hc_map = {"background": "SystemColorWindowColor", "surface": "SystemColorWindowColor", "surfaceRaised": "SystemColorWindowColor",
              "surfaceSunken": "SystemColorWindowColor", "border": "SystemColorWindowTextColor", "borderStrong": "SystemColorWindowTextColor",
              "textPrimary": "SystemColorWindowTextColor", "textSecondary": "SystemColorWindowTextColor", "textTertiary": "SystemColorWindowTextColor",
              "textDisabled": "SystemColorGrayTextColor", "success": "SystemColorWindowTextColor", "warning": "SystemColorWindowTextColor",
              "error": "SystemColorWindowTextColor", "scrim": "SystemColorWindowColor"}
    hc = []
    for k in T["color"]["dark"]:
        hc.append(f'            <SolidColorBrush x:Key="Potion{cap(k)}Brush" Color="{{ThemeResource {hc_map[k]}}}"/>')
    for k in ORDER:
        n = "PotionCategory" + cap(k)
        hc.append(f'            <SolidColorBrush x:Key="{n}TextBrush" Color="{{ThemeResource SystemColorWindowTextColor}}"/>')
        hc.append(f'            <SolidColorBrush x:Key="{n}TintBrush" Color="{{ThemeResource SystemColorWindowColor}}"/>')
    fixed = []
    for k in ORDER:
        n = "PotionCategory" + cap(k)
        fixed.append(f'    <Color x:Key="{n}FillColor">{argb(CAT[k]["fill"])}</Color>')
        fixed.append(f'    <SolidColorBrush x:Key="{n}FillBrush" Color="{argb(CAT[k]["fill"])}"/>')
        fixed.append(f'    <SolidColorBrush x:Key="{n}OnFillBrush" Color="{argb(CAT[k]["onFill"])}"/>')
    fixed.append(f'    <SolidColorBrush x:Key="PotionInkBrush" Color="{argb(INK)}"/>')
    for k, v in T["spacing"].items():
        fixed.append(f'    <x:Double x:Key="PotionSpace{k.replace("0.5", "Half").replace(".", "_")}">{v}</x:Double>')
    r = T["radius"]
    fixed += [f'    <CornerRadius x:Key="PotionRadiusXs">{r["xs"]}</CornerRadius>',
              f'    <CornerRadius x:Key="PotionRadiusSm">{r["sm"]["windows"]}</CornerRadius>',
              f'    <CornerRadius x:Key="PotionRadiusMd">{r["md"]}</CornerRadius>',
              f'    <CornerRadius x:Key="PotionRadiusLg">{r["lg"]}</CornerRadius>']
    sz = T["size"]
    fixed += [f'    <x:Double x:Key="PotionCategoryTileSize">{sz["categoryTile"]["md"]}</x:Double>',
              f'    <x:Double x:Key="PotionCategoryTileSizeLarge">{sz["categoryTile"]["lg"]}</x:Double>',
              f'    <x:Double x:Key="PotionCategoryGlyphSize">{sz["categoryGlyph"]["md"]}</x:Double>',
              f'    <x:Double x:Key="PotionQueueRowHeight">{sz["queueRowHeight"]["regular"]}</x:Double>',
              f'    <x:Double x:Key="PotionDropZoneMinHeight">{sz["dropZoneMinHeight"]}</x:Double>',
              '    <FontFamily x:Key="PotionBrandBoldFontFamily">ms-appx:///Assets/Fonts/SpaceGrotesk-Bold.ttf#Space Grotesk</FontFamily>',
              '    <FontFamily x:Key="PotionBrandMediumFontFamily">ms-appx:///Assets/Fonts/SpaceGrotesk-Medium.ttf#Space Grotesk Medium</FontFamily>',
              '    <FontFamily x:Key="PotionMonoFontFamily">Cascadia Mono, Consolas</FontFamily>']
    return f'''<!--
  PotionTheme.xaml
  Generated by tools/build.py from tokens/tokens.json. Do not edit by hand.
  Merge into App.xaml:  <ResourceDictionary Source="ms-appx:///Shared/PotionTheme.xaml"/>
  "Default" = Dark theme in WinUI. HighContrast maps to system colors.
  Interactive accent: use the system accent ({{ThemeResource AccentFillColorDefaultBrush}} etc.), never a category color.
-->
<ResourceDictionary
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <ResourceDictionary.ThemeDictionaries>
        <ResourceDictionary x:Key="Default">
{theme("dark")}
        </ResourceDictionary>
        <ResourceDictionary x:Key="Light">
{theme("light")}
        </ResourceDictionary>
        <ResourceDictionary x:Key="HighContrast">
{chr(10).join(hc)}
        </ResourceDictionary>
    </ResourceDictionary.ThemeDictionaries>

{chr(10).join(fixed)}
</ResourceDictionary>
'''


def cs_source():
    mo = T["motion"]
    label = "\n".join(f'            PotionCategory.{cap(k)} => "{CAT[k]["label"]}",' for k in ORDER)
    glyph = "\n".join(f'            PotionCategory.{cap(k)} => "{FLUENT[k]}",' for k in ORDER)
    space = "\n".join(f"    public const double X{k.replace('0.5', 'Half').replace('.', '_')} = {v};" for k, v in T["spacing"].items())
    e = mo["easing"]
    return f'''// PotionDesign.cs
// Generated by tools/build.py from tokens/tokens.json. Do not edit by hand.
using System;

namespace Potion.Design;

public enum PotionCategory {{ {", ".join(cap(k) for k in ORDER)} }}

public static class PotionCategoryInfo
{{
    public static string Label(this PotionCategory c) => c switch
    {{
{label}
        _ => throw new ArgumentOutOfRangeException(nameof(c))
    }};

    /// Segoe Fluent Icons code point for the category glyph (FontFamily "Segoe Fluent Icons").
    public static string Glyph(this PotionCategory c) => c switch
    {{
{glyph}
        _ => throw new ArgumentOutOfRangeException(nameof(c))
    }};

    /// Resource key prefix in PotionTheme.xaml, e.g. "PotionCategoryVideo" + "TextBrush".
    public static string ResourcePrefix(this PotionCategory c) => "PotionCategory" + c;
}}

/// 4 px grid. XN = N * 4.
public static class PotionSpacing
{{
{space}
}}

public static class PotionMotion
{{
    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds({mo["duration"]["fast"]});
    public static readonly TimeSpan Base = TimeSpan.FromMilliseconds({mo["duration"]["base"]});
    public static readonly TimeSpan Slow = TimeSpan.FromMilliseconds({mo["duration"]["slow"]});
    public static readonly TimeSpan SplashOnce = TimeSpan.FromMilliseconds({mo["duration"]["splashOnce"]});
    /// Cubic-bezier control points (x1, y1, x2, y2) for Microsoft.UI.Composition CubicBezierEasingFunction.
    public static readonly (float X1, float Y1, float X2, float Y2) Standard = ({", ".join(f"{x}f" for x in e["standard"])});
    public static readonly (float X1, float Y1, float X2, float Y2) Emphasized = ({", ".join(f"{x}f" for x in e["emphasized"])});
    public static readonly (float X1, float Y1, float X2, float Y2) Exit = ({", ".join(f"{x}f" for x in e["exit"])});
}}
'''


# ---------------------------------------------------------------- data
def build_formats():
    formats = {
        "video": ["mp4", "mov", "mkv", "avi", "webm", "wmv", "flv", "mpeg", "mpg", "m4v", "3gp", "ts"],
        "audio": ["mp3", "wav", "m4a", "aac", "flac", "ogg", "opus", "aiff", "aif", "wma", "alac"],
        "image": ["png", "jpg", "jpeg", "webp", "heic", "heif", "tiff", "tif", "bmp", "gif", "svg", "ico", "avif",
                  "cr2", "cr3", "nef", "arw", "dng", "raf", "orf", "rw2"],
        "docs": ["pdf", "docx", "doc", "odt", "rtf", "txt", "html", "htm", "md", "markdown", "epub"],
    }
    by_ext = {e: c for c, exts in formats.items() for e in exts}
    write_json("data/formats.json", {
        "note": "Maps a file extension to its Potion category, which decides the tile color and glyph. "
                "This is for visuals only; which conversions are allowed comes from the app's conversion matrix. "
                "GIF counts as image when it is an input (it can still be a video output). "
                "ALAC usually lives in .m4a, so detect the codec before choosing the badge text.",
        "categories": {k: {"label": CAT[k]["label"], "sfSymbol": SF_SYMBOL[k], "segoeFluentGlyph": "U+" + FLUENT[k][2:]} for k in ORDER},
        "byCategory": formats,
        "byExtension": by_ext,
    })


# ---------------------------------------------------------------- splash
def build_splash():
    c = {k: CAT[k]["fill"] for k in ORDER}
    bg = B["marketingBackground"]
    write("splash/splash.html", f'''<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>Potion splash animation (reference)</title>
<meta name="viewport" content="width=device-width,initial-scale=1">
<style>
@font-face{{font-family:'Space Grotesk';font-weight:700;src:url('../fonts/SpaceGrotesk-Bold.woff2') format('woff2');}}
html,body{{margin:0;height:100%;background:{bg};color:#fff;font-family:-apple-system,'Segoe UI Variable Text','Segoe UI',system-ui,sans-serif}}
.stage{{height:100%;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:24px}}
/* Timeline (ms, one pass = 1300): bands rise bottom->top at 0/150/300/450, each 420 long; neck drops 600-900 (overshoot); wordmark fades 750-1150. */
.band{{transform-box:fill-box;opacity:0;animation:rise 420ms cubic-bezier(.2,.8,.2,1) forwards}}
.b-docs{{animation-delay:0ms}} .b-video{{animation-delay:150ms}} .b-audio{{animation-delay:300ms}} .b-image{{animation-delay:450ms}}
.neck{{transform-box:fill-box;opacity:0;animation:drop 300ms cubic-bezier(.3,1.3,.5,1) 600ms forwards}}
.word{{opacity:0;animation:fade 400ms cubic-bezier(.2,.8,.2,1) 750ms forwards}}
@keyframes rise{{from{{transform:translateY(16px);opacity:0}}to{{transform:none;opacity:1}}}}
@keyframes drop{{from{{transform:translateY(-12px);opacity:0}}to{{transform:none;opacity:1}}}}
@keyframes fade{{from{{transform:translateY(10px);opacity:0}}to{{transform:none;opacity:1}}}}
@media (prefers-reduced-motion:reduce){{.band,.neck,.word{{animation:fadeonly 120ms linear forwards!important}}@keyframes fadeonly{{to{{opacity:1}}}}}}
h1{{margin:0;font:700 80px/0.9 'Space Grotesk',sans-serif;letter-spacing:-0.05em}}
p{{margin:0;font-size:18px;color:#9A94A8}}
button{{margin-top:24px;font:inherit;font-size:13px;color:#B6B0C4;background:transparent;border:1px solid #3A3547;border-radius:6px;padding:6px 12px;cursor:pointer}}
</style>
</head>
<body>
<main class="stage" id="stage">
<svg viewBox="0 0 100 100" width="220" height="220" role="img" aria-label="Potion">
<defs><clipPath id="bulb"><circle cx="50" cy="60" r="28"/></clipPath></defs>
<rect class="neck" x="43" y="12" width="14" height="18" rx="3" fill="#FFFFFF"/>
<g clip-path="url(#bulb)">
<rect class="band b-image" x="20" y="32" width="60" height="12.5" fill="{c['image']}"/>
<rect class="band b-audio" x="20" y="46.5" width="60" height="12.5" fill="{c['audio']}"/>
<rect class="band b-video" x="20" y="61" width="60" height="12.5" fill="{c['video']}"/>
<rect class="band b-docs" x="20" y="75.5" width="60" height="12.5" fill="{c['docs']}"/>
</g>
</svg>
<h1 class="word">potion</h1>
<p class="word">Any file in. The format you need out.</p>
<button type="button" id="replay">Replay</button>
</main>
<script>
function replay(){{
  const s=document.getElementById('stage'); const c=s.cloneNode(true); s.replaceWith(c);
  c.querySelector('#replay').addEventListener('click',replay);
}}
document.getElementById('replay').addEventListener('click',replay);
</script>
</body>
</html>
''')


# ---------------------------------------------------------------- preview page
def build_preview():
    def row(name, fg, bg):
        return f"<tr><td>{name}</td><td><code>{fg}</code></td><td>{contrast(fg, bg):.2f}:1</td></tr>"

    cat_cards = ""
    for k in ORDER:
        c = CAT[k]
        cat_cards += f'''<div class="cat">
  <div class="sw" style="background:{c['fill']}"><span style="color:{c['onFill']}">{glyph_svg(k, c['onFill'], 28)}</span></div>
  <div class="meta"><strong>{c['label']}</strong>
  <span>fill <code>{c['fill']}</code></span>
  <span>text on dark <code>{c['textOnDark']}</code> · {contrast(c['textOnDark'], T['color']['dark']['surface']):.2f}:1</span>
  <span>text on light <code>{c['textOnLight']}</code> · {contrast(c['textOnLight'], T['color']['light']['surface']):.2f}:1</span>
  <span>glyph on fill <code>{c['onFill']}</code> · {contrast(c['onFill'], c['fill']):.2f}:1</span></div>
</div>'''

    def neutral_table(mode):
        c = T["color"][mode]
        rows = "".join(row(k, c[k], c["surface"]) for k in ("textPrimary", "textSecondary", "textTertiary", "success", "warning", "error"))
        sw = "".join(f'<div class="nsw" style="background:{c[k]}"><span>{k}<br><code>{c[k]}</code></span></div>'
                     for k in ("background", "surface", "surfaceRaised", "border", "borderStrong"))
        return f'<div class="neutral {mode}"><h3>{cap(mode)}</h3><div class="nrow">{sw}</div><table><tr><th>Text / status</th><th>Hex</th><th>vs surface</th></tr>{rows}</table></div>'

    def tile(k, size=28):
        return (f'<span class="tile" style="width:{size}px;height:{size}px;background:var(--potion-{k}-tint);color:var(--potion-{k}-text)">'
                f'{glyph_svg(k, "currentColor", round(size * 0.57))}</span>')

    def queue(theme):
        items = [("video", "Interview_final.mov", "MOV · 48.2 MB", "MP4", 0.62, "62%"),
                 ("audio", "voice-memo-0412.opus", "OPUS · 2.1 MB", "MP3", 1.0, "Done"),
                 ("image", "IMG_3148.HEIC", "HEIC · 3.4 MB", "JPG", 0, "Waiting"),
                 ("docs", "Lab report draft.docx", "DOCX · 820 KB", "PDF", 0, "Waiting")]
        rows = ""
        for k, n, meta, out, p, st in items:
            bar = f'<div class="bar"><i style="width:{p * 100:.0f}%;background:var(--potion-{k}-fill)"></i></div>' if 0 < p < 1 else ""
            status = (f'<span class="ok">{glyph_svg("check", "currentColor", 16)} Done</span>' if st == "Done"
                      else f'<span class="st">{st}</span>')
            rows += (f'<div class="qrow">{tile(k)}<div class="qmain"><div class="fname">{n}</div><div class="fmeta">{meta}</div>{bar}</div>'
                     f'<span class="arrow">{glyph_svg("convert", "currentColor", 14)}</span><span class="badge">{out}</span>{status}</div>')
        return f'<div class="win" data-theme="{theme}"><div class="qhead"><strong>Queue</strong><span>4 files · 1 of 4 done</span></div>{rows}</div>'

    def sheet(theme):
        return f'''<div class="win sheet" data-theme="{theme}">
  <div class="srow">{tile("audio", 40)}<div><div class="fname">Interview.opus</div><div class="fmeta">Audio · 2.1 MB</div></div></div>
  <label class="field"><span>Convert to</span><span class="popup">MP3 <svg width="10" height="10" viewBox="0 0 10 10" aria-hidden="true"><path d="M2 4l3-3 3 3M2 6l3 3 3-3" fill="none" stroke="currentColor" stroke-width="1.3"/></svg></span></label>
  <div class="disclosure">› Advanced</div>
  <div class="sfoot"><label class="chk"><span class="box"></span> Move original to Trash</label><span class="btns"><span class="btn">Cancel</span><span class="btn primary">Convert</span></span></div>
</div>'''

    def dropzone(theme):
        return f'''<div class="win dz" data-theme="{theme}"><div class="dzin">
  <span class="dzmark">{potion_inner_svg_currentcolor()}</span>
  <strong>Drop files or folders</strong><span class="fmeta">Video, audio, images, documents and PDFs</span>
  <span class="btn">Choose Files…</span></div></div>'''

    def dropper(theme):
        return f'''<div class="win dr" data-theme="{theme}">
  <div class="url"><span class="ic">{glyph_svg("link", "currentColor", 16)}</span><span class="ph">Paste a video link</span><span class="btn primary">Fetch</span></div>
  <div class="card"><div class="thumb"></div><div><div class="fname">Media title appears here</div><div class="fmeta">12:48 · 1080p available</div></div></div>
  <div class="seg"><span class="on">{tile("video", 20)} Video</span><span>{tile("audio", 20)} Audio</span></div>
</div>'''

    roles = T["typography"]["roles"]
    type_rows = ""
    samples = {"brandDisplay": "potion", "title": "Convert 4 files", "sectionHeader": "Output", "body": "Converted files are saved next to the originals.",
               "secondary": "MOV · 48.2 MB", "caption": "Last converted 2 min ago", "formatBadge": "MP4", "fileName": "Interview_final_v2_EXPORT.mov"}
    for k, r in roles.items():
        fam = {"brand": "var(--potion-font-brand)", "ui": "var(--potion-font-ui)", "mono": "var(--potion-font-mono)"}[r["family"]]
        style = f"font-family:{fam};font-weight:{r['weight']};font-size:{r['size']['macos']}px;"
        if r.get("tracking"):
            style += f"letter-spacing:{r['tracking']};"
        if r.get("transform"):
            style += "text-transform:uppercase;"
        type_rows += (f"<tr><td><code>{k}</code></td><td style=\"{style}\">{samples[k]}</td>"
                      f"<td>{r['size']['macos']} / {r['size']['windows']}</td><td>{r.get('macosStyle') or '—'}</td><td>{r.get('windowsStyle') or '—'}</td></tr>")

    html = f'''<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Potion Design System {T["meta"]["version"]}</title>
<link rel="stylesheet" href="../tokens/tokens.css">
<style>
body{{margin:0;background:#0E0C14;color:#fff;font-family:var(--potion-font-ui);line-height:1.5}}
.wrap{{max-width:1180px;margin:0 auto;padding:56px 32px 96px}}
header{{display:flex;align-items:center;justify-content:space-between;gap:24px;flex-wrap:wrap;margin-bottom:56px}}
header img{{height:72px}}
header p{{margin:0;color:#9A94A8}}
h2{{font:700 32px/1.1 var(--potion-font-brand);letter-spacing:-0.03em;margin:72px 0 8px}}
h2+p{{color:#9A94A8;margin:0 0 24px;max-width:720px}}
h3{{font-size:14px;margin:0 0 12px}}
code{{font-family:var(--potion-font-mono);font-size:12px}}
.grid{{display:grid;grid-template-columns:repeat(auto-fill,minmax(220px,1fr));gap:16px}}
.card-l{{border-radius:12px;padding:24px;display:flex;flex-direction:column;align-items:center;gap:12px;font-size:12px;color:#9A94A8;border:1px solid #2E2939}}
.card-l.d{{background:#17141F}} .card-l.l{{background:#fff;color:#4D475A;border-color:#E4E1EA}}
.card-l img{{height:120px}}
.lockups{{display:grid;grid-template-columns:repeat(auto-fit,minmax(320px,1fr));gap:16px}}
.lockups div{{border-radius:12px;padding:40px;display:flex;align-items:center;justify-content:center}}
.lockups img{{height:56px;max-width:100%}}
.cats{{display:grid;grid-template-columns:repeat(auto-fit,minmax(250px,1fr));gap:16px}}
.cat{{background:#17141F;border:1px solid #2E2939;border-radius:12px;overflow:hidden}}
.cat .sw{{height:96px;display:flex;align-items:center;justify-content:center}}
.cat .meta{{padding:14px 16px;display:flex;flex-direction:column;gap:2px;font-size:12px;color:#B6B0C4}}
.cat .meta strong{{color:#fff;font-size:14px}}
.neutrals{{display:grid;grid-template-columns:repeat(auto-fit,minmax(420px,1fr));gap:16px}}
.neutral{{border-radius:12px;padding:20px}} .neutral.dark{{background:#17141F;border:1px solid #2E2939}} .neutral.light{{background:#fff;color:#17141F}}
.nrow{{display:flex;gap:8px;margin-bottom:16px}}
.nsw{{flex:1;height:84px;border-radius:8px;display:flex;align-items:flex-end;padding:6px;font-size:10px;border:1px solid rgba(128,128,128,.3)}}
.neutral.dark .nsw span{{color:#fff}} .neutral.light .nsw span{{color:#17141F}}
table{{width:100%;border-collapse:collapse;font-size:13px}} th,td{{text-align:left;padding:8px 10px;border-bottom:1px solid rgba(128,128,128,.25);vertical-align:middle}}
th{{font-size:11px;text-transform:uppercase;letter-spacing:.04em;color:#9A94A8}}
.typetable{{background:#17141F;border:1px solid #2E2939;border-radius:12px;padding:8px 12px}}
.comps{{display:grid;grid-template-columns:repeat(auto-fit,minmax(460px,1fr));gap:24px}}
.win{{background:var(--potion-surface);color:var(--potion-textPrimary);border:1px solid var(--potion-border);border-radius:12px;padding:16px;box-shadow:var(--potion-shadow-popover)}}
.tile{{display:inline-flex;align-items:center;justify-content:center;border-radius:8px;flex-shrink:0}}
.fname{{font-weight:500;font-size:13px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}}
.fmeta{{font-size:11px;color:var(--potion-textSecondary)}}
.qhead{{display:flex;justify-content:space-between;align-items:baseline;font-size:13px;padding:0 4px 8px;border-bottom:1px solid var(--potion-border)}}
.qhead span{{font-size:11px;color:var(--potion-textTertiary)}}
.qrow{{display:flex;align-items:center;gap:12px;height:56px;padding:0 4px;border-bottom:1px solid var(--potion-border)}}
.qrow:last-child{{border-bottom:0}}
.qmain{{flex:1;min-width:0}}
.bar{{height:4px;border-radius:2px;background:var(--potion-surfaceRaised);margin-top:5px;overflow:hidden}} .bar i{{display:block;height:100%;border-radius:2px}}
.arrow{{color:var(--potion-textTertiary);display:inline-flex}}
.badge{{font:600 10px var(--potion-font-mono);letter-spacing:.04em;padding:2px 6px;border-radius:4px;background:var(--potion-surfaceRaised);color:var(--potion-textSecondary)}}
.st{{font-size:11px;color:var(--potion-textTertiary);width:56px;text-align:right}}
.ok{{font-size:11px;color:var(--potion-success);display:inline-flex;align-items:center;gap:4px;width:56px;justify-content:flex-end}}
.sheet{{max-width:460px;display:flex;flex-direction:column;gap:16px}}
.srow{{display:flex;gap:12px;align-items:center}}
.field{{display:flex;align-items:center;gap:12px;font-size:13px;padding-left:52px}}
.popup{{display:inline-flex;align-items:center;justify-content:space-between;gap:8px;min-width:200px;padding:4px 8px;border-radius:6px;background:var(--potion-surfaceRaised);border:1px solid var(--potion-borderStrong);font-size:13px}}
.disclosure{{font-size:13px;color:var(--potion-textSecondary);padding-left:52px}}
.sfoot{{display:flex;justify-content:space-between;align-items:center;border-top:1px solid var(--potion-border);padding-top:14px;font-size:13px}}
.chk{{display:flex;align-items:center;gap:8px}} .box{{width:14px;height:14px;border-radius:4px;border:1px solid var(--potion-borderStrong)}}
.btns{{display:flex;gap:8px}}
.btn{{display:inline-block;font-size:13px;padding:4px 14px;border-radius:6px;background:var(--potion-surfaceRaised);border:1px solid var(--potion-borderStrong)}}
.btn.primary{{background:#0A84FF;border-color:#0A84FF;color:#fff}}
.dz{{display:flex}} .dzin{{flex:1;min-height:220px;border:1.5px dashed var(--potion-borderStrong);border-radius:12px;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:6px;font-size:13px}}
.dzmark{{color:var(--potion-textTertiary);width:48px;height:48px;margin-bottom:6px}} .dzmark svg{{width:48px;height:48px}}
.dzin .btn{{margin-top:10px}}
.dr{{display:flex;flex-direction:column;gap:12px}}
.url{{display:flex;align-items:center;gap:8px;padding:6px 6px 6px 10px;border-radius:8px;border:1px solid var(--potion-borderStrong);background:var(--potion-surfaceSunken)}}
.url .ic{{color:var(--potion-textTertiary);display:inline-flex}} .url .ph{{flex:1;font-size:13px;color:var(--potion-textTertiary)}}
.card{{display:flex;gap:12px;align-items:center}} .thumb{{width:96px;height:54px;border-radius:6px;background:var(--potion-surfaceRaised)}}
.seg{{display:inline-flex;padding:2px;border-radius:8px;background:var(--potion-surfaceRaised);gap:2px;align-self:flex-start}}
.seg span{{display:inline-flex;align-items:center;gap:6px;font-size:13px;padding:4px 12px 4px 4px;border-radius:6px}} .seg .on{{background:var(--potion-surface);box-shadow:0 1px 2px rgba(0,0,0,.2)}}
.icons{{display:flex;gap:24px;align-items:flex-end;flex-wrap:wrap}} .icons figure{{margin:0;text-align:center;font-size:12px;color:#9A94A8}}
.icons img{{display:block;margin:0 auto 8px}}
.glyphs{{display:flex;gap:20px;flex-wrap:wrap;color:#B6B0C4;font-size:11px}} .glyphs div{{display:flex;flex-direction:column;align-items:center;gap:6px;width:64px}}
iframe{{width:100%;height:520px;border:1px solid #2E2939;border-radius:12px}}
</style>
</head>
<body>
<div class="wrap">
<header><img src="../logos/svg/potion-lockup-on-dark.svg" alt="Potion"><p>Design system {T["meta"]["version"]} · {T["meta"]["updated"]} · macOS + Windows</p></header>

<h2>Logo</h2>
<p>A circle cut into four bands (image, audio, video, docs) under a plain neck. Use the small variant at 32 px and below, mono where the OS tints icons.</p>
<div class="grid">
<div class="card-l d"><img src="../logos/svg/potion-mark.svg" alt="">potion-mark.svg</div>
<div class="card-l l"><img src="../logos/svg/potion-mark-on-light.svg" alt="">potion-mark-on-light.svg</div>
<div class="card-l d"><img src="../logos/svg/dropper-mark.svg" alt="">dropper-mark.svg</div>
<div class="card-l l"><img src="../logos/svg/dropper-mark-on-light.svg" alt="">dropper-mark-on-light.svg</div>
<div class="card-l d"><img src="../logos/svg/potion-mark-mono-white.svg" alt="">potion-mark-mono-white.svg</div>
<div class="card-l l"><img src="../logos/svg/potion-mark-mono-black.svg" alt="">potion-mark-mono-black.svg</div>
<div class="card-l d"><img src="../logos/svg/potion-mark-small.svg" alt="">potion-mark-small.svg (≤32 px)</div>
<div class="card-l l"><img src="../logos/svg/dropper-mark-mono-small-black.svg" alt="">dropper-mark-mono-small-black.svg</div>
</div>
<h3 style="margin-top:24px">Lockups</h3>
<div class="lockups">
<div style="background:#17141F"><img src="../logos/svg/potion-lockup-on-dark.svg" alt="Potion"></div>
<div style="background:#fff"><img src="../logos/svg/potion-lockup-on-light.svg" alt="Potion"></div>
<div style="background:#17141F"><img src="../logos/svg/dropper-lockup-on-dark.svg" alt="Potion Dropper"></div>
<div style="background:#fff"><img src="../logos/svg/dropper-lockup-on-light.svg" alt="Potion Dropper"></div>
</div>

<h2>App icons</h2>
<p>macOS icons follow the 1024 grid (824 px rounded square, drop shadow). Windows icons are freestanding glyphs.</p>
<div class="icons">
<figure><img src="../macos/Potion/Assets.xcassets/AppIcon.appiconset/icon_256x256.png" width="256" height="256" alt="">Potion · macOS</figure>
<figure><img src="../macos/PotionDropper/Assets.xcassets/AppIcon.appiconset/icon_128x128@2x.png" width="128" height="128" alt="">Dropper · macOS</figure>
<figure><img src="../macos/Potion/Assets.xcassets/AppIcon.appiconset/icon_32x32.png" width="32" height="32" alt="">32</figure>
<figure><img src="../macos/Potion/Assets.xcassets/AppIcon.appiconset/icon_16x16.png" width="16" height="16" alt="">16</figure>
<figure style="background:#F3F3F3;padding:16px;border-radius:12px;color:#4A4A4A"><img src="../windows/Potion/Assets/Square44x44Logo.targetsize-256_altform-lightunplated.png" width="96" height="96" alt="">Windows · light</figure>
<figure style="background:#202020;padding:16px;border-radius:12px"><img src="../windows/Potion/Assets/Square44x44Logo.targetsize-256_altform-unplated.png" width="96" height="96" alt="">Windows · dark</figure>
<figure style="background:#202020;padding:16px;border-radius:12px"><img src="../windows/PotionDropper/Assets/Square44x44Logo.targetsize-256_altform-unplated.png" width="96" height="96" alt="">Dropper · dark</figure>
</div>

<h2>Category colors</h2>
<p>The four band colors identify file types and nothing else. Always pair the color with a glyph and visible text. On light surfaces use the darker text variant.</p>
<div class="cats">{cat_cards}</div>

<h2>Neutrals</h2>
<p>For custom-drawn surfaces (drop zone, tiles, onboarding, About). Window chrome and standard controls use system colors.</p>
<div class="neutrals">{neutral_table("dark")}{neutral_table("light")}</div>

<h2>Typography</h2>
<p>System UI font in the apps (SF Pro / Segoe UI Variable). Space Grotesk is reserved for the wordmark and brand moments.</p>
<div class="typetable"><table><tr><th>Role</th><th>Sample</th><th>Size mac / win</th><th>macOS style</th><th>WinUI style</th></tr>{type_rows}</table></div>

<h2>Glyphs</h2>
<p>Custom 24 px stroke set in icons/category/. In the apps, prefer SF Symbols (macOS) and Segoe Fluent Icons (Windows); see docs/07-iconography.md.</p>
<div class="glyphs">{"".join(f"<div>{glyph_svg(n, 'currentColor', 28)}<span>{n}</span></div>" for n in GLYPHS)}</div>

<h2>Components</h2>
<p>Reference mockups in both themes. The blue primary button stands in for the system accent color.</p>
<div class="comps">
{dropzone("dark")}{dropzone("light")}
{sheet("dark")}{sheet("light")}
{queue("dark")}{queue("light")}
{dropper("dark")}{dropper("light")}
</div>

<h2>Motion</h2>
<p>Bands rise bottom to top, then the neck drops on. Use it in onboarding and the About window, not as a launch splash.</p>
<iframe src="../splash/splash.html" title="Splash animation"></iframe>
</div>
</body>
</html>
'''
    write("preview/index.html", html)


def potion_inner_svg_currentcolor():
    return f'<svg viewBox="0 0 100 100" aria-hidden="true">{potion_inner("currentColor", ["currentColor"], uid="dzm")}</svg>'


def main():
    build_css()
    build_glyphs()
    build_logos()
    build_macos()
    build_windows()
    build_formats()
    build_splash()
    build_preview()
    print("Built Potion design system into", ROOT)


if __name__ == "__main__":
    main()
