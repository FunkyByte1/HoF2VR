#!/usr/bin/env python3
"""lumaraw.py <tag> <frame> [W H] [--span N] [--scale S]
Frames frame-N..frame+N of testdata/luma/<tag>.raw (the mod's `lumalog <tag> <n> raw`: per CSV row one RGB24 image,
rows bottom-up, left eye then right) as a contact sheet: one row per frame, left | right | right minus the previous
frame's right (difference x4, grey = 0). Writes testdata/shots/LR_<tag>_<frame>.png."""
import csv
import sys
import numpy as np
from PIL import Image, ImageDraw

import os as _os, sys as _sys  # noqa: E401
_sys.path.insert(0, _os.path.dirname(_os.path.abspath(__file__)))
import hof2env  # noqa: E402  (this worktree's test environment, docs/two-environments.md)
D = hof2env.H + "/testdata/"
a = sys.argv[1:]
tag, frame = a[0], int(a[1])
w, h = (int(a[2]), int(a[3])) if len(a) > 3 and not a[2].startswith("--") else (84, 94)
span = int(a[a.index("--span") + 1]) if "--span" in a else 2
scale = int(a[a.index("--scale") + 1]) if "--scale" in a else 4
rows = []
with open(D + "luma/" + tag + ".csv") as f:
    for r in csv.DictReader(f):
        rows.append((int(r["frame"]), int(r["eye"])))
raw = np.fromfile(D + "luma/" + tag + ".raw", dtype=np.uint8)
n = len(raw) // (w * h * 3)
imgs = {}
for i, (fr, e) in enumerate(rows[:n]):
    if frame - span - 1 <= fr <= frame + span:
        imgs[(fr, e)] = raw[i * w * h * 3:(i + 1) * w * h * 3].reshape(h, w, 3)[::-1].astype(np.int16)
frames = sorted({fr for fr, e in imgs if fr >= frame - span})
tiles = []
for fr in frames:
    L, R = imgs.get((fr, 0)), imgs.get((fr, 1))
    P = imgs.get((fr - 1, 1))
    if L is None or R is None:
        continue
    d = np.clip(128 + 4 * (R - P), 0, 255) if P is not None else np.full_like(R, 128)
    dl = np.clip(128 + 4 * (L - imgs[(fr - 1, 0)]), 0, 255) if (fr - 1, 0) in imgs else np.full_like(L, 128)
    row = np.concatenate([L, dl, R, d], axis=1).astype(np.uint8)
    im = Image.fromarray(row).resize((row.shape[1] * scale, row.shape[0] * scale), Image.NEAREST)
    dr = ImageDraw.Draw(im)
    dr.text((4, 4), "frame %d  L | dL | R | dR" % fr, fill=(255, 255, 0))
    tiles.append(im)
sheet = Image.new("RGB", (tiles[0].size[0], sum(t.size[1] for t in tiles)))
y = 0
for t in tiles:
    sheet.paste(t, (0, y))
    y += t.size[1]
out = D + "shots/LR_%s_%d.png" % (tag, frame)
sheet.save(out)
print(out, sheet.size)
