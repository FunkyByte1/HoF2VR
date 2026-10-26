#!/usr/bin/env python3
"""Weapon smoke test (coordinator 2026-10-11: "I've only tested the sword and shield so far").

  weapons_test.py [--tag W] Id [Id ...]      e.g. RustedAxe HugeHammer DuellingDaggers Deatharangs ElvenStaff
                                             a shield id (BroadShield, Buckler, ...) is tested with the current weapon

For each card: autoplay until the dungeon map, `equip <Id>` (the game's own SwapEquip), autoplay into the next fight,
then: what CombatVR holds in which hand (log), both-eye shots of the hands in front of the face, three sword swings
(registered swings and hits), the hands' state, and exceptions since the equip. Results in logs/weapons_test.log,
shots testdata/shots/<tag>_<Id>_*.
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
LOG = H + "/logs/weapons_test.log"
GLOG = hof2env.GLOG
args = sys.argv[1:]
TAG = "W"
if "--tag" in args:
    i = args.index("--tag")
    TAG = args[i + 1]
    del args[i:i + 2]


def log(msg):
    line = time.strftime("%H:%M:%S ") + msg
    print(line, flush=True)
    with open(LOG, "a") as f:
        f.write(line + "\n")


def sim(c):
    subprocess.run(["python3", H + "/scripts/xrsim.py", c], capture_output=True, text=True)


def send(c, t=8):
    try:
        return cmd.send(c, t)
    except SystemExit as e:
        return "ERR " + str(e)


def st():
    for _ in range(5):
        try:
            return cmd.status()
        except Exception:
            time.sleep(0.2)
    return {}


def state():
    return (st().get("state") or "").strip('"')


def mark():
    with open(GLOG, errors="replace") as f:
        return sum(1 for _ in f)


def since(m):
    with open(GLOG, errors="replace") as f:
        return f.read().splitlines()[m:]


def autoplay_until(pred, timeout):
    p = subprocess.Popen(["python3", H + "/scripts/autoplay.py", "20"], stdout=open(H + "/logs/weapons_test-autoplay.out", "a"), stderr=subprocess.STDOUT)
    try:
        deadline = time.time() + timeout
        while time.time() < deadline:
            if pred():
                return True
            time.sleep(1.0)
        return False
    finally:
        p.terminate()
        try:
            p.wait(10)
        except Exception:
            p.kill()


def main():
    send("gate auto 1.5")
    for wid in args:
        log("=== " + wid)
        if not autoplay_until(lambda: state() == "GameState_Map", 600):
            log("FAIL %s - never reached the map (state %s)" % (wid, state()))
            continue
        time.sleep(2)
        m0 = mark()
        log("equip: " + send("equip " + wid))
        time.sleep(3)
        eq = [l for l in since(m0) if "equip seam: equipped" in l or "Equipping" in l]
        log("  " + (eq[-1].split("] ")[-1] if eq else "no equip line"))
        if not autoplay_until(lambda: state() == "GameState_Level_Play", 600):
            log("FAIL %s - never reached a fight" % wid)
            continue
        time.sleep(2.5)
        send("god", 4)
        lines = since(m0)
        held = [l.split("] ")[-1] for l in lines if "CombatVR: holding" in l or "VRHands: grip of" in l or "handle of" in l]
        for h in held[-6:]:
            log("  " + h)
        # hands up in front of the face, both eyes
        sim("head 0 1.75 0 0 -25 0; hand r 0.18 1.35 -0.38 0 30 0; hand l -0.18 1.35 -0.38 0 30 0")
        time.sleep(1.2)
        send("shot %s_%s_hold" % (TAG, wid), 8)
        time.sleep(2)
        sim("hand r 0.32 1.2 -0.30 -20 10 0; hand l -0.30 1.25 -0.32 20 10 0")
        time.sleep(1.0)
        send("shot %s_%s_side" % (TAG, wid), 8)
        time.sleep(2)
        g0 = st().get("gestures", "")
        o0 = st().get("outcomes", "")
        for i in range(3):
            sim("hand r 0.25 1.3 -0.35 0 0 0; swing r right 160")
            time.sleep(1.2)
        g1 = st().get("gestures", "")
        o1 = st().get("outcomes", "")
        log("  gestures %s -> %s | outcomes %s -> %s" % (g0, g1, o0, o1))
        log("  hands: " + send("hands", 4)[:300])
        m1 = mark()
        send("held", 6)
        time.sleep(0.5)
        for l in since(m1):
            if "held renderers" in l or l.startswith("  "):
                log("  " + l.strip()[:300])
        exc = [l for l in since(m0) if "Exception" in l or "[Error" in l]
        log("  exceptions since equip: %d %s" % (len(exc), (exc[0][:200] if exc else "")))
        send("god off", 4)
    log("=== done")


if __name__ == "__main__":
    main()
