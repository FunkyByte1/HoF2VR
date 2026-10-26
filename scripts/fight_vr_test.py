#!/usr/bin/env python3
"""Checks for the 2026-10-11 afternoon feedback round, from the table into a fight and out through a death.

  fight_vr_test.py [--no-autoplay] [--no-death] [--tag F3]

Needs a session at the table (session.py start --snapshot first_fight --to table). Autoplay plays the table until a
fight starts loading, then this script takes over:
  gate_fight      the waiting space waits for A when the arena is ready; the board names the fight
  fight_hold      the game stays in Level_In while the gate waits (no combat before the player is there)
  go_button       a simulated A press lets the player go; Level_Play starts after the reveal + grace
  enemy_health    enemies spawned with Settings EnemyHealth of their health
  enemy_bars      health bars exist, small, just above the heads
  offview         an attack starting outside the view raises a marker + side buzz (head turned away), and slows it
  death           `die`: the death text is on the panel in the waiting space (no backdrop), the gate waits, A -> table
  hub_spot        the side table spot after the death faces the side table (yaw ~270), not outside the caravan
Results: PASS/FAIL lines on stdout and in logs/fight_vr_test.log; shots testdata/shots/<tag>_*.
"""
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cmd  # noqa: E402

import hof2env  # noqa: E402  (this worktree's test environment, docs/two-environments.md)

H = hof2env.H
LOG = H + "/logs/fight_vr_test.log"
GLOG = hof2env.GLOG
RESULTS = []
TAG = sys.argv[sys.argv.index("--tag") + 1] if "--tag" in sys.argv else "F3"


def log(msg):
    line = time.strftime("%H:%M:%S ") + msg
    print(line, flush=True)
    with open(LOG, "a") as f:
        f.write(line + "\n")


def result(name, ok, detail):
    RESULTS.append((name, ok))
    log(("PASS " if ok else "FAIL ") + name + " - " + detail)


def sim(c):
    subprocess.run(["python3", H + "/scripts/xrsim.py", c], capture_output=True, text=True)


def send(c, t=8):
    try:
        return cmd.send(c, t)
    except SystemExit as e:
        return "ERR " + str(e)


def st():
    for _ in range(5):
        try:
            return cmd.status()
        except Exception:
            time.sleep(0.2)
    return {}


def state():
    return (st().get("state") or "").strip('"')


def wait(pred, timeout, step=0.3):
    deadline = time.time() + timeout
    while time.time() < deadline:
        v = pred()
        if v:
            return v
        time.sleep(step)
    return None


def log_since(mark):
    with open(GLOG, errors="replace") as f:
        return f.read().splitlines()[mark:]


def log_mark():
    with open(GLOG, errors="replace") as f:
        return sum(1 for _ in f)


def main():
    log("=== fight_vr_test " + TAG)
    send("gate auto 0")   # this test presses A itself
    auto = None
    if "--no-autoplay" not in sys.argv:
        auto = subprocess.Popen(["python3", H + "/scripts/autoplay.py", "15"], stdout=open(H + "/logs/fight_vr_test-autoplay.out", "w"), stderr=subprocess.STDOUT)
        log("autoplay until a fight loads")
    try:
        s = wait(lambda: "Level_In" in state() and state(), 900, 1.0)
    finally:
        if auto is not None:
            auto.terminate()
            auto.wait(10)
    if not s:
        result("gate_fight", False, "never reached a fight")
        return
    sim("head 0 1.75 0 0 0 0; hand r 0.25 1.0 -0.25 0 -60 0; hand l -0.25 1.0 -0.25 0 -60 0")
    # B1 (2026-10-13, [Comfort] TouchToFight): the encounter card held out at the table comes first - A deals the player in,
    # then the campfire's gate waits as before
    c = wait(lambda: (lambda x: x if x.startswith("gate waiting") else None)(send("gate", 4)), 60, 0.5)
    direct = False
    mark = log_mark()
    if c and "encounter card at the table" in c:
        time.sleep(0.6)
        sim("btn r a 150")
        log("B1: the encounter card at the table answered with A")
        # item 30 (2026-10-18): A at a card whose fight has loaded goes straight into the fight - no campfire, no gate
        for _ in range(10):
            time.sleep(0.5)
            went = [l for l in log_since(mark) if "fight card: touched (" in l]
            if went:
                direct = "straight into the fight" in went[-1]
                break
    if direct:
        result("gate_fight", True, "straight into the fight from the card (item 30): no campfire gate")
    else:
        g = wait(lambda: (lambda x: x if x.startswith("gate waiting") and "encounter card" not in x else None)(send("gate", 4)), 60, 0.5)
        result("gate_fight", g is not None and "Into battle" in g, (g or "no gate")[:200])
        if not g:
            return
        time.sleep(1.0)
        send("shot %s_board" % TAG, 8)
        time.sleep(3.0)
        s1 = state()
        showcase = "showcase" in send("gate", 4)
        result("fight_hold", "Level_In" in s1, "state %s %s after 4 s of waiting at the gate" % (s1, "(showcase fight)" if showcase else ""))
        mark = log_mark()
        sim("btn r a 150")
    t0 = time.time()
    # the game's own enemy showcase (a new enemy's title card) waits for A as in the flat game
    s2 = None
    while time.time() - t0 < 60:
        if "Level_Play" in state():
            s2 = state()
            break
        if "Showcase" in (st().get("menu") or "") and time.time() - t0 > 3:
            sim("btn r a 150")
            time.sleep(1.5)
        time.sleep(0.2)
    dt = time.time() - t0
    lines = log_since(mark)
    went = direct or any("gate: go (button)" in l for l in lines)
    held = [l for l in lines if "gate: the fight starts" in l]
    result("go_button", went and s2 is not None, "A -> gate go %s, Level_Play after %.1f s %s" % (went, dt, held[0].split("] ")[-1] if held else "(no Level_In hold logged: showcase?)"))
    time.sleep(2.0)
    s = st()
    tun = s.get("enemyTuning", "")
    m = re.search(r"scaled (\d+)", tun)
    # (EnemyHealth 1.0 is the default since merge-ready #2: nothing to scale then - "health x1.00 scaled 0" is right)
    full = re.search(r"health x1\.00", tun) is not None
    result("enemy_health", m is not None and (int(m.group(1)) > 0 or full), "%s | %s" % (tun, s.get("enemies")))
    eb = send("enemybars", 4)
    m = re.search(r"(?:bars|meters) (\d+)", eb)   # (combat Stage 2 reports "meters N")
    result("enemy_bars", m is not None and int(m.group(1)) > 0, eb[:200])
    send("god", 4)
    sim("head 0 1.75 0 0 0 0")
    time.sleep(1.0)
    send("shot %s_fight_front" % TAG, 8)
    # turn the head away from the fight: attacks start out of view
    offview = None
    for yaw in (150, -150, 180, 120, -120):
        sim("head 0 1.75 0 %d 0 0" % yaw)
        deadline = time.time() + 12
        while time.time() < deadline:
            th = st().get("threats", "")
            m = re.search(r"off-view starts (\d+)", th)
            if m and int(m.group(1)) > 0:
                offview = th
                break
            time.sleep(0.25)
        if offview:
            send("shot %s_offview" % TAG, 8)
            break
    tu = st().get("enemyTuning", "")
    result("offview", offview is not None, "%s | %s | %s" % (offview or "no off-view attack in 60 s", tu, st().get("haptics", "")[:120]))
    sim("head 0 1.75 0 0 0 0")
    if "--no-death" in sys.argv:
        return
    # the death flow
    send("god off", 4)
    mark = log_mark()
    log("die: " + send("die", 4))
    sim("head 0 1.75 0 0 0 0")
    d = wait(lambda: "Level_Out" in state() and state(), 90, 0.3)
    log("death: " + str(d))
    time.sleep(2.5)
    send("shot %s_death_text" % TAG, 8)
    sim("head 0 1.75 0 70 0 0")
    time.sleep(1.5)
    send("shot %s_death_turned" % TAG, 8)
    sim("head 0 1.75 0 0 0 0")
    # (since 13 October a death goes back to the card table: the death text above the Dealer, then the end of the run -
    # no waiting space to press A in; a gate that waits counts too)
    sp = wait(lambda: (lambda x, p: x if x.startswith("gate waiting") else p if p.startswith(("place Caravan", "place Hub")) and "Level_" not in state() else None)(
        send("gate", 4), send("place", 4)), 90, 0.5)
    lines = log_since(mark)
    bg = [l for l in lines if "failure screen backdrop" in l]
    send("shot %s_death_space" % TAG, 8)
    time.sleep(1.5)
    send("rtshot %s_death_space" % TAG, 8)
    send("widgets 300", 8)
    send("widgets 0 Failure", 8)
    send("widgets 1000", 8)
    s = st()
    result("death", sp is not None, "back: %s | board %s | backdrops hidden: %d (logged) | menu %s" % ((sp or "none")[:80], s.get("board"), len(bg), s.get("menu")))
    sim("btn r a 150")
    time.sleep(6)
    # on to the challenge map (side table): A through the Post screens
    for _ in range(30):
        if "ChallengeMap" in state() or "Deckbuilder" in (st().get("menu") or ""):
            break
        send("press A 6", 4)
        g = send("gate", 4)
        if g.startswith("gate waiting"):
            sim("btn r a 150")
        time.sleep(3)
    time.sleep(3)
    pl = send("place", 4)
    lines = log_since(mark)
    side = [l for l in lines if "station caravan side table" in l]
    ok = False
    if side:
        m = re.search(r"floor \(([-\d.]+),([-\d.]+),([-\d.]+)\) yaw (\d+)", side[-1])
        if m:
            x, z, yaw = float(m.group(1)), float(m.group(3)), int(m.group(4))
            ok = 255 <= yaw <= 285 and -1.0 <= x <= 0.6 and -2.3 <= z <= -0.9
    send("shot %s_hub" % TAG, 8)
    result("hub_spot", ok, (side[-1].split("] ")[-1] if side else "no side table arrival") + " | " + pl)


if __name__ == "__main__":
    try:
        main()
    finally:
        n = len(RESULTS)
        bad = sum(1 for _, ok in RESULTS if not ok)
        log("=== %d checks, %d failed" % (n, bad))
