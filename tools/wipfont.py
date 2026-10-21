#!/usr/bin/env python3
"""Builds the glyph atlas for the in-game corner mark ("TLD RevaMP <version> · WIP").

The mark is styled after Unity's "Development Build" watermark: bold white letters with a soft dark drop shadow.
Fira Sans Bold is the closest open font to it. The game has no font loader we can feed a .ttf, so the glyphs are
rasterised here once, packed into src/TLDRevamp/Assets/WipFont.png (white, coverage in alpha) and described in
WipFont.txt; WipMark.cs lays the text out from those at run time. Re-run only to change the font or the size.

  tools/wipfont.py [path/to/FiraSans-Bold.ttf]     (default: fc-match "Fira Sans:style=Bold"; needs Pillow)

Fira Sans is under the SIL Open Font License 1.1 (packaging/licenses/FiraSans-OFL.txt, shipped in every package).
"""
import os
import subprocess
import sys

from PIL import Image, ImageDraw, ImageFont

EM = 64          # pixel size the glyphs are rasterised at; the game scales the finished mark down from this
PAD = 2          # empty pixels around each glyph so bilinear sampling never bleeds a neighbour in
WIDTH = 512
CHARS = [chr(c) for c in range(32, 127)] + ["·"]

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
out = os.path.join(root, "src", "TLDRevamp", "Assets")
path = sys.argv[1] if len(sys.argv) > 1 else subprocess.run(
    ["fc-match", "-f", "%{file}", "Fira Sans:style=Bold"], capture_output=True, text=True, check=True).stdout
if "FiraSans-Bold" not in os.path.basename(path):
    sys.exit(f"Fira Sans Bold not found (fc-match gave {path!r}); pass the .ttf path")
font = ImageFont.truetype(path, EM)
ascent, descent = font.getmetrics()

glyphs, x, y, row = [], PAD, PAD, 0
for ch in CHARS:
    x0, y0, x1, y1 = font.getbbox(ch, anchor="ls")   # relative to the pen on the baseline; y grows downwards
    w, h = max(0, x1 - x0), max(0, y1 - y0)
    if x + w + PAD > WIDTH:
        x, y, row = PAD, y + row + PAD, 0
    glyphs.append((ch, x, y, w, h, x0, -y0, font.getlength(ch)))
    x += w + PAD
    row = max(row, h)
height = 1
while height < y + row + PAD:
    height *= 2

coverage = Image.new("L", (WIDTH, height), 0)
draw = ImageDraw.Draw(coverage)
for ch, gx, gy, w, h, bx, by, _ in glyphs:
    if w and h:
        draw.text((gx - bx, gy + by), ch, font=font, fill=255, anchor="ls")
atlas = Image.new("RGBA", (WIDTH, height), (255, 255, 255, 0))
atlas.putalpha(coverage)
os.makedirs(out, exist_ok=True)
atlas.save(os.path.join(out, "WipFont.png"), optimize=True)
with open(os.path.join(out, "WipFont.txt"), "w", newline="\n") as f:
    f.write(f"# Fira Sans Bold at {EM} px, made by tools/wipfont.py. em ascent descent, then per glyph:\n")
    f.write("# codepoint x y(top, from the image top) w h bearingX bearingY(baseline to glyph top) advance\n")
    f.write(f"{EM} {ascent} {descent}\n")
    for ch, gx, gy, w, h, bx, by, adv in glyphs:
        f.write(f"{ord(ch)} {gx} {gy} {w} {h} {bx} {by} {adv:.2f}\n")
print(f"{len(glyphs)} glyphs, atlas {WIDTH}x{height} -> {os.path.relpath(out, root)}")
