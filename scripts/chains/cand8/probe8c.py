#!/usr/bin/env python3
"""Candidate 8: the end coins - where they are (env 1). From the map: Table/End coins, then both-eye shots in four
directions from the table's line at 2 s and 5 s, and the token bubble's count. Shots -> captures/cand8/fix3_coins_*."""
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


def main():
    pt17.sim(pt17.REST)
    T.ensure_map()
    r = C.send("headlook dealer_caravan_cardtable/Table from 0 1.66 0", 5)
    f = r.split()
    base_yaw, base_pitch = float(f[4]), float(f[5])
    print("table line: yaw %.1f pitch %.1f" % (base_yaw, base_pitch), flush=True)
    t0 = st().get("tableHud", "")
    C.send("scen run Table/End coins", 10)
    t_start = time.time()
    for when in (2.0, 5.0):
        while time.time() - t_start < when:
            time.sleep(0.1)
        for name, dy, dp in (("ahead", 0, 15), ("up", 0, 45), ("left", 60, 15), ("right", -60, 15)):
            pt17.look(base_yaw + dy, base_pitch + dp)
            time.sleep(0.35)
            print(pt17.shot("fix3_coins_%ds_%s" % (int(when), name)), flush=True)
    print("tableHud before: " + t0[-40:] + " | after: " + st().get("tableHud", "")[-40:], flush=True)
    print("state: " + C.send("state?", 3) + " | menu " + st().get("menu", ""), flush=True)


if __name__ == "__main__":
    main()
