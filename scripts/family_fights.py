#!/usr/bin/env python3
"""One full fight per enemy family in Physical (UYF) combat, by scripted real swings (combat Stage 2 checkpoint).

  family_fights.py [--families a,b,...] [--boot] [--limit 240] [--tag FF]

For each family: the fixture fight (combat_fixture.py --jump EL_Ambush_Thral --with SPEC), god mode, the enemies mortal.
Then, until no enemy is left or --limit seconds pass: the nearest enemy - stand 1.3 m in front of it (`heroat`), face it,
a big real swing through it (xrsim) while its guard is up (a guard hit); once it is broken, a cut along each slash that
opens (`poise?` gives its line). Per family: kills, guard breaks, crits landed and wrong, guard hits, deflects, the time,
and whether the fight ended. Results in logs/family_fights.log; exit 1 when a fight did not end.
"""
import math
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cmd  # noqa: E402
import hof2env  # noqa: E402

H = hof2env.H
LOG = H + "/logs/family_fights.log"
args = sys.argv[1:]
TAG = args[args.index("--tag") + 1] if "--tag" in args else "FF"
LIMIT = float(args[args.index("--limit") + 1]) if "--limit" in args else 240
FAMILIES = {
    "thieves": "ThievesThugREF*2,ThievesAssassinREF*1,ThievesPoisonREF*1",
    "empire": "EmpireSoldierREF*1,EmpireGuardREF*1,EmpirePhalanxREF*1",
    "norse": "NorseWarriorREF*1,NorseBerserkerREF*1,NorseTankREF*1",
    "corrupted": "CorruptedMinionREF*2,CorruptedTankREF*1",
    "shadow": "ShadowWarriorREF*2,ShadowElderREF*1",
    "skeletons": "SkeletonNEWREF*2,SkeletonKnightREF*1",
    "goblins": "GoblinWarriorREF*2,GoblinArcherREF*1",
    "ogres": "OgreGenericREF*1",
    "treants": "TreantNEWREF*1,TreantKnightGreenREF*1",
    "wraiths": "WraithREF*2",
    "villagers": "VillagerMeleeREF*2,VillagerEnemyMeleeREF*1",
    "boss_knight": "SpecialBlackKnight_BossREF*1",
}


def log(msg):
    line = time.strftime("%H:%M:%S ") + msg
    print(line, flush=True)
    with open(LOG, "a") as f:
        f.write(line + "\n")


def send(c, t=8):
    try:
        return cmd.send(c, t)
    except SystemExit as e:
        return "ERR " + str(e)


def sim(c):
    subprocess.run(["python3", H + "/scripts/xrsim.py", c], capture_output=True, text=True)


def user_in_vr():
    try:
        return '"user": true' in open("/run/user/1000/vr-session/status.json").read()
    except OSError:
        return True


def counts():
    s = send("pcombat?")
    d = {}
    for k, v in re.findall(r"([A-Za-z]+) (-?[\d.]+)", s):
        if k not in d:
            try:
                d[k] = float(v)
            except ValueError:
                pass
    return d


def alive():
    """The fight's own count (`fight?`: alive N); -1 when it can't be read (not 0: a missing enemy in range is no win -
    CPZ9's first version took `enemy? nearest` = none as the end and "won" the thieves in 6 s)."""
    m = re.search(r"alive (\d+)", send("fight?"))
    return int(m.group(1)) if m else -1


def poise(who):
    p = send("poise? " + who)
    d = {"raw": p}
    m = re.search(r"phase (\w+)", p)
    d["phase"] = m.group(1) if m else "?"
    m = re.search(r"axisT (-?[\d.]+),(-?[\d.]+),(-?[\d.]+)", p)
    if m:
        d["axis"] = [float(m.group(i)) for i in (1, 2, 3)]
    m = re.search(r"at \((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\)", p)
    if m:
        d["at"] = [float(m.group(i)) for i in (1, 2, 3)]
    m = re.search(r"id (#-?\d+)", p)
    if m:
        d["id"] = m.group(1)
    return d


def cut(who, axis, at):
    """A cut along `axis` (tracking) through the enemy's chest, from 1.8 m of its centre."""
    send("heroat %s 0 1.8" % who)
    send("face " + who)
    time.sleep(0.15)
    e = send("enemy? " + who)
    m = re.search(r"dist ([\d.]+).* head \((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\)", e)
    dist = float(m.group(1)) if m else 1.8
    p = poise(who)
    ax = p.get("axis", axis)
    chest = (p["at"][1] - float(m.group(3))) if (m and "at" in p) else -0.35
    n = math.sqrt(sum(c * c for c in ax)) or 1.0
    ax = [c / n for c in ax]
    cx, cy, cz = 0.0, 1.7 + chest, -max(0.25, dist - 0.55)
    sim("head 0 1.7 0 0 0 0; hand r %.3f %.3f %.3f 0 0 0" % (cx - ax[0] * 0.9, cy - ax[1] * 0.9, cz + ax[2] * 0.9))
    time.sleep(0.3)
    sim("move r %.3f %.3f %.3f 180" % (ax[0] * 1.8, ax[1] * 1.8, -ax[2] * 1.8))
    time.sleep(0.35)
    sim("hand r 0.25 1.35 -0.35 0 5 0")


def swing(who):
    send("heroat %s 0 1.3" % who)
    send("face " + who)
    sim("head 0 1.7 0 0 0 0; hand r 0.8 1.4 -0.3 0 5 0")
    time.sleep(0.3)
    sim("move r -1.6 -0.2 -0.15 170")
    time.sleep(0.35)
    sim("hand r 0.25 1.35 -0.35 0 5 0")


def leave():
    """End the fight, walk out (hold A at the exit portal), press A through the encounter card and loot until the map's own
    menu is up - never into a new encounter's campfire (Level_In): twice a press at the map began the next encounter."""
    import indicator_shots
    return indicator_shots.leave()


def fight(name, spec, boot):
    how = ["--jump", "EL_Ambush_Thral", "--with", spec]
    r = subprocess.run(["python3", H + "/scripts/combat_fixture.py"] + how + (["--boot"] if boot else []) + ["--god", "--physical", "--equip", "BasicShield"],
                       capture_output=True, text=True)
    st = send("state?")
    if "Level_Play" not in st:
        log("%s: no fight (%s | %s)" % (name, st[:60], (r.stdout or "").strip()[-120:]))
        return None
    send("god on")
    send("tough off")
    send("vrcombat physical")
    send("pcombat? reset")
    log("%s %s: table %s" % (TAG, name, send("enemytable? all")[:400]))
    n0 = alive()
    t0 = time.time()
    swings = 0
    only_empty = False
    while time.time() - t0 < LIMIT:
        if user_in_vr():
            log("the user is in VR: stopping")
            return None
        if alive() == 0:
            break
        # an enemy with its guard broken and a slash open first (CPZ9: swinging at the nearest let 4 of 8 slashes run out
        # while another thief was nearer), else the nearest one standing with its guard up
        b = poise("broken")
        if b.get("phase") == "Broken" and "axis" in b:
            cut(b.get("id", "broken"), b["axis"], b.get("at", [0, 1.3, 0]))
            swings += 1
            continue
        p = poise("nearest")
        if p.get("raw", "").startswith("no enemy"):
            time.sleep(0.5)   # none near: they come
            continue
        if p.get("raw", "").startswith("Empty "):
            # an invisible "Empty" target on the enemy team (the ambush's own helper, class Basic): the real enemies are down
            log("%s %s: only the ambush's 'Empty' target left" % (TAG, name))
            only_empty = True
            break
        who = p.get("id", "nearest")
        if p.get("phase") in ("Broken", "Recover"):
            time.sleep(0.2)
        else:
            swing(who)
        swings += 1
    took = time.time() - t0
    c = counts()
    ended = alive() == 0 or only_empty
    log("%s %s: %s in %.0f s, %d enemies, %d swings | guard hits %d, breaks %d, crits %d (wrong %d, timed out %d), combos %d, deflects %d, kills %s, hits taken %d, unguarded %d" % (
        TAG, name, "ENDED" if ended else "NOT ENDED", took, n0, swings, c.get("guardHits", 0), c.get("breaks", 0), c.get("critsLanded", 0), c.get("critsWrong", 0),
        c.get("critsTimedOut", 0), c.get("combosDone", 0), c.get("deflects", 0), "all" if ended else "-", c.get("hitsTaken", 0), c.get("unguarded", 0)))
    leave()
    return ended


def main():
    names = args[args.index("--families") + 1].split(",") if "--families" in args else list(FAMILIES)
    boot = "--boot" in args or "GameState_Map" not in send("state?")
    results = {}
    for name in names:
        r = fight(name, FAMILIES[name], boot)
        boot = "GameState_Map" not in send("state?")
        results[name] = r
    log("%s: %s" % (TAG, ", ".join("%s %s" % (k, "ok" if v else ("no fight" if v is None else "NOT ENDED")) for k, v in results.items())))
    return 0 if all(v for v in results.values()) else 1


if __name__ == "__main__":
    sys.exit(main())
