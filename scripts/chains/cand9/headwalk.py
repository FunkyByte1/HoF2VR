#!/usr/bin/env python3
"""Candidate 9 (env 1): the head-moving picture walk at one place - both eyes at rest and with the head moved (turned
30 deg left and right, leaned 20 cm forward and 20 cm to the right, back at rest), the game's time stopped (timescale 0;
the mod runs on unscaled time, so what the mod moves with the head still moves). At each pose:
  - `prox still check`: every card, token, stack, die and the figurine, the mod's own draw-time move against rest (P2-1);
  - `bounds HoF2VR` and `bounds Containers`: every renderer of the mod's own objects (boards, plates, labels, markers, the
    glove...) and of the game's 3D UI containers, its world box centre against rest. A thing that moves at a moved pose but
    is back at the final rest follows the head; one that also moved at the final rest animates by itself.
Both eyes -> $CAND9_PICS/<tag>_<pose>.jpg. Results: one "HEADWALK <tag> ..." line on stdout and in logs/cand9_walk.log;
details in logs/cand9_headwalk.log.

  headwalk.py TAG [--part PATH|--yaw DEG] [--pitch DEG] [--no-freeze] [--keep-hands]
The hands go down at the sides in the place's frame first (--keep-hands: as they are; the fights' guard pose).
"""
import math
import os
import re
import sys
import time

H = "/home/bara/Projects/flat2vr/HandOfFate2"
sys.path.insert(0, H + "/scripts")
sys.path.insert(0, H + "/scripts/chains")
import cmd as C  # noqa: E402
import hof2env  # noqa: E402
import pt17  # noqa: E402

OUT = os.environ.get("CAND9_PICS") or H + "/captures/cand9"
pt17.OUT = OUT
LOG = H + "/logs/cand9_walk.log"
DLOG = H + "/logs/cand9_headwalk.log"
EYE = 1.66
KEYS = ("HoF2VR", "Containers")
# name, yaw, pitch (added to the place's), right, up, forward (m, in the place's frame)
POSES = [
    ("rest", 0, 0, 0, 0, 0),
    ("turn_left30", 30, 0, 0, 0, 0),
    ("turn_right30", -30, 0, 0, 0, 0),
    ("lean_fwd20", 0, -8, 0, -0.04, 0.20),
    ("lean_right20", 0, 0, 0.20, -0.03, 0),
    ("rest_again", 0, 0, 0, 0, 0),
]
SHOT = {"rest", "turn_left30", "turn_right30", "lean_fwd20", "lean_right20"}


def log(m, detail=False):
    line = time.strftime("%H:%M:%S ") + m
    if not detail:
        print(line, flush=True)
        with open(LOG, "a") as f:
            f.write(line + "\n")
    with open(DLOG, "a") as f:
        f.write(line + "\n")


def send(c, t=8):
    try:
        return C.send(c, t)
    except SystemExit as e:
        return "ERR " + str(e)


def base_yaw(part):
    r = send("headlook %s from 0 1.66 0" % part, 5)
    try:
        return float(r.split()[4])
    except Exception:
        return None


def pose(base, yaw, pitch, right=0.0, up=0.0, fwd=0.0):
    y = math.radians(base)
    fx, fz = -math.sin(y), -math.cos(y)
    rx, rz = math.cos(y), -math.sin(y)
    x = rx * right + fx * fwd
    z = rz * right + fz * fwd
    return pt17.sim("head %.4f %.4f %.4f %g %g 0" % (x, EYE + up, z, base + yaw, pitch))


def hands(base, right=0.25, up=1.0, fwd=0.2, pitch=-60):
    """Both hands in the place's frame (turned by its yaw, as a player's body faces it): by default down at the sides."""
    y = math.radians(base)
    fx, fz = -math.sin(y), -math.cos(y)
    rx, rz = math.cos(y), -math.sin(y)
    cmds = []
    for side, r in (("r", right), ("l", -right)):
        cmds.append("hand %s %.4f %.4f %.4f %g %g 0" % (side, rx * r + fx * fwd, up, rz * r + fz * fwd, base, pitch))
    return pt17.sim("; ".join(cmds) + "; trig r 0; trig l 0; touch r 0; touch l 0")


def prox(op):
    send("prox still " + op, 5)
    r = "?"
    for _ in range(24):
        time.sleep(0.25)
        r = send("prox still", 5)
        if r not in ("marking", "checking"):
            return r
    return r


LINE = re.compile(r"^\s+(.+?) active=(True|False)(?: \| R min=\(([^)]*)\) max=\(([^)]*)\))?")


def dump():
    """{path: [centre, ...]} of the renderers of the mod's objects and the game's 3D UI containers, from the game log."""
    res = {}
    for key in KEYS:
        try:
            off = os.path.getsize(hof2env.GLOG)
        except OSError:
            off = 0
        send("bounds %s 2500" % key, 10)
        block = None
        for _ in range(20):
            time.sleep(0.15)
            with open(hof2env.GLOG, "rb") as f:
                f.seek(off)
                text = f.read().decode("utf-8", "replace")
            i = text.find("bounds '%s':" % key)
            if i >= 0:
                block = text[i:].splitlines()[1:]
                break
        if block is None:
            continue
        for ln in block:
            m = LINE.match(ln)
            if not m:
                if ln.startswith("  "):
                    continue
                break
            if m.group(3) is None or m.group(2) != "True":
                continue
            a = [float(v) for v in m.group(3).split(",")]
            b = [float(v) for v in m.group(4).split(",")]
            c = tuple((a[k] + b[k]) / 2 for k in range(3))
            res.setdefault(m.group(1), []).append(c)
    return res


def diff(a, b, sc):
    """[(metres, path)] of the renderers whose box centre moved more than 3 mm (matched per path in order)."""
    out = []
    for p, ca in a.items():
        cb = b.get(p)
        if not cb or len(cb) != len(ca):
            continue
        for k in range(len(ca)):
            d = math.dist(ca[k], cb[k]) / sc
            if d > 0.003:
                out.append((d, p if len(ca) == 1 else "%s #%d" % (p, k)))
    out.sort(reverse=True)
    return out


def scale():
    try:
        s = C.status()
        for k in ("scale", "rigScale"):
            if k in s:
                return float(s[k])
    except Exception:
        pass
    m = re.search(r"scale ([0-9.]+)", send("posture", 4))
    return float(m.group(1)) if m else 1.0


def walk(tag, part=None, yaw=0.0, pitch=-30.0, freeze=True, keep_hands=False):
    base = base_yaw(part) if part else None
    if base is None:
        base = yaw
    else:
        base += yaw
    if not keep_hands:
        hands(base)
    sc = scale()
    st = send("state?", 3)
    pose(base, 0, pitch)
    time.sleep(1.5)
    if freeze:
        send("timescale 0", 4)
        time.sleep(0.4)
    try:
        mark = prox("mark")
        rest = dump()
        log("headwalk %s: %s | %s | base yaw %.1f pitch %.0f | mod/UI renderers %d | scale %.2f" % (
            tag, st[:50], mark[:60], base, pitch, sum(len(v) for v in rest.values()), sc), True)
        moved_at = {}
        cards_worst = 0.0
        cards_bad = []
        cards_at = {}
        for name, dy, dp, right, up, fwd in POSES:
            pose(base, dy, pitch + dp, right, up, fwd)
            time.sleep(1.0)
            r = prox("check")
            mm = re.search(r"by the mod.s moves (\d+), most ([0-9.]+) mm", r)
            v = float(mm.group(2)) if mm else -1.0
            cards_worst = max(cards_worst, v)
            cards_at[name] = v
            if v > 1.0:
                cards_bad.append("%s %.0f mm" % (name, v))
            d = diff(rest, dump(), sc)
            moved_at[name] = d
            pic = ""
            if name in SHOT:
                pic = pt17.shot("%s_%s" % (tag, name)).split(" -> ")[-1]
            log("  %s %-13s cards: %s | mod/UI renderers moved %d%s | %s" % (
                tag, name, r[:200], len(d), "".join(" | %s %.0f mm" % (p, x * 1000) for x, p in d[:6]), os.path.basename(pic)), True)
        again = {p for _, p in moved_at.get("rest_again", [])}
        follow = {}
        for name, d in moved_at.items():
            if name in ("rest", "rest_again"):
                continue
            for x, p in d:
                if p not in again and x >= 0.01:
                    follow[p] = max(follow.get(p, 0.0), x)
        anim = sorted(again)
        fl = sorted(follow.items(), key=lambda kv: -kv[1])
        # a card move that is there at the final rest too is the mod's own motion (a coin's bubble, a settling hold), not the
        # head's: counted only where it is over the final rest's by more than 1 mm
        base_mv = max(0.0, cards_at.get("rest_again", 0.0))
        if base_mv > 1.0:
            cards_bad = ["%s %.0f mm" % (n, v) for n, v in cards_at.items() if n not in ("rest", "rest_again") and v > base_mv + 1.0]
            cards_bad.append("(%.0f mm at the final rest too: by itself)" % base_mv)
        ok = not [c for c in cards_bad if not c.startswith("(")] and not fl
        log("HEADWALK %s %s: cards (mod's moves) worst %.1f mm%s | follow the head (>= 1 cm, back at rest): %d%s | move by "
            "themselves: %d%s" % (tag, "PASS" if ok else "CHECK", cards_worst, (" at " + ", ".join(cards_bad)) if cards_bad else "",
                                  len(fl), "".join(" | %s %.0f mm" % (p, x * 1000) for p, x in fl[:8]),
                                  len(anim), (" (" + ", ".join(anim[:4]) + ")") if anim else ""))
        return ok
    finally:
        if freeze:
            send("timescale 1", 4)
        pose(base, 0, pitch)


def main(a):
    if not a:
        print(__doc__)
        return
    tag = a[0]
    part = a[a.index("--part") + 1] if "--part" in a else None
    yaw = float(a[a.index("--yaw") + 1]) if "--yaw" in a else 0.0
    pitch = float(a[a.index("--pitch") + 1]) if "--pitch" in a else -30.0
    walk(tag, part, yaw, pitch, "--no-freeze" not in a, "--keep-hands" in a)


if __name__ == "__main__":
    main(sys.argv[1:])
