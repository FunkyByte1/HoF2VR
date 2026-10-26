#!/usr/bin/env python3
"""Checks for combat Phase 1B, the lean core loop in `[Combat] Mode = Physical` (docs/combat-progress.md).

  phys_test.py [--parts a,b,...] [--fixture NAME] [--no-fixture] [--tag P1]

Scripted swings and guards through the simulated controllers (xrsim), in a fixture fight (combat_fixture.py --fixture
thieves --boot --god unless --no-fixture). Parts (default: all of the thieves fixture's):
  mode        Physical: a trigger pull presses no Attack; Classic: it does (the game's own moves stay selectable)
  swing       a real swing through an attacker: a hit, the enemy loses health, a hit-stop of 50-100 ms on it
  big         a long fast swing: Big quality (x1.2), a 100 ms hit-stop
  fast        a very fast swing (40 ms) is never rejected
  waggle      a 6 Hz wrist waggle through an enemy for 1 s: no hit
  slow        a slow push through an enemy: no hit
  block       the shield held on the marker early: blocked, no hit taken, the attacker recoils
  counter     the shield meets the blow in its last 0.3 s on a gold strike: countered (the attacker frozen, the counter's
              slow motion), then a swing within 2 s is the riposte
  dodge       the head moves 0.3 m sideways before the impact: dodged
  reach       the head steps 1.2 m back before the impact: out of reach
  pace        over the run: never two live strikes; every in-view strike's telegraph -> impact >= its class's lead
  feedback    one blow let through on purpose (hands down, the head still): its burst in the world, the red screen
              edge not drawn; over the run slow motion only when earned, layered sounds
  shot        both eyes with a strike's marker in view (testdata/shots/<tag>_marker_eyes.png)
  bladeguard  the sword laid across the marker (upright or level) blocks too
  finisher    a downed enemy takes the finisher (the outcome through the seam if no knockdown lies it down in time)
  relaxed     Pace Relaxed: every in-view lead >= 1.5 s
  lastkill    the last enemy killed: the last-kill slow motion
Fixture parts (run with --fixture NAME): armour (quick real hits are flurry hits; the armour drops), red (a red strike
is never blocked: the corrupted tank), bash and shield (fixture shield: the shield driven into an enemy is the game's
bash; real swings at a raised shield are blocked), behind (assassins land behind: warned; the shield held out toward
them blocks), snare (a bolas snares the hero; shaking the controllers struggles free).
Results: PASS/FAIL lines on stdout and in logs/phys_test.log; exit 1 on any FAIL.
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
LOG = H + "/logs/phys_test.log"
args = sys.argv[1:]
TAG = args[args.index("--tag") + 1] if "--tag" in args else "P1"
results = []

HAND_R = "hand r 0.25 1.35 -0.35 0 5 0"
HAND_L = "hand l -0.25 1.2 -0.3 0 0 0"
HEAD = "head 0 1.7 0 0 0 0"


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


def enemy(who="nearest"):
    e = send("enemy? " + who)
    m = re.search(r"dist ([\d.]+) bearing (-?\d+) hp ([\d.]+) armour (\S+) blocking (\w+) prone (\w+) action (\S+) alive (\d+)", e)
    if not m:
        return None
    return {"dist": float(m.group(1)), "bearing": int(m.group(2)), "hp": float(m.group(3)), "armour": m.group(4),
            "blocking": m.group(5) == "True", "prone": m.group(6) == "True", "action": m.group(7), "alive": int(m.group(8)), "raw": e}


def strike(timeout=20.0, inview=True, fresh=0.0):
    """Wait for a live strike (in view; with at least `fresh` s to its impact): (text, eta, colour) or None."""
    t0 = time.time()
    while time.time() - t0 < timeout:
        s = send("strike?", 4)
        if s and s != "none" and not s.startswith("ERR") and not s.startswith("no "):
            first = s.split(" | ")[0]
            if inview and "offview" in first:
                send("face striker")   # the player turns to it (the right stick's snap), then guards
                time.sleep(0.05)
                continue
            if not inview or "offview" not in first:
                m = re.search(r"(\w+) (Gold|White|Red)(?: offview)? eta (-?[\d.]+)", first)
                if m and float(m.group(3)) >= fresh:
                    return first, float(m.group(3)), m.group(2)
        time.sleep(0.05)
    return None


def reset_pose():
    sim(HEAD + "; " + HAND_R + "; " + HAND_L)


def swing_into(kind="left", ms=90, big=False, timeout=30):
    """During an attacker's wind-up (it stands in reach), face it and swing: returns (before, after counts, enemy before)."""
    t0 = time.time()
    while time.time() - t0 < timeout:
        st = strike(timeout=max(1.0, timeout - (time.time() - t0)))
        if st is None:
            return None
        e = enemy()
        if e and e["dist"] < 1.8:
            send("face nearest")
            time.sleep(0.15)
            e = enemy()
            c0 = counts()
            if big:
                sim("hand r 0.8 1.4 -0.3 0 5 0; wait 300; move r -1.6 -0.2 -0.15 170")
            else:
                sim("swing r %s %d" % (kind, ms))
            time.sleep(0.25)
            c1 = counts()
            reset_pose()
            return c0, c1, e
        time.sleep(0.2)
    return None


def gesture_swings():
    m = re.search(r"swings (\d+)", cmd.status().get("gestures", ""))
    return int(m.group(1)) if m else -1


def part_mode():
    """Physical: neither a swing gesture nor the trigger presses the game's Attack; Classic: the swing gesture does."""
    send("vrcombat physical")
    reset_pose()
    g0 = gesture_swings()
    sim("swing r left 90; btn r trigger 200")
    time.sleep(0.4)
    g1 = gesture_swings()
    send("vrcombat classic")
    time.sleep(0.3)
    sim("swing r left 90")
    time.sleep(0.6)
    g2 = gesture_swings()
    send("vrcombat physical")
    reset_pose()
    result("mode", g1 == g0 and g2 > g1, "swing gestures pressing Attack: Physical %d -> %d; Classic -> %d" % (g0, g1, g2))


def crits_only():
    """Stage 2: `[Combat] Damage = Crits` (UYF): a guard hit chips the guard, takes no health and has no hit-stop."""
    return "Crits" in send("vrdamage")


def part_swing():
    r = swing_into("left", 90)
    if r is None:
        return result("swing", False, "no attacker came into reach")
    c0, c1, e = r
    e1 = enemy()
    hits = c1["hits"] - c0["hits"]
    last = re.search(r"last ([^|]*)\|", c1["_raw"]).group(1).strip()
    dmg = re.search(r"dmg ([\d.]+)", last)
    if crits_only():
        ok = hits >= 1 and dmg is not None and float(dmg.group(1)) == 0 and c1.get("chips", 0) > c0.get("chips", 0)
    else:
        ok = hits >= 1 and c1["stops"] > c0["stops"] and c1["lastStopMs"] >= 50 and dmg is not None and float(dmg.group(1)) > 0
    result("swing", ok, "hits +%d (standard +%d big +%d under +%d), hit-stops +%d (last %d ms) | %s" % (
        hits, c1["standard"] - c0["standard"], c1["big"] - c0["big"], c1["under"] - c0["under"], c1["stops"] - c0["stops"],
        c1["lastStopMs"], last))


def part_big():
    for _ in range(3):
        r = swing_into(big=True)
        if r is None:
            break
        c0, c1, e = r
        if c1["hits"] > c0["hits"]:
            ok = c1["big"] > c0["big"] and (c1["lastStopMs"] >= 100 or (crits_only() and abs(c1.get("lastChip", 0) - 8.4) < 0.05))
            return result("big", ok, "big +%d standard +%d, last hit-stop %d ms | %s" % (c1["big"] - c0["big"], c1["standard"] - c0["standard"], c1["lastStopMs"],
                                                                                    re.search(r"last ([^|]*)\|", c1["_raw"]).group(1).strip()))
    result("big", False, "no long swing landed in 3 tries")


def part_fast():
    for _ in range(3):
        r = swing_into("left", 40)
        if r is None:
            break
        c0, c1, e = r
        if c1["swings"] > c0["swings"] and (c1["hits"] > c0["hits"] or c1["illegal"] == c0["illegal"]):
            return result("fast", c1["hits"] > c0["hits"], "a 40 ms swing: legal swings +%d, hits +%d" % (c1["swings"] - c0["swings"], c1["hits"] - c0["hits"]))
    result("fast", False, "no 40 ms swing reached an enemy in 3 tries")


def part_waggle():
    st = strike()
    e = enemy()
    if st is None or e is None:
        return result("waggle", False, "no attacker")
    send("face nearest")
    time.sleep(0.1)
    c0 = counts()
    # +-0.07 m at the hand and a +-0.05 m vertical flick, 6 Hz, for 1 s, in front at the enemy's distance
    seg = []
    for i in range(6):
        seg += ["0.14 0.05 0", "-0.14 -0.05 0"]
    sim("hand r 0.18 1.35 -0.45 0 5 0; path r " + " ".join(seg) + " 1000")
    time.sleep(0.2)
    c1 = counts()
    reset_pose()
    result("waggle", c1["hits"] == c0["hits"], "6 Hz waggle for 1 s at %.2f m: hits +%d, legal swings +%d, slow touches +%d" % (
        e["dist"], c1["hits"] - c0["hits"], c1["swings"] - c0["swings"], c1["illegal"] - c0["illegal"]))


def part_slow():
    st = strike()
    if st is None:
        return result("slow", False, "no attacker")
    send("face nearest")
    time.sleep(0.1)
    c0 = counts()
    sim("hand r 0.6 1.35 -0.4 0 5 0; wait 100; move r -1.2 0 -0.1 1200")
    time.sleep(0.2)
    c1 = counts()
    reset_pose()
    result("slow", c1["hits"] == c0["hits"], "a 1 m/s push through the enemy: hits +%d, slow touches +%d" % (c1["hits"] - c0["hits"], c1["illegal"] - c0["illegal"]))


def guard_to(dx=0.0, dy=0.0, dz=0.0, ms=80, shield=True):
    g = send("guardat %s %.3f %.3f %.3f" % ("shield" if shield else "blade", dx, dy, dz))
    m = re.match(r"(move [lr] \S+ \S+ \S+)", g)
    if not m:
        return None
    sim(m.group(1) + " %d" % ms)
    return g


def wait_outcome(c0, keys, timeout=4.0):
    t0 = time.time()
    while time.time() - t0 < timeout:
        c = counts()
        for k in keys:
            if c.get(k, 0) > c0.get(k, 0):
                return k, c
        time.sleep(0.1)
    return None, counts()


def part_block():
    for _ in range(4):
        reset_pose()
        st = strike(timeout=25, fresh=0.7)
        if st is None:
            break
        text, eta, colour = st
        if eta < 0.6 or colour == "Red":
            time.sleep(eta + 0.5)
            continue
        c0 = counts()
        g = guard_to(ms=60)
        what, c1 = wait_outcome(c0, ["blocks", "counters", "hitsTaken", "dodges", "outOfRange"])
        if what in ("blocks", "hitsTaken"):
            return result("block", what == "blocks" and c1["pushes"] > c0["pushes"],
                          "%s strike, eta %.2f s, shield on the marker early -> %s; attacker pushed +%d | %s" % (colour, eta, what, c1["pushes"] - c0["pushes"], g[:60]))
        log("  block: outcome " + str(what))
    result("block", False, "no blockable strike was blocked in 4 tries")


def part_bladeguard():
    """The weapon (no shield needed) laid across the marker blocks too: upright for a side swing, level for a thrust."""
    for _ in range(5):
        reset_pose()
        # the shield out of the way; the sword held upright: turned from pointing ahead to level it swept through a thug
        # standing close for its thrust, and that hit ended the strike before the guard (P2T8, 5 tries in a row)
        sim("hand l -0.3 0.9 -0.1 0 -60 0; rot r 0 70 0")
        st = strike(timeout=25, fresh=0.8)
        if st is None:
            break
        text, eta, colour = st
        if colour == "Red":
            time.sleep(max(0.0, eta) + 0.4)
            continue
        g = send("guardat blade")
        m = re.search(r"axis \((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\)", g)
        upright = m is not None and abs(float(m.group(2))) > 0.7
        # the blade along the marker: pointing up (pitch 70 + the grip's tilt) or level to the left (yaw 90)
        sim("rot r 0 70 0" if upright else "rot r 90 -20 0")
        time.sleep(0.1)
        c0 = counts()
        g = guard_to(ms=60, shield=False)
        time.sleep(0.12)
        log("  bladeguard: after the move: %s | %s" % (send("strike?")[:150], send("blade?")[:150]))
        what, c1 = wait_outcome(c0, ["blocks", "counters", "hitsTaken", "dodges", "outOfRange"])
        if what in ("blocks", "counters"):
            return result("bladeguard", True, "%s strike, the sword laid %s across the marker -> %s | %s" % (colour, "upright" if upright else "level", what, (g or "")[:50]))
        log("  bladeguard: outcome %s (%s)" % (what, "upright" if upright else "level"))
    result("bladeguard", False, "the sword on the marker never blocked in 5 tries")


def part_counter():
    send("vrcounter meet")   # Stage 1: UYF has no counter (Counter = Off); the earlier rule is the setting Meet
    try:
        return part_counter_meet()
    finally:
        send("vrcounter setting")


def part_counter_meet():
    for _ in range(5):
        reset_pose()
        st = strike(timeout=25, fresh=0.8)
        if st is None:
            break
        text, eta, colour = st
        if colour != "Gold" or eta < 0.7:
            time.sleep(max(0.0, eta) + 0.5)
            continue
        t0 = time.time()
        guard_to(0, -0.3, 0, 60)                 # held low, off the marker
        c0 = counts()
        wait = eta - (time.time() - t0) - 0.28    # then up into the blow in its last 0.28 s
        if wait > 0:
            time.sleep(wait)
        sim("move l 0 0.3 0 110")
        what, c1 = wait_outcome(c0, ["counters", "blocks", "hitsTaken", "dodges", "outOfRange"])
        if what == "counters":
            tc = time.time()
            # the riposte: at the countered attacker within 2 s (real time, from 0.15 s after the counter) - turn to IT (not
            # the nearest: shield fixtures crowd the player with soldiers and a phalanx); the counter's push often leaves it
            # out of sword reach, and the homing dash toward it closes the distance
            send("face riposte")
            # the shield lowered first: a dash carries a shield held in front into the enemy as a bash (the dash's speed
            # counts), and a bash knocks it back out of reach
            sim("hand l -0.45 0.8 0.2 0 0 0")
            e = enemy("riposte")
            e2 = e
            how = "in reach"
            if e2 and e2["dist"] > 1.5:
                # a player's flick: the stick pushed, A tapped, the stick let go at once - the dash begins only when the
                # game's dodge does (0.2-0.3 s after the press in the counter's slow motion) and must keep the pushed way
                sim("stick l 0 1 0; wait 80; btn r a 80; wait 100; stick l 0 0 0")
                time.sleep(0.45)
                send("face riposte")
                e2 = enemy("riposte")
                how = "dashed"
            if e2 and e2["dist"] > 1.5:
                send("heroat riposte 0 0.9")   # the test's last resort (logged): the riposte itself is what is checked
                time.sleep(0.15)
                send("face riposte")
                e2 = enemy("riposte")
                how = "placed"
            c2 = counts()
            ts = time.time() - tc
            sim("swing r left 90")
            time.sleep(0.4)
            c3 = counts()
            log("  riposte: the attacker at %s m, then %s m (%s), the swing %.2f s after the counter was seen" % (
                e["dist"] if e else "?", e2["dist"] if e2 else "?", how, ts))
            return result("counter", c3["earnedSlow"] > c0["earnedSlow"] and c3["riposte"] > c0["riposte"],
                          "gold strike, eta %.2f s, the shield met the blow -> countered; earned slow-motion frames +%d; riposte swing: ripostes +%d, hits +%d" % (
                              eta, c3["earnedSlow"] - c0["earnedSlow"], c3["riposte"] - c0["riposte"], c3["hits"] - c2["hits"]))
        log("  counter: outcome " + str(what) + " (eta %.2f)" % eta)
    result("counter", False, "no counter in 5 tries")


def part_dodge(kind="dodge"):
    if kind == "dodge":
        send("vrlean on")   # Stage 1: UYF dodges only along the arrow of an unblockable blow (stage1_test.py); the lean is a setting
    try:
        return part_dodge_run(kind)
    finally:
        send("vrlean setting")


def part_dodge_run(kind="dodge"):
    for _ in range(4):
        reset_pose()
        st = strike(timeout=25, fresh=0.8)
        if st is None:
            break
        t_read = time.time()
        text, eta, colour = st
        if eta < 0.6:
            time.sleep(eta + 0.5)
            continue
        # straight back = along the strike's line (a step at an angle is also a sidestep); the striker, not the nearest
        # thief: a sideways lean toward a striker off to one side is partly along its line (MRap: hit)
        send("face striker")
        sim("hand r 0.3 0.9 -0.1 0 -60 0; hand l -0.3 0.9 -0.1 0 -60 0")   # hands down: no guard
        c0 = counts()
        # 0.45 s before the blow, counted from when its eta was read (the calls since took 0.2-0.4 s: MRjp leaned at the impact)
        time.sleep(max(0.0, eta - (time.time() - t_read) - 0.45))
        # reach: 2.4 m back (a thief's run attack lunges ~1 m after the player; 1.4 m was caught - stage2's sector part too)
        sim("head 0.3 1.7 0 0 0 0" if kind == "dodge" else "head 0 1.7 2.4 0 0 0")
        what, c1 = wait_outcome(c0, ["dodges", "outOfRange", "blocks", "counters", "hitsTaken"])
        sim(HEAD)
        want = "dodges" if kind == "dodge" else "outOfRange"
        if what in (want, "hitsTaken"):
            return result(kind, what == want, "%s strike, the head %s before the impact -> %s" % (colour, "0.3 m sideways" if kind == "dodge" else "2.4 m back", what))
        log("  %s: outcome %s" % (kind, what))
    result(kind, False, "no clear outcome in 4 tries")


def part_relaxed():
    """Pace Relaxed: every in-view strike's lead >= 1.5 s (1.2 x 1.25)."""
    send("vrpace relaxed")
    send("pcombat? reset")
    n = 0
    t0 = time.time()
    while time.time() - t0 < 40 and n < 3:
        st = strike(timeout=20, fresh=0.0)
        if st is None:
            break
        n += 1
        time.sleep(max(0.0, st[1]) + 0.6)
    c = counts()
    send("vrpace setting")
    result("relaxed", n >= 1 and c["minLead"] >= 1.45 and c["leadShort"] == 0, "Pace Relaxed: %d strikes, shortest in-view lead %.2f s (needs 1.50)" % (n, c["minLead"]))


def part_pace():
    """Never two live strikes; every in-view strike's lead at least its floor (`leadShort` 0); the shortest lead seen at
    least the thieves' floor - read from the enemy table (Stage 2: UYF's lead x 1.2 = 1.20 s; with the difficulty ladder
    UYF's own at Normal = 1.00 s)."""
    c = counts()
    t = send("enemytable? ThievesThug")
    m = re.search(r"lead ([\d.]+)", t)
    floor = float(m.group(1)) if m else 1.2
    ok = c["maxLive"] <= 1 and c["leadShort"] == 0 and c["minLead"] >= floor - 0.1
    result("pace", ok, "max live strikes %d, shortest in-view lead %.2f s (the thieves' floor %.2f), short leads %d, held starts %d, swap holds %d" % (
        c["maxLive"], c["minLead"], floor, c["leadShort"], c["held"], c["swaps"]))


def part_feedback():
    """Slow motion only when earned; the red screen edge never drawn; the hit shown in the world (a burst); layered
    sounds. The hit it checks is arranged, not left to luck: one blow let through on purpose - both hands down at the
    sides (a blade held in front guards too), the head still, the attacker turned to - and the burst and the skipped red
    edge must come with that blow."""
    hit = None
    for _ in range(6):
        st = strike(timeout=25)
        if st is None:
            break
        text, eta, colour = st
        c0 = counts()
        sim(HEAD + "; hand r 0.42 0.85 0.12 0 -60 0; hand l -0.42 0.85 0.12 0 -60 0")
        send("face striker")
        what, c1 = wait_outcome(c0, ["hitsTaken", "blocks", "dodges", "outOfRange", "counters"], timeout=max(2.5, eta + 1.5))
        if what == "hitsTaken":
            time.sleep(0.3)
            hit = (text.split(" ")[0], c0, counts())
            break
        log("  feedback: the blow let through came out %s (%s strike, eta %.2f)" % (what, colour, eta))
        if what == "outOfRange":
            # it struck from the edge of its reach: stand in front of it for the next one (seam heroat, where present)
            send("heroat %s 0 1.0" % text.split(" ")[0])
        time.sleep(0.5)
    reset_pose()
    c = counts()
    if hit is None:
        return result("feedback", False, "no blow let through in 6 tries (nothing to check) | unearned slow-motion frames %d" % c["unearnedSlow"])
    who, a, b = hit
    ok = (c["unearnedSlow"] == 0 and b["vignettes"] > a["vignettes"] and b["bursts"] > a["bursts"] and c["layers"] >= 1)
    result("feedback", ok, "a blow let through (%s): red screen edge not drawn +%d, hit burst +%d; over the run: slow motion earned frames %d, "
                           "unearned %d; layered sounds %d; hit-stops %d" % (
                               who, b["vignettes"] - a["vignettes"], b["bursts"] - a["bursts"], c["earnedSlow"], c["unearnedSlow"], c["layers"], c["stops"]))


def gold_pixels(path):
    """Marker-gold pixels in the left and right eye halves of an eyes shot (the gold marker is #FFC71F-ish)."""
    try:
        from PIL import Image
        im = Image.open(path).convert("RGB")
        w, h = im.size
        px = im.load()
        n = [0, 0]
        for y in range(0, h, 2):
            for x in range(0, w, 2):
                r, g, b = px[x, y]
                # gold, also pale where the bar lies over snow: yellow-orange (r > g > b), never white, sky or green;
                # since Stage 1 the block bar is cyan (0, 0.78, 1) with a white core when the guard is on it
                gold = r > 200 and 140 < g < 240 and b < 165 and r - b > 80 and 5 < r - g < 80
                cyan = b > 190 and g > 140 and r < 140 and b - r > 90
                if gold or cyan:
                    n[0 if x < w // 2 else 1] += 1
        return n
    except Exception as e:  # noqa: BLE001
        log("  shot: " + repr(e))
        return [-1, -1]


def part_shot():
    """Both eyes: a strike's marker in view (hands down, facing the attacker), then with the shield on it (the lock-in).
    The gold marker must show in both eyes."""
    reset_pose()
    sim("hand r 0.3 0.9 -0.1 0 -60 0; hand l -0.3 0.9 -0.1 0 -60 0")
    for _ in range(4):
        st = strike(timeout=25, fresh=0.7)
        if st is None:
            break
        if st[2] == "Red":
            time.sleep(max(0.0, st[1]) + 0.4)
            continue
        send("face striker")
        time.sleep(0.12)
        r1 = send("shot %s_marker" % TAG, 15)
        time.sleep(1.0)
        g1 = gold_pixels(H + "/testdata/shots/%s_marker_eyes.png" % TAG)
        st2 = strike(timeout=25, fresh=0.6)
        g2 = [0, 0]
        if st2:
            send("face striker")
            guard_to(ms=60)
            time.sleep(0.15)
            send("shot %s_marker_guard" % TAG, 15)
            time.sleep(1.0)
            g2 = gold_pixels(H + "/testdata/shots/%s_marker_guard_eyes.png" % TAG)
        reset_pose()
        return result("shot", min(g1) >= 40, "testdata/shots/%s_marker_eyes.png: gold marker pixels left/right eye %s; with the shield on it (%s_marker_guard): %s" % (
            TAG, g1, TAG, g2))
    result("shot", False, "no blockable strike to photograph")


def part_lastkill():
    send("vrdamage everyhit")   # Stage 2: under Damage = Crits only crits take health; this part checks the last kill's beat
    c0 = counts()
    t0 = time.time()
    while time.time() - t0 < 60:
        e = enemy()
        if e is None:
            break
        send("bladetest nearest big")
        time.sleep(0.4)
    c1 = counts()
    send("vrdamage setting")
    result("lastkill", c1["lastKills"] > c0["lastKills"] and c1["slowmos"] > c0["slowmos"],
           "the last kill: lastKills +%d, slow motion +%d (%s)" % (c1["lastKills"] - c0["lastKills"], c1["slowmos"] - c0["slowmos"], send("fight?")))


def approach(who):
    """Face that enemy and, if it is out of blade reach, dash at it (the homing dash stops at blade range)."""
    send("face " + who)
    e = enemy(who)
    if e and e["dist"] > 1.5:
        sim("stick l 0 1 0; wait 80; btn r a 80; wait 100; stick l 0 0 0")
        time.sleep(0.35)
        send("face " + who)
    return enemy(who)


def part_armour():
    """Quick real hits on armour are the game's flurry hits: the armour drops (no health damage until it breaks)."""
    e = enemy("NorseTank")
    if e is None:
        return result("armour", False, "no armoured enemy")
    a0 = e["armour"]
    c0 = counts()
    for _ in range(5):
        approach("NorseTank")
        sim("swing r left 90")
        time.sleep(0.5)
    reset_pose()
    c1 = counts()
    e1 = enemy("NorseTank")
    real = c1["flurry"] - c0["flurry"]
    a1 = e1["armour"] if e1 else "?"
    for _ in range(12):
        send("bladetest NorseTank slash")
        time.sleep(0.25)
    c2 = counts()
    e2 = enemy("NorseTank")
    result("armour", real >= 1 and c2["flurry"] > c1["flurry"] and e2 is not None and e2["armour"] != a0,
           "real swings at the armoured attackers: flurry hits +%d (armour %s -> %s); 12 quick seam hits: flurry +%d, armour -> %s" % (
               real, a0, a1, c2["flurry"] - c1["flurry"], e2["armour"] if e2 else "?"))


def part_red():
    """A red (unblockable) strike: a guard on its marker does not block it; the head moving out of the way does."""
    got = {}
    for _ in range(6):
        reset_pose()
        send("face CorruptedTank")
        st = strike(timeout=25, fresh=0.5)
        if st is None:
            break
        text, eta, colour = st
        if colour != "Red":
            time.sleep(max(0.0, eta) + 0.4)
            send("face CorruptedTank")   # red strikes from behind wait until the player faces their attacker (Q3)
            continue
        c0 = counts()
        guard_to(ms=60)
        what, c1 = wait_outcome(c0, ["blocks", "counters", "hitsTaken", "dodges", "outOfRange"])
        got[what] = got.get(what, 0) + 1
        if what == "hitsTaken":
            break
    result("red", got.get("hitsTaken", 0) >= 1 and got.get("blocks", 0) == 0 and got.get("counters", 0) == 0,
           "red strikes with the shield on the marker: %s (never blocked or countered)" % got)


def part_bash():
    c0 = counts()
    r = send("bladetest nearest bash")
    c1 = counts()
    # the real thing: drive the shield face-first into an enemy in reach (the dash brings the player there; no strike
    # needs to be live - the bash rule is the shield's speed into a body)
    real = 0
    for _ in range(5):
        e = approach("nearest")
        if e is None or e["dist"] > 1.6:
            time.sleep(0.5)
            continue
        time.sleep(0.1)
        c2 = counts()
        sim("hand l -0.1 1.3 -0.25 0 0 0; wait 80; move l 0.05 0 -0.65 180")
        time.sleep(0.3)
        c3 = counts()
        reset_pose()
        real += int(c3["bash"] - c2["bash"])
        if real:
            break
    result("bash", c1["bash"] > c0["bash"] and real >= 1, "seam bash +%d (%s); shield driven into an attacker: bashes +%d" % (c1["bash"] - c0["bash"], r[:70], real))


def part_shield():
    """Shield bearers: a phalanx holds its shield up, soldiers raise theirs after a few hits (the game's hit-count guard:
    2-5 hits on the same soldier, up for 3 s, unless it holds the attack slot and counter-attacks): a blade on a raised
    shield is blocked (a clank, no damage). Swings go at the phalanx while its shield is up, else the nearest soldier."""
    c0 = counts()
    swings = 0
    blocking_seen = 0
    t0 = time.time()
    while time.time() - t0 < 60 and swings < 16:
        # a raised shield first: the phalanx holds its up; else the nearest soldier (its guard comes up after a few hits)
        p = enemy("Phalanx")
        who = "Phalanx" if p and p["blocking"] else "Soldier"
        e = approach(who) or approach("Phalanx")
        if e is None:
            break
        if e["blocking"]:
            blocking_seen += 1
        sim("swing r left 90")
        swings += 1
        time.sleep(0.6)
        if counts()["blocked"] > c0["blocked"]:
            break
    reset_pose()
    c1 = counts()
    result("shield", c1["blocked"] > c0["blocked"], "%d real swings at shield bearers (raised guard seen %d times): hits +%d, blocked by the shield +%d" % (
        swings, blocking_seen, c1["hits"] - c0["hits"], c1["blocked"] - c0["blocked"]))


def part_behind():
    """Attackers that land behind (assassins): every strike from outside the view comes with the warning (announcement,
    sting, heartbeat, edge marker); the shield held out toward the attacker, without turning, blocks it."""
    send("warn? reset")
    c0 = counts()
    got = {}
    tries = 0
    t0 = time.time()
    while time.time() - t0 < 90 and tries < 6:
        sim(HEAD + "; " + HAND_R + "; " + HAND_L)
        st = strike(timeout=20, inview=False, fresh=0.5)
        if st is None:
            break
        text, eta, colour = st
        if "offview" not in text:
            time.sleep(max(0.0, eta) + 0.4)
            send("face nearest")
            sim("head 0 1.7 0 180 0 0")   # look away from the fight again
            continue
        tries += 1
        guard_to(ms=80)          # held out toward the attacker's side (the marker's spot, 0.25-0.5 m out), no turn
        what, c1 = wait_outcome(c0 if not got else counts(), ["blocks", "counters", "hitsTaken", "dodges", "outOfRange"])
        got[what] = got.get(what, 0) + 1
        time.sleep(0.5)
    sim(HEAD)
    c1 = counts()
    w = send("warn?")
    stings = int(re.search(r"stings (\d+)", w).group(1)) if re.search(r"stings (\d+)", w) else 0
    beats = int(re.search(r"heartbeats (\d+)", w).group(1)) if re.search(r"heartbeats (\d+)", w) else 0
    marks = int(re.search(r"markers (\d+)", w).group(1)) if re.search(r"markers (\d+)", w) else 0
    ok = tries >= 1 and got.get("blocks", 0) + got.get("counters", 0) >= 1 and stings >= 1 and beats >= 1 and marks >= 1
    result("behind", ok, "%d strikes from outside the view; with the shield held out toward them: %s; warnings: stings %d, heartbeats %d, edge markers %d" % (
        tries, got, stings, beats, marks))


def part_snare():
    """Snared (a bolas): shaking both controllers struggles free (each shake = the game's struggle press)."""
    send("god")
    t0 = time.time()
    snared = False
    while time.time() - t0 < 90:
        if "snared True" in send("pcombat?"):
            snared = True
            break
        e = enemy()
        if e and e["dist"] > 4:
            send("face nearest")
        time.sleep(0.3)
    if not snared:
        return result("snare", False, "the hero was not snared in 90 s")
    c0 = counts()
    t1 = time.time()
    seg = " ".join(["0 0.25 0", "0 -0.25 0"] * 8)
    sim("path l %s 1600; path r %s 1600" % (seg, seg))
    while time.time() - t1 < 10 and "snared True" in send("pcombat?"):
        sim("path l %s 1000" % seg)
    held = time.time() - t1
    c1 = counts()
    reset_pose()
    result("snare", c1["struggles"] > c0["struggles"] and held < 10, "snared; shaking the controllers: struggles +%d, free after %.1f s" % (c1["struggles"] - c0["struggles"], held))


def part_finisher():
    """A downed enemy (the game's knockdown, as heavy hits do): any real hit on it is the finisher."""
    c0 = counts()
    for _ in range(4):
        e = approach("nearest")
        if e is None:
            break
        k = send("knockdown nearest")
        for _ in range(15):   # until its tree lies it down (the game's prone action)
            time.sleep(0.1)
            e = enemy()
            if e and e["prone"]:
                break
        if not e or not e["prone"]:
            log("  finisher: " + k)
            continue
        send("face nearest")
        sim("swing r down 120")
        time.sleep(0.6)
        c1 = counts()
        if c1["finisher"] > c0["finisher"]:
            return result("finisher", True, "%s; a real swing at it: finishers +%d, slow motion earned frames +%d | %s" % (
                k, c1["finisher"] - c0["finisher"], c1["earnedSlow"] - c0["earnedSlow"], re.search(r"last ([^|]*)\|", c1["_raw"]).group(1).strip()))
        log("  finisher: swing missed (%s)" % c1["_raw"][:120])
    # the knockdown seam sets the game's prone state but its tree may not lie the enemy down: the finisher outcome itself
    # (the game's finisher attack, its slow motion) through the seam
    e0 = enemy()
    k = send("knockdown nearest")
    r = send("bladetest nearest finisher")
    time.sleep(0.5)
    c2 = counts()
    e1 = enemy()
    result("finisher", c2["finisher"] > c0["finisher"], "real swings on a downed enemy: none was lying down in time (the seam's knockdown sets only the "
           "prone state); the finisher outcome through the seam: %s -> finishers +%d, enemy hp %s -> %s" % (
               r[:60], c2["finisher"] - c0["finisher"], e0["hp"] if e0 else "?", e1["hp"] if e1 else "dead"))


THIEVES = ["mode", "swing", "big", "fast", "waggle", "slow", "block", "bladeguard", "counter", "dodge", "reach", "finisher", "shot", "pace", "feedback", "lastkill"]
FIXTURE_PARTS = {"armour": "armour", "bash": "shield", "shield": "shield", "behind": "behind"}


def main():
    parts = args[args.index("--parts") + 1].split(",") if "--parts" in args else THIEVES
    fixture = args[args.index("--fixture") + 1] if "--fixture" in args else "thieves"
    if "--no-fixture" not in args:
        r = subprocess.run(["python3", H + "/scripts/combat_fixture.py", "--fixture", fixture, "--god"] + (["--boot"] if "--boot" in args or "GameState_Map" not in send("state?") else []), capture_output=True, text=True)
        log("fixture %s: %s" % (fixture, (r.stdout.strip().splitlines() or ["?"])[-1]))
    send("god")
    send("vrcombat physical")
    send("pcombat? reset")
    reset_pose()
    time.sleep(1.0)
    fns = {"mode": part_mode, "bladeguard": part_bladeguard, "swing": part_swing, "big": part_big, "fast": part_fast, "waggle": part_waggle, "slow": part_slow,
           "block": part_block, "counter": part_counter, "dodge": lambda: part_dodge("dodge"), "reach": lambda: part_dodge("reach"),
           "pace": part_pace, "feedback": part_feedback, "shot": part_shot, "lastkill": part_lastkill, "armour": part_armour,
           "bash": part_bash, "shield": part_shield, "behind": part_behind, "red": part_red,
           "snare": part_snare, "finisher": part_finisher, "relaxed": part_relaxed}
    for p in parts:
        send("tough " + ("off" if p in ("lastkill", "armour", "finisher") else "on"))   # armour ignores hits on an invulnerable enemy
        if send("state?").find("Level_Play") < 0:
            result(p, False, "not in a fight (" + send("state?") + ")")
            continue
        try:
            fns[p]()
        except Exception as e:  # noqa: BLE001
            result(p, False, "error: %r" % e)
    send("pcombat? log")
    if "--stay" not in args and "--no-fixture" not in args:
        import indicator_shots
        log("left the fight: %s %s" % (indicator_shots.leave(), send("state?")[:60]))
    n_ok = sum(1 for _, ok in results if ok)
    log("phys_test %s: %d/%d passed" % (TAG, n_ok, len(results)))
    return 0 if n_ok == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
