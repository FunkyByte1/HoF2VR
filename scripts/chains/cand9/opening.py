#!/usr/bin/env python3
"""Candidate 9 (env 1): after p2fix.py hubwalk, to the challenge's opening at the table (GameState_ChallengeSetup, the hero
on the table) for figurine_real there: the inventory closed if hubwalk left it open; if hubwalk did not get there, from the
deck builder B, Y, A, X (NOTES "Playtest 2 fixes (env 1)": that sequence reached it by hand at 00:58)."""
import sys
import time

H = "/home/bara/Projects/flat2vr/HandOfFate2"
sys.path.insert(0, H + "/scripts")
import cmd as C  # noqa: E402


def st():
    try:
        s = C.status()
        return s.get("state", ""), s.get("menu", "")
    except Exception:
        return "", ""


def say(m):
    print(time.strftime("%H:%M:%S ") + m, flush=True)


def wait(part, timeout):
    t0 = time.time()
    while time.time() - t0 < timeout:
        s, m = st()
        if part in s or part in m:
            return True
        time.sleep(1.0)
    return False


s, m = st()
say("start: %s / %s" % (s, m))
if "ChallengeSetup" not in s:
    if "ChallengeMap" in m:
        C.send("press A 6", 5)
        wait("Deckbuilder", 15)
        time.sleep(2)
    for b in ("B", "Y", "A", "X"):
        C.send("press %s 6" % b, 5)
        time.sleep(3)
        say("  %s -> %s / %s" % (b, *st()))
    wait("ChallengeSetup", 40)
    time.sleep(6)
s, m = st()
if "ChallengeSetup" in s and "Inventory" in m:
    C.send("press B 6", 5)
    time.sleep(2.5)
s, m = st()
say("the challenge's opening: %s / %s | fig %s" % (s, m, C.send("fig where", 5)[:120] if "ChallengeSetup" in s else "-"))
