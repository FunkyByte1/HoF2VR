#!/usr/bin/env python3
"""eyediff.py SHOT_eyes.png P00L P02L P11L P12L P00R P02R P11R P12R [out_prefix]

Compares the two eyes of a `shot` capture (left|right side by side, raw eye textures). Each right-eye
pixel is mapped to the left-eye pixel that looks in the same direction (both eyes' projection
matrices, parallel views; exact for distant content, near objects differ by parallax), then:
  raw    mean |L - R| luminance (0..255) over the overlapping area
  blur   the same after a 31-px box blur of both (parallax edges average out; smooth differences
         such as fog that is thicker in one eye stay)
  bias   mean (L - R) after the blur (one eye brighter overall)
Writes <out_prefix>_diff.png (blurred |L-R| x 8, right-eye frame) when out_prefix is given.
The projection values come from the mod's `stereofog info` log line.
"""
import sys

import numpy as np
from PIL import Image


def box(img, r):
    """separable box blur with edge clamping, radius r"""
    k = 2 * r + 1
    pad = np.pad(img, ((r, r), (r, r)), mode="edge")
    c = np.cumsum(np.cumsum(pad, axis=0), axis=1)
    c = np.pad(c, ((1, 0), (1, 0)))
    s = c[k:, k:] - c[:-k, k:] - c[k:, :-k] + c[:-k, :-k]
    return s / (k * k)


def main():
    a = sys.argv[1:]
    if len(a) < 9:
        raise SystemExit(__doc__)
    img = np.asarray(Image.open(a[0]).convert("RGB")).astype(np.float32)
    p00l, p02l, p11l, p12l, p00r, p02r, p11r, p12r = map(float, a[1:9])
    h, w2, _ = img.shape
    w = w2 // 2
    lum = img @ np.array([0.2126, 0.7152, 0.0722], dtype=np.float32)
    L, R = lum[:, :w], lum[:, w:]
    ys, xs = np.mgrid[0:h, 0:w].astype(np.float32)
    ndx = 2 * (xs + 0.5) / w - 1
    ndy = 1 - 2 * (ys + 0.5) / h            # PNG rows run top to bottom
    tx = (ndx + p02r) / p00r
    ty = (ndy + p12r) / p11r
    lx = ((tx * p00l - p02l) + 1) * w / 2 - 0.5
    ly = (1 - (ty * p11l - p12l)) * h / 2 - 0.5
    valid = (lx >= 0) & (lx <= w - 1) & (ly >= 0) & (ly <= h - 1)
    x0 = np.clip(np.floor(lx).astype(int), 0, w - 2)
    y0 = np.clip(np.floor(ly).astype(int), 0, h - 2)
    fx, fy = np.clip(lx - x0, 0, 1), np.clip(ly - y0, 0, 1)
    Lw = (L[y0, x0] * (1 - fx) * (1 - fy) + L[y0, x0 + 1] * fx * (1 - fy) +
          L[y0 + 1, x0] * (1 - fx) * fy + L[y0 + 1, x0 + 1] * fx * fy)
    # trim the border band where one eye sees nothing of the other's view
    m = valid.copy()
    m[:20, :] = m[-20:, :] = False
    m[:, :20] = m[:, -20:] = False
    raw = np.abs(Lw - R)[m].mean()
    Lb, Rb = box(np.where(valid, Lw, R), 15), box(R, 15)
    d = Lb - Rb
    blur = np.abs(d)[m].mean()
    bias = d[m].mean()
    big = (np.abs(d) > 4)[m].mean() * 100
    print("%s: raw %.2f  blur %.2f  bias %+.2f  area|d|>4 %.1f%%  (overlap %d%% of the right eye)"
          % (a[0].split("/")[-1], raw, blur, bias, big, 100 * valid.mean()))
    if len(a) > 9:
        out = np.clip(np.abs(d) * 8, 0, 255).astype(np.uint8)
        out[~valid] = 0
        Image.fromarray(out).save(a[9] + "_diff.png")


if __name__ == "__main__":
    main()
