#!/usr/bin/env python3
"""flashes.py <tag> [--min N]: frames where >= N view directions of one eye spike at once (a one-frame flash),
with the interval to the previous such frame, head yaw/pitch and speed."""
import csv, sys
import os as _os, sys as _sys  # noqa: E401
_sys.path.insert(0, _os.path.dirname(_os.path.abspath(__file__)))
import hof2env  # noqa: E402  (this worktree's test environment, docs/two-environments.md)
D = hof2env.H + "/testdata/luma/"
tag = sys.argv[1]
mn = int(sys.argv[sys.argv.index("--min") + 1]) if "--min" in sys.argv else 6
thr, rel = 8.0, 0.12
rows = {0: [], 1: []}
with open(D + tag + ".csv") as f:
    rd = csv.DictReader(f)
    NC = sum(1 for k in rd.fieldnames if k.startswith("c"))
    for r in rd:
        rows[int(r["eye"])].append((int(r["frame"]), float(r["t"]), float(r["yaw"]), float(r["pitch"]), [float(r["c%d" % k]) for k in range(NC)]))
for e in (0, 1):
    R = rows[e]
    last = None
    out = []
    for t in range(1, len(R) - 1):
        n = 0
        up = 0
        for k in range(NC):
            a, b, c = R[t - 1][4][k], R[t][4][k], R[t + 1][4][k]
            d1, d2 = b - a, b - c
            if d1 * d2 > 0 and min(abs(d1), abs(d2)) > thr and min(abs(d1), abs(d2)) > rel * max(a, b, c, 1):
                n += 1
                up += 1 if d1 > 0 else 0
        if n >= mn:
            fr = R[t][0]
            sp = abs(((R[t][2] - R[t - 1][2] + 180) % 360) - 180)
            out.append("  frame %d (+%s) %d dirs (%d brighter) yaw %.1f pitch %.1f speed %.2f t %.3f" % (fr, fr - last if last else "-", n, up, R[t][2], R[t][3], sp, R[t][1]))
            last = fr
    print("eye %s: %d flash frames" % ("LR"[e], len(out)))
    print("\n".join(out[:40]))
