#!/usr/bin/env python3
"""Candidate 9 (env 1): the campfire before a fight with the head walk (walk.py's campfire step turned the test gate off
before the call to battle, so the fight card waited to be touched and the walk stood at the table). Here the gate touches
the card, is turned off as soon as the waiting space loads (GameState_Level_In), and the head walks once its board says
READY; then the gate takes the player in and the fight is left. From a session at the dungeon map."""
import os
import sys
import time

H = "/home/bara/Projects/flat2vr/HandOfFate2"
sys.path.insert(0, H + "/scripts")
sys.path.insert(0, H + "/scripts/chains")
sys.path.insert(0, H + "/scripts/chains/cand9")
import cmd as C  # noqa: E402
import headwalk as HW  # noqa: E402

log = HW.log


def status():
    try:
        return C.status()
    except Exception:
        return {}


C.send("gate auto 1.5", 5)
# (item 30, 2026-10-18: a card touched once its fight has loaded goes straight into the fight - the simulator loads within the
# card's glide; the fight counts as loaded only 8 s into its hold here, so the touch comes before: the campfire as before)
log("slow load: " + C.send("fightcard slowload 8", 5))
log("campfire: " + C.send("scen run Places/Campfire (fight)", 10))
t0 = time.time()
while time.time() - t0 < 150 and "Level_In" not in C.send("state?", 3):
    time.sleep(0.3)
log("  gate off at %s: %s" % (C.send("state?", 3)[:40], C.send("gate auto 0", 5)))
t0 = time.time()
while time.time() - t0 < 90 and "READY" not in status().get("board", ""):
    time.sleep(1)
time.sleep(1.5)
log("  board: " + status().get("board", "")[:120])
HW.walk("w12_campfire_into_battle", None, 0.0, 0.0)
HW.walk("w12_campfire_into_battle_down", None, 0.0, -30.0)
C.send("gate auto 1.5", 5)
C.send("fightcard slowload 0", 5)
t0 = time.time()
while time.time() - t0 < 90 and "Level_Play" not in C.send("state?", 3):
    time.sleep(1)
import indicator_shots  # noqa: E402
log("  leave the fight: " + str(indicator_shots.leave()))
