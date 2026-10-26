#!/usr/bin/env python3
"""review.py NAME... - one image per name: left eye (raw) + UI texture on green, side by side,
saved as testdata/shots/review_NAME.png (for looking at what a screen looks like in VR)."""
import os
import sys
from PIL import Image
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hof2env  # noqa: E402  (this worktree's test environment, docs/two-environments.md)
S = hof2env.H + "/testdata/shots/"
for n in sys.argv[1:]:
    try:
        eyes = Image.open(S + n + "_eyes.png").convert("RGB")
        w, h = eyes.size
        left = eyes.crop((0, 0, w // 2, h))
    except FileNotFoundError:
        left = Image.new("RGB", (672, 756))
    try:
        ui = Image.open(S + n + "_ui.png")
        bg = Image.new("RGBA", ui.size, (60, 90, 60, 255))
        bg.alpha_composite(ui)
        ui = bg.convert("RGB").resize((960, 540))
    except FileNotFoundError:
        ui = Image.new("RGB", (960, 540))
    out = Image.new("RGB", (left.width + 10 + ui.width, max(left.height, ui.height)))
    out.paste(left, (0, 0))
    out.paste(ui, (left.width + 10, 0))
    out.save(S + "review_" + n + ".png")
    print(S + "review_" + n + ".png")
