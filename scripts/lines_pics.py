#!/usr/bin/env python3
"""Pictures of each kind of fight line, both eyes, in a live fight of this environment (the user, 2026-10-18: "In order to
make sure we are talking about the same lines, can you show me pictures of each kind of line and label them?").

  lines_pics.py strike NAME [White|Red|Gold] [live|valid|blocked|hit|out]   a melee blow's bar (waits for one in view)
  lines_pics.py shotbar NAME [any|white|red] [live|valid]                   a shot's / lob's bar (waits for one)
  lines_pics.py crit NAME [who]                                             the finishing arrows (the guard broken by the seam)
  lines_pics.py premo NAME SPEC...                                          a still marker from the `premo` seam
  lines_pics.py now NAME                                                    the view as it is
Out: $LINES_OUT (default the main worktree's HandOfFate2/captures/lines-<date>/) NAME.jpg (both eyes) and NAME_left.jpg.
The look in use is the cfg's or `vrlook`'s (the user's: Gilded) - set it with cmd.py before.
"""
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cmd  # noqa: E402
import hof2env  # noqa: E402

H = hof2env.H
OUT = os.environ.get("LINES_OUT", "/home/bara/Projects/flat2vr/HandOfFate2/captures/lines-" + time.strftime("%Y-%m-%d"))
SHOTS = H + "/testdata/shots"
HEAD = "head 0 1.7 0 0 0 0"
HANDS_DOWN = "hand r 0.32 0.85 0.05 0 -80 0; hand l -0.32 0.85 0.05 0 -80 0"
SHIELD_AWAY = "hand l -0.45 1.0 0.25 0 0 0"
GUARD = "hand r 0.25 1.35 -0.35 0 5 0; hand l -0.25 1.2 -0.3 0 0 0"
OUTCOMES = ["blocks", "counters", "hitsTaken", "dodges", "outOfRange"]


def send(c, t=8):
    try:
        return cmd.send(c, t)
    except SystemExit as e:
        return "ERR " + str(e)


def sim(c):
    subprocess.run(["python3", H + "/scripts/xrsim.py", c], capture_output=True, text=True)


def user_in_vr():
    try:
        return '"user": true' in open("/run/user/1000/vr-session/status.json").read()
    except OSError:
        return True


def shot(name):
    os.makedirs(OUT, exist_ok=True)
    src = SHOTS + "/LP_" + name + "_eyes.png"
    try:
        os.remove(src)
    except OSError:
        pass
    send("shot LP_" + name, 20)
    t0 = time.time()
    while time.time() - t0 < 6 and not (os.path.exists(src) and os.path.getsize(src) > 0):
        time.sleep(0.05)
    time.sleep(0.3)
    if not os.path.exists(src):
        print("NO SHOT", name)
        return None
    from PIL import Image
    im = Image.open(src).convert("RGB")
    im.save(OUT + "/" + name + ".jpg", quality=90)
    w, h = im.size
    im.crop((0, 0, w // 2, h)).save(OUT + "/" + name + "_left.jpg", quality=90)
    print("saved", OUT + "/" + name + ".jpg", im.size)
    return OUT + "/" + name + ".jpg"


def counts():
    s = send("pcombat?")
    d = {}
    for k, v in re.findall(r"([A-Za-z]+) (-?[\d.]+)", s):
        if k not in d:
            try:
                d[k] = float(v)
            except ValueError:
                pass
    return d


def strike(timeout=40.0, fresh=0.5, colours=("White", "Gold", "Red")):
    t0 = time.time()
    while time.time() - t0 < timeout:
        if user_in_vr():
            sys.exit("the user is in VR")
        s = send("strike?", 4)
        if s and s != "none" and not s.startswith(("ERR", "no ")):
            first = s.split(" | ")[0]
            if "offview" in first:
                send("face striker")
                time.sleep(0.05)
                continue
            m = re.search(r"(\w+) (Gold|White|Red) eta (-?[\d.]+) valid (\w+)", first)
            if m and float(m.group(3)) >= fresh and m.group(2) in colours:
                return {"who": m.group(1), "colour": m.group(2), "eta": float(m.group(3)), "raw": first}
        time.sleep(0.04)
    return None


def wait_outcome(c0, timeout=4.0):
    t0 = time.time()
    while time.time() - t0 < timeout:
        c = counts()
        for k in OUTCOMES:
            if c.get(k, 0) > c0.get(k, 0):
                return k
        time.sleep(0.03)
    return None


def do_strike(name, colour, mode):
    sim(HEAD + "; " + HANDS_DOWN)
    colours = (colour,) if colour else ("White", "Gold", "Red")
    for attempt in range(6):
        st = strike(colours=colours, fresh=0.75 if mode != "live" else 0.45)
        if st is None:
            print("no strike")
            return
        send("face striker")
        print("strike", st["raw"][:160])
        c0 = counts()
        if mode == "live":
            # about half way through its wind-up
            time.sleep(max(0.0, st["eta"] * 0.45))
            shot(name)
            return
        if mode == "out":
            # step back out of its reach: the bar dims; then its result (OUT OF REACH)
            send("heroat %s 0 4.0" % st["who"])
            send("face striker")
            time.sleep(0.12)
            shot(name + "_dim")
            what = wait_outcome(c0)
            shot(name + "_result_" + str(what))
            return
        if mode in ("valid", "blocked"):
            g = send("guardat shield")
            m = re.match(r"(move [lr] \S+ \S+ \S+)", g)
            if not m:
                print("guardat:", g)
                continue
            sim(m.group(1) + " 40")
            time.sleep(0.15)
            if mode == "valid":
                shot(name)
                return
            what = wait_outcome(c0)
            shot(name + "_" + str(what))
            if what == "blocks":
                return
            continue
        if mode == "hit":
            sim(SHIELD_AWAY)
            what = wait_outcome(c0)
            shot(name + "_" + str(what))
            if what == "hitsTaken":
                return
            continue


def do_shotbar(name, kind, mode):
    sim(HEAD + "; " + HANDS_DOWN)
    t0 = time.time()
    while time.time() - t0 < 60:
        if user_in_vr():
            sys.exit("the user is in VR")
        s = send("shots?", 4)
        for part in s.split(" | ")[1:]:
            m = re.search(r"(\S+) (lob|prj) eta (-?[\d.]+) blockable (\w+) valid (\w+)", part)
            if not m or float(m.group(3)) < 0.25:
                continue
            bl = m.group(4) == "True"
            if kind == "white" and not bl or kind == "red" and bl:
                continue
            print("shot", part[:160])
            if mode == "valid":
                g = send("shotguard")
                mm = re.match(r"(move [lr] \S+ \S+ \S+)", g)
                if mm:
                    sim(mm.group(1) + " 30")
                    time.sleep(0.06)
            shot(name)
            return
        time.sleep(0.03)
    print("no shot in 60 s")


def do_crit(name, who):
    send("face " + who)
    e = send("poise? " + who)
    print("poise:", e[:200])
    im = re.search(r"id (#-?\d+)", e)
    target = im.group(1) if im else who
    print(send("poiseset %s 0" % target))
    send("heroat %s 0 2.0" % target)
    send("face " + target)
    sim(HEAD + "; " + HANDS_DOWN)
    time.sleep(0.6)
    shot(name)


def main():
    a = sys.argv[1:]
    if not a:
        sys.exit(__doc__)
    what, name = a[0], a[1] if len(a) > 1 else "x"
    if what == "strike":
        colour = a[2] if len(a) > 2 and a[2] in ("White", "Red", "Gold") else None
        mode = a[3] if len(a) > 3 else "live"
        do_strike(name, colour, mode)
    elif what == "shotbar":
        do_shotbar(name, a[2] if len(a) > 2 else "any", a[3] if len(a) > 3 else "live")
    elif what == "crit":
        do_crit(name, a[2] if len(a) > 2 else "nearest")
    elif what == "premo":
        sim(HEAD + "; " + HANDS_DOWN)
        print(send("premo " + " ".join(a[2:])))
        time.sleep(0.3)
        shot(name)
        send("premo off")
    elif what == "now":
        shot(name)


if __name__ == "__main__":
    main()
