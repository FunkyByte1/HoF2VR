#!/usr/bin/env python3
"""eyes.py <name> [brightness] [both] - crop the left eye out of testdata/shots/<name>_eyes.png"""
import os
import sys
from PIL import Image, ImageEnhance
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hof2env  # noqa: E402  (this worktree's test environment, docs/two-environments.md)
H = hof2env.H + "/testdata/shots/"
n = sys.argv[1]
b = float(sys.argv[2]) if len(sys.argv) > 2 else 1.0
im = Image.open(H + n + "_eyes.png").convert("RGB")
w, h = im.size
out = im if "both" in sys.argv else im.crop((0, 0, w // 2, h))
if b != 1.0:
    out = ImageEnhance.Brightness(out).enhance(b)
out.save(H + n + "_l.png")
print(H + n + "_l.png", out.size)
