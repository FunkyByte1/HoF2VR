#!/usr/bin/env python3
"""The playtest fixes' second test run (2026-10-17 afternoon, env 1): figurine set-down alone, the dice re-roll's rim,
T6 made to act, the VR settings sliders, N5's timing after the gate, then the walk, limbo_test and both table sweeps.
Output: logs/pt17_tests2.log (and each tool's own log). Run outside the Bash sandbox, from the main worktree."""
import os
import re
import subprocess
import sys
import time

H = os.path.realpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))   # this worktree's (its env's game)
sys.path.insert(0, H + "/scripts")
import cmd as C  # noqa: E402
import hof2env  # noqa: E402
import pt17  # noqa: E402

LOG = H + "/logs/pt17_tests2.log"
GLOG = hof2env.GLOG


def log(m):
    line = time.strftime("%H:%M:%S ") + m
    print(line, flush=True)
    with open(LOG, "a") as f:
        f.write(line + "\n")


def run(args, timeout):
    try:
        r = subprocess.run(args, cwd=H, capture_output=True, text=True, timeout=timeout)
        return r.stdout + r.stderr
    except subprocess.TimeoutExpired as e:
        return "TIMEOUT " + str(e)


def st():
    try:
        return C.status()
    except Exception:
        return {}


def gamelog():
    try:
        with open(GLOG, errors="replace") as f:
            return f.read()
    except Exception:
        return ""


def lines(out, pat):
    return [l for l in out.splitlines() if re.search(pat, l)]


def fresh_table():
    out = run(["python3", "scripts/session.py", "start", "--snapshot", "magician_mid", "--to", "table"], 1200)
    log("session: " + " | ".join(out.strip().splitlines()[-2:]))


def drag(seam, idx, frm, to):
    """A simulated fingertip onto a slider at `frm`, pressed 8 mm in, dragged to `to`, lifted off (seam: handcheck slider /
    vrslider)."""
    def point(v, depth):
        r = C.send("%s %d %s %s" % (seam, idx, v, depth), 8)
        return r.split("|")[0].strip(), r
    pt17.sim("hand r 0.25 1.2 -0.15 0 -30 0")
    time.sleep(0.4)
    for v, d, ms in ((frm, -3, 400), (frm, 0.8, 250), (to, 0.8, 700), (to, -5, 300)):
        p, _ = point(v, d)
        mv = C.send("handto r tip " + p, 8)
        pt17.sim(mv + " %d" % ms)
        time.sleep(ms / 1000.0 + 0.3)
    return point(to, 0.8)[1]


def main():
    open(LOG, "a").close()
    log("== tests2 begin")
    os.environ["XAUTHORITY"] = sorted(f for f in os.listdir("/run/user/1000") if f.startswith("xauth_"))[0].join(["/run/user/1000/", ""])
    run(["scripts/monado.sh", "start"], 120)
    # 1. the figurine's set-down, alone on a fresh map
    fresh_table()
    out = run(["python3", "scripts/interact_test.py", "figurine"], 600)
    for l in lines(out, r"PASS|FAIL|passed"):
        log("  " + l)
    # 2. the dice re-roll: the rim counted
    out = run(["python3", "scripts/interact_test.py", "dice_reroll_vr"], 600)
    for l in lines(out, r"PASS|FAIL|passed"):
        log("  " + l)
    # 3. T6: the stat fan put onto the map's middle -> it must step off it
    pt17.sim(pt17.REST)
    log("T6 before: " + st().get("tableHud", "")[:160])
    log("  " + C.send("tablehud -0.10 0.50 0.06 0", 5))
    time.sleep(2.5)
    moved = [l for l in gamelog().splitlines() if "stat cards moved" in l]
    log("T6 fan: " + (moved[-1][-80:] if moved else "never moved") + " | " + st().get("tableHud", "")[:160])
    r = C.send("headlook Card_ST_Health from 0 1.66 0", 5)
    if r.startswith("head"):
        pt17.sim(r)
        time.sleep(0.8)
        log("  " + pt17.shot("T6_fan_off_the_map"))
    log("  " + C.send("tablehud -0.56 0.26 0.06 0", 5))
    log("T6 lift: " + st().get("keepout", "")[:200])
    # 4. M1: the VR settings page's number rows
    C.send("press START 6", 5)
    time.sleep(2.0)
    log("M1 pause: " + C.send("vrset open Turning", 5))
    time.sleep(1.5)
    vs = st().get("vrsettings", "")
    log("M1 page: " + vs[:300])
    res = None
    for i in range(1, 10):
        r = C.send("vrslider %d 0 0.8" % i, 5)
        if r.startswith("no slider"):
            continue
        m = re.search(r"\| (.+?) (-?[\d.]+) drags", r)
        if not m:
            continue
        name, now = m.group(1), float(m.group(2))
        lo_hi = re.search(r"%s=([^;]+);" % re.escape(name), vs)
        log("M1 row %d '%s' now %s (%s)" % (i, name, now, lo_hi.group(1) if lo_hi else "-"))
        target = now * 0.5 if now > 0 else 1.0
        res = drag("vrslider", i, now, target)
        log("M1 after the drag: " + res)
        r2 = C.send("headlook HoF2VR_VRSettings from 0 1.66 0", 5)
        if r2.startswith("head"):
            pt17.sim(r2)
            time.sleep(0.6)
        log("  " + pt17.shot("M1_vrsettings_slider"))
        log("M1 page after: " + st().get("vrsettings", "")[:300])
        break
    if res is None:
        log("M1: no number row with a slider on this tab")
    C.send("vrset close", 5)
    time.sleep(1.0)
    C.send("press START 6", 5)
    time.sleep(2.0)
    # 5. N5: a fight's call to battle, A pressed 6 s after the campfire is ready; the progress line must time from the A
    C.send("gate auto 0", 5)
    log("N5: " + C.send("scen run Places/Campfire (fight)", 10))
    t0 = time.time()
    while time.time() - t0 < 150 and "READY" not in st().get("board", ""):
        time.sleep(1)
    log("N5 board: " + st().get("board", "")[:120])
    time.sleep(6)
    n0 = gamelog().count("progress: fight_in done")
    pt17.sim("btn r a 150")
    t0 = time.time()
    while time.time() - t0 < 60 and gamelog().count("progress: fight_in done") <= n0:
        time.sleep(1)
    done = [l for l in gamelog().splitlines() if "progress: fight_in done" in l or "gate: go" in l]
    for l in done[-3:]:
        log("N5 " + l[-230:])
    C.send("gate auto 1.5", 5)
    log("T6 lift after the call to battle: " + st().get("keepout", "")[:220])
    time.sleep(3)
    # 6. the walk (a fresh map: the fight above left the table)
    fresh_table()
    out = run(["python3", "scripts/chains/pt17_walk.py"], 1200)
    log("walk: " + " | ".join(out.strip().splitlines()[-2:]))
    run(["python3", "scripts/session.py", "stop"], 120)
    # 7. limbo_test, the sweeps
    out = run(["python3", "scripts/limbo_test.py", "--snapshot", "magician", "--noshots"], 2400)
    for l in out.strip().splitlines()[-14:]:
        log("  limbo " + l[:220])
    fresh_table()
    out = run(["python3", "scripts/table_sweep.py", "--lean"], 3600)
    with open(H + "/logs/pt17_sweep_standing.out", "w") as f:
        f.write(out)
    for l in lines(out, r"^====|violation|VIOL"):
        log("  sweep " + l[:260])
    fresh_table()
    out = run(["python3", "scripts/table_sweep.py", "--seated", "--lean", "--reach"], 3600)
    with open(H + "/logs/pt17_sweep_seated.out", "w") as f:
        f.write(out)
    for l in lines(out, r"^====|violation|VIOL"):
        log("  sweep seated " + l[:260])
    run(["python3", "scripts/session.py", "stop"], 120)
    log("savecheck: " + run(["scripts/savecheck.sh"], 60).strip().splitlines()[-1])
    log("== tests2 done")


if __name__ == "__main__":
    main()
