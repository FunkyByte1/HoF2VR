#!/usr/bin/env python3
"""Combat Phase 0 probes (docs/combat-uyf-design.md §8.7): strike timings and reach, swing sides, counterable attacks,
the game's glows and sounds, and which fights hold which enemy types. No gameplay change (test seams only).

  phase0_probes.py [--seconds 40] [--fights a,b,...] [--max N]

Needs a session (session.py start --snapshot first_fight --to table). For each fight (DevJump `jump fight <name>` from
the dungeon map): the waiting space's gate (A), then god mode, the off-view warning off (the game's own timings),
`strikelog on`, `fxlog on`, --seconds of the enemies attacking a passive player, `enemysurvey <fight>`, `jump win`, the
exit portal (`exitcard touch`), back to the map. At the end `strikelog dump` and `fxlog dump`:
testdata/probes/strikes.tsv, atlas.tsv, effects.txt, enemies.tsv. Progress in logs/phase0_probes.log.
"""
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cmd  # noqa: E402

import hof2env  # noqa: E402

H = hof2env.H
LOG = H + "/logs/phase0_probes.log"
args = sys.argv[1:]
SECONDS = float(args[args.index("--seconds") + 1]) if "--seconds" in args else 40.0
MAX = int(args[args.index("--max") + 1]) if "--max" in args else 99
DEFAULT = ("EN_00_Fool_Ambush,EL_Ambush_Corrupt,EL_Ambush_Empire,EL_Ambush_Goblins,EL_Ambush_Mage,EL_Ambush_Ogre,"
           "EL_Ambush_Thral,EL_Ambush_Treants,EL_Ambush_Undead,EN_05_Goblins,EN_09_MageHunting,EN_WraithEncounter,"
           "EN_NecromancerEncounter,EN_16_BandOfThugs,EN_13_Death_Ambush,EN_11_Justice_battle,EN_Gun_01_Intro,"
           "EN_18_CityStreets,WarringKingdoms_RedAmbush,EN_ThiefAmbush")
FIGHTS = (args[args.index("--fights") + 1] if "--fights" in args else DEFAULT).split(",")


def log(msg):
    line = time.strftime("%H:%M:%S ") + msg
    print(line, flush=True)
    with open(LOG, "a") as f:
        f.write(line + "\n")


def send(c, t=10):
    try:
        return cmd.send(c, t)
    except SystemExit as e:
        return "ERR " + str(e)


def state():
    return send("state?", 6)


def sim(c):
    subprocess.run(["python3", H + "/scripts/xrsim.py", c], capture_output=True, text=True)


def wait(pred, timeout, step=0.5):
    t0 = time.time()
    while time.time() - t0 < timeout:
        v = pred()
        if v:
            return v
        time.sleep(step)
    return None


def to_map(timeout=240):
    """Press on (A) until the dungeon map; autoplay for anything longer."""
    if "GameState_Map" in state():
        return True
    t0 = time.time()
    while time.time() - t0 < 60:
        s = state()
        if "GameState_Map" in s:
            return True
        send("press A 6")
        time.sleep(2.5)
    auto = subprocess.Popen(["python3", H + "/scripts/autoplay.py", "4"], stdout=open(H + "/logs/phase0-autoplay.out", "w"), stderr=subprocess.STDOUT)
    try:
        return wait(lambda: "GameState_Map" in state(), timeout, 1.0) is not None
    finally:
        auto.terminate()
        auto.wait(10)


def into_fight():
    # the encounter's text and choices come first (as on the map): pick the first choice, or press on
    t0 = time.time()
    while time.time() - t0 < 60:
        s = state()
        if "Level_In" in s or "Level_Play" in s:
            break
        if not send("boards click 1").startswith("clicking"):
            send("press A 6")
        time.sleep(3)
    if not wait(lambda: any(k in state() for k in ("Level_In", "Level_Play")), 30, 0.5):
        return False
    sim("head 0 1.75 0 0 0 0; hand r 0.25 1.0 -0.25 0 -60 0; hand l -0.25 1.0 -0.25 0 -60 0")
    t0 = time.time()
    while time.time() - t0 < 90 and "Level_Play" not in state():
        g = send("gate", 4)
        try:
            menu = cmd.status().get("menu") or ""
        except Exception:
            menu = ""
        if g.startswith("gate waiting"):
            send("gate go")
            time.sleep(1.5)
        elif "Showcase" in menu:
            sim("btn r a 150")
            time.sleep(1.5)
        time.sleep(0.5)
    return "Level_Play" in state()


def out_of_fight():
    send("jump win")
    time.sleep(2.5)
    for _ in range(20):
        r = send("exitcard", 4)
        if r.startswith("pending") and "card (" in r:
            send("exitcard touch")
            break
        if "GameState_Map" in state():
            break
        time.sleep(0.5)
    ok = wait(lambda: "GameState_Map" in state(), 120, 1.0) is not None
    if not ok:
        # loot / reward screens: press on
        for _ in range(30):
            if "GameState_Map" in state():
                return True
            send("press A 6")
            time.sleep(2.0)
    return "GameState_Map" in state()


def main():
    log("=== phase0_probes: %d fights, %.0f s each" % (min(MAX, len(FIGHTS)), SECONDS))
    send("gate auto 0")
    send("strikelog on")
    send("fxlog on")
    done = 0
    snaps = (args[args.index("--snapshots") + 1].split(",") if "--snapshots" in args else [])
    per = int(args[args.index("--per") + 1]) if "--per" in args else 2
    plan = [(sn, k) for sn in snaps for k in range(per)] if snaps else [(None, k) for k in range(MAX)]
    cur = None
    for sn, k in plan[:MAX if not snaps else len(plan)]:
        if sn is not None and sn != cur:
            # DevJump's `jump fight` breaks Level_In from these saves (CardContainer.ReleaseCard out of range, then
            # MeshWormhole.InitTargets null): the fights come the game's own way, by autoplay from each snapshot
            r = subprocess.run(["python3", H + "/scripts/session.py", "start", "--snapshot", sn, "--to", "table"], capture_output=True, text=True, timeout=400)
            log("session %s: %s" % (sn, r.stdout.strip().split("\n")[-1][:100]))
            cur = sn
            send("gate auto 0")
            send("strikelog on")
            send("fxlog on")
        r = subprocess.run(["python3", H + "/scripts/combat_fixture.py", "--timeout", "420"], capture_output=True, text=True, timeout=600)
        name = (sn or "-") + "#" + str(k + 1)
        if "Level_Play" not in state():
            log("%s: no fight (%s)" % (name, state()))
            continue
        m = re.search(r"active=(\S+)", send("scenes"))
        name += ":" + (m.group(1) if m else "?")
        send("strikelog fight " + name)
        send("god")
        send("cfg OffViewWarning false")
        n0 = re.search(r"strikes (\d+)", send("strikelog"))
        t0 = time.time()
        # look round slowly, as a passive player would: enemies come from every side
        yaw = 0
        while time.time() - t0 < SECONDS and "Level_Play" in state():
            sim("head 0 1.75 0 %d 0 0" % yaw)
            yaw = (yaw + 45) % 360
            time.sleep(2.0)
        survey = send("enemysurvey " + name)
        send("strikelog dump", 20)   # after every fight: nothing is lost if a later one hangs
        send("fxlog dump", 20)
        pd = hof2env.TESTDATA + "/probes"
        for f in ("strikes.tsv", "effects.txt"):
            if os.path.exists(pd + "/" + f):
                base, ext = f.split(".")
                subprocess.run(["cp", pd + "/" + f, "%s/%s_%s.%s" % (pd, base, sn or "run", ext)])
        n1 = re.search(r"strikes (\d+)", send("strikelog"))
        log("  %s; strikes %s -> %s" % (survey, n0.group(1) if n0 else "?", n1.group(1) if n1 else "?"))
        if not out_of_fight():
            log("  not back on the map (%s)" % state())
        done += 1
    send("cfg OffViewWarning true")
    log("strikelog dump: " + send("strikelog dump", 20))
    log("fxlog dump: " + send("fxlog dump", 20))
    log("=== %d fights probed" % done)


if __name__ == "__main__":
    main()
