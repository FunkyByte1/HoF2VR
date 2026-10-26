#!/usr/bin/env python3
"""Candidate 8's held items (env 1, 2026-10-17 evening), one session at the map: the dice hint and the roll status, the
story page without the arrow box, a catalog fight through @confirm, the end coins over 10 s. Shots -> captures/cand8/fix_*;
PASS / FAIL lines on stdout."""
import os
import re
import sys
import time

os.environ["PT17_OUT"] = "/home/bara/Projects/flat2vr/HandOfFate2/captures/cand8"
H = "/home/bara/Projects/flat2vr/HandOfFate2"
sys.path.insert(0, H + "/scripts")
sys.path.insert(0, H + "/scripts/chains")
import cmd as C  # noqa: E402
import pt17  # noqa: E402
import interact_test as T  # noqa: E402
from pt17_tests2 import st, gamelog  # noqa: E402

RES = []


def result(name, ok, ev):
    RES.append(ok)
    print("%s %s %s - %s" % (time.strftime("%H:%M:%S"), "PASS" if ok else "FAIL", name, ev[:300]), flush=True)


def look_at(part, dpitch=0.0):
    r = C.send("headlook %s from 0 1.66 0" % part, 5)
    if r.startswith("head"):
        f = r.split()
        pt17.look(float(f[4]), float(f[5]) + dpitch)
        time.sleep(0.6)
        return True
    return False


def shot(name):
    print("  " + pt17.shot(name), flush=True)


def scen(label, timeout=90):
    C.send("scen run " + label, 10)
    t0 = time.time()
    while time.time() - t0 < timeout:
        time.sleep(1.5)
        sc = st().get("scenarios", "")
        if "(" + label.split("/")[-1] + ": done)" in sc or "stopped" in sc:
            return sc
    return st().get("scenarios", "")


def main():
    pt17.sim(pt17.REST)
    # 1. the dice: no panel in front of the Dealer; after 4 s untaken a hint over the dice; the roll status in view
    T.ensure_map()
    T.rest_hands()
    ok = T.start_mg("dice 99 0 0", T.dice_ready)
    time.sleep(1.5)
    look_at("PlayerDiceRoller", -5)
    shot("fix_dice_waiting_1s")
    help_up = "Dice" in st().get("help", "") or "DICE" in st().get("help", "")
    time.sleep(4.0)
    shot("fix_dice_waiting_5s_hint")
    hint = [l for l in gamelog().splitlines() if "the hint over the dice" in l]
    result("dice_hint", ok and bool(hint) and not help_up, "dice waited %s; hint lines %d; help card %s" % (ok, len(hint), help_up))
    print("  roll status: " + C.send("uiwhere RollStatus 12", 8)[:200], flush=True)
    look_at("Dealer_Caravan", 0)
    shot("fix_dice_dealer_view")
    T.send("dice throw 0 0.3 1.8 r game")
    time.sleep(5)
    look_at("PlayerDiceRoller", -5)
    shot("fix_dice_marking_view")
    T.finish_task()
    # 2. a story page: no box with an arrow, at 1, 3 and 6 s
    T.ensure_map()
    C.send("scen run Table/Dialogue", 10)
    t0 = time.time()
    while time.time() - t0 < 60 and "Encounter" not in st().get("menu", ""):
        time.sleep(0.5)
    boxes = []
    for k, wait in enumerate((1.0, 2.0, 3.0)):
        time.sleep(wait)
        b = st().get("boards", "")
        boxes.append(re.findall(r"\[(\d+) ([^\]]*)\]", b))
        look_at("Dealer_Caravan", -10)
        shot("fix_story_%d" % k)
    arrows = [p for bl in boxes for p in bl if not p[1].startswith("nav:") and (p[1].strip() in ("", "1") or "PageOption" in p[1])]
    result("story_no_arrow_box", not arrows, "plates over time: %s" % boxes)
    for _ in range(4):
        C.send("press A", 5)
        time.sleep(1.5)
    # 3. a catalog fight through @confirm
    sc = scen("Fights/Bandits", 120)
    t0 = time.time()
    while time.time() - t0 < 60 and "Level_Play" not in C.send("state?", 3):
        time.sleep(1)
    s = C.send("state?", 3)
    result("scenario_fight_confirm", "Level_Play" in s, "%s | %s" % (s, sc[:120]))
    print("  left: " + str(__import__("indicator_shots").leave()), flush=True)
    time.sleep(4)
    # 4. the end coins over 10 s
    T.ensure_map()
    n0 = gamelog().count("TokenBubble") + gamelog().count("tokens pushed")
    C.send("scen run Table/End coins", 10)
    for k in range(10):
        time.sleep(1.0)
        if k in (1, 3, 5, 7, 9):
            look_at("Dealer_Caravan", -25)
            shot("fix_end_coins_%ds" % (k + 1))
    print("  tableHud: " + st().get("tableHud", "")[:200], flush=True)
    toks = [l for l in gamelog().splitlines() if "wincoins" in l or "coin" in l.lower() and "end" in l.lower()]
    for l in toks[-4:]:
        print("  log: " + l[-200:], flush=True)
    print("%s probe8: %d/%d passed" % (time.strftime("%H:%M:%S"), sum(RES), len(RES)), flush=True)


if __name__ == "__main__":
    main()
