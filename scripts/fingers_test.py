#!/usr/bin/env python3
"""Finger-curl checks (VR/FingerCurl.cs) through the real input path: xrsim -> Monado (Index-style remote controllers)
-> OpenComposite's legacy controller state -> the gloves. Run OUTSIDE the Bash sandbox, at the card table of a test
session (e.g. after `session.py start --snapshot magician --to table`); restarts the xrsim daemon (new commands).

  fingers_test.py [--no-shots]
Checks (right hand; pose values in HandGeometry's space: 0 open .. 1 round the handle .. 2 fist):
  trigger touch -> index resting (~0.45) | finger off the trigger -> pointing | trigger pulled half / full -> index
  0.73 / 1.0 | grip half / full -> middle, ring, pinky ~0.8 / 1.0 (ring and pinky slightly later) | thumb touch on A ->
  thumb on the face (1.15), off -> hovering (0.8) | full grip, thumb off -> thumbs-up | the left hand stays as it was.
Results: PASS/FAIL lines; logs/fingers_test.log. Shots testdata/shots/FG_*.
"""
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cmd  # noqa: E402

import hof2env  # noqa: E402  (this worktree's test environment, docs/two-environments.md)

H = hof2env.H
LOG = H + "/logs/fingers_test.log"
RESULTS = []


def log(msg):
    line = time.strftime("%H:%M:%S ") + msg
    print(line, flush=True)
    with open(LOG, "a") as f:
        f.write(line + "\n")


def check(name, ok, detail=""):
    RESULTS.append((name, ok))
    log(("PASS " if ok else "FAIL ") + name + (" - " + detail if detail else ""))


def sim(c):
    r = subprocess.run(["python3", H + "/scripts/xrsim.py", c], capture_output=True, text=True)
    if r.returncode != 0:
        log("  xrsim error: " + (r.stdout + r.stderr).strip())


def pose(side):
    """(index, middle, ring, pinky, point, thumb) of a hand's smoothed pose, and the raw describe text."""
    out = cmd.send("fingers", 6)
    part = out.split("|| " + side + " ")[1].split(" || ")[0] if ("|| " + side + " ") in out else ""
    m = re.search(r"pose i([\d.]+) m([\d.]+) r([\d.]+) p([\d.]+) pt([\d.]+) th([\d.]+)", part)
    return (tuple(float(x) for x in m.groups()) if m else None), part, out


def near(a, b, tol):
    return a is not None and abs(a - b) <= tol


def step(name, sim_cmd, expect, wait=0.6, shot=None):
    """expect: dict index/middle/ring/pinky/point/thumb -> (value, tolerance)"""
    sim(sim_cmd)
    time.sleep(wait)
    p, part, _ = pose("R")
    if p is None:
        return check(name, False, "no pose: " + part[:200])
    keys = ["index", "middle", "ring", "pinky", "point", "thumb"]
    vals = dict(zip(keys, p))
    bad = [k for k, (v, t) in expect.items() if not near(vals[k], v, t)]
    check(name, not bad, " ".join("%s %.2f" % (k, vals[k]) for k in keys) + (" | off: " + ",".join(bad) if bad else ""))
    if shot and "--no-shots" not in sys.argv:
        cmd.send("shot FG_" + shot, 6)
        time.sleep(1.2)


def main():
    # the daemon must know `squeeze` / `ctouch` (only this environment's daemon is restarted)
    hof2env.restart_xrsim_daemon()
    time.sleep(0.5)
    # right hand up in front of the eyes (palm toward the face is not needed: the side view shows the fingers)
    base = "head 0 1.6 0 0 -10 0; hand r 0.12 1.42 -0.32 25 10 -60; hand l -0.25 1.1 -0.35 0 -40 0; trig r 0; trig l 0; squeeze r 0; squeeze l 0; " \
           "ctouch r trigger 0; ctouch r a 0; ctouch r b 0; ctouch r stick 0"
    sim(base)
    time.sleep(0.8)
    p0, part, out = pose("R")
    log("start: " + out[:400])
    l0, _, _ = pose("L")
    step("trigger touched -> index resting", "ctouch r trigger 1", {"index": (0.45, 0.06), "point": (0.0, 0.05)}, shot="rest")
    seen = cmd.send("fingers", 6).split(" || ")[0]
    check("the runtime's trigger touch arrives", "trigger=1" in seen or "trigger=True" in seen, seen)
    step("finger off the trigger -> lifted / pointing", "ctouch r trigger 0", {"index": (0.1, 0.06), "point": (0.55, 0.08)}, wait=0.8, shot="lifted")
    step("trigger half -> index 0.73", "trig r 0.5", {"index": (0.73, 0.06), "point": (0.0, 0.05)})
    step("trigger full -> index 1.0", "trig r 1.0", {"index": (1.0, 0.05)})
    sim("trig r 0; ctouch r trigger 1")
    step("grip half -> middle/ring/pinky ~0.8, ring/pinky later", "squeeze r 0.5", {"middle": (0.80, 0.08), "ring": (0.75, 0.10), "pinky": (0.70, 0.12)})
    p, _, _ = pose("R")
    check("ring and pinky close a little later than the middle", p is not None and p[1] >= p[2] >= p[3], "m %.2f r %.2f p %.2f" % (p[1], p[2], p[3]) if p else "-")
    step("grip full -> fingers closed", "squeeze r 1", {"middle": (1.0, 0.05), "ring": (1.0, 0.06), "pinky": (1.0, 0.08)})
    step("thumb on A -> on the face", "ctouch r a 1", {"thumb": (1.15, 0.06)})
    seen = cmd.send("fingers", 6).split(" || ")[0]
    check("the runtime's thumb touch arrives", "thumb=1" in seen or "thumb=True" in seen, seen)
    step("full fist (trigger pulled, grip, thumb on A)", "trig r 1.0", {"index": (1.0, 0.05), "middle": (1.0, 0.05), "thumb": (1.15, 0.06)}, shot="fist")
    sim("trig r 0; ctouch r trigger 1")
    step("thumb off A, grip full -> thumbs-up after 0.4 s", "ctouch r a 0", {"thumb": (0.05, 0.08)}, wait=1.2, shot="thumbup")
    step("grip open, thumb off -> hovering thumb, open hand", "squeeze r 0", {"thumb": (0.8, 0.08), "middle": (0.05, 0.05)}, wait=1.0, shot="open")
    l1, _, _ = pose("L")
    # (its index may change: once the runtime has shown trigger touch at all, an untouched trigger means a lifted finger)
    check("the left hand's grip and thumb did not follow the right", l0 is not None and l1 is not None and max(abs(a - b) for a, b in zip(l0[1:4] + l0[5:], l1[1:4] + l1[5:])) < 0.05,
          "L before %s after %s" % (l0, l1))
    sim(base)
    n = sum(1 for _, ok in RESULTS if ok)
    log("RESULT %d/%d PASS" % (n, len(RESULTS)))


if __name__ == "__main__":
    main()
