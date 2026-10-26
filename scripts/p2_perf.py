#!/usr/bin/env python3
"""Frame times in a live fight, Physical vs Classic (combat QA): the mod's own perf lines (every 10 s: avg, p95, p99, max,
stalls) over --seconds in each mode, the player idle facing the fight (god mode, enemies tough). Run it inside
`scripts/testenv.sh quiet` (the quiet flag: background GPU jobs pause) with no other test game running.
  p2_perf.py [--seconds 60] [--tag PERF]
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
args = sys.argv[1:]
SECS = float(args[args.index("--seconds") + 1]) if "--seconds" in args else 60


def send(c, t=6):
    try:
        return cmd.send(c, t)
    except SystemExit as e:
        return "ERR " + str(e)


def windows(since):
    with open(hof2env.GLOG, errors="replace") as f:
        f.seek(since)
        text = f.read()
    return re.findall(r"perf 10s @[\d:.]+ \[GameState_Level_Play\] (\d+) frames [\d.]+s: avg ([\d.]+) p50 [\d.]+ p95 ([\d.]+) p99 ([\d.]+) max ([\d.]+) ms.*?stalls (\d+)", text)


def measure(mode):
    send("vrcombat " + mode)
    time.sleep(12)   # the next full 10 s window starts in this mode
    start = os.path.getsize(hof2env.GLOG)
    t0 = time.time()
    while time.time() - t0 < SECS:
        send("face nearest")
        time.sleep(5)
    time.sleep(1)
    w = windows(start)
    alive = send("fight?")
    if not w:
        return "%s: no perf windows (%s)" % (mode, alive[:60])
    n = len(w)
    avg = sum(float(x[1]) for x in w) / n
    p99 = max(float(x[3]) for x in w)
    mx = max(float(x[4]) for x in w)
    st = sum(int(x[5]) for x in w)
    fr = sum(int(x[0]) for x in w)
    return "%s: %d windows, %d frames, avg %.2f ms, worst p99 %.1f ms, max %.1f ms, stalls %d (%s)" % (mode, n, fr, avg, p99, mx, st, alive[:40])


def main():
    send("god on")
    send("tough on")
    subprocess.run(["python3", H + "/scripts/xrsim.py", "head 0 1.7 0 0 0 0; hand r 0.25 1.35 -0.35 0 5 0; hand l -0.25 1.2 -0.3 0 0 0"], capture_output=True)
    r1 = measure("physical")
    print(r1, flush=True)
    r2 = measure("classic")
    print(r2, flush=True)
    r3 = measure("physical")
    print(r3, flush=True)
    send("vrcombat physical")
    return 0


if __name__ == "__main__":
    sys.exit(main())
