#!/usr/bin/env python3
"""Playtest 3 (env 1): the call to battle at the table, pictured (both eyes -> captures/pt3/<tag>_*.jpg): the deal (items 28, 25:
cards flying from the Dealer's deck and gathering), the card held out with its loading bar (item 14), the moment it says ready.
  reveal.py TAG [scenario]   (from the dungeon map; default Fights/Bandits; leaves the waiting space up: `gate go` goes on)
"""
import os
import sys
import time

H = "/home/bara/Projects/flat2vr/HandOfFate2"
sys.path.insert(0, H + "/scripts")
os.environ.setdefault("PT17_OUT", H + "/captures/pt3")
import cmd as C  # noqa: E402
import pt17  # noqa: E402


def st():
    try:
        return C.status()
    except Exception:
        return {}


def shot(name, freeze=True):
    if freeze:
        C.send("timescale 0", 5)
    time.sleep(0.4)
    r = pt17.shot(name).split(" -> ")[-1]
    if freeze:
        C.send("timescale 1", 5)
    print("  " + r, flush=True)


def main(a):
    tag = a[0]
    scen = a[1] if len(a) > 1 else "Fights/Bandits"
    C.send("gate auto 0", 5)
    pt17.sim(pt17.REST)
    pt17.look(0, -25)
    t0 = time.time()
    if scen == "jump":
        # b1_test's way: the dev jump into a goblin ambush from the dungeon map, its page answered at the table
        sys.path.insert(0, H + "/scripts")
        import b1_test as B
        B.into_level_in()
        tin = time.time() - 0.7
    else:
        print("scenario: " + C.send("scen run " + scen, 10), flush=True)
        while time.time() - t0 < 120 and st().get("state") != "GameState_Level_In":
            time.sleep(0.2)
        tin = time.time()
    print("Level_In after %.1f s" % (tin - t0), flush=True)
    for at in (0.8, 1.6, 2.4):
        while time.time() - tin < at:
            time.sleep(0.05)
        shot("%s_deal_%02d" % (tag, int(at * 10)))
    t1 = time.time()
    while time.time() - t1 < 15 and "offered" not in st().get("fightCard", ""):
        time.sleep(0.1)
    print("offered: " + st().get("fightCard", "")[:160], flush=True)
    time.sleep(0.9)
    shot("%s_offered" % tag)
    pt17.look(0, -40)
    time.sleep(0.5)
    shot("%s_offered_down" % tag)
    t2 = time.time()
    while time.time() - t2 < 12 and "loaded" not in st().get("fightCard", ""):
        time.sleep(0.2)
    shot("%s_ready" % tag, freeze=False)
    print("fight card: " + st().get("fightCard", "")[:200], flush=True)


if __name__ == "__main__":
    main(sys.argv[1:])
