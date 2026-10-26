#!/usr/bin/env python3
"""Draw the menu board's plates (from p4_explore's "BOARDS <name>:" log lines) on the UI texture dumps.
  p4_overlay.py [LOG] [DIR]   -> DIR/<tag>_<name>_plates.jpg (green: plates, yellow: the board's area)"""
import re
import sys
from PIL import Image, ImageDraw

LOG = sys.argv[1] if len(sys.argv) > 1 else "/home/bara/Projects/flat2vr/HandOfFate2/logs/p4_explore.log"
DIR = sys.argv[2] if len(sys.argv) > 2 else "/home/bara/Projects/flat2vr/HandOfFate2/captures/p4/explore"
TAG = sys.argv[3] if len(sys.argv) > 3 else "x"
last = {}
for line in open(LOG, errors="replace"):
    m = re.search(r"BOARDS (\S+): (.*)", line)
    if m:
        last[m.group(1)] = m.group(2)
for name, b in last.items():
    try:
        im = Image.open("%s/%s_%s_ui.png" % (DIR, TAG, name)).convert("RGB")
    except OSError:
        continue
    d = ImageDraw.Draw(im)
    H = im.size[1]
    labels = dict(re.findall(r"\[(\d+) ([^\]]*)\]", b))
    for i, x0, y0, x1, y1 in re.findall(r"plate (\d+) px \((-?\d+),(-?\d+)\)-\((-?\d+),(-?\d+)\)", b):
        x0, y0, x1, y1 = int(x0), int(y0), int(x1), int(y1)
        d.rectangle([x0, H - y1, x1, H - y0], outline=(0, 255, 0), width=2)
        d.text((x0 + 3, H - y1 + 2), i + " " + labels.get(i, "")[:20], fill=(0, 255, 0))
    m = re.search(r"menu board \S+ \((-?\d+),(-?\d+)\)-\((-?\d+),(-?\d+)\)", b)
    if m:
        x0, y0, x1, y1 = map(int, m.groups())
        d.rectangle([x0, H - y1, x1, H - y0], outline=(255, 255, 0), width=3)
    im.save("%s/%s_%s_plates.jpg" % (DIR, TAG, name), quality=85)
    print("wrote", name)
