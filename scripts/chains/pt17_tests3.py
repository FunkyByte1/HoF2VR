#!/usr/bin/env python3
"""Candidate-8 prerequisites (2026-10-17 afternoon, env 1): limbo_test through a fight (first_fight snapshot), the
figurine test alone, N5's timing after the campfire gate (A pressed 6 s after it is ready). logs/pt17_tests3.log."""
import os
import subprocess
import sys
import time

H = "/home/bara/Projects/flat2vr/HandOfFate2"
sys.path.insert(0, H + "/scripts")
sys.path.insert(0, H + "/scripts/chains")
import cmd as C  # noqa: E402
import pt17  # noqa: E402
from pt17_tests2 import log as _log, run, st, gamelog, lines, fresh_table  # noqa: E402
import pt17_tests2  # noqa: E402

pt17_tests2.LOG = H + "/logs/pt17_tests3.log"
log = pt17_tests2.log


def main():
    os.environ["XAUTHORITY"] = "/run/user/1000/" + sorted(f for f in os.listdir("/run/user/1000") if f.startswith("xauth_"))[0]
    log("== tests3 begin")
    out = run(["python3", "scripts/limbo_test.py", "--snapshot", "first_fight", "--tag", "L8"], 2400)
    with open(H + "/logs/pt17_limbo.out", "w") as f:
        f.write(out)
    for l in out.strip().splitlines()[-16:]:
        log("  limbo " + l[:220])
    fresh_table()
    out = run(["python3", "scripts/interact_test.py", "figurine"], 600)
    for l in lines(out, r"PASS|FAIL|passed"):
        log("  " + l)
    log("N5 map: " + C.send("scen run Table/Gain gold", 10))
    t0 = time.time()
    while time.time() - t0 < 90 and "(Gain gold: done)" not in st().get("scenarios", ""):
        time.sleep(2)
    C.send("gate auto 0", 5)
    log("N5: " + C.send("scen run Places/Campfire (fight)", 10))
    t0 = time.time()
    while time.time() - t0 < 150 and not ("READY" in st().get("board", "") and "Into battle" in st().get("board", "")):
        time.sleep(1)
    log("N5 board: " + st().get("board", "")[:120])
    time.sleep(6)
    n0 = gamelog().count("progress: fight_in done")
    pt17.sim("btn r a 150")
    t0 = time.time()
    while time.time() - t0 < 60 and gamelog().count("progress: fight_in done") <= n0:
        time.sleep(1)
    for l in [l for l in gamelog().splitlines() if "progress: fight_in" in l or "gate: go" in l or "fade release" in l][-5:]:
        log("N5 " + l[-230:])
    C.send("gate auto 1.5", 5)
    run(["python3", "scripts/session.py", "stop"], 120)
    log("savecheck: " + run(["scripts/savecheck.sh"], 60).strip().splitlines()[-1])
    log("== tests3 done")


if __name__ == "__main__":
    main()
