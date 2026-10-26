#!/usr/bin/env python3
"""sheet.py OUT NAME... [--both] [--w PX] [--cols N] [--bright F]
Contact sheet of eye captures: testdata/shots/<NAME>_eyes.png (left eye, or both with --both), each scaled to
--w pixels wide (default 420), labelled, in --cols columns (default 3). Writes testdata/shots/<OUT>.png and prints
the path. Keeps looking at many shots cheap."""
import sys
from PIL import Image, ImageDraw, ImageEnhance

import os as _os, sys as _sys  # noqa: E401
_sys.path.insert(0, _os.path.dirname(_os.path.abspath(__file__)))
import hof2env  # noqa: E402  (this worktree's test environment, docs/two-environments.md)
H = hof2env.H + "/testdata/shots/"
args = sys.argv[1:]
both = "--both" in args
w = 420
cols = 3
bright = 1.0
names = []
i = 1
out = args[0]
while i < len(args):
    a = args[i]
    if a == "--both":
        pass
    elif a == "--w":
        w = int(args[i + 1]); i += 1
    elif a == "--cols":
        cols = int(args[i + 1]); i += 1
    elif a == "--bright":
        bright = float(args[i + 1]); i += 1
    else:
        names.append(a)
    i += 1
tiles = []
for n in names:
    p = n if n.endswith(".png") else H + n + "_eyes.png"
    im = Image.open(p).convert("RGB")
    if not both and not n.endswith(".png"):
        im = im.crop((0, 0, im.size[0] // 2, im.size[1]))
    h = int(im.size[1] * w / im.size[0])
    im = im.resize((w, h), Image.LANCZOS)
    if bright != 1.0:
        im = ImageEnhance.Brightness(im).enhance(bright)
    d = ImageDraw.Draw(im)
    d.rectangle((0, 0, w, 16), fill=(0, 0, 0))
    d.text((4, 2), n.split("/")[-1][:60], fill=(255, 255, 0))
    tiles.append(im)
rows = (len(tiles) + cols - 1) // cols
th = max(t.size[1] for t in tiles)
sheet = Image.new("RGB", (cols * w, rows * th), (40, 40, 40))
for k, t in enumerate(tiles):
    sheet.paste(t, ((k % cols) * w, (k // cols) * th))
path = H + out + ".png"
sheet.save(path)
print(path, sheet.size)
