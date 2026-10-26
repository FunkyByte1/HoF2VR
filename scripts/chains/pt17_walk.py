#!/usr/bin/env python3
"""The walk of the places a player sees (playtest fixes 2026-10-17, env 1): Scenarios one after another, both-eye shots
kept in captures/fixes-2026-10-17/WALK_<n>_<place>_<view>.jpg. Start a session at the title first
(session.py start --snapshot magician_mid). Results on stdout and logs/pt17_walk.log."""
import os
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import cmd as C  # noqa: E402
import hof2env  # noqa: E402
import pt17  # noqa: E402

LOG = os.path.join(hof2env.H, "logs", "pt17_walk.log")


def log(m):
    line = time.strftime("%H:%M:%S ") + m
    print(line, flush=True)
    with open(LOG, "a") as f:
        f.write(line + "\n")


def status():
    try:
        return C.status()
    except Exception:
        return {}


def scen(label, until=None, timeout=90):
    log("scenario " + label + ": " + C.send("scen run " + label, 10))
    t0 = time.time()
    while time.time() - t0 < timeout:
        time.sleep(2)
        s = status()
        sc = s.get("scenarios", "")
        if "(" + label.split("/")[-1] + ": done)" in sc and (until is None or until in (s.get("menu", "") + " " + s.get("state", ""))):
            return s
    log("  timed out: " + str(status().get("scenarios", ""))[:120])
    return status()


def shots(n, place, views):
    pt17.sim(pt17.REST)
    for name, yaw, pitch in views:
        log("  " + _shot("WALK_%02d_%s_%s" % (n, place, name), yaw, pitch))


def _shot(name, yaw, pitch):
    """yaw / pitch: degrees from the line to the Dealer's table (the map) - each boot faces the station another way, so the
    look is taken from `headlook` at the map, then turned by these (yaw + = left, pitch from the eyes' level)."""
    r = C.send("headlook Scale/Map/MapLayout from 0 1.66 0", 5)
    base = 0.0
    try:
        base = float(r.split()[4])
    except Exception:
        pass
    pt17.look(base + yaw, pitch)
    time.sleep(0.7)
    return pt17.shot(name)


def main():
    open(LOG, "a").close()
    log("walk begins")
    # 1. the title
    shots(1, "title", [("ahead", 0, -5), ("feet", 0, -60), ("right", -70, -25)])
    # 2. a table event with a story choice
    scen("Table/Dialogue", "Encounter")
    time.sleep(2)
    shots(2, "story", [("dealer", 25, 0), ("board", 0, -25), ("column", -30, -20)])
    for _ in range(3):
        C.send("press A", 5)
        time.sleep(2)
        if "Choice" in status().get("menu", ""):
            break
    shots(2, "choice", [("board", 0, -25), ("dealer", 25, 0)])
    # 3. choose a card
    s = scen("Games/Choose a card")
    time.sleep(3)
    shots(3, "choose_card", [("ahead", 10, -25), ("down", 10, -45)])
    # 4. the pendulum
    scen("Games/Pendulum")
    time.sleep(3)
    shots(4, "pendulum", [("ahead", -10, -15), ("right", -35, -15)])
    C.send("press A", 5)
    time.sleep(3)
    # 5. a shop
    scen("Table/Shop")
    time.sleep(4)
    shots(5, "shop", [("ahead", 10, -20), ("down", 10, -40)])
    # 6. the end coins
    scen("Table/End coins")
    for k in range(4):
        time.sleep(1.2)
        _shot("WALK_06_end_coins_%d" % k, 10, -15)
    # 7. the call to battle and the campfire (gate auto is off: the campfire waits)
    C.send("gate auto 0", 5)
    log("scenario Places/Campfire (fight): " + C.send("scen run Places/Campfire (fight)", 10))
    t0 = time.time()
    while time.time() - t0 < 120 and "Level_In" not in C.send("state?", 3):
        time.sleep(0.5)
    for k in range(5):
        _shot("WALK_07_call_to_battle_%d" % k, 20, -15)
        time.sleep(0.4)
    t0 = time.time()
    while time.time() - t0 < 60 and "READY" not in status().get("board", ""):
        time.sleep(1)
    time.sleep(1.5)
    shots(8, "campfire", [("ahead", 0, 0), ("left", 35, -5), ("right", -35, -5)])
    C.send("gate auto 1.5", 5)
    log("walk done")


if __name__ == "__main__":
    main()
