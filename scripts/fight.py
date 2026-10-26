#!/usr/bin/env python3
"""Fight a combat to the end with simulated motion controls (Monado remote driver).

  fight.py [max_seconds]

Loop: if the game says a counter would succeed -> shield jab (counter gesture); otherwise sword
swings toward the gaze; every few seconds turn with the right stick toward a new direction so the
gaze sweeps over enemies. Prints outcome counters and exits when combat is over.
"""
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cmd  # noqa: E402

import hof2env  # noqa: E402  (this worktree's test environment, docs/two-environments.md)

H = hof2env.H


def sim(c):
    subprocess.run(["python3", H + "/scripts/xrsim.py", c], check=False)


def status():
    try:
        return cmd.status()
    except Exception:
        return {}


def main():
    limit = float(sys.argv[1]) if len(sys.argv) > 1 else 180
    sim("hand r 0.25 1.3 -0.35 0 0 0; hand l -0.25 1.25 -0.35 0 0 0; wait 200")
    t0 = time.time()
    last_turn = 0
    n = 0
    while time.time() - t0 < limit:
        st = status()
        state = st.get("state", "")
        if "Level_Play" not in state:
            print("combat over? state=%s after %.0fs" % (state, time.time() - t0))
            break
        try:
            r = cmd.send("tele?", 2)
        except SystemExit:
            r = ""
        if "COUNTER" in r:
            sim("move l 0 0.05 -0.4 110; wait 100; move l 0 -0.05 0.4 300")
            continue
        if time.time() - last_turn > 4:
            sim("stick r 1 0 350")   # smooth-turn ~40 degrees to sweep the gaze
            last_turn = time.time()
        sim("swing r right 130; wait 120")
        n += 1
        if n % 10 == 0:
            st = status()
            print("  %3.0fs %s | %s | %s" % (time.time() - t0, st.get("outcomes"), st.get("enemies"), st.get("combat", "")[:40]), flush=True)
    st = status()
    print("final:", st.get("state"), "|", st.get("outcomes"), "|", st.get("gestures"), "|", st.get("enemies"))


if __name__ == "__main__":
    main()
