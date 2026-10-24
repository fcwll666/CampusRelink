"""Render original README artwork with the project's own vector icon.

Optional documentation tool for Windows: Python 3 + Pillow.
Does not start the app or access network/system configuration.
"""
from pathlib import Path
import xml.etree.ElementTree as ET

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
OUTPUT = ROOT / "docs" / "images"
INK = "#12324B"
WHITE = "#F7FCFF"
MINT = "#38D9B5"


def main():
    OUTPUT.mkdir(parents=True, exist_ok=True)
    icon = ROOT / "branding/CampusRelink/CampusRelink-icon.png"
    canvas = Image.new("RGBA", (1320, 420), INK)
    with Image.open(icon) as mark:
        canvas.alpha_composite(mark.convert("RGBA").resize((272, 272), Image.Resampling.LANCZOS), (65, 69))
    fonts = Path("C:/Windows/Fonts")
    draw = ImageDraw.Draw(canvas)
    draw.text((382, 92), "CampusRelink", fill=WHITE,
              font=ImageFont.truetype(str(fonts / "seguisb.ttf"), 92))
    draw.text((389, 222), "Campus network auto-reconnect", fill=MINT,
              font=ImageFont.truetype(str(fonts / "segoeui.ttf"), 32))
    draw.text((390, 287), "校园网自动认证与断线重连", fill="#C5DFEA",
              font=ImageFont.truetype(str(fonts / "msyh.ttc"), 27))
    canvas.convert("RGB").save(OUTPUT / "hero.png")
    original = (ROOT / "branding/CampusRelink/CampusRelink-icon.svg").read_text(encoding="utf-8")
    body = original.split("\n", 3)[3].rsplit("</svg>", 1)[0]
    svg = f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1320 420" role="img" aria-labelledby="title desc">
  <title id="title">CampusRelink</title>
  <desc id="desc">Campus network auto-reconnect. 校园网自动认证与断线重连。</desc>
  <rect width="1320" height="420" fill="{INK}"/>
  <g transform="translate(65 69) scale(1.0625)">{body}</g>
  <text x="382" y="185" font-family="Segoe UI, Arial, sans-serif" font-size="92" font-weight="600" fill="{WHITE}">CampusRelink</text>
  <text x="389" y="254" font-family="Segoe UI, Arial, sans-serif" font-size="32" fill="{MINT}">Campus network auto-reconnect</text>
  <text x="390" y="316" font-family="Microsoft YaHei, sans-serif" font-size="27" fill="#C5DFEA">校园网自动认证与断线重连</text>
</svg>
'''
    ET.fromstring(svg)
    (OUTPUT / "hero.svg").write_text(svg, encoding="utf-8")
    print("README hero: docs/images/hero.png and hero.svg (1320 x 420)")


if __name__ == "__main__":
    main()
