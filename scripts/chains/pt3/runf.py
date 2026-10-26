#!/usr/bin/env python3
"""Playtest 3 (env 1), run F: the challenge map's symbols (item 2: the row and the banner as two crops) - pictures f_*."""
import os
import sys
import time

H = "/home/bara/Projects/flat2vr/HandOfFate2"
sys.path.insert(0, H + "/scripts")
sys.path.insert(0, H + "/scripts/chains/pt3")
import runb  # noqa: E402
runb.pt17.OUT = H + "/captures/pt3"
C, P, log, shot, st = runb.C, runb.P, runb.log, runb.shot, runb.st

for _ in range(3):
    time.sleep(2)
    C.send("press A 6", 5)
    if P.wait_menu("ChallengeMap", 25):
        break
time.sleep(4)
log("at %s / %s" % (st().get("state"), st().get("menu")))
log("markers: " + C.send("markers", 5))
shot("f_map_rest", 0, -30)
shot("f_map_up", 0, -12)
# the chosen challenge's banner: hover the pin by the laser-free way (select it, then back)
C.send("press A 6", 5)
time.sleep(4)
C.send("press B 6", 5)
time.sleep(4)
log("markers: " + C.send("markers", 5))
shot("f_map_after", 0, -20)
