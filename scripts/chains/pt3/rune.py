#!/usr/bin/env python3
"""Playtest 3 (env 1), run E: from the dungeon map of a magician_mid session, a fight won and the run ended (as session 4 did by
chance: the hub then has the Campfire section and The High Priestess), then the challenge map: the section plates (item 7: no
plate for the Campfire's own tab), the symbols' row (item 2) with the Campfire section chosen too. Pictures captures/pt3/e_*."""
import os
import sys
import time

H = "/home/bara/Projects/flat2vr/HandOfFate2"
sys.path.insert(0, H + "/scripts")
os.environ["PT17_OUT"] = H + "/captures/pt3"
import cmd as C  # noqa: E402
import pt17  # noqa: E402

pt17.OUT = H + "/captures/pt3"


def st():
    try:
        return C.status()
    except Exception:
        return {}


def log(m):
    print(time.strftime("%H:%M:%S ") + m, flush=True)


def shot(name, yaw=0, pitch=-30):
    pt17.sim(pt17.REST)
    pt17.look(yaw, pitch)
    time.sleep(0.8)
    log("  " + pt17.shot(name).split(" -> ")[-1])


def main():
    import b1_test as B
    B.into_level_in()
    log("level in: " + str(st().get("fightCard", ""))[:80])
    for _ in range(25):
        if "Level_Play" in st().get("state", ""):
            break
        C.send("gate go", 5)
        time.sleep(3)
    import indicator_shots
    log("left the fight: %s" % indicator_shots.leave())
    for _ in range(30):
        s = st()
        if s.get("state") == "GameState_ChallengeMap" and s.get("menu") == "ChallengeMap":
            break
        C.send("press A 6", 5)
        C.send("gate go", 5)
        time.sleep(3)
    time.sleep(4)
    log("at %s / %s" % (st().get("state"), st().get("menu")))
    log("boards: " + C.send("boards", 5)[:300])
    log("markers: " + C.send("markers", 5))
    shot("e_map_rest", 0, -30)
    shot("e_map_menu_left", 30, -40)
    r = C.send("ptouch Campfire", 5)
    log("campfire: " + r)
    time.sleep(3)
    log("boards: " + C.send("boards", 5)[:300])
    log("markers: " + C.send("markers", 5))
    shot("e_campfire_rest", 0, -30)
    shot("e_campfire_menu", 30, -40)


if __name__ == "__main__":
    main()
    # (item 24) the pendulum window, from a dev run's map
    sys.path.insert(0, H + "/scripts/chains/pt3")
    import runb
    runb.pt17.OUT = H + "/captures/pt3"
    runb.pendulum()
