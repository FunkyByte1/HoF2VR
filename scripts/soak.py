#!/usr/bin/env python3
"""Soak test: repeat boot -> table -> combat -> fight -> table, under gdb; log crashes.

  soak.py <cycles>          results appended to logs/soak.log
"""
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cmd  # noqa: E402
import session  # noqa: E402

import hof2env  # noqa: E402  (this worktree's test environment, docs/two-environments.md)

H = hof2env.H
LOG = H + "/logs/soak.log"


def alive():
    return hof2env.game_alive()   # this environment's test instance


def log(msg):
    line = time.strftime("%H:%M:%S ") + msg
    print(line, flush=True)
    with open(LOG, "a") as f:
        f.write(line + "\n")


def cycle(i):
    try:
        session.start(to="combat", snapshot="first_fight", gdb=True, install=(i == 0))
    except SystemExit as e:
        return "failed before combat: %s (alive=%s)" % (e, alive())
    cmd.send("god on")
    r = subprocess.run(["python3", H + "/scripts/fight.py", "300"], capture_output=True, text=True)
    tail = r.stdout.strip().splitlines()[-1:] if r.stdout else []
    if not alive():
        return "CRASH during fight: " + " ".join(tail)
    try:
        s = session.wait_state(["GameState_Map", "ChallengeMap", "Post"], 180)
    except SystemExit as e:
        return ("CRASH" if not alive() else "stuck") + " after fight: %s %s" % (e, " ".join(tail))
    time.sleep(8)
    if not alive():
        return "CRASH after returning to table"
    return "ok (" + s + ") " + " ".join(tail)


def main():
    n = int(sys.argv[1]) if len(sys.argv) > 1 else 5
    for i in range(n):
        res = cycle(i)
        log("cycle %d: %s" % (i + 1, res))
        subprocess.run([H + "/scripts/stop.sh"], stdout=subprocess.DEVNULL)
    log("soak done")


if __name__ == "__main__":
    main()
