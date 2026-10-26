#!/usr/bin/env python3
"""The table's garbage-collection stalls (queued 2026-10-12: "GC stalls at the table - Boehm GC env vars").

  gc_probe.py [--label NAME] [--rounds N]

At the dungeon map (a session at the table): N rounds of the table's everyday events from the Scenarios catalog (gains,
losses, the zoom, a blessing; no fight), then the count of the game's own long frames at the table: every `perf stall`
line (>= 40 ms) in GameState_Map, those with a collection in them (`gc`), their worst, and the 10 s windows' p99.
Run twice - once as usual, once with the collector's settings in the environment of the launch, e.g.
  GC_INITIAL_HEAP_SIZE=1073741824 GC_FREE_SPACE_DIVISOR=1 python3 scripts/session.py start --snapshot magician_mid --to table
- and compare logs/gc_probe.log.
"""
import os
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cmd  # noqa: E402
import hof2env  # noqa: E402

H = hof2env.H
LOG = H + "/logs/gc_probe.log"
EVENTS = ["Gain_life", "Gain_gold", "Lose_gold", "Gain_food", "Lose_food", "Max_health_up"]


def log(msg):
    line = time.strftime("%H:%M:%S ") + msg
    print(line, flush=True)
    with open(LOG, "a") as f:
        f.write(line + "\n")


def send(c, t=10):
    try:
        return cmd.send(c, t)
    except SystemExit as e:
        return "ERR " + str(e)


def nlines():
    with open(hof2env.GLOG, errors="replace") as f:
        return sum(1 for _ in f)


def ready(timeout=40):
    t0 = time.time()
    while time.time() - t0 < timeout:
        if send("jump encounter zz_ready_probe").startswith("no encounter like"):
            return True
        time.sleep(1.5)
    return False


def main():
    a = sys.argv[1:]
    label = a[a.index("--label") + 1] if "--label" in a else os.environ.get("GC_INITIAL_HEAP_SIZE", "default")
    rounds = int(a[a.index("--rounds") + 1]) if "--rounds" in a else 3
    send("cfg Scenarios true")
    ready(60)
    n0 = nlines()
    t0 = time.time()
    for r in range(rounds):
        for e in EVENTS:
            send("scen here " + e)
            time.sleep(9)
            ready(30)
    with open(hof2env.GLOG, errors="replace") as f:
        lines = f.read().splitlines()[n0:]
    stalls = [l for l in lines if "perf stall" in l and "GameState_Map" in l]
    gcs = [l for l in stalls if re.search(r"\) gc", l)]
    busy = [int(m.group(1)) for m in (re.search(r"busy (\d+)", l) for l in stalls) if m]
    p99 = [float(m.group(1)) for m in (re.search(r"p99 ([\d.]+)", l) for l in lines if "perf 10s" in l and "GameState_Map" in l) if m]
    log("%s: %d rounds in %.0f s: %d frames >= 40 ms at the map, %d with a collection, worst %d ms; 10 s p99 median %.1f ms" % (
        label, rounds, time.time() - t0, len(stalls), len(gcs), max(busy) if busy else 0, sorted(p99)[len(p99) // 2] if p99 else -1))
    for l in gcs[:6]:
        log("   " + l.split("] ", 1)[-1][:150])


if __name__ == "__main__":
    main()
