#!/usr/bin/env python3
"""candidate 8 (env 1, from env 2's pf/pics.py): eye pictures.

  pics.py shot NAME                 both eyes now -> captures/cand8/NAME.jpg (+ NAME_left.jpg)
  pics.py strike NAME [timeout]     waits for an enemy strike (strike? not "none"), then the shot
  pics.py crit NAME [timeout]       waits for an open crit on the nearest enemy (poise? shows "open"), then the shot
"""
import os, sys, time, shutil, subprocess
H = "/home/bara/Projects/flat2vr/HandOfFate2"
sys.path.insert(0, H + "/scripts")
import cmd  # noqa: E402
OUT = os.environ.get("CAND8_PICS") or H + "/captures/cand8"
SHOTS = H + "/testdata/shots"


def send(c, t=8):
    try:
        return cmd.send(c, t)
    except SystemExit as e:
        return "ERR " + str(e)


def shot(name):
    os.makedirs(OUT, exist_ok=True)
    src = SHOTS + "/" + name + "_eyes.png"
    before = os.path.getmtime(src) if os.path.exists(src) else 0
    send("shot " + name)
    for _ in range(40):
        time.sleep(0.25)
        if os.path.exists(src) and os.path.getmtime(src) > before and os.path.getsize(src) > 0:
            time.sleep(0.5)
            break
    from PIL import Image
    im = Image.open(src).convert("RGB")
    im.save(OUT + "/" + name + ".jpg", quality=88)
    w, h = im.size
    im.crop((0, 0, w // 2, h)).save(OUT + "/" + name + "_left.jpg", quality=88)
    print("saved", OUT + "/" + name + ".jpg", im.size)


def wait_for(probe, test, timeout):
    t0 = time.time()
    while time.time() - t0 < timeout:
        r = send(probe, 4)
        if test(r):
            return r
        time.sleep(0.08)
    return None


def main():
    a = sys.argv[1:]
    if a[0] == "shot":
        shot(a[1])
    elif a[0] == "strike":
        r = wait_for("strike?", lambda r: r.strip() not in ("", "none") and not r.startswith("ERR"), float(a[2]) if len(a) > 2 else 30)
        print("strike:", (r or "none (timeout)")[:200])
        shot(a[1])
    elif a[0] == "strikered":
        r = wait_for("strike?", lambda r: " Red eta " in r.split(" | ")[0] and "offview" not in r.split(" | ")[0] and float(r.split(" eta ")[1].split()[0]) >= 0.55, float(a[2]) if len(a) > 2 else 40)
        print("strike:", (r or "none (timeout)")[:200])
        shot(a[1])
    elif a[0] == "crit":
        r = wait_for("poise? nearest", lambda r: "open" in r and "open -" not in r, float(a[2]) if len(a) > 2 else 30)
        print("crit:", (r or "none (timeout)")[:200])
        shot(a[1])


if __name__ == "__main__":
    main()
