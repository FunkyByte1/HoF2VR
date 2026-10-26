#!/usr/bin/env python3
"""Summarise the Phase 0 probe files (testdata/probes, from phase0_probes.py) as Markdown for docs/combat-probes.md.

  phase0_report.py [--min 1]

Prints: the strike atlas per enemy and attack (all sessions' strikes_*.tsv, each session's last dump), the fights and their
enemy kinds (enemies.tsv), and the effect events with what they contain (effects_*.txt): the telegraph glows, hit,
parry, block and swing sounds.
"""
import glob
import os
import statistics
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hof2env  # noqa: E402

P = hof2env.TESTDATA + "/probes"
MIN = int(sys.argv[sys.argv.index("--min") + 1]) if "--min" in sys.argv else 1


def strikes():
    rows = []
    files = sorted(glob.glob(P + "/strikes_*.tsv")) or [P + "/strikes.tsv"]
    for f in files:
        if not os.path.exists(f):
            continue
        with open(f) as fh:
            head = fh.readline().rstrip("\n").split("\t")
            for line in fh:
                v = line.rstrip("\n").split("\t")
                if len(v) == len(head):
                    rows.append(dict(zip(head, v)))
    return rows


def atlas(rows):
    groups = {}
    for r in rows:
        groups.setdefault((r["enemy"], r["action"], r["glow"]), []).append(r)
    print("| Enemy | Attack | Glow | n | Telegraph to end, s: median (min-max) | Reach at the end, m | Counter worked | Swing | Hit the player |")
    print("|---|---|---|---|---|---|---|---|---|")
    for (enemy, action, glow), g in sorted(groups.items()):
        if len(g) < MIN:
            continue
        secs = sorted(float(r["seconds"]) for r in g)
        reach = sorted(float(r["reach1"]) for r in g if float(r["reach1"]) >= 0)
        counter = sum(1 for r in g if r["counter_frac"] not in ("-", "0.00"))
        swings = {}
        for r in g:
            k = r["swing"].split(" ")[0]
            swings[k] = swings.get(k, 0) + 1
        sw = ", ".join("%s %d" % (k, n) for k, n in sorted(swings.items(), key=lambda x: -x[1])[:2])
        hit = sum(1 for r in g if r["damage"] != "-")
        print("| %s | %s | %s | %d | %.2f (%.2f-%.2f) | %s | %d/%d | %s | %d/%d |" % (
            enemy, action.replace("Action", ""), glow, len(g), statistics.median(secs), secs[0], secs[-1],
            "%.1f" % statistics.median(reach) if reach else "-", counter, len(g), sw, hit, len(g)))


def enemies():
    f = P + "/enemies.tsv"
    if not os.path.exists(f):
        return
    print("\n| Fight (snapshot#n:arena) | Enemy | Health | Rig | Polite | Kinds | Attacks |")
    print("|---|---|---|---|---|---|---|")
    seen = set()
    for line in open(f):
        v = line.rstrip("\n").split("\t")
        if len(v) < 7 or tuple(v) in seen:
            continue
        seen.add(tuple(v))
        print("| %s | %s | %s | %s | %s | %s | %s |" % (v[0], v[1], v[2], v[3], v[4], v[5] or "-", v[6].replace("Action", "")))


def effects():
    events, inst = {}, {}
    for f in sorted(glob.glob(P + "/effects_*.txt")) or [P + "/effects.txt"]:
        if not os.path.exists(f):
            continue
        part = 0
        for line in open(f, errors="replace"):
            line = line.rstrip("\n")
            if line.startswith("# effect instances"):
                part = 1
                continue
            if not line or line.startswith("#"):
                continue
            if part == 0:
                k, _, n = line.rpartition("\t")
                try:
                    events[k] = events.get(k, 0) + int(n)
                except ValueError:
                    continue
            else:
                k, _, v = line.partition(":\t")
                inst.setdefault(k, v)
    print("\n### Effect events (event | effect set | model: count)\n")
    by_event = {}
    for k, n in events.items():
        ev = k.split(" | ")[0]
        by_event[ev] = by_event.get(ev, 0) + n
    print(", ".join("`%s` %d" % (ev, n) for ev, n in sorted(by_event.items(), key=lambda x: -x[1])))
    print("\n### What the effects contain (start event | controller: contents)\n")
    for k, v in sorted(inst.items()):
        print("- `%s`: %s" % (k, v.strip()[:400]))


if __name__ == "__main__":
    r = strikes()
    print("## Strike atlas (%d strikes)\n" % len(r))
    atlas(r)
    enemies()
    effects()
