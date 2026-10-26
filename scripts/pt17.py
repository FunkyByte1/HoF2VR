#!/usr/bin/env python3
"""Small helpers for the playtest fixes of 2026-10-17 (env 1): look somewhere, take a both-eye shot, keep it.

  pt17.py look <yaw> <pitch> [eye_height]   the simulated head turned about a neck pivot (as boards_test.head)
  pt17.py shot <name> [yaw pitch]           look (optional), capture both eyes -> testdata/shots/<name>_eyes.png and keep
                                            a copy as captures/fixes-2026-10-17/<name>.jpg
  pt17.py rest                              both hands down at the sides (out of the view)
"""
import math
import os
import shutil
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cmd  # noqa: E402
import hof2env  # noqa: E402

H = hof2env.H
OUT = os.environ.get("PT17_OUT") or os.path.join(H, "captures", "fixes-2026-10-17")
REST = "hand r 0.25 1.0 -0.2 0 -60 0; hand l -0.25 1.0 -0.2 0 -60 0; trig r 0; trig l 0; touch r 0; touch l 0"


def sim(c):
    r = subprocess.run(["python3", os.path.join(H, "scripts", "xrsim.py"), c], capture_output=True, text=True)
    return (r.stdout + r.stderr).strip()


def look(yaw, pitch, eye=1.66):
    off = (0.0, 0.10, -0.10)
    p, y = math.radians(pitch), math.radians(yaw)
    y1 = off[1] * math.cos(p) - off[2] * math.sin(p)
    z1 = off[1] * math.sin(p) + off[2] * math.cos(p)
    x2, z2 = z1 * math.sin(y), z1 * math.cos(y)
    return sim("head %.4f %.4f %.4f %g %g 0" % (x2, eye - off[1] + y1, -off[2] + z2, yaw, pitch))


def shot(name):
    r = cmd.send("shot " + name, 20)
    src = os.path.join(hof2env.TESTDATA, "shots", name + "_eyes.png")
    for _ in range(40):
        if os.path.exists(src) and time.time() - os.path.getmtime(src) < 30:
            break
        time.sleep(0.25)
    time.sleep(0.5)
    os.makedirs(OUT, exist_ok=True)
    dst = os.path.join(OUT, name + ".jpg")
    try:
        from PIL import Image
        Image.open(src).convert("RGB").save(dst, quality=88)
    except Exception:
        dst = os.path.join(OUT, name + ".png")
        shutil.copy(src, dst)
    return r + " -> " + dst


def main(a):
    if not a:
        print(__doc__)
        return
    if a[0] == "look":
        print(look(float(a[1]), float(a[2]), float(a[3]) if len(a) > 3 else 1.66))
    elif a[0] == "shot":
        if len(a) >= 4:
            look(float(a[2]), float(a[3]), float(a[4]) if len(a) > 4 else 1.66)
            time.sleep(0.6)
        print(shot(a[1]))
    elif a[0] == "rest":
        print(sim(REST))
    else:
        print(__doc__)


if __name__ == "__main__":
    main(sys.argv[1:])
