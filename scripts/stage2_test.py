#!/usr/bin/env python3
"""Combat Stage 2 checks (docs/combat-uyf-design.md §9.2; docs/combat-progress.md): the rest of Until You Fall.

  stage2_test.py [--parts a,b,...] [--fixture NAME] [--no-fixture] [--tag S2T] [--captures] [--stay]

In a fixture fight (Physical mode, god mode), through the simulated headset (xrsim). Parts:
 Item 3, swing grading per weapon (WeaponTable):
  weapons     the UYF weapon each HoF2 weapon class maps to (`vrweapon`, `weapon?`): UYF's numbers
  legal       swings through the air at set speeds: the arming sword (4 m/s over 0.175 s) rejects 3.0 m/s and accepts
              6 m/s; the heavy hammer (3 m/s) accepts 4.5 m/s; daggers (4.5 m/s over 0.1 s) reject 3.5 m/s
  waggle      a 6 Hz waggle for 1 s with each weapon: no legal swing
  stab        the blade pointing forward, a thrust along it (2.5 m/s): a legal stab (UYF's stab energy); the same move
              square to the blade: no stab
  quality     real swings through an attacker: a long fast swing is Big, a short chop from close by is not
 Item 1, the enemy guard, guard break and crit combo (Poise) and item 7, the guard bar:
  chip        a real swing at an attacker: its guard drops by the sword's 7 x quality (Husk: x1); no health lost
              (Damage = Crits); a white flash for Standard/Big, the guard-hit shake
  blockchip   the shield on a blow's bar: blocked, and the attacker's guard drops by 7 x 1.5 (Husk)
  break       the guard brought to 0: GUARD BREAK - the enemy stunned and held (starts nothing), the guard-break crit opens
  crit        a cut along the open crit's line through the enemy: a crit (0.2 s hit-stop, pale yellow flash); the next crit
              opens 0.3 s later on another line
  wrong       a cut square to the crit's line: no crit (wrong way), the crit stays open
  combo       CritCombo 3: three cuts -> combo done; the enemy recovers, then its guard refills (phase Up again)
  timeout     a guard break left alone: the crit times out (7 s), the combo drops, the guard refills
  health      (tough off) Damage = Crits: a guard hit takes no health, a crit does; EveryHit: a guard hit does too
  bar         the meter's guard bar: shots of the guard full, chipped, broken (with the crit on the enemy)
  interrupt   (--fixture combo: an ogre) an interrupt crit between its combo's blows; a cut along it stops the combo
  interruptcut  an interrupt crit opened by the seam on a thief: a cut along it interrupts, a cut across it is a guard hit
 UYF's blocking (2026-10-13 night; the shield fixture): a shield goes up when its carrier was hit more often in the last
 3 s than its archetype's Block_AggressionThreshold (a soldier = Knight 2.25: the third hit), the captains also 0.5 s after
 their own attack; each block's super armor is a pool the hits wear down; a block that lasts costs the turn:
  aggression  two quick hits on a soldier with its shield down: still down; the third: up (`poise?` hits3 3/2.25)
  pool        a soldier blocking: big hits from the front deflected until its pool (Knight 60: 8.4 a hit) runs out - the
              eighth breaks through (a guard break, the slash open)
  postattack  45 s with the player passive: the soldiers (knights) never raise a shield after their own attack (UYF: only
              the captains do)
 Item 2, enemies that block (--fixture shield: two soldiers with ActionBlock, a phalanx):
  deflect     a swing at the phalanx's raised shield from the front: deflected (no chip, no damage, the blade kicked); from
              behind it: the flank rule, a guard hit
  blocktiming the soldiers' blocks under UYF's timing (2-5 s, 2 s apart)
 Item 4, reach sectors:
  sector      the head 1.6 m to the side or 1.4 m back just before the impact: out of reach; standing still: hit
 Item 5, turn-taking tickets: tickets (40 s: handed out by UYF's ranking, the ticket wait, one live strike)
 Item 6, feedback: knockhit (a blow let through: 1.6 m knockback, the hit shake), magnet (a hit from 1.5 m pulls to 1.0 m),
  rangedhit (--fixture archer: a shot: 3 m knockback; no red flash by default, a red one with HitTakenFlash on)
 Item 9, the enemy table: table (--fixture big: each family's UYF archetype, guard and lead)
 Shots UYF's way: archers (--fixture archer: shots on the global ranged cooldown at the difficulty, not held by a live melee
  strike; a shooter outside the view announced first)
 Item 8, the super meter: super (+0.5 a guard hit, +1 a block; the weapon ability only when full, emptied by its use;
  shots of the glove)
Results: PASS/FAIL lines on stdout and in logs/stage2_test.log; exit 1 on any FAIL.
"""
import math
import os
import json
import re
import shutil
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cmd  # noqa: E402
import hof2env  # noqa: E402

H = hof2env.H
LOG = H + "/logs/stage2_test.log"
CAPTURES = "/home/bara/Projects/flat2vr/HandOfFate2/captures"
args = sys.argv[1:]
TAG = args[args.index("--tag") + 1] if "--tag" in args else "S2T"
results = []
HEAD = "head 0 1.7 0 0 0 0"
HANDS_DOWN = "hand r 0.32 0.85 0.05 0 -80 0; hand l -0.32 0.85 0.05 0 -80 0"
HAND_R = "hand r 0.25 1.35 -0.35 0 5 0"
HAND_L = "hand l -0.25 1.2 -0.3 0 0 0"
OUTCOMES = ["blocks", "counters", "hitsTaken", "dodges", "outOfRange"]


def log(msg):
    line = time.strftime("%H:%M:%S ") + msg
    print(line, flush=True)
    with open(LOG, "a") as f:
        f.write(line + "\n")


def result(name, ok, detail):
    results.append((name, ok))
    log(("PASS " if ok else "FAIL ") + name + ": " + detail)


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
    d["_raw"] = s
    return d


def last_note(c):
    m = re.search(r" last ([^|]*)\|", c["_raw"])
    return m.group(1).strip() if m else "?"


def vec(s):
    return [float(x) for x in s.strip("()").split(",")]


def enemy(who="nearest"):
    e = send("enemy? " + who)
    m = re.search(r"dist ([\d.]+) bearing (-?\d+) hp ([\d.]+)", e)
    if not m:
        return None
    return {"dist": float(m.group(1)), "bearing": int(m.group(2)), "hp": float(m.group(3)), "raw": e}


def strike(timeout=20.0, fresh=0.0, colours=("White", "Gold", "Red")):
    """A live strike in view with at least `fresh` s to its impact: dict or None."""
    t0 = time.time()
    while time.time() - t0 < timeout:
        if user_in_vr():
            return None
        s = send("strike?", 4)
        if s and s != "none" and not s.startswith(("ERR", "no ")):
            first = s.split(" | ")[0]
            if "offview" in first:
                send("face striker")
                time.sleep(0.05)
                continue
            m = re.search(r"(\w+) (Gold|White|Red) eta (-?[\d.]+) valid (\w+) bar (\([^)]*\)) axis (\([^)]*\))", first)
            if m and float(m.group(3)) >= fresh and m.group(2) in colours:
                return {"who": m.group(1), "colour": m.group(2), "eta": float(m.group(3)), "bar": vec(m.group(5)), "axis": vec(m.group(6)), "raw": first}
        time.sleep(0.05)
    return None


def wait_outcome(c0, keys=OUTCOMES, timeout=4.0):
    t0 = time.time()
    while time.time() - t0 < timeout:
        c = counts()
        for k in keys:
            if c.get(k, 0) > c0.get(k, 0):
                return k, c
        time.sleep(0.08)
    return None, counts()


def reset_pose():
    sim(HEAD + "; " + HAND_R + "; " + HAND_L)


# ------------------------------------------------------------------------------------------------ item 3: grading

EXPECT = {
    "arming": "arming sword: swing 4.0 m/s over 0.175 s, Standard 0.050 / Big 0.20 m; stab 2.0 m/s over 0.05 s, 0.02 / 0.45 m; re-hit 0.3 s; guard 7",
    "curved": "curved axe: swing 4.0 m/s over 0.150 s, Standard 0.200 / Big 0.55 m",
    "mace": "mace: swing 4.0 m/s over 0.175 s, Standard 0.050 / Big 0.20 m; stab 2.0 m/s over 0.05 s, 0.25 / 0.60 m; re-hit 0.3 s; guard 10 (x0.3 / 1 / x1.4)",
    "daggers": "daggers: swing 4.5 m/s over 0.100 s, Standard 0.075 / Big 0.35 m; stab 2.0 m/s over 0.05 s, 0.10 / 0.45 m; re-hit 0.2 s; guard 5",
    "heavyhammer": "heavy hammer: swing 3.0 m/s over 0.175 s, Standard 0.150 / Big 0.20 m; stab 2.0 m/s over 0.10 s, 0.05 / 0.20 m; re-hit 0.3 s; guard 16",
    "giant": "giant greatsword: swing 3.0 m/s over 0.175 s, Standard 0.150 / Big 0.20 m; stab 2.0 m/s over 0.10 s, 0.05 / 0.20 m; re-hit 0.3 s; guard 12 (x0.1",
}


def part_weapons():
    bad = []
    for k, want in EXPECT.items():
        got = send("vrweapon " + k)
        if want not in got:
            bad.append("%s -> %s" % (k, got[:160]))
    game = send("vrweapon game")
    result("weapons", not bad, ("the held weapon: %s | " % game[:90]) + ("all %d UYF weapons as in weapon_stats.tsv" % len(EXPECT) if not bad else "; ".join(bad)))


def air_swing(speed, dist=0.75, weapon=None):
    """The right hand straight across in front at `speed` m/s (no enemy near the blade): legal swings counted."""
    if weapon:
        send("vrweapon " + weapon)
    sim("head 0 1.7 0 180 0 0; hand r -0.45 1.75 0.35 180 5 0")
    time.sleep(0.35)
    c0 = counts()
    ms = dist / speed * 1000.0
    sim("move r %.3f 0 0 %d" % (dist, ms))
    time.sleep(0.25)
    c1 = counts()
    sim("hand r -0.45 1.75 0.35 180 5 0")
    time.sleep(0.3)
    return int(c1.get("swings", 0) - c0.get("swings", 0)), int(c1.get("stabsLegal", 0) - c0.get("stabsLegal", 0))


def turn_away():
    """Face away from every enemy (the swings go through the air)."""
    e = enemy()
    if e:
        send("face nearest")
        time.sleep(0.1)
        sim("head 0 1.7 0 180 0 0")   # the head turned 180 deg: the blade swings away from them
        time.sleep(0.2)


def part_legal():
    turn_away()
    rows = []
    ok = True
    for weapon, speed, want in (("arming", 3.0, 0), ("arming", 6.0, 1), ("heavyhammer", 4.5, 1), ("heavyhammer", 2.0, 0),
                                ("daggers", 3.5, 0), ("daggers", 7.0, 1)):
        got, _ = air_swing(speed, weapon=weapon)
        good = (got >= 1) == (want == 1)
        ok = ok and good
        rows.append("%s %.1f m/s -> %d legal%s" % (weapon, speed, got, "" if good else " (WRONG)"))
    send("vrweapon game")
    reset_pose()
    result("legal", ok, "; ".join(rows))


def part_waggle():
    turn_away()
    rows = []
    ok = True
    seg = []
    for _ in range(6):
        seg += ["0.14 0.05 0", "-0.14 -0.05 0"]
    for weapon in ("arming", "daggers", "heavyhammer"):
        send("vrweapon " + weapon)
        sim("head 0 1.7 0 180 0 0; hand r 0.18 1.45 0.35 180 5 0")
        time.sleep(0.3)
        c0 = counts()
        sim("path r " + " ".join(seg) + " 1000")
        time.sleep(0.2)
        c1 = counts()
        n = int(c1["swings"] - c0["swings"])
        ok = ok and n == 0
        rows.append("%s: %d legal" % (weapon, n))
    send("vrweapon game")
    reset_pose()
    result("waggle", ok, "6 Hz waggle (+-7 cm) for 1 s: " + "; ".join(rows))


def blade_dir():
    b = send("blade?")
    m = re.search(r"dirT \((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\)", b)
    return [float(m.group(1)), float(m.group(2)), float(m.group(3))] if m else None, b


def part_stab():
    turn_away()
    send("vrweapon arming")
    sim("head 0 1.7 0 180 0 0; hand r 0.2 1.4 0.2 180 -30 0")
    time.sleep(0.4)
    d, raw = blade_dir()
    if d is None:
        return result("stab", False, "no blade: " + raw[:120])
    # along the blade (xrsim z = -tracking z), 0.5 m in 200 ms = 2.5 m/s
    c0 = counts()
    sim("move r %.3f %.3f %.3f 200" % (d[0] * 0.5, d[1] * 0.5, -d[2] * 0.5))
    time.sleep(0.3)
    c1 = counts()
    along = int(c1["stabsLegal"] - c0["stabsLegal"])
    sim("hand r 0.2 1.4 0.2 180 -30 0")
    time.sleep(0.4)
    # the same speed square to the blade (sideways, level)
    side = [-d[2], 0.0, d[0]]
    n = math.hypot(side[0], side[2]) or 1.0
    c0 = counts()
    sim("move r %.3f 0 %.3f 200" % (side[0] / n * 0.5, -side[2] / n * 0.5))
    time.sleep(0.3)
    c1 = counts()
    square = int(c1["stabsLegal"] - c0["stabsLegal"])
    send("vrweapon game")
    reset_pose()
    result("stab", along >= 1 and square == 0, "a 2.5 m/s thrust along the blade: legal stabs +%d; the same move square to it: +%d | blade dirT %s" % (
        along, square, ",".join("%.2f" % x for x in d)))


def swing_at(kind):
    """During an attacker's wind-up within 1.8 m: face it and swing; returns (before, after, enemy) or None."""
    t0 = time.time()
    while time.time() - t0 < 40:
        st = strike(timeout=20)
        if st is None:
            return None
        e = enemy()
        if e and e["dist"] < 1.8:
            send("face nearest")
            time.sleep(0.15)
            c0 = counts()
            if kind == "big":
                sim("hand r 0.8 1.4 -0.3 0 5 0; wait 300; move r -1.6 -0.2 -0.15 170")
            else:   # a 6 m/s chop from just beside the body: legal only once it is in it (graded by its speed: Standard)
                sim("hand r 0.55 1.35 -0.35 0 5 0; wait 250; move r -1.0 0 0 165")
            time.sleep(0.3)
            c1 = counts()
            reset_pose()
            return c0, c1, e
        time.sleep(0.2)
    return None


def part_quality():
    got = {}
    for kind in ("big", "chop"):
        for _ in range(4):
            r = swing_at(kind)
            if r is None:
                break
            c0, c1, e = r
            if c1["hits"] > c0["hits"]:
                q = "big" if c1["big"] > c0["big"] else "standard" if c1["standard"] > c0["standard"] else "under"
                got[kind] = (q, last_note(c1))
                break
    ok = got.get("big", ("", ""))[0] == "big" and got.get("chop", ("", ""))[0] in ("under", "standard")
    result("quality", ok, " | ".join("%s -> %s (%s)" % (k, v[0], v[1][:110]) for k, v in got.items()) or "no hit landed")


# ------------------------------------------------------------------------------------------------ item 1 + 7: guard, crits

def poise(who="nearest"):
    p = send("poise? " + who)
    m = re.search(r"(\w+) (\w[\w ]*?) guard ([\d.]+)/(\d+) phase (\w+) step (\d+) crit (.*?) stunned (\w+) held ([\d.]+)", p)
    if not m:
        return {"raw": p}
    d = {"who": m.group(1), "kind": m.group(2), "guard": float(m.group(3)), "max": float(m.group(4)), "phase": m.group(5), "step": int(m.group(6)),
         "crit": m.group(7), "stunned": m.group(8) == "True", "held": float(m.group(9)), "raw": p}
    cm = re.search(r"axisT (-?[\d.]+),(-?[\d.]+),(-?[\d.]+)", p)
    if cm:
        d["axis"] = [float(cm.group(i)) for i in (1, 2, 3)]
    am = re.search(r"at \((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\)", p)
    if am:
        d["at"] = [float(am.group(i)) for i in (1, 2, 3)]
    im = re.search(r"id (#-?\d+)", p)
    if im:
        d["id"] = im.group(1)
    return d


def cut(axis, square=False, length=1.8, ms=180, who="nearest"):
    """Face the enemy and cut through its chest along `axis` (tracking space; square: across it). The other enemies are put
    3 m from it first (`aside`): the cut meets nobody else on the way (CPZ1 and env 1's main run: a follow-up cut taken by
    another thief standing nearer)."""
    if who not in ("nearest", "broken"):
        send("aside %s 3" % who)
    send("face " + who)
    time.sleep(0.15)
    e = send("enemy? " + who)
    m = re.search(r"dist ([\d.]+).* at \((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\) head \((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\)", e)
    dist = float(m.group(1)) if m else 1.3
    p = poise(who)
    chest_dy = (p["at"][1] - float(m.group(6))) if (m and "at" in p) else -0.35
    # the crit's line in tracking space as it is now (facing the enemy turned the view: a line read before is stale)
    ax = list(p["axis"]) if "axis" in p else list(axis)
    if square:
        # square to it in the plane facing the player (x, y): keeping the line's depth made a slanted cut that started
        # inside the body when the slash leant toward the player (MRa: no hit at all)
        ax = [-ax[1], ax[0], 0.0]
    n = math.sqrt(sum(c * c for c in ax)) or 1.0
    ax = [c / n for c in ax]
    cx, cy, cz = 0.0, 1.7 + chest_dy, -max(0.25, dist - 0.55)
    sx, sy, sz = cx - ax[0] * length / 2, cy - ax[1] * length / 2, cz + ax[2] * length / 2
    sim("head 0 1.7 0 0 0 0; hand r %.3f %.3f %.3f 0 0 0" % (sx, sy, sz))
    time.sleep(0.3)
    c0 = counts()
    sim("move r %.3f %.3f %.3f %d" % (ax[0] * length, ax[1] * length, -ax[2] * length, ms))
    time.sleep(0.35)
    c1 = counts()
    sim(HAND_R)
    return c0, c1, dist


def broken_enemy(near="nearest"):
    """Break the nearest enemy's guard (`near`: the nearest of that name) through the seam (once it is up); wait for its
    guard-break crit. Returns poise(its id) or None."""
    send("face " + near)
    p0 = poise(near)
    who = p0.get("id", near)
    if p0.get("phase") != "Up":
        # a broken guard's first slash stays up to 7 s, then 2.5-3 s of recovery (BA1: 8 s was too short after the block
        # chip before had broken it)
        recovered(who, 14)
    send("poiseset %s 0" % who)
    t0 = time.time()
    while time.time() - t0 < 2.0:
        p = poise(who)
        if p.get("phase") == "Broken" and "axis" in p:
            return p
        time.sleep(0.1)
    return None


def recovered(who, timeout=12.0):
    t0 = time.time()
    while time.time() - t0 < timeout:
        p = poise(who)
        if p.get("phase") == "Up":
            return p
        time.sleep(0.2)
    return poise(who)


def part_chip():
    send("vrdamage setting")
    for _ in range(5):
        r = swing_at("big")
        if r is None:
            break
        c0, c1, e = r
        if c1["hits"] > c0["hits"]:
            p = poise()
            chip = c1["lastChip"]
            # the quality of the last hit (one swing can hit two thieves: lastChip is the last one's)
            qm = re.match(r"(big|standard|under) ", last_note(c1))
            q = qm.group(1) if qm else ("big" if c1["big"] > c0["big"] else "standard" if c1["standard"] > c0["standard"] else "under")
            want = 7 * {"big": 1.2, "standard": 1.0, "under": 0.3}[q]
            flash = c1.get("flashes", 0) - c0.get("flashes", 0)
            shakes = c1.get("shakes", 0) - c0.get("shakes", 0)
            ok = abs(chip - want) < 0.05 and c1["healthHits"] == c0["healthHits"] and (flash >= 1 or q == "under") and shakes >= 1
            return result("chip", ok, "%s hit: guard -%.1f (want %.1f) -> %s; health hits +%d; flashes +%d, shakes +%d | %s" % (
                q, chip, want, p.get("raw", "")[:90], c1["healthHits"] - c0["healthHits"], flash, shakes, last_note(c1)[:120]))
    result("chip", False, "no hit landed")


def part_blockchip():
    for _ in range(6):
        reset_pose()
        sim("hand l -0.45 1.0 0.25 0 0 0")
        st = strike(timeout=25, fresh=0.75, colours=("White", "Gold"))
        if st is None:
            break
        att = st["who"]
        send("face striker")
        p0 = poise(att)
        c0 = counts()
        g = send("guardat shield 0 0 0")
        mm = re.match(r"(move [lr] \S+ \S+ \S+)", g)
        if mm:
            sim(mm.group(1) + " 60")
        what, c1 = wait_outcome(c0)
        if what == "blocks":
            p1 = poise(att)
            chip = c1["lastChip"]
            ok = c1["blockChips"] > c0["blockChips"] and abs(chip - 10.5) < 0.05
            return result("blockchip", ok, "blocked %s: block chips +%d, chip %.1f (want 7 x 1.5 = 10.5); guard %s -> %s" % (
                att, c1["blockChips"] - c0["blockChips"], chip, p0.get("guard"), p1.get("guard")))
        log("  blockchip: outcome %s" % what)
    result("blockchip", False, "no block in 6 tries")


def part_break():
    send("vrcombo setting")
    p = broken_enemy()
    if p is None:
        return result("break", False, "no guard break: " + poise().get("raw", "")[:200])
    who = p["id"]
    time.sleep(1.5)
    c0 = counts()
    time.sleep(2.0)
    c1 = counts()
    p2 = poise(who)
    e = send("enemy? " + who)
    act = re.search(r"action (\S+)", e)
    ok = p2.get("phase") == "Broken" and p2.get("stunned") and p2.get("held", 0) > 0 and "break" in p2.get("crit", "")
    result("break", ok, "3.5 s after the break: %s | action %s" % (p2.get("raw", "")[:220], act.group(1) if act else "?"))


def part_crit():
    p = poise("broken")
    if p.get("phase") != "Broken" or "axis" not in p:
        p = broken_enemy()
    if p is None or "axis" not in p:
        return result("crit", False, "no open crit: " + poise().get("raw", "")[:200])
    who = p["id"]
    line0 = p["crit"].split()[1] if p.get("crit", "-") != "-" else "?"
    # from 1.8 m of it, facing it (BA1: from 2.2 m another thief stood nearer and took the cut as a guard hit)
    send("heroat %s 0 1.8" % who)
    c0, c1, dist = cut(p["axis"], who=who)
    time.sleep(0.4)
    p2 = poise(who)
    stops = c1.get("stops", 0) - c0.get("stops", 0)
    flash = c1.get("flash", "")
    ok = c1["critsLanded"] > c0["critsLanded"] and stops >= 1 and p2.get("step", 0) >= 1
    result("crit", ok, "a cut along the %s deg crit at %.2f m: crits +%d, hit-stops +%d (last %d ms), next crit: %s | %s" % (
        line0, dist, c1["critsLanded"] - c0["critsLanded"], stops, c1.get("lastStopMs", 0), p2.get("crit", "")[:60], last_note(c1)[:140]))


def crit_left(p):
    m = re.search(r"left (-?[\d.]+)", p.get("crit", ""))
    return float(m.group(1)) if m else 0.0


def part_wrong():
    p = poise("broken")
    if p.get("phase") != "Broken" or "axis" not in p or crit_left(p) < 3.0:
        # a fresh guard break: its first crit stays up 7 s (a combo crit left over from `crit` may run out mid-check - NB3)
        p = broken_enemy()
    if p is None or "axis" not in p:
        return result("wrong", False, "no open crit")
    who = p["id"]
    # from 1.8 m of its centre (the crit before pulled the player in to the sword's reach: a cut that starts inside the body
    # is no swing - CPZ1 missed so)
    send("heroat %s 0 1.8" % who)
    # the cut square to its line through the seam (a scripted hand can't get to a square cut's start beside the body
    # without sweeping the blade through it: MRj graded the jump, MRq's slow approach made a stab) - the same judging
    send("aside %s 3" % who)
    c0 = counts()
    send("bladetest %s square" % who)
    c1 = counts()
    p2 = poise(who)
    ok = c1["critsWrong"] > c0["critsWrong"] and c1["critsLanded"] == c0["critsLanded"] and p2.get("phase") == "Broken" and p2.get("crit", "-") != "-"
    result("wrong", ok, "a cut square to the crit's line: wrong +%d, crits +%d; crit still open: %s | %s" % (
        c1["critsWrong"] - c0["critsWrong"], c1["critsLanded"] - c0["critsLanded"], p2.get("crit", "")[:50], last_note(c1)[:120]))


def part_linecut():
    """F3 (the user's playtest 2026-10-17): a cut through the crit's drawn line counts at any depth. The hero 2.6 m from a
    broken thief: the blade (0.5 m ahead of the eyes, its tip ~1.5 m) never reaches the body (~2.3 m) nor the line (~2.1 m),
    but seen from the eyes it sweeps along the line through its middle -> a crit, through the line."""
    p = poise("broken")
    if p.get("phase") != "Broken" or "axis" not in p or crit_left(p) < 3.0:
        p = broken_enemy()
    if p is None or "axis" not in p:
        return result("linecut", False, "no open crit: " + poise().get("raw", "")[:200])
    who = p["id"]
    send("aside %s 3" % who)
    # no crit magnetism for this cut (it pulled the hero to 1.3-1.8 m: the blade met the body - PF3c)
    send("vrmagnet off")
    try:
        for attempt in range(5):
            r = _linecut(who)
            if r != "near":
                return r
            log("  linecut: the enemy walked in under 2.2 m or its crit closed - set up again (attempt %d)" % (attempt + 1))
        return result("linecut", False, "no clean setup in 5 tries (the enemy under 2.2 m or its crit closed)")
    finally:
        send("vrmagnet setting")


def _linecut(who):
    for _ in range(3):
        send("heroat %s 0 2.6" % who)
        send("face " + who)
        time.sleep(0.3)
        e = send("enemy? " + who)
        m = re.search(r"dist ([\d.]+)", e)
        if m and float(m.group(1)) >= 2.2:
            break
    p = poise(who)
    e = send("enemy? " + who)
    m = re.search(r"dist ([\d.]+).* at \((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\) head \((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\)", e)
    if "axis" not in p or not m:
        log("  linecut: no line after the placing: " + p.get("raw", "")[:120])
        send("poiseset %s 0" % who)   # broken again: its fresh crit lasts 7 s
        return "near"
    dist = float(m.group(1))
    if dist < 2.2:
        return "near"
    # the line's height against the eyes (the view camera's world y), not the enemy's head
    eye_y = None
    try:
        st = json.loads(subprocess.run(["python3", H + "/scripts/cmd.py", "--status"], capture_output=True, text=True).stdout)
        eye_y = vec(st.get("mainCamPos", ""))[1]
    except Exception:
        pass
    dy = (p["at"][1] - (eye_y if eye_y is not None else float(m.group(6)))) if "at" in p else -0.35
    ax = list(p["axis"])
    n = math.sqrt(sum(c * c for c in ax)) or 1.0
    ax = [c / n for c in ax]
    depth_line = max(1.2, dist - 0.45)
    k = 0.5 / depth_line                 # the hand 0.5 m ahead, on the eyes' ray to the line's middle
    cx, cy, cz = 0.0, 1.7 + dy * k, -0.5
    L = 1.0   # a real cut's length and speed (6.7 m/s; 0.55 m in 130 ms never turned legal: swings +0)
    sx, sy, sz = cx - ax[0] * L / 2, cy - ax[1] * L / 2, cz + ax[2] * L / 2
    sim("head 0 1.7 0 0 0 0; hand r %.3f %.3f %.3f 0 0 0" % (sx, sy, sz))
    time.sleep(0.3)
    # the others well away just before the cut (pf17d: a second thief walked back into the blade's reach - "guard -7.0
    # (43/50)" on a thief with its guard up)
    send("aside %s 6" % who)
    # the crit still open, with time left (PF3i: the guard was back up by the cut)
    p2 = poise(who)
    if p2.get("phase") != "Broken" or "axis" not in p2 or crit_left(p2) < 2.0:
        send("poiseset %s 0" % who)
        return "near"
    c0 = counts()
    sim("move r %.3f %.3f %.3f 150" % (ax[0] * L, ax[1] * L, -ax[2] * L))
    time.sleep(0.4)
    c1 = counts()
    sim(HAND_R)
    d = lambda key: c1.get(key, 0) - c0.get(key, 0)
    if d("swings") >= 1 and d("hits") == 0 and d("lineCrossings") == 0:
        # a whiff: the line passed by the blade (the enemy moved after the aim - candidate 8 and pf17d) - set up again
        log("  linecut: a whiff (swings +1, no hit, no crossing): " + re.sub(r".*lineLast \[([^]]*)\].*", r"\1", c1.get("_raw", ""))[:140])
        return "near"
    # a crit through the line (another thief the blade also met does not spoil it: pf17e F2 'hits +2 ... line hits +1')
    ok = d("critsLanded") >= 1 and d("lineHits") >= 1
    result("linecut", ok, "a cut along the line at %.2f m from the enemy (the hand 0.5 m ahead): swings +%d, hits +%d, crits +%d, line hits +%d, crossings +%d, wrong +%d | %s" % (
        dist, d("swings"), d("hits"), d("critsLanded"), d("lineHits"), d("lineCrossings"), d("critsWrong"), last_note(c1)[:140]))


def abil():
    r = send("ability?")
    d = {}
    for k in ("taps", "tapsSpoiled", "holds", "refused", "used", "invalid", "stickHolds"):
        m = re.search(r"\b%s (\d+)" % k, r)
        d[k] = int(m.group(1)) if m else 0
    d["_raw"] = r
    return d


def part_stickuse():
    """F4 (the user's playtest 2026-10-17): abilities on the stick click, not the grips. Holding the weapon hand's grip 2 s
    starts nothing (no hold, no aim); a tap of that hand's stick click uses the weapon ability on the enemy in view; a
    1.2 s hold of the stick is no tap: since playtest 3 it aims and is used when let go; the shield hand's grip held starts
    nothing either."""
    send("face nearest")
    sim("head 0 1.7 0 0 0 0; " + HAND_R)
    send("abilitycharge 3")
    time.sleep(0.3)
    a0 = abil()
    sim("btn r grip 2000")
    sim("btn l grip 2000")
    time.sleep(2.3)
    a1 = abil()
    gripped = a1["holds"] == a0["holds"] and a1["taps"] == a0["taps"] and a1["used"] == a0["used"]
    send("abilitycharge 3")
    send("face nearest")
    time.sleep(0.2)
    sim("btn r stick 150")
    time.sleep(0.6)
    a2 = abil()
    tapped = a2["taps"] == a1["taps"] + 1 and a2["used"] == a1["used"] + 1
    send("abilitycharge 3")
    send("face nearest")
    time.sleep(0.2)
    sim("btn r stick 1200")
    time.sleep(1.6)
    a3 = abil()
    # playtest 3 (2026-10-18, mark 11): a hold aims (the ring where the hand points) and is used when let go - no tap
    held = a3["taps"] == a2["taps"] and a3.get("stickHolds", 0) >= a2.get("stickHolds", 0) + 1
    m = re.search(r"last (.*)$", a2["_raw"])
    m3 = re.search(r"last (.*)$", a3["_raw"])
    result("stickuse", gripped and tapped and held, "grips held 2 s: holds +%d taps +%d used +%d | a right stick tap: taps +%d used +%d (%s) | a 1.2 s stick hold: taps +%d stick holds +%d used +%d (%s)" % (
        a1["holds"] - a0["holds"], a1["taps"] - a0["taps"], a1["used"] - a0["used"], a2["taps"] - a1["taps"], a2["used"] - a1["used"],
        (m.group(1) if m else "?")[:120], a3["taps"] - a2["taps"], a3.get("stickHolds", 0) - a2.get("stickHolds", 0), a3["used"] - a2["used"], (m3.group(1) if m3 else "?")[:100]))


def part_combo():
    send("vrcombo 3")
    # a husk's combo (a thief's crits last 3, 2.75, 2.5 s): the combo fixture's ogre, an elite captain (2, 1.75, 1.5 s), now
    # often stands nearest - a turn spans its holder's attacks - and its second crit closed before the scripted cut came,
    # about 2 s from crit to crit (MR4h: crits +1, then "no hit Ogre (recovering)")
    husk = "ThievesThug" if send("enemy? ThievesThug").startswith("ThievesThug") else "nearest"
    p = broken_enemy(husk)
    if p is None:
        return result("combo", False, "no guard break")
    who = p["id"]
    c0 = counts()
    send("aside %s 3" % who)
    ee = send("enemy? " + who)
    mm = re.search(r"head \((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\)", ee)
    head_y = float(mm.group(2)) if mm else None
    sim("head 0 1.7 0 0 0 0")
    t_cut = time.time()
    for _ in range(12):
        p = poise(who)
        if p.get("phase") != "Broken":
            break
        if "axis" not in p:
            time.sleep(0.1)   # the next crit not open yet
            continue
        # each cut from 1.8 m of its centre: a crit's magnetism pulls the player in, and a scripted cut whose blade starts
        # inside the body is ignored (only an entry counts). Placed and faced, the crit's line read again (facing turns the
        # view), then one xrsim line: the cut comes about 0.5 s after the crit opens (test reliability 2026-10-17)
        # the others put 6 m from it before every cut: the fixture's ogre walked back into the cut's way between two cuts
        # and took them as guard hits (round 2: rel17 r4, crits +1 then "big Ogre ... guard -7.6" twice); put 4 m from it, it
        # stood behind the hero and hit him - his hit reaction moved him 1.2 m off the view, and the next two cuts met
        # nothing ("facing ThievesThug at 2.98 m" after `heroat ... 1.8`; round 2's final chain r1). Placed again once when
        # the view is not at the cut's distance
        send("aside %s 6" % who)
        for _ in range(2):
            send("heroat %s 0 1.8" % who)
            fm = re.search(r"at ([\d.]+) m", send("face " + who))
            if not fm or float(fm.group(1)) <= 2.3:
                break
            time.sleep(0.3)
        p = poise(who)
        if "axis" not in p:
            continue
        cc0 = counts()
        interrupt_cut(who, p, (1.8, head_y))
        time.sleep(0.3)
        cc1 = counts()
        log("  combo: cut %.1f s after the last -> %s" % (time.time() - t_cut, last_note(cc1)[:100]))
        t_cut = time.time()
        sim(HAND_R)
    c1 = counts()
    p1 = poise(who)
    t_rec = time.time()
    rec = recovered(who, 12)
    rec_s = time.time() - t_rec
    # since the rulings of 2026-10-18 (p3combat 68ab8b4) the enemy is up again at once with a nearly empty guard, which
    # regrows over its tier's refill time (basic 10 s, never under 4 s): judge that it is up, then that the guard grows back
    # to full no sooner than 4 s and within 13 s (not the old "3 s recovery + 1.25 s refill, full when up")
    g_up = rec.get("guard", 0)
    sim(HANDS_DOWN)   # the blade away from it: a touch would chip the guard and restart its refill delay
    t_up = time.time()
    full, grew = None, False
    while time.time() - t_up < 13.0:
        q = poise(who)
        if q.get("phase") != "Up":
            break
        grew = grew or q.get("guard", 0) > g_up + 0.5
        if q.get("guard", 0) >= q.get("max", 1) - 0.5:
            full = q
            break
        time.sleep(0.25)
    full_s = time.time() - t_up
    started_low = g_up < rec.get("max", 1) * 0.5
    ok = c1["combosDone"] > c0["combosDone"] and p1.get("phase") in ("Recover", "Up") and rec.get("phase") == "Up" \
        and full is not None and grew and (full_s >= 3.5 or not started_low) and full.get("held", 9) < 0.5
    send("vrcombo setting")
    result("combo", ok, "crits +%d, combos done +%d; then %s; up again %.1f s later with guard %.1f, full %.1f s after that (want 4-13 s from low): %s" % (
        c1["critsLanded"] - c0["critsLanded"], c1["combosDone"] - c0["combosDone"], p1.get("phase"), rec_s, g_up, full_s,
        (full or poise(who)).get("raw", "")[:140]))


def part_timeout():
    p = broken_enemy()
    if p is None:
        return result("timeout", False, "no guard break")
    who = p["id"]
    sim(HEAD + "; " + HANDS_DOWN)
    c0 = counts()
    t0 = time.time()
    while time.time() - t0 < 9.0 and poise(who).get("phase") == "Broken":
        time.sleep(0.3)
    took = time.time() - t0
    c1 = counts()
    rec = recovered(who, 8)
    ok = c1["combosDropped"] > c0["combosDropped"] and c1["critsTimedOut"] > c0["critsTimedOut"] and 6.0 <= took <= 8.5 and rec.get("phase") == "Up"
    result("timeout", ok, "the guard-break crit left alone: dropped after %.1f s (UYF 7 s), timed out +%d, dropped +%d; then %s" % (
        took, c1["critsTimedOut"] - c0["critsTimedOut"], c1["combosDropped"] - c0["combosDropped"], rec.get("raw", "")[:100]))


def hp(who="nearest"):
    e = enemy(who)
    return e["hp"] if e else None


def interrupt_cut(who, p, pre=None):
    """Cut along an open interrupt crit at once (one xrsim line: the window is short). `pre`: (dist, head y) read before
    the window opened (saves a round trip of ~0.15 s)."""
    if pre is None:
        ee = send("enemy? " + who)
        mm = re.search(r"dist ([\d.]+).* head \((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\)", ee)
        pre = (float(mm.group(1)), float(mm.group(3))) if mm else (1.5, None)
    dist, head_y = pre
    chest_dy = p["at"][1] - head_y if head_y is not None and "at" in p else -0.3
    ax = p["axis"]
    n = math.sqrt(sum(c * c for c in ax)) or 1.0
    ax = [c / n for c in ax]
    cx, cy, cz = 0.0, 1.7 + chest_dy, -max(0.25, dist - 0.55)
    # the jump to the start, a pause (the 50 ms the swing's direction is read over must not hold the jump), the cut
    sim("hand r %.3f %.3f %.3f 0 0 0; wait 60; move r %.3f %.3f %.3f 120" % (cx - ax[0] * 0.9, cy - ax[1] * 0.9, cz + ax[2] * 0.9, ax[0] * 1.8, ax[1] * 1.8, -ax[2] * 1.8))


def part_interrupt():
    """(fixture combo: an ogre) its combo goes on only while its blows miss: the first blow is dodged along its arrow (or
    blocked), then an interrupt crit opens before the next blow; a cut along it at once: the combo stops (the next blow's
    bar goes), a crit's damage."""
    e = send("poise? Ogre")
    m = re.search(r"id (#-?\d+)", e)
    if not m:
        return result("interrupt", False, "no ogre: " + e[:100])
    who = m.group(1)
    # the harness's cut needs ~0.45 s from the crit's opening; the ogre's window is UYF's 0.5 s: stretched x3 for the test
    # (the combo's next blow waits for it) - the window's own length is UYF's data, the cut along it is what is tested here
    send("vrcritwindow 3")
    seen = 0
    tried = 0
    t0 = time.time()
    while time.time() - t0 < 180:
        if user_in_vr():
            return
        send("face " + who)
        s0 = send("strike?", 4)
        first = s0.split(" | ")[0] if s0 and s0 != "none" else ""
        if not first.startswith("Ogre"):
            time.sleep(0.1)
            continue
        em = re.search(r"eta (-?[\d.]+)", first)
        eta = float(em.group(1)) if em else 0.5
        dm = re.search(r"dodge (\S+) ([\d.]+) m progress (-?[\d.]+) dirT (-?[\d.]+),(-?[\d.]+),(-?[\d.]+)", first)
        c0 = counts()
        if dm:
            # dodge it: the head along the arrow by its distance + 4 cm shortly before the impact
            need = float(dm.group(2)) + 0.04
            d = [float(dm.group(4)) * need, float(dm.group(5)) * need, float(dm.group(6)) * need]
            time.sleep(max(0.0, eta - 0.35))
            sim("head %.3f %.3f %.3f 0 0 0" % (d[0], 1.7 + d[1], -d[2]))
        else:
            sim("hand l -0.45 1.0 0.25 0 0 0")
            g = send("guardat shield 0 0 0")
            gm = re.match(r"(move [lr] \S+ \S+ \S+)", g)
            if gm:
                sim(gm.group(1) + " 60")
        # the blow lands (or not); then watch for the interrupt crit before the next one (the distance and head read now:
        # the window is ~0.5 s)
        ee = send("enemy? " + who)
        mm = re.search(r"dist ([\d.]+).* head \((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\)", ee)
        pre = (float(mm.group(1)), float(mm.group(3))) if mm else None
        t1 = time.time()
        cut_done = False
        while time.time() - t1 < 2.5:
            p = poise(who)
            if "interrupt" in p.get("crit", "") and "axis" in p:
                seen += 1
                tried += 1
                interrupt_cut(who, p, pre)
                cut_done = True
                time.sleep(0.3)
                break
            time.sleep(0.03)
        c1 = counts()
        sim(HEAD + "; " + HAND_R + "; hand l -0.45 1.0 0.25 0 0 0")
        if cut_done and c1["interrupts"] > c0["interrupts"]:
            time.sleep(0.2)
            st = send("strike?")
            send("vrcritwindow setting")
            return result("interrupt", True, "interrupt crits seen %d (window x3 for the harness); a cut along it: interrupts +%d, crits +%d | %s | live strikes after: %s" % (
                seen, c1["interrupts"] - c0["interrupts"], c1["crits"] - c0["crits"], last_note(c1)[:120], st[:80]))
        if cut_done:
            log("  interrupt: the cut came too late or missed (%s)" % last_note(c1)[:120])
        time.sleep(0.2)
    c = counts()
    send("vrcritwindow setting")
    result("interrupt", False, "interrupt crits seen %d (opened %s, follow-ups %s), none cut in time in 180 s" % (seen, c.get("interruptsOpened"), c.get("followUps")))


def settle(who, timeout=3.0):
    """Wait until the enemy stands still (two reads 0.25 s apart within 5 cm): a thief hit just before slides or hops back."""
    t0, prev = time.time(), None
    while time.time() - t0 < timeout:
        m = re.search(r" at \((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\)", send("enemy? " + who))
        at = [float(m.group(i)) for i in (1, 2, 3)] if m else None
        if at is not None and prev is not None and math.dist(at, prev) < 0.05:
            return True
        prev = at
        time.sleep(0.25)
    return False


def part_interruptcut():
    """An interrupt crit opened through the seam (`poiseint`, as between a combo's blows) on the nearest attacker; a cut along
    it: interrupts +1 (a crit: the game's whole attack), its strike ended; the same cut square to it: a plain guard hit."""
    rows = []
    ok = True
    # on a husk thief, as `combo`: the combo fixture's ogre now often stands nearest (a turn spans its holder's attacks) and
    # the cut along met nothing or the ogre's guard (MR4k: 0/2; MR4h's pass and candidate 6b's were on the thief)
    near = "ThievesThug" if send("enemy? ThievesThug").startswith("ThievesThug") else "nearest"
    # a cut along that meets nothing is cut again once: the thief (the turn holder) may begin its running attack as the
    # window opens and be gone from the blade's path (round 2's tail: swings +1, hits +0, the thief 2.3 m off in
    # ActionMeleeAI; the note shown was combo's last crit) - a whiff tests no crit
    for square in (False, True):
      for attempt in range(1 if square else 2):
        p0 = poise(near)
        who = p0.get("id", "nearest")
        if p0.get("phase") != "Up":
            # a guard broken by the part before (`wrong`): its crit runs out (7 s), it recovers (3 s) and refills (1.25 s)
            recovered(who, 14)
        settle(who)
        send("aside %s 3" % who)
        send("heroat %s 0 1.8" % who)
        send("face " + who)
        send("poiseint %s 3" % who)
        p = poise(who)
        if "interrupt" not in p.get("crit", "") or "axis" not in p:
            ok = False
            rows.append("no interrupt crit: " + p.get("raw", "")[:100])
            break
        if square:
            # square to it through the seam (see `wrong`)
            c0 = counts()
            send("bladetest %s square" % who)
            c1 = counts()
        else:
            c0 = counts()
            interrupt_cut(who, p)
            time.sleep(0.35)
            c1 = counts()
            sim(HAND_R)
            if attempt == 0 and c1.get("hits", 0) == c0.get("hits", 0) and c1.get("interrupts", 0) == c0.get("interrupts", 0):
                am = re.search(r"dist ([\d.]+) .*? action (\w+)", send("enemy? " + who))
                rows.append("along: the cut met nothing (%s) - cut again" % ("%s m, %s" % am.groups() if am else "?"))
                time.sleep(1.0)
                continue
        n = c1["interrupts"] - c0["interrupts"]
        good = (n >= 1) if not square else (n == 0 and c1["chips"] > c0["chips"])
        ok = ok and good
        rows.append("%s: interrupts +%d, chips +%d (%s)%s" % ("square" if square else "along", n, c1["chips"] - c0["chips"], last_note(c1)[:80], "" if good else " WRONG"))
        time.sleep(0.5)
        break
    result("interruptcut", ok, "; ".join(rows))


# ------------------------------------------------------------------------------------------------ item 2: enemies that block

def swing_at_enemy(who, deg=0, dist=1.3):
    """Stand `dist` m from that enemy, `deg` deg off its facing (0 = in front), facing it; a big swing through it."""
    send("heroat %s %d %.2f" % (who, deg, dist))
    send("face " + who)
    sim(HEAD + "; " + HAND_R)
    time.sleep(0.4)
    c0 = counts()
    e0 = send("enemy? " + who)
    sim("hand r 0.8 1.4 -0.3 0 5 0; wait 250; move r -1.6 -0.2 -0.15 170")
    time.sleep(0.35)
    c1 = counts()
    reset_pose()
    return c0, c1, e0


def part_deflect():
    """(--fixture shield) a big swing at the phalanx's raised shield from the front: deflected - no chip, no damage, the
    clank and the blade kicked; the same swing from behind it gets past (the flank rule): a guard hit."""
    e = send("poise? Phalanx")
    m = re.search(r"id (#-?\d+)", e)
    if not m:
        return result("deflect", False, "no phalanx: " + e[:80])
    who = m.group(1)
    rows = []
    ok = True
    got_front = False
    for _ in range(6):
        send("aside %s 3" % who)   # the soldiers away from it: their shields must not take the swing (BE4)
        g0 = poise(who).get("guard")
        c0, c1, e0 = swing_at_enemy(who, 0, 1.3)
        if "blocking True" not in e0:
            time.sleep(0.5)
            continue
        if c1["hits"] == c0["hits"] and c1["deflects"] == c0["deflects"]:
            continue
        g1 = poise(who).get("guard")
        # no guard hit from the blade (its guard may still drop by a block chip: the player's shield in its rest pose blocked
        # one of its blows meanwhile - CPZ2: 150 -> 144.8 = 7 x its blocked multiplier 0.75)
        good = c1["deflects"] > c0["deflects"] and c1["kicks"] > c0["kicks"] and c1["chips"] == c0["chips"]
        ok = ok and good
        rows.append("front: deflects +%d, kicks +%d, its guard %s -> %s (%s)" % (c1["deflects"] - c0["deflects"], c1["kicks"] - c0["kicks"], g0, g1, last_note(c1)[:90]))
        got_front = True
        break
    if not got_front:
        ok = False
        rows.append("no swing at a raised phalanx shield landed")
    for _ in range(8):
        send("aside %s 3" % who)
        g0 = poise(who).get("guard")
        # the hero 170 deg round from its facing and the hit at once (`bladetest`: the same big hit, no swing): a real swing
        # takes ~0.9 s from the move, and the phalanx's turtle turns to face the player meanwhile (NB2: deflected from the front)
        c0 = counts()
        e0 = send("enemy? " + who)
        send("heroat %s 170 1.0" % who)
        send("bladetest %s big" % who)
        c1 = counts()
        reset_pose()
        if c1["hits"] == c0["hits"]:
            time.sleep(0.5)
            continue
        # from behind its block never deflects: the flank rule, or (the phalanx lowers its shield to the rear) a plain guard
        # hit - on the phalanx itself (its guard down, or the flank counted; a soldier beside it may deflect the same swing)
        blocking = "blocking True" in e0
        g1 = poise(who).get("guard")
        good = c1["flank"] > c0["flank"] or (g0 is not None and g1 is not None and g1 < g0)
        ok = ok and good
        rows.append("behind (%s): flank +%d, deflects +%d, chips +%d, its guard %s -> %s" % ("blocking" if blocking else "not blocking", c1["flank"] - c0["flank"], c1["deflects"] - c0["deflects"], c1["chips"] - c0["chips"], g0, g1))
        break
    result("deflect", ok and len(rows) == 2, "; ".join(rows))


def part_blocktiming():
    """(--fixture shield) the soldiers' blocks (ActionBlock) under UYF's timing: watched for 45 s while they are hit now and
    then - each block 2-5 s (the counters: blocks held to 2 s, given up at 5 s, refused within 2 s of the last)."""
    e = send("poise? EmpireSoldier")
    m = re.search(r"id (#-?\d+)", e)
    if not m:
        return result("blocktiming", False, "no soldier: " + e[:80])
    who = m.group(1)
    send("vrguard 10")   # no guard breaks meanwhile (a staggered soldier never blocks)
    send("shields? reset")
    c0 = counts()
    spans = []
    since = None
    t0 = time.time()
    last_swing = 0
    while time.time() - t0 < 45:
        if user_in_vr():
            return
        e = send("enemy? " + who)
        if e == "none":
            break
        b = "blocking True" in e
        now = time.time()
        if b and since is None:
            since = now
        elif not b and since is not None:
            spans.append(now - since)
            since = None
        if now - last_swing > 3.0:
            # a burst of three quick swings (HoF2's hit-count guard - which starts the soldier's ActionBlock - resets after a
            # pause between hits)
            last_swing = now
            send("heroat %s 0 1.2" % who)
            send("face " + who)
            for _ in range(3):
                sim("hand r 0.8 1.4 -0.3 0 5 0; wait 120; move r -1.6 -0.2 -0.15 150")
            reset_pose()
        time.sleep(0.1)
    c1 = counts()
    log("  blockcond: " + send("blockcond " + who)[:600])
    send("vrguard setting")
    held, gave, refused = c1["blocksHeld"] - c0["blocksHeld"], c1["blocksGivenUp"] - c0["blocksGivenUp"], c1["blocksRefused"] - c0["blocksRefused"]
    drops, holds, soon = (c1.get(k, 0) - c0.get(k, 0) for k in ("shieldDrops", "shieldHolds", "shieldTooSoon"))
    # the shields' own times up (Poise.BlockTiming's record: exact), the polled spans beside them (blurred by the bursts)
    own = [float(x) for x in re.findall(r"\d+\.\d+", send("shields?").split(":", 1)[-1])]
    longest = max(own) if own else (max(spans) if spans else 0)
    shortest = min(own) if own else (min(spans) if spans else 0)
    begins = c1["blockBegins"] - c0["blockBegins"]
    # each shield up 2-5 s (the mod's own record when it has one; else the polled spans, blurred by up to ~0.5 s)
    if own:
        ok = longest <= 5.05 and shortest >= 1.95
    else:
        ok = len(spans) >= 1 and longest <= 5.6 and shortest >= 1.6
    spans = own or spans
    result("blocktiming", ok, "%d blocks seen (%s s): shortest %.1f, longest %.1f | the shield: given up at 5 s +%d, held to 2 s +%d, too soon +%d | "
           "ActionBlock begun %d, held to 2 s +%d, given up +%d, refused +%d" % (
               len(spans), ", ".join("%.1f" % x for x in spans[:12]), shortest, longest, drops, holds, soon, begins, held, gave, refused))


# ------------------------------------------------------------------------------------------------ item 4: reach sectors

def step_once(dx, dz):
    """During a blockable strike in view, the head moves (dx, dz) m (tracking: x right, z back) 0.4 s before the impact;
    hands down. Returns (outcome, before, after, strike) or None."""
    reset_pose()
    sim(HANDS_DOWN)
    st = strike(timeout=25, fresh=0.8, colours=("White", "Gold"))
    if st is None:
        return None
    send("face striker")
    c0 = counts()
    time.sleep(max(0.0, st["eta"] - 0.4))
    sim("head %.2f 1.7 %.2f 0 0 0" % (dx, dz))
    what, c1 = wait_outcome(c0)
    time.sleep(0.2)
    sim(HEAD)
    return what, c0, c1, st


def part_sector():
    """UYF's damage sector: the head stepped 1.6 m to the side just before the impact (outside the +-45 deg arc) or 2.4 m
    back (beyond the reach - a thief's run attack lunges ~1 m after the player: CP1c's 1.4 m step was caught): out of reach;
    standing still: hit."""
    rows = []
    ok = True
    for name, dx, dz, want in (("side", 1.6, 0.0, "outOfRange"), ("back", 0.0, 2.4, "outOfRange"), ("still", 0.0, 0.0, "hitsTaken")):
        got = None
        for _ in range(4):
            r = step_once(dx, dz)
            if r is None:
                break
            what, c0, c1, st = r
            if what in ("outOfRange", "hitsTaken", "blocks"):
                got = what
                side = int(c1.get("sideMisses", 0) - c0.get("sideMisses", 0))
                rows.append("%s -> %s%s" % (name, what, " (outside the arc +%d)" % side if side else ""))
                break
        if got != want:
            ok = False
            if got is None:
                rows.append("%s -> no outcome" % name)
    log("  sector: " + send("pcombat? log")[:10])
    result("sector", ok, "; ".join(rows))


# ------------------------------------------------------------------------------------------------ item 5: tickets

def part_tickets():
    """UYF's tickets over 40 s of a fight (hands down, standing): the Engaged ticket is handed out by UYF's ranking (ranked
    or kept counted), the 0.35 s wait after gaining it holds a strike now and then, never two live strikes; the share of
    samples whose engaged enemy is in the player's view."""
    reset_pose()
    sim(HANDS_DOWN)
    t0c = send("tickets?")
    c0 = counts()
    seen, inview = 0, 0
    t0 = time.time()
    while time.time() - t0 < 40:
        if user_in_vr():
            return
        tk = send("tickets?")
        m = re.search(r"engaged \S+?#(-?\d+)", tk)
        if m:
            e = send("enemy? #" + m.group(1))
            b = re.search(r"bearing (-?\d+)", e)
            if b:
                seen += 1
                inview += 1 if abs(int(b.group(1))) <= 60 else 0
        time.sleep(0.5)
    tk = send("tickets?")
    c1 = counts()
    r0 = re.search(r"ranked (\d+) kept (\d+) expired (\d+) gainWaits (\d+)", t0c)
    r1 = re.search(r"ranked (\d+) kept (\d+) expired (\d+) gainWaits (\d+)", tk)
    if not (r0 and r1):
        return result("tickets", False, "no tickets?: " + tk[:120])
    ranked, kept, expired, waits = (int(r1.group(i)) - int(r0.group(i)) for i in range(1, 5))
    ok = ranked + kept >= 3 and waits >= 1 and c1.get("maxLive", 9) <= 1
    result("tickets", ok, "40 s: tickets handed out %d (re-ranked %d, kept %d), expired %d, ticket waits %d, max live strikes %d; engaged in view %d of %d samples | %s" % (
        ranked + kept, ranked, kept, expired, waits, c1.get("maxLive", -1), inview, seen, tk[tk.find("|"):][:120]))


# ------------------------------------------------------------------------------------------------ item 6: knockback, magnetism

def part_knockhit():
    """A blow let through (hands down, standing still): UYF's knockback 1.6 m along the attacker's line (walls and bodies may
    stop it short), the hit-taken shake."""
    for _ in range(5):
        reset_pose()
        sim(HANDS_DOWN)
        st = strike(timeout=25, fresh=0.6, colours=("White", "Gold"))
        if st is None:
            break
        send("face striker")
        c0 = counts()
        what, c1 = wait_outcome(c0)
        if what != "hitsTaken":
            continue
        time.sleep(0.6)   # 1.6 m at 10 -> 0.5 m/s
        c1 = counts()
        kb = c1.get("knockbacks", 0) - c0.get("knockbacks", 0)
        moved = c1.get("knocked", 0)
        why = re.search(r"knocked [\d.]+ shakes \d+ max [\d.]+ m last ([^|]*?) atlas", c1["_raw"])
        ok = kb >= 1 and 0.5 <= moved <= 1.65
        return result("knockhit", ok, "hit by %s: knockbacks +%d, moved %.2f m (UYF 1.6; walls and bodies stop it), last shake: %s" % (
            st["who"], kb, moved, why.group(1).strip() if why else "?"))
    result("knockhit", False, "no blow landed in 5 tries")


def part_magnet():
    """A hit on the enemy you face from ~1.35 m (its body): UYF's magnetism pulls you to the sword's 1.0 m from its body. The enemy is
    staggered first (it stands still) and the hit is a crit along its slash."""
    for k in range(4):
        p = broken_enemy()
        if p is None:
            continue
        who = p["id"]
        # ~2 m from its centre: well beyond the sword's 1.0 m from its body (another angle on each try: a wall or a body
        # behind the spot put the player at 0.94 m in CPZ1)
        send("heroat %s %d 2.1" % (who, (0, 35, -35, 70)[k]))
        time.sleep(0.2)
        e = send("enemy? " + who)
        dm = re.search(r"dist ([\d.]+)", e)
        if dm and float(dm.group(1)) < 1.6:
            log("  magnet: only %.2f m from it after heroat: another spot" % float(dm.group(1)))
            continue
        c0, c1, dist = cut(p["axis"], who=who)
        time.sleep(0.4)
        c1 = counts()
        if c1["critsLanded"] == c0["critsLanded"]:
            log("  magnet: the cut missed (%s)" % last_note(c1)[:100])
            continue
        n = c1["magnets"] - c0["magnets"]
        pulled = c1.get("magnetLast", 0)
        why = re.search(r"magnetWhy \(([^)]*)\)", c1["_raw"])
        bm = re.search(r"dist ([\d.]+) standoff", why.group(1)) if why else None
        want = float(bm.group(1)) - 1.0 if bm else 0.5
        ok = n >= 1 and abs(pulled - want) < 0.15
        return result("magnet", ok, "a crit from %.2f m (head to its centre): magnets +%d, pulled %.2f m (to the sword's 1.0 m from its body), stops %d, %s" % (
            dist, n, pulled, c1["magnetStops"] - c0["magnetStops"], why.group(1) if why else "?"))
    result("magnet", False, "no crit landed in 3 tries")


def part_rangedhit():
    """(--fixture archer) a shot that hits: UYF's ranged knockback 3 m and shake; no red flash by default (HitTakenFlash off),
    a red flash with it on."""
    rows = []
    ok = True
    for flash in ("setting", "on"):
        send("vrhitflash " + flash)
        reset_pose()
        sim(HANDS_DOWN + "; hand l -0.45 0.9 0.3 0 0 0")
        c0 = counts()
        t0 = time.time()
        got = False
        while time.time() - t0 < 60:
            c1 = counts()
            if c1["rangedHits"] > c0["rangedHits"]:
                time.sleep(0.8)
                c1 = counts()
                fl = c1.get("flashes", 0) - c0.get("flashes", 0)
                kb = c1.get("knockbacks", 0) - c0.get("knockbacks", 0)
                good = kb >= 1 and ((fl == 0) if flash == "setting" else (fl >= 1 and "ranged hit" in c1["_raw"]))
                ok = ok and good
                rows.append("HitTakenFlash %s: ranged hits +%d, knockbacks +%d (moved %.2f m), flashes +%d%s" % (
                    flash, c1["rangedHits"] - c0["rangedHits"], kb, c1.get("knocked", 0), fl, "" if good else " WRONG"))
                got = True
                break
            time.sleep(0.3)
        if not got:
            ok = False
            rows.append("HitTakenFlash %s: no shot hit in 60 s" % flash)
    send("vrhitflash setting")
    result("rangedhit", ok, "; ".join(rows))


# ------------------------------------------------------------------------------------------------ item 8: the super meter

def super_val():
    m = re.search(r"super ([\d.]+)/", send("super?"))
    return float(m.group(1)) if m else -1.0


def part_super():
    """UYF's super meter: +0.5 a guard hit, +1 a block; the weapon's hit-counter ability usable only when the meter is full,
    and using it empties the meter; shots of the meter on the shield glove beside the health gems."""
    rows = []
    ok = True
    send("super? set 0")
    a = send("ability?")
    rows.append("empty: " + (re.search(r"weapon (.*?) \|", a).group(1)[:70] if re.search(r"weapon (.*?) \|", a) else a[:70]))
    weapon_waits = "usable no" in a.split("|")[0]
    # a guard hit
    for _ in range(4):
        v0 = super_val()
        cpre = counts()
        r = swing_at("big")
        if r and r[1]["hits"] > r[0]["hits"] and r[1]["crits"] == r[0]["crits"]:
            v1 = super_val()
            n = int(r[1]["hits"] - r[0]["hits"])
            # the swing comes during an attacker's wind-up, and swing_at waits for one: a blow may be blocked or dodged
            # (UYF's +1) or land (+5) on the way, counted from the meter's first reading (MR2c: two hits taken; MR2d: a
            # block while swing_at waited)
            nb = int(r[1].get("blocks", 0) - cpre.get("blocks", 0)) + int(r[1].get("dodges", 0) - cpre.get("dodges", 0))
            nh = int(r[1].get("hitsTaken", 0) - cpre.get("hitsTaken", 0))
            good = abs((v1 - v0) - 0.5 * n - 1.0 * nb - 5.0 * nh) < 0.01 or (v1 >= 99.9 and v1 - v0 >= 0.5 * n)
            ok = ok and good
            rows.append("%d guard hit(s)%s%s: %.1f -> %.1f%s" % (n, " and %d block(s) or dodge(s)" % nb if nb else "", " and %d hit(s) taken" % nh if nh else "",
                                                               v0, v1, "" if good else " WRONG"))
            break
    # a block
    for _ in range(5):
        reset_pose()
        sim("hand l -0.45 1.0 0.25 0 0 0")
        st = strike(timeout=25, fresh=0.75, colours=("White", "Gold"))
        if st is None:
            break
        send("face striker")
        v0 = super_val()
        c0 = counts()
        g = send("guardat shield 0 0 0")
        mm = re.match(r"(move [lr] \S+ \S+ \S+)", g)
        if mm:
            sim(mm.group(1) + " 60")
        what, c1 = wait_outcome(c0)
        if what == "blocks":
            v1 = super_val()
            good = v1 - v0 >= 0.99
            ok = ok and good
            rows.append("a block: %.1f -> %.1f%s" % (v0, v1, "" if good else " WRONG"))
            break
    # full: the weapon ability usable; used: empty
    send("super? set 100")
    a = send("ability?")
    usable_full = "usable yes" in a.split("|")[0]
    t = send("abilitytest weapon nearest")
    time.sleep(1.0)
    v = super_val()
    good = (usable_full or not weapon_waits) and (v < 1.0 or not weapon_waits)
    ok = ok and good
    rows.append("full: weapon usable %s; used (%s) -> %.1f%s" % (usable_full, t[:50], v, "" if good else " WRONG"))
    # the glove (the shield hand's back toward the eyes), half full
    send("super? set 60")
    sim(HEAD + "; hand l -0.05 1.45 -0.28 0 75 0")
    time.sleep(0.5)
    p1 = shot("super_glove")
    sim("hand l -0.05 1.45 -0.28 0 75 180")
    time.sleep(0.4)
    p2 = shot("super_glove_b")
    reset_pose()
    rows.append("shots: " + ", ".join(os.path.basename(p) for p in (p1, p2) if p))
    result("super", ok and len(rows) >= 4, "; ".join(rows))


def part_glove():
    """(--noshield --boot) the super meter on the free glove beside the health gems: half full, then full (glowing)."""
    took = []
    for v, name in (("60", "glove_half"), ("100", "glove_full")):
        send("super? set " + v)
        sim(HEAD + "; " + HAND_R + "; hand l -0.02 1.42 -0.27 0 70 0")
        time.sleep(0.5)
        log("  super?: " + send("super?")[:200])
        p = shot(name)
        if p:
            took.append(p)
    send("super? set 0")
    reset_pose()
    result("glove", len(took) == 2 and "hidden" not in send("super?"), "shots: " + ", ".join(os.path.basename(t) for t in took))


# ------------------------------------------------------------------------------------------------ item 9: the enemy table

TABLE_EXPECT = {"ThievesThug": ("husk", 50, 1.2), "EmpireSoldier": ("knight", 100, 1.08), "Goblin": ("spellsword", 40, 1.08),
                "NorseWarrior": ("knight", 100, 1.08), "CorruptedMinion": ("husk", 50, 1.2)}


def part_table():
    """(--fixture big: thieves, soldiers, goblin archers, Norse warriors, a corrupted minion) each family's UYF archetype,
    its guard (poise?) and its lead (UYF's x 1.2 on Normal)."""
    rows = []
    ok = True
    for name, (arch, guard, lead) in TABLE_EXPECT.items():
        t = send("enemytable? " + name)
        if t.startswith("no enemy"):
            rows.append("%s: none in the fight" % name)
            continue
        m = re.search(r": (\w+) \((.*?)\)(?: at \w+:)? guard ([\d.]+) regen [\d.]+ lead ([\d.]+) s", t)
        if not m:
            ok = False
            rows.append("%s: %s" % (name, t[:80]))
            continue
        # with the difficulty ladder (A) the lead at Normal is UYF's own (before it: x 1.2)
        want_lead = lead / 1.2 if (lead is not None and " at Normal:" in t) else lead
        p = poise(name)
        good = (arch is None or m.group(1) == arch) and (guard is None or abs(p.get("max", -1) - guard) < 0.5) and (want_lead is None or abs(float(m.group(4)) - want_lead) < 0.011)
        ok = ok and good
        rows.append("%s: %s (%s) guard %s lead %s%s" % (name, m.group(1), m.group(2), p.get("max"), m.group(4), "" if good else " WRONG"))
    result("table", ok, "; ".join(rows))


def part_bendshot():
    """Shots of the thieves' bent run attack near its blow (the blade at chest height, the bar on it): three strikes in view,
    each shot ~0.15 s before its impact (hands down, standing)."""
    took = []
    t0 = time.time()
    while len(took) < 3 and time.time() - t0 < 90:
        reset_pose()
        sim(HANDS_DOWN)
        st = strike(timeout=20, fresh=0.0, colours=("White", "Gold"))
        if st is None:
            continue
        if st["eta"] > 0.32:
            time.sleep(max(0.0, st["eta"] - 0.18))
        p = shot("bend_%d" % (len(took) + 1))
        if p:
            took.append(p)
        log("  bendshot: %s eta %.2f -> %s | %s" % (st["who"], st["eta"], os.path.basename(p) if p else "-", send("swingbend?")[:150]))
        time.sleep(1.0)
    result("bendshot", len(took) >= 2, ", ".join(os.path.basename(t) for t in took))


def part_shieldup():
    """(--fixture shield) the soldiers raise their shields on UYF's timing (0.5 s after their own attack; Poise.ShieldsUp): a
    raised shield deflects a hit from the front (its guard unchanged); with the super armor lowered (`vrsuperarmor 5`) the
    same hit breaks through: a guard break, the slashes open."""
    rows = []
    ok = True
    c0 = counts()
    who, t0 = None, time.time()
    reset_pose()
    sim(HANDS_DOWN)
    while time.time() - t0 < 60:
        if user_in_vr():
            return
        e = send("enemy? blocking")
        m = re.search(r"^(\w+) .* id (-?\d+)", e)
        if m and "Soldier" in m.group(1):
            who = "#" + m.group(2)
            break
        # UYF's rule: three quick hits on a soldier raise its shield (Knight: more than 2.25 hits in 3 s)
        p = poise("EmpireSoldier")
        if p.get("id") and p.get("phase") == "Up" and "blocking False" in p.get("raw", ""):
            send("heroat %s 0 1.2" % p["id"])
            for _ in range(3):
                send("bladetest %s standard" % p["id"])
                time.sleep(0.15)
        time.sleep(0.5)
    c1 = counts()
    raised = c1.get("shieldsRaised", 0) - c0.get("shieldsRaised", 0)
    rows.append("shields raised by the driver +%d in %.0f s, a soldier blocking: %s" % (raised, time.time() - t0, who or "none"))
    if who is None:
        return result("shieldup", False, "; ".join(rows) + " | " + send("blockcond EmpireSoldier")[:200])
    # a weak hit from the front: deflected, its guard unchanged (a swing that misses it - it turned or stepped - tries again
    # while the shield is up)
    defl, g0, g1 = 0, None, None
    for _ in range(4):
        if "blocking True" not in send("enemy? " + who):
            time.sleep(0.3)
            continue
        g0 = poise(who).get("guard")
        c2 = counts()
        send("heroat %s 0 1.2" % who)
        send("bladetest %s big" % who)   # the same big hit at once (a real swing's front deflect: `deflect`)
        c3 = counts()
        g1 = poise(who).get("guard")
        defl = c3["deflects"] - c2["deflects"]
        if defl >= 1 or c3["hits"] > c2["hits"]:
            break
    good = defl >= 1 and g1 == g0
    ok = ok and good
    rows.append("a hit on the raised shield: deflects +%d, its guard %s -> %s%s" % (defl, g0, g1, "" if good else " WRONG"))
    # the same with the super armor lowered: break through, the slashes open
    send("vrsuperarmor 5")
    try:
        t1 = time.time()
        tries = 0
        while time.time() - t1 < 40 and tries < 6:
            if "blocking True" not in send("enemy? " + who):
                # the shield comes up again after the soldier's next attack, or after a few hits (the hit-count guard)
                time.sleep(0.3)
                continue
            tries += 1
            c4 = counts()
            send("heroat %s 0 1.2" % who)
            send("bladetest %s big" % who)
            c5 = counts()
            if c5["breakThroughs"] > c4["breakThroughs"]:
                time.sleep(0.4)
                # the one the swing broke through (the swing may have met the other soldier's raised shield first)
                p = poise(who)
                if p.get("phase") != "Broken":
                    p = poise("broken")
                # broken, a slash open (the break's own, or the next one when the same swing already cut the first)
                good = p.get("phase") == "Broken" and p.get("crit", "-") not in ("", "-")
                ok = ok and good
                rows.append("super armor 5: break through +1 -> %s, crit %s%s" % (p.get("phase"), p.get("crit", "-")[:30], "" if good else " WRONG"))
                break
        else:
            ok = False
            rows.append("no hit on a raised shield with the super armor lowered")
    finally:
        send("vrsuperarmor setting")
    result("shieldup", ok and len(rows) == 3, "; ".join(rows))


def soldier_down(timeout=20):
    """A soldier with its guard up, its shield down and no hit on it for 3 s (its history empty): its id or None."""
    t0 = time.time()
    while time.time() - t0 < timeout:
        p = poise("EmpireSoldier")
        raw = p.get("raw", "")
        if p.get("id") and p.get("phase") == "Up" and "blocking False" in raw and " hits3 0/" in raw:
            return p["id"]
        time.sleep(0.5)
    return None


def part_aggression():
    """Two quick hits on a soldier with its shield down: still down; a third within 3 s: up (UYF's Block_AggressionThreshold,
    Knight 2.25, over the hits it took in the last 3 s)."""
    send("vrguard 10")   # no guard break meanwhile (a staggered soldier never blocks)
    rows = []
    ok = False
    try:
        for _ in range(4):
            who = soldier_down()
            if who is None:
                rows.append("no soldier with its shield down and 3 s without a hit")
                break
            send("aside %s 3" % who)
            send("heroat %s 0 1.2" % who)
            c0 = counts()
            for _ in range(2):
                send("bladetest %s standard" % who)
                time.sleep(0.2)
            time.sleep(0.4)
            p2 = poise(who)
            after2 = "blocking True" in p2.get("raw", "")
            send("heroat %s 0 1.2" % who)
            send("bladetest %s standard" % who)
            up3 = False
            for _ in range(10):
                time.sleep(0.1)
                if "blocking True" in send("enemy? " + who):
                    up3 = True
                    break
            p3 = poise(who)
            c1 = counts()
            h = re.search(r"hits3 (\d+)/([\d.]+)", p3.get("raw", ""))
            rows.append("two hits: shield %s (%s); the third: shield %s within 1 s (hits3 %s), raised for aggression +%d" % (
                "UP" if after2 else "down", re.search(r"hits3 \S+", p2.get("raw", "")).group(0) if "hits3" in p2.get("raw", "") else "?",
                "up" if up3 else "DOWN", h.group(0) if h else "?", c1.get("shieldsAggression", 0) - c0.get("shieldsAggression", 0)))
            if after2 and not up3:
                continue
            ok = (not after2) and up3
            break
    finally:
        send("vrguard setting")
    result("aggression", ok, "; ".join(rows))


def part_pool():
    """A soldier blocking: big hits from the front (8.4 each) are deflected until its block's super armor pool (Knight 60)
    runs out - the eighth breaks through: a guard break with its slash open."""
    rows = []
    ok = False
    try:
        for _ in range(4):
            who = soldier_down(25)
            if who is None:
                rows.append("no soldier with its shield down")
                break
            send("aside %s 3" % who)
            send("heroat %s 0 1.2" % who)
            for _ in range(3):
                send("bladetest %s standard" % who)
                time.sleep(0.15)
            up = False
            for _ in range(10):
                time.sleep(0.1)
                if "blocking True" in send("enemy? " + who):
                    up = True
                    break
            if not up:
                rows.append("its shield did not come up")
                continue
            p0 = poise(who)
            c0 = counts()
            n = 0
            broke = False
            send("heroat %s 0 1.2" % who)
            while n < 10:
                # quick hits (a block lasts 2-5 s): no other call between them
                r = send("bladetest %s big" % who)
                n += 1
                if "BREAK THROUGH" in r:
                    broke = True
                    break
                if "DEFLECTED" not in r:
                    break   # its shield came down (or the hit went elsewhere): another block
            time.sleep(0.4)
            p1 = poise(who)
            c1 = counts()
            pm = re.search(r"pool ([\d.]+)/(\d+)", p0.get("raw", ""))
            rows.append("pool at the block's start %s; big hits %d: deflected %d, broke through %s -> %s, crit %s" % (
                pm.group(0) if pm else "?", n, c1.get("deflects", 0) - c0.get("deflects", 0), broke, p1.get("phase"), p1.get("crit", "-")[:24]))
            if not broke and n < 8:
                continue   # its shield came down first (5 s): another block
            ok = broke and n == 8 and p1.get("phase") == "Broken" and c1.get("deflects", 0) - c0.get("deflects", 0) == 7
            break
    finally:
        pass
    result("pool", ok, "; ".join(rows))


def part_postattack():
    """45 s with the player passive (no hits): the soldiers attack, and none raises its shield after its own attack (UYF's
    Block_PostAttackBlockTimer is the captains' only)."""
    reset_pose()
    sim(HANDS_DOWN)
    send("face EmpireSoldier")
    c0 = counts()
    t0 = time.time()
    while time.time() - t0 < 45:
        if user_in_vr():
            return
        time.sleep(1.0)
    c1 = counts()
    strikes = c1.get("strikes", 0) - c0.get("strikes", 0)
    after = c1.get("shieldsAfterAttack", 0) - c0.get("shieldsAfterAttack", 0)
    aggr = c1.get("shieldsAggression", 0) - c0.get("shieldsAggression", 0)
    gated = c1.get("guardGated", 0) - c0.get("guardGated", 0)
    result("postattack", strikes >= 2 and after == 0, "%d strikes in 45 s; shields raised after an attack +%d, for aggression +%d; the hit-count guard held back +%d" % (
        strikes, after, aggr, gated))


def part_settingslook():
    """The VR settings page's Fights tab (the coordinator, 2026-10-13: the user flips between the looks mid-fight): in the
    fight, the pause menu -> VR Settings -> Fights shows the Difficulty row, the Marker look row (six looks) and the Marker
    colour row; real-hand pokes change the difficulty (A) and the look live (`vrdiff`, `vrlook` report them), back to Normal
    and Gilded (the defaults); a shot of the page."""
    import playtest as pt
    sim(pt.REST + "; head 0 1.75 0 0 0 0")
    send("press START 6")
    b = pt.wait_boards(lambda b: "menu board pause" in b and "Resume" in b, 10)
    v = pt.wait_vrset(lambda v: v.startswith("entry"), 8) if b else None
    if v is None:
        send("press START 6")
        return result("settingslook", False, "no pause menu board or VR Settings entry: " + (pt.vrset_now() or "-")[:80])
    pt.poke_vrset("VR_Settings")
    v = pt.wait_vrset(lambda v: v.startswith("page"), 8)
    if v and not v.startswith("page Fights"):
        pt.poke_vrset("Fights")
    v = pt.wait_vrset(lambda v: v.startswith("page Fights"), 8) or ""
    rows = ["page: " + v[:160]]
    ok = "Marker look=" in v and "Marker colour=" in v and "Difficulty=" in v
    sim("head 0 1.75 0 0 -20 0")
    time.sleep(0.6)
    shot("settings_fights")
    sim("head 0 1.75 0 0 0 0")
    time.sleep(0.3)
    for step in ("Hard", "Normal"):
        pt.poke_vrset("Difficulty/" + step)
        got = send("vrdiff")
        good = got.startswith("difficulty " + step)
        ok = ok and good
        rows.append("difficulty %s -> %s%s" % (step, got[:24], "" if good else " WRONG"))
    for look in ("Ghost", "Ring", "Pill", "Gilded"):
        pt.poke_vrset("Marker_look/" + look)
        got = send("vrlook")
        good = got.endswith(look) or (" " + look) in got
        ok = ok and good
        rows.append("%s -> %s%s" % (look, got[:40], "" if good else " WRONG"))
    pt.poke_vrset("Marker_colour/By_blow")
    bt = send("vrbytype")
    pt.poke_vrset("Marker_colour/One_colour")
    bt2 = send("vrbytype")
    good = bt.endswith("True") and bt2.endswith("False")
    ok = ok and good
    rows.append("colour by blow %s -> one colour %s%s" % (bt[-5:], bt2[-5:], "" if good else " WRONG"))
    pt.poke_vrset("Back")
    time.sleep(1.0)
    send("press START 6")
    time.sleep(1.5)
    result("settingslook", ok, "; ".join(rows))


def wf():
    """weaponfeel? as numbers: max lag (ms), max hand speed, max offset (m), impacts, springs."""
    w = send("weaponfeel?")
    d = {"raw": w}
    for k, rx in (("maxlag", r"\(max ([\d.]+) ms at"), ("speed", r"speeds up to ([\d.]+) m/s"), ("offset", r"offset [\d.]+ m \(max ([\d.]+)\)"),
                  ("impacts", r"impacts (\d+)"), ("springs", r"springs ([\d.]+)"), ("springsMin", r"springsMin ([\d.]+)")):
        m = re.search(rx, w)
        d[k] = float(m.group(1)) if m else -1.0
    return d


def part_lag():
    """WeaponPhysics (UYF's held weapon, B for the user): a straight swing through the air at 7 m/s with each weapon's UYF
    numbers (`vrweapon`): the largest history lag is the weapon's own (arming sword 25 ms, curved axe 50, mace 25, heavy
    longsword 50, daggers 0, a heavy axe in one hand 150); the drawn weapon trails the controller by about speed x lag (the
    grip limit 0.2 m on top); WeaponPhysics off: none."""
    rows = []
    ok = True
    turn_away()
    try:
        for w, want in (("arming", 25.0), ("curvedaxe", 50.0), ("mace", 25.0), ("heavylongsword", 50.0), ("daggers", 0.0), ("heavyaxe", 150.0)):
            send("vrweapon " + w)
            sim("head 0 1.7 0 180 0 0; hand r -0.55 1.75 0.35 180 5 0")
            time.sleep(0.4)
            send("weaponfeel? reset")
            sim("move r 1.05 0 0 150")   # 7 m/s
            time.sleep(0.4)
            d = wf()
            good = abs(d["maxlag"] - want) <= max(3.0, want * 0.12)
            ok = ok and good
            rows.append("%s %.0f ms (want %.0f; hand %.1f m/s, drawn %.2f m behind)%s" % (w, d["maxlag"], want, d["speed"], d["offset"], "" if good else " WRONG"))
        send("vrweapon arming")
        send("vrweaponphysics off")
        sim("hand r -0.55 1.75 0.35 180 5 0")
        time.sleep(0.4)
        send("weaponfeel? reset")
        sim("move r 1.05 0 0 150")
        time.sleep(0.4)
        d = wf()
        good = d["maxlag"] <= 0.5 and d["offset"] <= 0.01
        ok = ok and good
        rows.append("off: %.1f ms, %.3f m%s" % (d["maxlag"], d["offset"], "" if good else " WRONG"))
    finally:
        send("vrweaponphysics setting")
        send("vrweapon game")
        reset_pose()
    result("lag", ok, "; ".join(rows))


def part_knock():
    """WeaponPhysics: a blow blocked with the sword knocks it (UYF's SpringImpact): impacts +1, the springs loosened (their
    loosest since the reset below 1: with Lead = Hold a read right after the block can come after the 0.5 s), back to full
    0.6 s later."""
    import stage1_test as s1
    for _ in range(6):
        if user_in_vr():
            return
        send("weaponfeel? reset")
        r = s1.block_once("blade")
        if r is None:
            break
        what, c0, c1, st, g = r
        if what != "blocks":
            log("  knock: outcome %s" % what)
            continue
        d1 = wf()
        time.sleep(0.7)
        d2 = wf()
        ok = d1["impacts"] >= 1 and 0 < d1["springsMin"] < 1.0 and d2["springs"] >= 0.99
        return result("knock", ok, "%s blocked with the sword: impacts %d, springs at their loosest %.2f (%.2f when read) -> %.2f 0.7 s later" % (
            st["who"], d1["impacts"], d1["springsMin"], d1["springs"], d2["springs"]))
    result("knock", False, "no blow blocked with the sword in 6 tries")


def part_twohand():
    """(--equip RustedAxe --noshield: a two-handed axe, UYF's heavy axe) UYF's two-handed grip: the free hand's grip on the
    haft takes hold (twohand? True, the crit leeway 60 since 2026-10-17, UYF 40), the drawn weapon points along the line between the hands, it holds
    0.9 m apart (never breaks by distance), the artefact's aim does not start; letting go -> one hand (leeway 50, UYF 30)."""
    rows = []
    t0 = send("twohand?")
    if "two-hander True" not in t0:
        return result("twohand", False, "not a two-hander that UYF lets be held in two hands: " + t0[:120])
    turn_away()
    send("vrweaponphysics off")   # the turn alone: the weapon on the line exactly (the lag has its own part)
    try:
        sim("head 0 1.7 0 0 0 0; hand r 0.15 1.1 -0.35 0 30 0; hand l -0.4 0.9 -0.1 0 0 0")
        time.sleep(0.5)
        h = send("haft? 0.3")
        if not h.startswith("hand"):
            return result("twohand", False, "haft? " + h[:80])
        a0 = send("ability?")
        sim(h.split("|")[0].strip())
        time.sleep(0.3)
        sim("hold l grip 1")
        time.sleep(0.4)
        t1 = send("twohand?")
        a1 = send("ability?")
        held = "two-handed True" in t1 and "leeway 60" in t1
        no_aim = "AIMING" not in a1.split("|")[1] if "|" in a1 else True
        m0, m1 = re.search(r"holds (\d+) refused (\d+)", a0), re.search(r"holds (\d+) refused (\d+)", a1)
        no_aim = no_aim and m0 and m1 and m0.groups() == m1.groups()
        rows.append("grabbed: %s; the artefact's aim %s" % (t1[t1.find("two-handed"):][:140], "not started" if no_aim else "STARTED"))
        # move the free hand: the weapon follows the line between the hands
        angs = []
        for dx, dy, dz in ((0.1, 0.35, -0.05), (-0.2, 0.25, 0.1), (0.45, 0.5, -0.6)):
            g = re.search(r"hand l (-?[\d.]+) (-?[\d.]+) (-?[\d.]+)", h)
            sim("hand l %.3f %.3f %.3f 0 0 0" % (float(g.group(1)) + dx, float(g.group(2)) + dy, float(g.group(3)) + dz))
            time.sleep(0.35)
            t = send("twohand?")
            m = re.search(r"apart ([\d.]+) m, the drawn weapon ([\d.]+) deg", t)
            angs.append((float(m.group(1)), float(m.group(2))) if m else (0.0, 99.0))
        along = all(a < 6.0 for _, a in angs)
        still = "two-handed True" in send("twohand?")
        rows.append("the free hand moved: " + ", ".join("%.2f m apart %.1f deg" % x for x in angs) + ("" if along else " WRONG") +
                    ("; still held at %.2f m" % angs[-1][0] if still else "; LET GO"))
        sim("hold l grip 0")
        time.sleep(0.3)
        t2 = send("twohand?")
        released = "two-handed False" in t2 and "leeway 50" in t2
        rows.append("let go: %s" % t2[t2.find("two-handed"):][:60])
        ok = held and no_aim and along and still and released
    finally:
        sim("hold l grip 0")
        send("vrweaponphysics setting")
        reset_pose()
    result("twohand", ok, "; ".join(rows))


STEP_SPEED = {   # UYF's attack speed per archetype and step (EnemyTableData.Steps: Easy, Normal, Hard, Nightmare 1-3)
    "husk": (0.9, 1.0, 1.0, 1.1, 1.125, 1.2), "knight": (0.85, 0.95, 1.0, 1.05, 1.02, 1.05)}
STEP_LEAD = {"husk": (1.5, 1.0, 0.8, 0.7, 0.65, 0.6), "knight": (1.2, 0.9, 0.8, 0.7, 0.65, 0.6)}


def part_difficulty():
    """(--fixture big: thieves = UYF husks, soldiers = knights) [Combat] Difficulty, each step live (`vrdiff`): every judged
    blow's lead (telegraph -> impact) at least its archetype's lead at that step (UYF's PremonitionDuration; the clip may be
    slower: then the lead is the clip's own) and its attack speed the archetype's at that step (UYF's AttackSpeed x the
    game's own 1). Two classes per step; the mean lead per class reported."""
    steps = ["Easy", "Normal", "Hard", "Nightmare1", "Nightmare2", "Nightmare3"]
    rows = []
    ok = True
    lives = {}
    reset_pose()
    sim(HANDS_DOWN)
    try:
        for i, st in enumerate(steps):
            if user_in_vr():
                return
            send("vrdiff " + st.lower())
            send("leads? reset")
            got = {}
            t0 = time.time()
            while time.time() - t0 < 70:
                l = send("leads?")
                got = {}
                for m in re.finditer(r"(\w+)/(\w+)@(\w+) ([\d.]+)/([\d.]+) x([\d.]+);", l):
                    if m.group(3) != st:
                        continue
                    got.setdefault(m.group(2), []).append((float(m.group(4)), float(m.group(5)), float(m.group(6))))
                if all(len(got.get(a, [])) >= 1 for a in ("husk", "knight")) and sum(len(v) for v in got.values()) >= 3:
                    break
                # face whoever strikes (an off-view blow is slowed by the off-view warning and not counted)
                for _ in range(4):
                    if not send("strike?").startswith("none"):
                        send("face striker")
                    time.sleep(0.25)
            parts = []
            for a in ("husk", "knight"):
                bl = got.get(a, [])
                if not bl:
                    parts.append("%s: no blow" % a)
                    ok = False
                    continue
                want_lead, want_speed = STEP_LEAD[a][i], STEP_SPEED[a][i]
                leads = [x[0] for x in bl]
                lead_ok = all(x[0] >= x[1] - 0.05 for x in bl) and all(abs(x[1] - want_lead) < 0.01 for x in bl)
                # the attack speed between strikes (at an impact it also carries the lead floor's slow-down, BA3: x0.3-0.7):
                # `enemytable?`'s live CalculateAttackSpeed of one of that class, not striking
                live = None
                for _ in range(12):
                    tm = re.search(r"speed ([\d.]+) \(live ([\d.]+)\)", send("enemytable? " + ("ThievesThug" if a == "husk" else "EmpireSoldier")))
                    if tm and abs(float(tm.group(2)) - float(tm.group(1))) < 0.02:
                        live = float(tm.group(2))
                        break
                    if tm:
                        live = float(tm.group(2))
                    time.sleep(0.25)
                lives[(a, i)] = live
                ok = ok and lead_ok
                parts.append("%s %d blows: lead %.2f-%.2f s (floor %.2f)%s, attack speed x%s" % (
                    a, len(bl), min(leads), max(leads), want_lead, "" if lead_ok else " WRONG", "%.2f" % live if live is not None else "?"))
            rows.append("%s: %s" % (st, "; ".join(parts)))
            log("  difficulty " + rows[-1])
    finally:
        send("vrdiff setting")
        reset_pose()
    # the attack speed scales by UYF's step factor: each step's live speed / Normal's = the table's ratio (the game's own
    # base speed of that enemy cancels out)
    for a in ("husk", "knight"):
        n = lives.get((a, 1))
        ratios = []
        for i in range(6):
            v = lives.get((a, i))
            if n and v:
                got, want = v / n, STEP_SPEED[a][i] / STEP_SPEED[a][1]
                good = abs(got - want) < 0.03
                ok = ok and good
                ratios.append("%.2f/%.2f%s" % (got, want, "" if good else " WRONG"))
            else:
                ratios.append("?")
                ok = False
        rows.append("%s speed by step vs Normal (got/want): %s" % (a, " ".join(ratios)))
    result("difficulty", ok, " | ".join(rows))


SHOT_RE = re.compile(r"^\s*([\d.]+) (.+?) gap (first|[\d.]+)/([\d.]+) bearing (-?\d+) announced (-|[\d.]+) inview (-|[\d.]+)( melee)?\s*$")


def shot_rows(raw):
    """`rangedshots?` -> its rows (after the header's colon, one per ';')."""
    body = raw.split(":", 1)[1] if ":" in raw else ""
    return [m.groups() for m in (SHOT_RE.match(seg) for seg in body.split(";")) if m]


def archer_phase(step, secs, min_shots):
    """One difficulty for `secs` s: the head turned every 5 s (toward a goblin archer, away from it, to each side) so shots
    start in view and out of it. Returns (ok, summary)."""
    info = send("vrdiff " + step)
    m = re.search(r"announce ([\d.]+) s", info)
    announce = float(m.group(1)) if m else 1.0
    send("rangedshots? reset")
    c0 = counts()
    yaws = [0, 180, 90, -90, 150, -150]
    t0 = time.time()
    i = 0
    while time.time() - t0 < secs:
        if user_in_vr():
            return False, "the user is in VR"
        send("face GoblinArcher")
        sim("head 0 1.7 0 %d 0 0; %s; %s" % (yaws[i % len(yaws)], HAND_R, HAND_L))
        i += 1
        time.sleep(5)
    sim(HEAD)
    raw = send("rangedshots?")
    c1 = counts()
    rows = [{"at": float(r[0]), "who": r[1], "gap": None if r[2] == "first" else float(r[2]), "cd": float(r[3]), "bearing": int(r[4]),
             "ann": None if r[5] == "-" else float(r[5]), "seen": None if r[6] == "-" else float(r[6]), "melee": bool(r[7])} for r in shot_rows(raw)]
    bad = []
    gaps = [r["gap"] for r in rows if r["gap"] is not None]
    for r in rows:
        if r["gap"] is not None and r["gap"] < r["cd"] - 0.1:
            bad.append("%.1f %s: %.2f s after the last shot (cooldown %.1f)" % (r["at"], r["who"], r["gap"], r["cd"]))
        if abs(r["bearing"]) > 55 and (r["ann"] is None or r["ann"] < announce - 0.05):
            bad.append("%.1f %s: from %d deg, announced %s s before (want %.2f)" % (r["at"], r["who"], r["bearing"], "never" if r["ann"] is None else "%.2f" % r["ann"], announce))
    off = [r for r in rows if abs(r["bearing"]) > 55]
    ok = len(rows) >= min_shots and not bad
    summary = "%s: %d shots in %d s (want >= %d), gaps %s (cooldown %s), %d from outside the view (announced %s s before; want >= %.2f), %d while a melee strike was live, rangedHolds +%d, beatsToMelee +%d%s" % (
        step, len(rows), secs, min_shots, " ".join("%.1f" % g for g in gaps) or "-", rows[0]["cd"] if rows else "?", len(off),
        " ".join("%.2f" % r["ann"] for r in off if r["ann"] is not None) or "-", announce, sum(1 for r in rows if r["melee"]),
        c1.get("rangedHolds", 0) - c0.get("rangedHolds", 0), c1.get("beatsToMelee", 0) - c0.get("beatsToMelee", 0), (" | WRONG: " + "; ".join(bad)) if bad else "")
    log("  archers " + summary)
    log("  archers %s raw: %s" % (step, raw[:900]))
    return ok, summary


def part_archers():
    """(--fixture archer: two goblin archers and a goblin warrior; the big fixture's archers come in a later wave, none in
    its first - NB1) shots UYF's way (2026-10-13): a shot or a throw waits only for
    the global ranged cooldown at the difficulty (Normal 3 s, Easy 10 s), not for a live melee strike or our other holds;
    ThreatWarning still announces a shooter outside the view first. Normal for 80 s, then Easy for 45 s, the head turned
    every 5 s: shots happen (Normal >= 3, Easy >= 1), each starts at least the cooldown after the last frame of the shot
    before, and each one started from outside the view (> 55 deg) was announced at least OffViewAnnounce before."""
    send("vrknock off")
    send("warn? reset")
    try:
        ok_n, sum_n = archer_phase("normal", 80, 3)
        ok_e, sum_e = archer_phase("easy", 45, 1)
    finally:
        send("vrdiff setting")
        send("vrknock setting")
        reset_pose()
    w = send("warn?")
    result("archers", ok_n and ok_e, "%s | %s | warn? %s" % (sum_n, sum_e, w[:220]))


def dmg_of(c):
    m = re.search(r"dmg ([\d.]+) hp -([\d.]+)", last_note(c))
    return (float(m.group(1)), float(m.group(2))) if m else (-1.0, -1.0)


def part_health():
    """Damage = Crits: a guard hit's attack does no damage, a crit's does; EveryHit: a guard hit's does. Then (tough off)
    one crit really takes health."""
    rows = []
    ok = True
    try:
        for mode in ("crits", "everyhit"):
            send("vrdamage " + mode)
            for _ in range(4):
                r = swing_at("big")
                if r is None:
                    break
                c0, c1, e = r
                if c1["hits"] > c0["hits"] and c1["crits"] == c0["crits"]:
                    d, lost = dmg_of(c1)
                    good = (d == 0.0) if mode == "crits" else (d > 0.0)
                    ok = ok and good
                    rows.append("%s, guard hit: damage %.1f%s" % (mode, d, "" if good else " (WRONG)"))
                    break
        send("vrdamage crits")
        p = broken_enemy()
        if p is not None:
            who = p["id"]
            send("tough off")
            h0 = hp(who)
            c0, c1, dist = cut(p["axis"], who=who)
            d, lost = dmg_of(c1)
            good = c1["critsLanded"] > c0["critsLanded"] and d > 0 and lost > 0
            ok = ok and good
            rows.append("crits, a crit (tough off): damage %.1f, hp %s -> -%.1f%s" % (d, h0, lost, "" if good else " (WRONG)"))
        else:
            ok = False
            rows.append("no guard break")
    finally:
        send("vrdamage setting")
        send("tough on")
    result("health", ok and len(rows) == 3, "; ".join(rows))


def shot(name):
    p = H + "/testdata/shots/%s_%s_eyes.png" % (TAG, name)
    try:
        os.remove(p)
    except OSError:
        pass
    send("shot %s_%s" % (TAG, name), 20)
    t0 = time.time()
    while time.time() - t0 < 4.0 and not os.path.exists(p):
        time.sleep(0.1)
    if os.path.exists(p) and "--captures" in args:
        os.makedirs(CAPTURES, exist_ok=True)
        shutil.copy(p, os.path.join(CAPTURES, "stage2_%s_eyes.png" % name))
    return p if os.path.exists(p) else None


def part_bar():
    """Shots of one enemy's meter and crits from 1.6 m, the fight frozen (timescale 0; the meters and crits run on real
    time)."""
    took = []
    p0 = poise()
    who = p0.get("id", "nearest")
    if p0.get("phase") != "Up":
        recovered(who, 8)
    send("poiseset %s 50" % who)
    send("heroat %s 0 1.6" % who)
    send("face " + who)
    sim(HEAD + "; " + HANDS_DOWN)
    time.sleep(0.5)
    send("timescale 0")
    send("face " + who)
    send("enemybars zoom 4")   # the meters are 2.2 cm pips: 4x for the simulator's small eye images
    time.sleep(0.6)
    took.append(shot("bar_full"))
    log("  bars?: " + send("bars?")[:600])
    send("poiseset %s 20" % who)
    time.sleep(0.3)
    took.append(shot("bar_chipped"))
    send("poiseset %s 0" % who)
    time.sleep(0.6)
    took.append(shot("bar_broken_crit"))
    p = poise(who)
    if "axis" in p:
        cut(p["axis"], who=who)
        time.sleep(0.05)
        took.append(shot("crit_landed"))
        time.sleep(0.4)
        took.append(shot("crit_next"))
    log("  bars?: " + send("bars?")[:600])
    send("enemybars zoom 1")
    send("timescale 1")
    took = [t for t in took if t]
    result("bar", len(took) >= 4, "%d stereo shots: %s" % (len(took), ", ".join(os.path.basename(t) for t in took)))


PARTS = {"chip": part_chip, "blockchip": part_blockchip, "break": part_break, "crit": part_crit, "linecut": part_linecut, "stickuse": part_stickuse, "wrong": part_wrong, "combo": part_combo,
         "timeout": part_timeout, "health": part_health, "bar": part_bar, "interrupt": part_interrupt, "interruptcut": part_interruptcut, "deflect": part_deflect, "blocktiming": part_blocktiming, "sector": part_sector, "tickets": part_tickets, "knockhit": part_knockhit, "magnet": part_magnet, "rangedhit": part_rangedhit, "super": part_super, "glove": part_glove, "table": part_table, "bendshot": part_bendshot, "shieldup": part_shieldup, "settingslook": part_settingslook, "lag": part_lag, "knock": part_knock, "twohand": part_twohand, "difficulty": part_difficulty, "archers": part_archers, "aggression": part_aggression, "pool": part_pool, "postattack": part_postattack,
         "weapons": part_weapons, "legal": part_legal, "waggle": part_waggle, "stab": part_stab, "quality": part_quality}
DEFAULT = ["weapons", "legal", "waggle", "stab", "quality", "chip", "blockchip", "break", "crit", "wrong", "combo", "timeout", "health", "interruptcut", "bar"]


def leave_fight():
    """indicator_shots.leave(): end the fight, walk out, through the encounter card and loot to the map - every step logged
    (logs/indicator_leave.log), never pressing on into a new encounter's campfire (S2T5/S2T6 ended in one)."""
    import indicator_shots
    ok = indicator_shots.leave()
    log("left the fight: %s %s" % (ok, send("state?")[:60]))


FIXTURES = {}


def main():
    fixture = args[args.index("--fixture") + 1] if "--fixture" in args else "thieves"
    parts = args[args.index("--parts") + 1].split(",") if "--parts" in args else DEFAULT
    if "--no-fixture" not in args:
        how = ["--jump", "EL_Ambush_Thral", "--with", FIXTURES[fixture]] if fixture in FIXTURES else ["--fixture", fixture]
        boot = ["--boot"] if "--boot" in args or "GameState_Map" not in send("state?") else []
        equip = [] if "--noshield" in args else ["--equip", "BasicShield"]   # --noshield: the snapshot hero's free shield hand (the glove)
        if "--equip" in args:
            equip = ["--equip", args[args.index("--equip") + 1]]   # e.g. RustedAxe (a two-handed axe) for `twohand`
        r = subprocess.run(["python3", H + "/scripts/combat_fixture.py"] + how + boot + ["--god", "--physical"] + equip, capture_output=True, text=True)
        log("fixture %s: %s" % (fixture, ((r.stdout or "").strip().splitlines() or ["?"])[-1]))
    if "Level_Play" not in send("state?"):
        result("fixture", False, "no fight: " + send("state?"))
        return 1
    send("god on")
    send("tough on")
    send("vrcombat physical")
    send("pcombat? reset")
    for p in parts:
        if user_in_vr():
            log("the user is in VR: stopping")
            break
        try:
            PARTS[p]()
        except Exception as e:  # noqa: BLE001
            result(p, False, "exception " + repr(e))
    send("vrweapon game")
    log("pcombat: " + send("pcombat?")[:700])
    if "--stay" not in args:
        leave_fight()
    ok = all(o for _, o in results)
    log("%s: %d/%d PASS" % (TAG, sum(1 for _, o in results if o), len(results)))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
