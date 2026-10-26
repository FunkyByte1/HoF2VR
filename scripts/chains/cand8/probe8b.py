#!/usr/bin/env python3
"""Candidate 8's held items, second look (env 1): the roll status in view while looking at the dice, and the end coins
(looking from the map's line up toward the Dealer) over 10 s. Shots -> captures/cand8/fix2_*."""
import os
import sys
import time

os.environ["PT17_OUT"] = "/home/bara/Projects/flat2vr/HandOfFate2/captures/cand8"
H = "/home/bara/Projects/flat2vr/HandOfFate2"
sys.path.insert(0, H + "/scripts")
sys.path.insert(0, H + "/scripts/chains")
import cmd as C  # noqa: E402
import pt17  # noqa: E402
import interact_test as T  # noqa: E402
from pt17_tests2 import st  # noqa: E402


def look_map(dpitch, dyaw=0.0):
    r = C.send("headlook Scale/Map/MapLayout from 0 1.66 0", 5)
    f = r.split()
    pt17.look(float(f[4]) + dyaw, float(f[5]) + dpitch)
    time.sleep(0.6)


def main():
    pt17.sim(pt17.REST)
    T.ensure_map()
    T.start_mg("dice 99 0 0", T.dice_ready)
    time.sleep(1.0)
    r = C.send("headlook PlayerDiceRoller from 0 1.66 0", 5)
    if r.startswith("head"):
        pt17.sim(r)
        time.sleep(0.6)
    print(pt17.shot("fix2_dice_looking_at_dice"), flush=True)
    look_map(0)
    print(pt17.shot("fix2_dice_looking_at_map"), flush=True)
    T.send("dice throw 0 0.3 1.8 r game")
    time.sleep(5)
    r = C.send("headlook PlayerDiceRoller from 0 1.66 0", 5)
    if r.startswith("head"):
        pt17.sim(r)
        time.sleep(0.6)
    print(pt17.shot("fix2_dice_marking_looking_at_dice"), flush=True)
    T.finish_task()
    T.ensure_map()
    t0 = st().get("tableHud", "")
    C.send("scen run Table/End coins", 10)
    for k in range(10):
        time.sleep(1.0)
        if k in (1, 3, 5, 7, 9):
            look_map(25)
            print(pt17.shot("fix2_end_coins_%ds" % (k + 1)), flush=True)
    print("tableHud before: " + t0[-60:] + " | after: " + st().get("tableHud", "")[-60:], flush=True)
    print("state: " + C.send("state?", 3) + " | menu " + st().get("menu", ""), flush=True)


if __name__ == "__main__":
    main()
