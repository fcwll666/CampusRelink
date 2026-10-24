"""Build original CampusRelink vector artwork and Windows icon assets.

Requires Python 3 and Pillow. Only writes alongside this script.
No application, network or Windows settings are accessed.
"""
from __future__ import annotations

import json
import math
from pathlib import Path
import struct
import xml.etree.ElementTree as ET

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent
INK = "#12324B"
MINT = "#38D9B5"
WHITE = "#F7FCFF"
PAPER = "#EEF4F7"
MUTED = "#506B7D"
FONT_DIR = Path("C:/Windows/Fonts")
SIZES = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)


def circle_point(radius, degrees, center=(128, 128)):
    radians = math.radians(degrees)
    return (center[0] + radius * math.cos(radians),
            center[1] + radius * math.sin(radians))


def ring_geometry():
    start = circle_point(81, 62)
    end = circle_point(81, 326)
    theta = math.radians(326)
    tangent = (-math.sin(theta), math.cos(theta))
    normal = (math.cos(theta), math.sin(theta))
    tip = tuple(end[i] + 14 * tangent[i] for i in range(2))
    base = tuple(end[i] - 11 * tangent[i] for i in range(2))
    wings = [tuple(base[i] + sign * 17 * normal[i] for i in range(2))
             for sign in (-1, 1)]
    return start, end, [tip, *wings]


def point_text(point):
    return " ".join(f"{value:.3f}" for value in point)


def icon_svg(monochrome=False):
    start, end, arrow = ring_geometry()
    accent = "currentColor" if monochrome else MINT
    signal = "currentColor" if monochrome else WHITE
    plate = "" if monochrome else (
        f'<rect x="8" y="8" width="240" height="240" rx="56" fill="{INK}"/>')
    arcs = []
    for radius in (52, 29):
        a = circle_point(radius, 225, (128, 156))
        b = circle_point(radius, 315, (128, 156))
        arcs.append(f'<path d="M {point_text(a)} A {radius} {radius} 0 0 1 '
                    f'{point_text(b)}"/>')
    return f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" fill="none" color="{INK}" role="img" aria-labelledby="title desc">
  <title id="title">CampusRelink</title>
  <desc id="desc">A reconnect arrow forming a C surrounds a Wi-Fi signal.</desc>
  {plate}
  <path d="M {point_text(start)} A 81 81 0 1 1 {point_text(end)}" stroke="{accent}" stroke-width="17" stroke-linecap="round"/>
  <path d="M {point_text(arrow[0])} L {point_text(arrow[1])} L {point_text(arrow[2])} Z" fill="{accent}"/>
  <g stroke="{signal}" stroke-width="12" stroke-linecap="round">{''.join(arcs)}</g>
  <circle cx="128" cy="156" r="7.5" fill="{signal}"/>
</svg>
'''


def icon_image(size, monochrome=False, color=WHITE):
    # Supersampling keeps the 16px and 20px Windows sizes legible.
    scale = max(4, math.ceil(1024 / size))
    canvas = Image.new("RGBA", (size * scale, size * scale), (0, 0, 0, 0))
    drawing = ImageDraw.Draw(canvas)
    unit = size * scale / 256

    def scaled(point):
        return tuple(round(v * unit) for v in point)

    def round_line(points, width, fill):
        drawing.line([scaled(p) for p in points], fill=fill,
                     width=round(width * unit), joint="curve")
        radius = width / 2
        for x, y in (points[0], points[-1]):
            drawing.ellipse((*scaled((x - radius, y - radius)),
                             *scaled((x + radius, y + radius))), fill=fill)

    if not monochrome:
        drawing.rounded_rectangle((*scaled((8, 8)), *scaled((248, 248))),
                                  radius=round(56 * unit), fill=INK)
    accent = color if monochrome else MINT
    signal = color if monochrome else WHITE
    ring = [circle_point(81, 62 + (326 - 62) * i / 600) for i in range(601)]
    round_line(ring, 17, accent)
    drawing.polygon([scaled(p) for p in ring_geometry()[2]], fill=accent)
    for radius in (52, 29):
        points = [circle_point(radius, 225 + 90 * i / 150, (128, 156))
                  for i in range(151)]
        round_line(points, 12, signal)
    drawing.ellipse((*scaled((120.5, 148.5)), *scaled((135.5, 163.5))), fill=signal)
    return canvas.resize((size, size), Image.Resampling.LANCZOS)


def font(size, bold=False, chinese=False):
    name = "msyh.ttc" if chinese else ("seguisb.ttf" if bold else "segoeui.ttf")
    return ImageFont.truetype(str(FONT_DIR / name), size)


def write_ico():
    # Native 32-bit DIB entries work with .NET Framework Icon.ToBitmap and the
    # Windows shell. PNG-only ICO entries can render as noise in the former.
    payloads = []
    for size in SIZES:
        image = icon_image(size)
        pixels = image.tobytes("raw", "BGRA", 0, -1)
        mask_stride = ((size + 31) // 32) * 4
        alpha = image.getchannel("A")
        mask = bytearray(mask_stride * size)
        for y in range(size):
            for x in range(size):
                if alpha.getpixel((x, y)) == 0:
                    mask[(size - 1 - y) * mask_stride + x // 8] |= 0x80 >> (x % 8)
        dib = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32,
                          0, len(pixels), 0, 0, 0, 0)
        payloads.append(dib + pixels + mask)
    header = struct.pack("<HHH", 0, 1, len(SIZES))
    offset = 6 + 16 * len(SIZES)
    directory = bytearray()
    for size, payload in zip(SIZES, payloads):
        directory.extend(struct.pack("<BBBBHHII", size % 256, size % 256,
                                     0, 0, 1, 32, len(payload), offset))
        offset += len(payload)
    (ROOT / "CampusRelink.ico").write_bytes(header + directory + b"".join(payloads))


def wordmark():
    image = Image.new("RGBA", (1440, 330), (0, 0, 0, 0))
    image.alpha_composite(icon_image(300), (0, 15))
    draw = ImageDraw.Draw(image)
    draw.text((350, 49), "CampusRelink", font=font(112, True), fill=INK)
    draw.text((354, 205), "Campus network auto-reconnect", font=font(39), fill=MUTED)
    image.save(ROOT / "CampusRelink-wordmark.png")
    source = icon_svg().split("\n", 3)[3].rsplit("</svg>", 1)[0]
    (ROOT / "CampusRelink-wordmark.svg").write_text(f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1440 330" role="img" aria-label="CampusRelink: Campus network auto-reconnect">
  <g transform="translate(0 15) scale(1.171875)">{source}</g>
  <text x="350" y="161" fill="{INK}" font-family="Segoe UI, Arial, sans-serif" font-size="112" font-weight="600">CampusRelink</text>
  <text x="354" y="244" fill="{MUTED}" font-family="Segoe UI, Arial, sans-serif" font-size="39">Campus network auto-reconnect</text>
</svg>
''', encoding="utf-8")


def preview():
    image = Image.new("RGBA", (1440, 880), PAPER)
    draw = ImageDraw.Draw(image)
    image.alpha_composite(icon_image(292), (72, 95))
    draw.text((410, 119), "CampusRelink", font=font(98, True), fill=INK)
    draw.text((416, 260), "Campus network auto-reconnect", font=font(37), fill=MUTED)
    draw.text((416, 323), "校园网自动重连", font=font(30, chinese=True), fill=INK)
    draw.line((80, 452, 1360, 452), fill="#CBD9E3", width=2)
    draw.text((84, 493), "App icon", font=font(28, True), fill=INK)
    for x, size in ((84, 128), (280, 64), (418, 32), (534, 16)):
        image.alpha_composite(icon_image(size), (x, 555 + (128 - size) // 2))
        draw.text((x, 715), f"{size} px", font=font(22), fill=MUTED)
    draw.rounded_rectangle((730, 505, 1358, 762), radius=30, fill=INK)
    image.alpha_composite(icon_image(148, True), (770, 555))
    draw.text((970, 562), "Tray icon", font=font(30, True), fill=WHITE)
    draw.text((970, 613), "Single-color variant", font=font(23), fill="#B7D4E3")
    for x, color, label in ((84, INK, "Deep blue"), (332, MINT, "Reconnect mint"),
                            (647, WHITE, "Signal white")):
        draw.rounded_rectangle((x, 801, x + 28, 829), radius=7, fill=color)
        draw.text((x + 42, 796), label, font=font(22), fill=MUTED)
    image.convert("RGB").save(ROOT / "CampusRelink-preview.png")


def main():
    (ROOT / "CampusRelink-icon.svg").write_text(icon_svg(), encoding="utf-8")
    (ROOT / "CampusRelink-symbol.svg").write_text(icon_svg(True), encoding="utf-8")
    icon_image(1024).save(ROOT / "CampusRelink-icon.png")
    icon_image(256, True).save(ROOT / "CampusRelink-tray-white.png")
    icon_image(256, True, INK).save(ROOT / "CampusRelink-tray-dark.png")
    write_ico()
    wordmark()
    preview()

    # Validate file formats and the actual ICO directory entries.
    for path in ROOT.glob("*.svg"):
        ET.parse(path)
    for path in ROOT.glob("*.png"):
        with Image.open(path) as png:
            png.verify()
    with Image.open(ROOT / "CampusRelink.ico") as ico:
        actual_sizes = sorted(ico.ico.sizes())
        assert actual_sizes == sorted((size, size) for size in SIZES)
        for size in actual_sizes:
            assert ico.ico.getimage(size).size == size
    report = {"name": "CampusRelink", "tagline": "Campus network auto-reconnect",
              "icon_sizes": list(SIZES), "svg_xml_valid": True,
              "png_decode_valid": True, "ico_decode_valid": True,
              "ico_encoding": "32-bit BGRA DIB with AND masks",
              "network_or_system_settings_accessed": False}
    (ROOT / "asset-check.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report))


if __name__ == "__main__":
    main()
