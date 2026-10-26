#!/usr/bin/env python3
"""Env 1, 2026-10-19: where the stat cards go at a seated call to battle (OffscreenStatHand), and the map's near row as the
watchdog measures it. Exploration only: transforms, KeepOut's trace, both-eye pictures looking down / down-left.
  HOF2VR_SEATED=1 scripts/chains/pt3/run.sh sx1 magician_mid scripts/chains/pt3/statx.py [--loss]"""
import os
import sys
import time

H = "/home/bara/Projects/flat2vr/HandOfFate2"
sys.path.insert(0, H + "/scripts")
import b1_test as B  # noqa: E402
import playtest as pt  # noqa: E402

pt.LOG = H + "/logs/pt3/statx.log"
send, sim = pt.send, pt.sim


def log(m):
    pt.log(m)


def look(yaw, pitch, name):
    sim("head 0 1.75 0 %g %g 0" % (yaw, pitch))
    time.sleep(0.6)
    send("shot PT_SX_%s" % name)
    time.sleep(1.8)


def main():
    pt.SEATED[0] = True
    sim("seat 0.55")
    sim(pt.REST + "; head 0 1.75 0 0 0 0")   # (seated eyes 1.20 m, as b1's failing run: the neutral = the live head)
    time.sleep(0.5)
    log("posture: " + send("posture seated"))
    time.sleep(2)
    send("cfg Scenarios true")
    pt.sim(pt.REST + "; head 0 1.75 0 0 0 0")
    log("tablehud: " + send("tablehud"))
    for k in ("StatHand", "CompanionHand", "Scalar/BottomLeft"):
        log("tf %s: %s" % (k, send("tf %s 40" % k)))
    log("bounds: " + send("bounds Card_ST_ 20"))
    log("table collider at the map: " + send("tf Table/Collider 5"))
    send("prox reset")
    send("kotrace Card_ST 1400")
    n0 = pt.nlines()
    ok = B.into_level_in()
    log("level_in: %s %s" % (ok, pt.state()))
    log("table collider in Level_In: " + send("tf Table/Collider 5"))
    log("scenes: " + send("scenes")[:300])
    for i in range(8):
        time.sleep(0.5)
        if i in (2, 7):
            send("tf Table/Collider 5")
        if i in (2, 5, 7):
            send("tf OffscreenStatHand 10")
            send("bounds OffscreenStatHand 10")
    look(0, -25, "down25")
    look(0, -45, "down45")
    look(35, -40, "downleft35")
    look(-35, -40, "downright35")
    sim("head 0 1.75 0 0 0 0")
    send("prox list")
    log("prox: " + send("prox"))
    lines, _ = pt.gamelog_since(n0)
    for l in lines:
        if "PROXIMITY" in l:
            log("  " + l.split("] ", 1)[-1][:300])
    # into the fight and back
    for _ in range(20):
        if B.fc().startswith("offered"):
            break
        time.sleep(0.5)
    time.sleep(1.5)
    pt.poke_pose("fightcard pose 6 r", settle=0.5)
    B.wait_level_play(False, "sx")
    log("fight: " + pt.state())
    log("back: %s" % B.win_and_back())
    sim("head 0 1.75 0 0 0 0")
    for k in ("StatHand",):
        log("tf %s after: %s" % (k, send("tf %s 40" % k)))
    if "--loss" in sys.argv:
        send("prox reset")
        send("kotrace Card_EN 600")
        sim("head 0 1.75 0 0 -40 0")
        n1 = pt.nlines()
        log("reload: " + send("scen reload mid", 10))
        for _ in range(40):
            time.sleep(1)
            if "GameState_Map" in pt.state() and pt.map_ready():
                break
        lines, _ = pt.gamelog_since(n1)
        for l in lines:
            if "PROXIMITY" in l:
                log("  " + l.split("] ", 1)[-1][:300])
        look(0, -40, "map_after_reload")
        send("prox list")
    sim("head 0 1.75 0 0 0 0; seat 0")


if __name__ == "__main__":
    main()
