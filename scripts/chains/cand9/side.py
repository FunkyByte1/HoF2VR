#!/usr/bin/env python3
"""Candidate 9 (env 1): the side table's P2 checks again, in the order the screens need (the chain's B_mine ran P2-5's
swipe in the deck builder's SelectMode, where a press selects at once, and reached the challenge's opening late):
from the caravan (a magician session: GameState_Attract): A -> the challenge map; P2-8 a pin pressed from above (8 s after
arriving); A -> the deck builder (SelectMode); A -> Build; P2-5 the row's press-then-slide and press-and-lift; shots;
B, Y, A, X -> the challenge's opening; P2-2 figurine_real there (while its encounter is up, as at 00:58)."""
import os
import subprocess
import sys
import time

H = "/home/bara/Projects/flat2vr/HandOfFate2"
sys.path.insert(0, H + "/scripts")
import cmd as C  # noqa: E402
import p2fix as P  # noqa: E402
import pt17  # noqa: E402

pt17.OUT = os.environ.get("CAND9_PICS") or H + "/captures/cand9"
P.LOG = os.environ.get("SIDE_LOG") or H + "/logs/cand9/side.log"
log = P.log


def st():
    s = C.status()
    return s.get("state", ""), s.get("menu", "")


pt17.sim(pt17.REST)
log("side: %s / %s" % st())
for _ in range(3):
    if "ChallengeMap" in st()[1]:
        break
    C.send("press A 6", 5)
    P.wait_menu("ChallengeMap", 25)
time.sleep(8)
ok_pin = P.pin("C9_P2-8_pin")
log("  pin: %s; now %s / %s" % (ok_pin, *st()))
if "Deckbuilder" not in st()[1]:
    C.send("press A 6", 5)
    P.wait_menu("Deckbuilder", 15)
time.sleep(3)
log("  deck builder: %s / %s" % st())
# (pin() goes back to the map; A there opens the deck builder's SelectMode; touching the Encounter deck picks it: Build.
# A there would pick the deck the game highlights, the Companion deck: one card and no row to swipe, captures/cand10/fails_side.md 3)
if "SelectMode" in st()[1]:
    C.send("touch Modes/Encounter/DeckBuilderDeck r tap", 5)
    P.wait_menu("Build", 10)
time.sleep(3)
log("  after A: %s / %s | %s" % (*st(), C.status().get("deckrows", "")[:200]))
for name, yaw, pitch in (("ahead", 0, -45), ("down", 0, -65)):
    pt17.look(yaw, pitch)
    time.sleep(0.8)
    log("  " + pt17.shot("C9_side_build_%s" % name))
if "Build" in st()[1]:
    P.swipe_press("CardSetBrowser/Card_EN", "C9_P2-5")
else:
    log("RESULT C9_P2-5 FAIL: not in Build mode (%s)" % st()[1])
for b in ("B", "Y", "A", "X"):
    C.send("press %s 6" % b, 5)
    time.sleep(3)
    log("  %s -> %s / %s" % (b, *st()))
P.wait_menu("ChallengeSetup", 30)
t0 = time.time()
while time.time() - t0 < 20 and "Encounter" not in st()[1]:
    time.sleep(0.5)
log("  the opening: %s / %s | fig %s" % (*st(), C.send("fig where", 5)[:120]))
base = P.base_yaw()
for name, yaw, pitch in (("ahead", 0, -40), ("left", 35, -45)):
    P.pose(base, yaw, pitch)
    time.sleep(0.8)
    log("  " + pt17.shot("C9_side_opening_%s" % name))
r = subprocess.run(["python3", H + "/scripts/interact_test.py", "figurine_real"], capture_output=True, text=True)
for ln in (r.stdout or "").splitlines():
    if "PASS" in ln or "FAIL" in ln or "passed" in ln:
        log("  " + ln[:400])
log("side done")
