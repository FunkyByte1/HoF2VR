#!/usr/bin/env python3
"""flicker.py <tag|csv> [--thr LEVELS] [--rel FRAC] [--top N]
Reads testdata/luma/<tag>.csv (the mod's `lumalog`) and reports flicker:
  * spikes: a view direction (16x16 per eye, each eye's own projection) whose brightness jumps against BOTH neighbouring frames in opposite directions
    (a one-frame flash or dip) by more than THR levels (of 255) and REL of its value - a smooth head turn changes a
    cell's brightness gradually, a flicker does not;
  * eye mismatch: the same view direction differing between the eyes by more than 2*THR and 2*REL in a frame;
  * mean-brightness spikes of the whole eye image.
Prints counts and the worst events (frame, eye, cell row/col, head yaw/pitch, values). Exit 0 always."""
import csv
import os
import sys

import os as _os, sys as _sys  # noqa: E401
_sys.path.insert(0, _os.path.dirname(_os.path.abspath(__file__)))
import hof2env  # noqa: E402  (this worktree's test environment, docs/two-environments.md)
H = hof2env.H + "/testdata/luma/"
args = sys.argv[1:]
src = args[0]
path = src if src.endswith(".csv") else H + src + ".csv"
thr, rel, top = 8.0, 0.12, 12
for i, a in enumerate(args):
    if a == "--thr":
        thr = float(args[i + 1])
    elif a == "--rel":
        rel = float(args[i + 1])
    elif a == "--top":
        top = int(args[i + 1])

rows = {0: [], 1: []}
with open(path) as f:
    rd = csv.DictReader(f)
    NC = sum(1 for k in rd.fieldnames if k.startswith("c"))
    G = int(round(NC ** 0.5))
    for r in rd:
        e = int(r["eye"])
        cells = [float(r["c%d" % k]) for k in range(NC)]
        rows[e].append((int(r["frame"]), float(r["t"]), float(r["yaw"]), float(r["pitch"]), float(r["mean"]), cells))

n = min(len(rows[0]), len(rows[1]))
print("%s: %d frames (left %d, right %d)" % (os.path.basename(path), n, len(rows[0]), len(rows[1])))
if n < 3:
    sys.exit(0)

def spikes(series):
    out = []
    for t in range(1, len(series) - 1):
        a, b, c = series[t - 1], series[t], series[t + 1]
        d1, d2 = b - a, b - c
        if d1 * d2 <= 0:
            continue
        m = min(abs(d1), abs(d2))
        if m > thr and m > rel * max(b, a, c, 1.0):
            out.append((t, m, a, b, c))
    return out

events = []
for e in (0, 1):
    R = rows[e]
    for k in range(NC):
        s = [r[5][k] for r in R]
        for t, m, a, b, c in spikes(s):
            fr = R[t]
            events.append((m, e, k, fr[0], fr[2], fr[3], a, b, c))
    for t, m, a, b, c in spikes([r[4] for r in R]):
        fr = R[t]
        events.append((m, e, -1, fr[0], fr[2], fr[3], a, b, c))

mismatch = []
L, Rr = rows[0], rows[1]
for t in range(n):
    if L[t][0] != Rr[t][0]:
        continue
    for k in range(NC):
        a, b = L[t][5][k], Rr[t][5][k]
        d = abs(a - b)
        if d > 2 * thr and d > 2 * rel * max(a, b, 1.0):
            mismatch.append((d, k, L[t][0], L[t][2], L[t][3], a, b))

yaws = [r[2] for r in rows[0]]
rate = [abs(((yaws[i] - yaws[i - 1] + 180) % 360) - 180) for i in range(1, len(yaws))]
print("head yaw %.0f..%.0f, mean speed %.2f deg/frame, max %.2f" % (min(yaws), max(yaws), sum(rate) / max(1, len(rate)), max(rate) if rate else 0))
cell_spikes = [x for x in events if x[2] >= 0]
mean_spikes = [x for x in events if x[2] < 0]
print("spikes: %d cell spikes (left %d, right %d), %d whole-image spikes; eye mismatches: %d cell-frames" % (
    len(cell_spikes), sum(1 for x in cell_spikes if x[1] == 0), sum(1 for x in cell_spikes if x[1] == 1), len(mean_spikes), len(mismatch)))
by_cell = {}
for x in cell_spikes:
    by_cell[x[2]] = by_cell.get(x[2], 0) + 1
if by_cell:
    print("cells with spikes (row,col:count):", " ".join("%d,%d:%d" % (k // G, k % G, v) for k, v in sorted(by_cell.items(), key=lambda kv: -kv[1])[:16]))
for m, e, k, fr, yaw, pitch, a, b, c in sorted(events, reverse=True)[:top]:
    where = "image" if k < 0 else "dir r%d c%d" % (k // G, k % G)
    print("  spike %5.1f %s eye %s frame %d yaw %.1f pitch %.1f: %.1f -> %.1f -> %.1f" % (m, where, "LR"[e], fr, yaw, pitch, a, b, c))
for d, k, fr, yaw, pitch, a, b in sorted(mismatch, reverse=True)[:top]:
    print("  mismatch %5.1f dir r%d c%d frame %d yaw %.1f pitch %.1f: left %.1f right %.1f" % (d, k // G, k % G, fr, yaw, pitch, a, b))
