#!/usr/bin/env python3
"""Playtest 3, item 3: from 'perf 10s' lines (a log on stdin or files), each window's state, render max, and how many of its
missed frames had a render phase over 8 ms (the table's spikes: whole-world searches inside a camera's cull)."""
import re
import sys
src = [open(f, errors="replace") for f in sys.argv[1:]] or [sys.stdin]
for fh in src:
    for l in fh:
        if "perf 10s" not in l:
            continue
        m = re.search(r"@(\S+) \[(GameState_\w+)\] (\d+) frames.*?miss ([\d.]+)% .*?logic ([\d.]+) .*?render ([\d.]+) \(max ([\d.]+)\)", l)
        if not m:
            continue
        rs = [float(x) for x in re.findall(r"r(\d+(?:\.\d+)?)\)", l)]
        print("%s %-26s frames %4s miss %5s%% logic %4s render %s max %5s | misses with r>8: %d of %d" % (
            m.group(1), m.group(2), m.group(3), m.group(4), m.group(5), m.group(6), m.group(7), sum(1 for r in rs if r > 8), len(rs)))
