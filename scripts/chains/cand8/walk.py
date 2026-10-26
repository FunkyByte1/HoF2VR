#!/usr/bin/env python3
"""Candidate 8's picture walk, the places at the table and the title (env 1; from pt17_walk.py): the title, a story and
its choice, the dice with a re-roll, choose-a-card, the pendulum, a shop, the call to battle and the campfire, the end
coins - both eyes -> captures/cand8/walk_NN_<place>_<view>.jpg. Start at the title of a session (magician_mid)."""
import os
import sys
import time

os.environ["PT17_OUT"] = os.environ.get("CAND8_PICS") or "/home/bara/Projects/flat2vr/HandOfFate2/captures/cand8"
H = "/home/bara/Projects/flat2vr/HandOfFate2"
sys.path.insert(0, H + "/scripts")
sys.path.insert(0, H + "/scripts/chains")
import cmd as C  # noqa: E402
import pt17  # noqa: E402
import pt17_walk as W  # noqa: E402

W.LOG = H + "/logs/cand8_walk.log"
log, scen, status, _shot = W.log, W.scen, W.status, W._shot


def shots(n, place, views):
    pt17.sim(pt17.REST)
    for name, yaw, pitch in views:
        log("  " + _shot("walk_%02d_%s_%s" % (n, place, name), yaw, pitch))


def main():
    log("cand8 walk begins")
    shots(1, "title", [("ahead", 0, -5), ("feet", 0, -60), ("right", -70, -25)])
    scen("Table/Dialogue", "Encounter")
    time.sleep(2)
    shots(2, "story", [("dealer", 0, 5), ("board", 0, -25), ("column", -30, -20)])
    for _ in range(3):
        C.send("press A", 5)
        time.sleep(2)
        if "Choice" in status().get("menu", ""):
            break
    shots(2, "choice", [("board", 0, -25), ("dealer", 0, 5)])
    # the dice with a re-roll: a target out of reach, a throw, a die marked, the Re-roll plate
    scen("Table/Gain gold")
    C.send("mg dice 99 0 0", 8)
    t0 = time.time()
    while time.time() - t0 < 40 and not status().get("dice", "").startswith("Catapault"):
        time.sleep(1)
    shots(3, "dice", [("waiting", 0, -40)])
    C.send("dice throw 0 0.3 1.8 r game", 8)
    t0 = time.time()
    while time.time() - t0 < 25 and not status().get("dice", "").startswith("Marking"):
        time.sleep(1)
    time.sleep(1.5)
    shots(3, "dice", [("spread", 0, -45)])
    C.send("world click PlayerDiceRoller/Container/D6", 8)
    time.sleep(1.5)
    shots(3, "dice", [("marked_reroll", 0, -45), ("roll_status", 0, -10)])
    log("  reroll: " + status().get("reroll", "")[:120])
    C.send("reroll press", 8)
    time.sleep(3)
    C.send("dice throw 0 0.2 1.5 r game", 8)
    time.sleep(6)
    for _ in range(4):
        C.send("press A", 5)
        time.sleep(1.5)
    scen("Games/Pendulum")
    time.sleep(3)
    shots(5, "pendulum", [("ahead", 0, -15), ("right", -35, -15)])
    C.send("press A", 5)
    time.sleep(4)
    scen("Games/Choose a card")
    time.sleep(3)
    shots(4, "choose_card", [("ahead", 0, -10), ("down", 0, -40)])
    import interact_test as T
    T.finish_task()
    time.sleep(2)
    scen("Table/Shop")
    time.sleep(4)
    shots(6, "shop", [("ahead", 0, -15), ("down", 0, -40)])
    # the call to battle and the campfire (the gate waits for A: auto off)
    C.send("gate auto 0", 5)
    log("scenario Places/Campfire (fight): " + C.send("scen run Places/Campfire (fight)", 10))
    t0 = time.time()
    while time.time() - t0 < 150 and "Level_In" not in C.send("state?", 3):
        time.sleep(0.5)
    for k in range(5):
        log("  " + _shot("walk_07_call_to_battle_%d" % k, 0, -10))
        time.sleep(0.3)
    t0 = time.time()
    while time.time() - t0 < 90 and not ("READY" in status().get("board", "") and "Into battle" in status().get("board", "")):
        time.sleep(1)
    time.sleep(1.5)
    log("  board: " + status().get("board", "")[:100])
    shots(8, "campfire", [("ahead", 0, 0), ("left", 35, -5), ("right", -35, -5)])
    C.send("gate auto 1.5", 5)
    t0 = time.time()
    while time.time() - t0 < 90 and "Level_Play" not in C.send("state?", 3):
        time.sleep(1)
    import indicator_shots
    log("  leave the fight: " + str(indicator_shots.leave()))
    time.sleep(15)
    for _ in range(6):
        if "GameState_Map" in C.send("state?", 3):
            break
        pt17.sim("btn r a 150")
        time.sleep(3)
    C.send("scen run Table/End coins", 10)
    t0 = time.time()
    k = 0
    while time.time() - t0 < 14 and k < 8:
        r = C.send("headlook TokenNode from 0 1.66 0", 4)
        if r.startswith("head"):
            pt17.sim(r)
            time.sleep(0.25)
            log("  " + pt17.shot("walk_09_end_coins_%d" % k) + " | " + r)
            k += 1
        else:
            time.sleep(0.3)
    if k == 0:
        log("  end coins: no token in sight in 14 s (state %s)" % C.send("state?", 3))
        for j in range(3):
            log("  " + _shot("walk_09_end_coins_none_%d" % j, 0, -10))
            time.sleep(1)
    log("cand8 walk done")


if __name__ == "__main__":
    main()
