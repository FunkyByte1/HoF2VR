#!/usr/bin/env python3
"""Automated checks for the physical table interactions (docs/interaction-design.md, phases 1-2).

  interact_test.py [test ...]        default: all except `fairness`
Tests (each starts the game's own mini-game task through the `mg` seam, from the dungeon map):
  stop_seam      pendulum stopped through the stop path (seam, no motion)
  too_early      a stop inside the game's own 0.2 s guard is refused (double tick), the pendulum swings on
  slap           pendulum: simulated hand slaps the table (xrsim motion) -> stopped by slap
  slap_slow      pendulum: the hand sinks slowly through the table top -> NOT stopped; then grip stops it
  grip           pendulum: a grip squeeze -> stopped by grip
  wheel_slap     wheel: slap -> stopped
  wheel_grip     wheel: grip -> stopped
  wheel_nudge    wheel with one nudge: stop, the game's nudge dialogue, a sideways flick picks "nudge left"
  dice_seam      dice: in-process throw 0 0.3 1.8 m/s -> strength ~0.67, faces read by the game
  dice_drop      dice: release without speed -> the game's no-force roll
  dice_cone      dice: throw backwards -> direction clamped, dice in the tray
  dice_reach     dice: hand to the dice, grip, shake, fling (xrsim) -> thrown, landed
  dice_summon    dice: hand away from the dice, point at them, grip -> summoned; fling
  dice_trigger   dice: the trigger draw fallback (hold trigger, release)
  dice_reroll    dice: target out of reach: throw, mark a die (laser), Reroll (X), throw again
  dice_classic   DiceThrow = Classic: a grip grabs nothing, the trigger draw throws
  touch_map_neg  map: far tile refused; swipe / slow push / graze / hover select nothing (seam fingertip)
  dice_grab2     hand feel Phase 2: an open hand at the dice is ready; grip 0.45 keeps them; a peaked throw is read at its peak
  figurine_real  P2-2: the fingertip at the figurine, a pinch / squeeze / trigger ramped up as a hand does -> picked up, glides
                 back (also in the challenge's opening encounter)
  figurine_drop  playtest 3, mark 9: wherever the map takes moves (also a challenge's opening): set down on the next place and
                 just beside it -> the hero goes there (leaves an encounter open)
  card_inspect   hand feel Phase 2: in the inventory, a grip at a touched card -> the game's close-up; open the hand -> back
  dice_multi     P2-3: real taps from above mark 2, then 3 dice, one unmarked again, Re-roll, the marked dice thrown again
  touch_mark     dice marking: tap a resting die -> marked, again -> unmarked (seam fingertip)
  touch_wheel    wheel: a fingertip held into the spinning cards -> stopped (seam fingertip)
  touch_chance   chance cards: tap while shuffling -> refused; after -> picked (seam fingertip)
  touch_chance_e2e  chance cards: the simulated hand pokes a presented card -> picked
  card_flip      feature A: a face-down chance card picked up and let go unturned -> back, nothing chosen; picked up and
                 turned over in the hand -> the game reveals it (the pick)
  touch_map_e2e  map: the simulated hand pokes a movable tile -> the hero walks there (run it last: an
                 encounter follows)
  fairness [N]   dice: N identical in-process throws (default 30) -> face histogram
Run from the dungeon map of a test session:
  python3 scripts/session.py start --to table --snapshot dice
Results: PASS/FAIL lines on stdout and in logs/interact_test.log.
"""
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cmd  # noqa: E402

import hof2env  # noqa: E402  (this worktree's test environment, docs/two-environments.md)

H = hof2env.H
LOG = H + "/logs/interact_test.log"
RESULTS = []


def log(msg):
    line = time.strftime("%H:%M:%S ") + msg
    print(line, flush=True)
    with open(LOG, "a") as f:
        f.write(line + "\n")


def sim(c):
    r = subprocess.run(["python3", H + "/scripts/xrsim.py", c], capture_output=True, text=True)
    if r.returncode != 0:
        log("  xrsim error: " + (r.stdout + r.stderr).strip())
    return r.stdout.strip()


def send(c, t=8):
    try:
        return cmd.send(c, t)
    except SystemExit as e:
        return "ERR " + str(e)


def st():
    for _ in range(5):
        try:
            return cmd.status()
        except Exception:
            time.sleep(0.2)
    return {}


def wait(pred, timeout, step=0.25):
    deadline = time.time() + timeout
    s = st()
    while time.time() < deadline:
        s = st()
        try:
            if pred(s):
                return s
        except Exception:
            pass
        time.sleep(step)
    return None


def result(name, ok, evidence):
    RESULTS.append((name, ok))
    log(("PASS " if ok else "FAIL ") + name + " - " + evidence)


def num(s, key):
    m = re.search(key + r" (\d+)", s or "")
    return int(m.group(1)) if m else -1


def at_map():
    s = st()
    return s.get("state") == "GameState_Map" and s.get("menu") == "Map" and "running" not in s.get("mg", "")


def ensure_map(timeout=40):
    if wait(lambda s: s.get("state") == "GameState_Map" and s.get("menu") == "Map" and "running" not in s.get("mg", ""), timeout):
        return True
    log("  not back on the map: %s / %s / %s" % (st().get("state"), st().get("menu"), st().get("mg")))
    return False


def rest_hands():
    """Hands low at the sides, pointing ahead (nothing hovered, below the table)."""
    sim("hand r 0.2 0.85 -0.3 0 0 0; hand l -0.2 0.85 -0.3 0 0 0; hold r grip 0; hold l grip 0; hold r trigger 0; hold l trigger 0")


def point_over_table():
    """The right hand over the table's near edge, pointing down and ahead, before a laser click at a die (candidate 11.1: from
    rest_hands' pose, 0.85 m - under the table top, 0.99 m at PlayerSize 1.0 - the laser's line up to a die that lay among
    the map's cards met the map's card (4, 2) first: "pointer: (4, 2) is not selectable right now", marked=0, in all three
    runs where it failed; env 3's dbb349b had this, 0cfb909 reverted it after an A/B that passed both ways). IT_NOPOINT=1:
    the old pose."""
    if os.environ.get("IT_NOPOINT") == "1":
        return
    sim("hand r 0.15 1.25 -0.3 0 -45 0")
    time.sleep(0.4)


def start_mg(kind, ready, timeout=30):
    r = send("mg " + kind)
    if not r.startswith("started"):
        log("  mg %s: %s" % (kind, r))
        return None
    return wait(ready, timeout)


def pendulum_ready(s):
    return s.get("pendulum", "").startswith("swinging")


def wheel_ready(s):
    return s.get("wheel", "").startswith("Looping")


def dice_ready(s):
    return s.get("dice", "").startswith("Catapault")


def finish_task(timeout=40):
    """Let the running seam task end (press Continue where the game waits for it)."""
    deadline = time.time() + timeout
    while time.time() < deadline:
        s = st()
        if "running" not in s.get("mg", ""):
            return True
        d = s.get("dice", "")
        if d.startswith("Ready") and "landed" in d:
            send("press A 6")
        elif d.startswith("Marking"):            # rolls left: reroll one die, then throw it
            point_over_table()                   # (the laser's line from under the table met a map card: see there)
            send("world click PlayerDiceRoller/Container/D6")
            time.sleep(1.0)
            send("press X 6")
        elif d.startswith("Catapault"):
            send("dice throw 0 0.3 1.8 r game")
        if s.get("pendulum", "").startswith("swinging") or s.get("wheel", "").startswith("Looping"):
            send("stop r grip")
        time.sleep(1.0)
    log("  task still running: " + st().get("mg", ""))
    return False


def hand_to(side, point, target, lift=0.0, ms=300):
    r = send("handto %s %s %s" % (side, point, target))
    if not r.startswith("move"):
        log("  handto: " + r)
        return False
    p = r.split()
    dx, dy, dz = float(p[2]), float(p[3]) + lift, float(p[4])
    sim("move %s %.4f %.4f %.4f %d" % (side, dx, dy, dz, ms))
    return True


# ---- pendulum / wheel ------------------------------------------------------------------------

def stops():
    return num(st().get("stops", ""), r"^")


def stop_count():
    m = re.match(r"(\d+)", st().get("stops", ""))
    return int(m.group(1)) if m else -1


def t_stop_seam():
    ensure_map()
    s = start_mg("pendulum", pendulum_ready)
    if not s:
        return result("stop_seam", False, "pendulum never swung: " + str(st().get("pendulum")))
    time.sleep(1.0)
    n0 = stop_count()
    r = send("stop r grip")
    s = wait(lambda s: s.get("pendulum", "").startswith("stopped"), 3)
    ok = r.startswith("stopped") and s is not None and stop_count() == n0 + 1
    result("stop_seam", ok, "stop -> %s; pendulum %s; stops %s" % (r, st().get("pendulum"), st().get("stops")))
    finish_task()


def t_too_early():
    ensure_map()
    r0 = re.search(r"refused (\d+)", st().get("stops", ""))
    r0 = int(r0.group(1)) if r0 else 0
    send("stop early")
    s = start_mg("pendulum", pendulum_ready)
    time.sleep(1.5)
    r1 = re.search(r"refused (\d+)", st().get("stops", ""))
    r1 = int(r1.group(1)) if r1 else 0
    still = st().get("pendulum", "").startswith("swinging")
    sim("btn r grip 200")
    s2 = wait(lambda s: s.get("pendulum", "").startswith("stopped"), 3)
    result("too_early", s is not None and r1 == r0 + 1 and still and s2 is not None,
           "a stop on the first armed frame: refused %d -> %d, still swinging %s; later grip -> %s" % (r0, r1, still, st().get("pendulum")))
    finish_task()


def slap_motion(side="r", speed_ms=150, drop=0.40, lift=0.25):
    """Palm `lift` m above the table point, then down `drop` m in `speed_ms` ms, then back up."""
    if not hand_to(side, "palm", "table", lift=lift, ms=400):
        return False
    time.sleep(0.4)
    sim("move %s 0 %.3f 0 %d" % (side, -drop, speed_ms))
    time.sleep(0.3)
    sim("move %s 0 %.3f 0 400" % (side, drop))
    return True


def t_slap():
    ensure_map()
    rest_hands()
    if not start_mg("pendulum", pendulum_ready):
        return result("slap", False, "pendulum never swung")
    time.sleep(1.0)
    n0 = stop_count()
    slap_motion("r", 150)
    s = wait(lambda s: s.get("pendulum", "").startswith("stopped"), 3)
    stp = st().get("stops", "")
    ok = s is not None and stop_count() == n0 + 1 and "slap" in stp
    result("slap", ok, "pendulum %s; stops %s; haptics %s" % (st().get("pendulum"), stp, st().get("haptics")))
    finish_task()
    rest_hands()


def t_slap_slow():
    ensure_map()
    rest_hands()
    if not start_mg("pendulum", pendulum_ready):
        return result("slap_slow", False, "pendulum never swung")
    time.sleep(1.0)
    n0 = stop_count()
    slap_motion("r", 1200)   # 0.33 m/s
    time.sleep(1.0)
    still = st().get("pendulum", "").startswith("swinging") and stop_count() == n0
    sim("btn l grip 200")
    s = wait(lambda s: s.get("pendulum", "").startswith("stopped"), 3)
    stp = st().get("stops", "")
    ok = still and s is not None and "grip" in stp
    result("slap_slow", ok, "after a slow sink: still swinging=%s; then grip -> %s; stops %s" % (still, st().get("pendulum"), stp))
    finish_task()
    rest_hands()


def t_grip():
    ensure_map()
    rest_hands()
    if not start_mg("pendulum", pendulum_ready):
        return result("grip", False, "pendulum never swung")
    time.sleep(1.5)
    n0 = stop_count()
    sim("btn r grip 200")
    s = wait(lambda s: s.get("pendulum", "").startswith("stopped"), 3)
    stp = st().get("stops", "")
    ok = s is not None and stop_count() == n0 + 1 and "grip" in stp
    result("grip", ok, "pendulum %s; stops %s" % (st().get("pendulum"), stp))
    finish_task()


def t_wheel(kind):
    name = "wheel_" + kind
    ensure_map()
    rest_hands()
    if not start_mg("wheel", wheel_ready, 40):
        return result(name, False, "wheel never reached full speed: " + str(st().get("wheel")))
    time.sleep(1.2)   # the game enables the stop 0.1-0.5 s after full speed
    n0 = stop_count()
    if kind == "slap":
        slap_motion("r", 150)
    else:
        sim("btn r grip 200")
    s = wait(lambda s: not s.get("wheel", "").startswith("Looping"), 3)
    stp = st().get("stops", "")
    ok = s is not None and stop_count() == n0 + 1 and kind in stp
    result(name, ok, "wheel %s; stops %s" % (st().get("wheel"), stp))
    finish_task(60)
    rest_hands()


def t_wheel_nudge():
    ensure_map()
    rest_hands()
    r = send("mg wheel Easy nudge")
    if not r.startswith("started") or not wait(wheel_ready, 40):
        return result("wheel_nudge", False, "wheel never reached full speed: %s %s" % (r, st().get("wheel")))
    send("mg nudge")   # the running wheel's nudge (the one given before the task was gone by the stop)
    time.sleep(1.2)
    sim("btn r grip 200")
    s = wait(lambda s: s.get("nudge", "-").startswith("open"), 20)
    if not s:
        return result("wheel_nudge", False, "no nudge dialogue: wheel %s nudge %s" % (st().get("wheel"), st().get("nudge")))
    options = s.get("nudge")
    before = st().get("wheel", "")
    time.sleep(0.8)
    m0 = re.search(r"nudges (\d+)", st().get("stops", ""))
    n0 = int(m0.group(1)) if m0 else 0
    sim("move r -0.25 0 0 200; wait 300; move r 0.25 0 0 600")   # flick left, come back slowly
    s2 = wait(lambda s: s.get("nudge", "-") == "-", 5)
    m1 = re.search(r"nudges (\d+)", st().get("stops", ""))
    n1 = int(m1.group(1)) if m1 else 0
    result("wheel_nudge", s2 is not None and n1 == n0 + 1, "dialogue %s; before %s; after the flick: stops %s, wheel %s" % (options, before, st().get("stops"), st().get("wheel")))
    finish_task(60)
    rest_hands()


# ---- dice ------------------------------------------------------------------------------------

def dice_status():
    return st().get("dice", "")


def landed_after(prev, timeout=12):
    return wait(lambda s: re.search(r"landed ([^|]*)", s.get("dice", "")).group(1) != prev and "rolls" in s.get("dice", ""), timeout)


def last_landed():
    m = re.search(r"landed ([^|]*)\|", dice_status())
    return m.group(1).strip() if m else "-"


def hand_rolls():
    m = re.search(r"rolls hand (\d+) classic (\d+)", dice_status())
    return (int(m.group(1)), int(m.group(2))) if m else (-1, -1)


def t_dice_seam(name="dice_seam", vel="0 0.3 1.8", expect=None):
    ensure_map()
    rest_hands()
    if not start_mg("dice 0", dice_ready):
        return result(name, False, "dice never waited for a throw: " + dice_status())
    time.sleep(0.5)
    before = hand_rolls()
    r = send("dice throw %s r game" % vel)
    s = wait(lambda s: hand_rolls()[0] == before[0] + 1, 15)
    d = dice_status()
    ok = s is not None and "on table" in d
    if expect:
        ok = ok and expect(d)
    m = re.search(r"on table (\d+)/(\d+)", d)
    if m and m.group(1) != m.group(2):
        ok = False
    result(name, ok, "%s -> %s" % (r, d))
    finish_task()


def t_dice_drop():
    t_dice_seam("dice_drop", "0 0 0.1", lambda d: "drop" in d)


def t_dice_cone():
    t_dice_seam("dice_cone", "0 0 -2", lambda d: "throw" in d)


def fling_and_check(name, side="r"):
    before = hand_rolls()
    thr0 = num(dice_status(), "throws")
    r0 = re.search(r"dice rattle=(\d+)", st().get("haptics", ""))
    r0 = int(r0.group(1)) if r0 else 0
    sim("path %s 0 0.06 0 0 -0.12 0 0 0.12 0 0 -0.12 0 0 0.06 0 500" % side)   # shake
    time.sleep(1.2)   # status.json is written once a second
    hap = st().get("haptics", "")
    sim("fling %s 0 0.10 -0.55 260 0.8" % side)                              # forward (-z) and a bit up
    s = wait(lambda s: hand_rolls()[0] == before[0] + 1, 15)
    d = dice_status()
    ok = s is not None and num(d, "throws") == thr0 + 1
    m = re.search(r"on table (\d+)/(\d+)", d)
    if m and m.group(1) != m.group(2):
        ok = False
    rattles = re.search(r"dice rattle=(\d+)", hap)
    rattles = int(rattles.group(1)) - r0 if rattles else 0
    ok = ok and rattles > 0
    result(name, ok, "%s | %d rattles in the shake | haptics %s" % (d, rattles, hap))
    return rattles > 0


def t_dice_reach():
    ensure_map()
    rest_hands()
    if not start_mg("dice 0", dice_ready):
        return result("dice_reach", False, "dice never waited")
    time.sleep(0.5)
    g0 = num(dice_status(), "grabs")
    hand_to("r", "palm", "dice", ms=500)
    time.sleep(0.4)
    send("log finger before grab: " + send("finger r"))
    sim("hold r grip 1")
    s = wait(lambda s: "held=R" in s.get("dice", ""), 3)
    if not s:
        sim("hold r grip 0")
        result("dice_reach", False, "no grab: " + dice_status() + " | " + send("finger r"))
        return finish_task()
    reached = "summoned" not in dice_status() or num(dice_status(), "grabs") == g0 + 1
    fling_and_check("dice_reach")
    finish_task()
    rest_hands()


def t_dice_summon():
    ensure_map()
    rest_hands()
    if not start_mg("dice 0", dice_ready):
        return result("dice_summon", False, "dice never waited")
    time.sleep(0.5)
    m0 = re.search(r"summoned (\d+)", dice_status())
    s0 = int(m0.group(1)) if m0 else 0
    # hand 0.45 m right of the dice and level with them, then aim the laser at them
    hand_to("r", "palm", "dice", ms=400)
    sim("move r 0.45 0.05 0.10 300")
    time.sleep(0.3)
    a = send("aim r dice")
    if not a.startswith("rot"):
        result("dice_summon", False, "aim: " + a)
        return finish_task()
    sim(a)
    time.sleep(0.3)
    sim("hold r grip 1")
    s = wait(lambda s: "held=R" in s.get("dice", ""), 3)
    m1 = re.search(r"summoned (\d+)", dice_status())
    s1 = int(m1.group(1)) if m1 else 0
    if not s or s1 != s0 + 1:
        sim("hold r grip 0")
        result("dice_summon", False, "not summoned: %s | aim %s | %s" % (dice_status(), a, send("finger r")))
        return finish_task()
    time.sleep(0.6)   # the dice fly into the hand
    sim("rot r 0 0 0")
    fling_and_check("dice_summon")
    finish_task()
    rest_hands()


def t_dice_trigger():
    ensure_map()
    rest_hands()
    if not start_mg("dice 0", dice_ready):
        return result("dice_trigger", False, "dice never waited")
    time.sleep(0.5)
    before = hand_rolls()
    sim("hold r trigger 1; wait 1200; hold r trigger 0")
    s = wait(lambda s: hand_rolls()[1] == before[1] + 1, 15)
    result("dice_trigger", s is not None, dice_status())
    finish_task()


def t_dice_reroll():
    """Target out of reach (the player's own reroll): throw, mark a die with the laser, Reroll (X), throw again."""
    ensure_map()
    rest_hands()
    if not start_mg("dice 99 0 0", dice_ready):
        return result("dice_reroll", False, "dice never waited")
    time.sleep(0.5)
    before = hand_rolls()
    send("dice throw 0 0.3 1.8 r game")
    if not wait(lambda s: s.get("dice", "").startswith("Marking"), 20):
        result("dice_reroll", False, "no marking state after the first throw: " + dice_status())
        return finish_task()
    time.sleep(1.0)
    point_over_table()
    r = send("world click PlayerDiceRoller/Container/D6")
    time.sleep(1.5)
    rest_hands()
    send("press X 6")
    s = wait(dice_ready, 20)
    if not s:
        result("dice_reroll", False, "no second catapult (marked via %s): %s" % (r, dice_status()))
        return finish_task()
    time.sleep(0.5)
    send("dice throw 0 0.2 1.5 r game")
    s2 = wait(lambda s: hand_rolls()[0] == before[0] + 2, 15)
    result("dice_reroll", s2 is not None, "mark: %s | %s" % (r, dice_status()))
    finish_task()


def t_dice_reroll_vr():
    """Playtest 2026-10-17 (T8): when the game offers a re-roll, the dice lie apart (a grid, DiceReroll), a die touched is
    marked and gets a rim, the Re-roll plate beside the dice (touched by the simulated fingertip seam) rolls again, and the
    second throw is by hand as before. Shots DR_spread, DR_marked."""
    ensure_map()
    rest_hands()
    if not start_mg("dice 99 0 0", dice_ready):
        return result("dice_reroll_vr", False, "dice never waited")
    time.sleep(0.5)
    before = hand_rolls()
    send("dice throw 0 0.3 1.8 r game")
    if not wait(lambda s: s.get("dice", "").startswith("Marking"), 20):
        result("dice_reroll_vr", False, "no marking state after the first throw: " + dice_status())
        return finish_task()
    time.sleep(1.5)
    rr = st().get("reroll", "")
    sim("head 0 1.75 0 -15 -45 0")
    time.sleep(0.4)
    send("shot DR_spread", 15)
    time.sleep(1.0)
    spread = "laid out" in rr or "apart already" in rr
    # (marked with the laser seam, as dice_reroll does: a fingertip tap on a resting die is touch_mark's own check)
    point_over_table()
    r1, c1 = send("world click PlayerDiceRoller/Container/D6"), "laser"
    time.sleep(1.5)
    m1 = re.search(r"marked=(\d+)", dice_status())
    rr2 = st().get("reroll", "")
    send("shot DR_marked", 15)
    time.sleep(1.0)
    marked = m1 is not None and int(m1.group(1)) >= 1 and "plate shown" in rr2 and re.search(r"marks shown [1-9]", rr2) is not None
    result("dice_reroll_spread_and_mark", spread and marked, "spread: %s | after the tap (%s %s): %s | %s" % (rr[:90], r1, c1, dice_status()[:80], rr2[:120]))
    send("reroll press")
    s = wait(dice_ready, 20)
    if not s:
        result("dice_reroll_plate", False, "no second catapult after the Re-roll plate: %s | %s" % (dice_status(), st().get("reroll", "")[:120]))
        return finish_task()
    time.sleep(0.5)
    send("dice throw 0 0.2 1.5 r game")
    s2 = wait(lambda s: hand_rolls()[0] == before[0] + 2, 15)
    result("dice_reroll_plate", s2 is not None, "%s | %s" % (st().get("reroll", "")[:120], dice_status()[:100]))
    finish_task()


def t_dice_classic():
    """DiceThrow = Classic (this run only): a grip at the dice grabs nothing; the trigger throws."""
    ensure_map()
    rest_hands()
    send("cfg DiceThrow Classic")
    try:
        if not start_mg("dice 0", dice_ready):
            return result("dice_classic", False, "dice never waited")
        time.sleep(0.5)
        g0 = num(dice_status(), "grabs")
        hand_to("r", "palm", "dice", ms=400)
        sim("hold r grip 1")
        time.sleep(1.5)
        no_grab = num(dice_status(), "grabs") == g0 and "held=" not in dice_status()
        sim("hold r grip 0")
        before = hand_rolls()
        sim("hold r trigger 1; wait 1200; hold r trigger 0")
        s = wait(lambda s: hand_rolls()[1] == before[1] + 1, 15)
        result("dice_classic", no_grab and s is not None, "grip grabbed nothing: %s; trigger: %s" % (no_grab, dice_status()))
        finish_task()
    finally:
        send("cfg DiceThrow Hybrid")
        rest_hands()


def t_fairness(n=30):
    send("dice hist reset")
    done = 0
    for i in range(n):
        if not ensure_map(30):
            break
        if not start_mg("dice 0", dice_ready):
            break
        time.sleep(0.3)
        before = hand_rolls()
        send("dice throw 0 0.3 1.8 r game")
        wait(lambda s: hand_rolls()[0] == before[0] + 1, 15)
        finish_task()
        done += 1
        if (i + 1) % 5 == 0:
            log("  fairness %d/%d: %s" % (i + 1, n, send("dice hist")))
    h = send("dice hist")
    m = re.search(r"hand ((?:\d:\d+ ){6})\(n=(\d+)\)", h)
    ok = False
    if m:
        counts = [int(x.split(":")[1]) for x in m.group(1).split()]
        total = int(m.group(2))
        ok = total > 0 and min(counts) >= 0.04 * total
    result("fairness", ok, "%d throws: %s" % (done, h))


# ---- touch (phase 3) ------------------------------------------------------------------------

def touch_counts():
    t = st().get("touch", "")
    g = lambda k: int(re.search(k + r" (\d+)", t).group(1)) if re.search(k + r" (\d+)", t) else -1
    return g("hovers"), g("selects"), g("refused")


def map_info():
    m = send("map", 4)
    pl = re.search(r"player (\(\d+, \d+\))", m)
    mv = re.findall(r"\(\d+, \d+\)", m.split("movable", 1)[1]) if "movable" in m else []
    return (pl.group(1) if pl else None), mv


def all_slots():
    r = send("world list MapLayout", 4)
    time.sleep(0.5)
    return r


def touch_seam(name, mode, settle=1.0):
    h0, s0, r0 = touch_counts()
    r = send("touch %s r %s" % (name, mode))
    time.sleep(1.6 if mode != "slow" else 3.2)
    time.sleep(settle)
    h1, s1, r1 = touch_counts()
    return r, (h1 - h0, s1 - s0, r1 - r0)


def t_touch_map_neg():
    """Map: a far (non-adjacent) tile is refused; swipe, slow push, graze and hover select nothing."""
    ensure_map()
    rest_hands()
    player, movable = map_info()
    if not player or not movable:
        return result("touch_map_neg", False, "map info: %s %s" % (player, movable))
    tiles = re.findall(r"\((\d+), (\d+)\)", send("map", 4))
    # a slot that is neither the player's nor movable: from the reach dump's slot list
    reach = send("reach touch_map", 4)
    txt = open(H + "/testdata/reach/touch_map.txt").read()
    slots = re.findall(r"slot (\(\d+, \d+\))", txt)
    far = [x for x in slots if x != player and x not in movable]
    ev = []
    ok = True
    if far:
        r, (dh, ds, dr) = touch_seam("MapLayout/" + far[0], "tap")
        p2, _ = map_info()
        good = ds == 0 and dr == 1 and p2 == player
        ok &= good
        ev.append("far %s: %s -> sel %d refused %d player %s" % (far[0], r.split(" ")[0], ds, dr, p2))
    for mode in ("swipe", "slow", "graze", "hover"):
        if mode == "hover":
            # the game's own hover must follow the finger onto the tile (the hero turns to it)
            h0, s0, r0 = touch_counts()
            send("touch MapLayout/%s r hover" % movable[0])
            time.sleep(0.5)
            game_hover = ("hover=" + movable[0]) in send("selection")
            time.sleep(2.0)
            h1, s1, r1 = touch_counts()
            dh, ds, dr = h1 - h0, s1 - s0, r1 - r0
        else:
            r, (dh, ds, dr) = touch_seam("MapLayout/" + movable[0], mode)
        p2, _ = map_info()
        good = ds == 0 and p2 == player and (mode != "hover" or (dh >= 1 and game_hover))
        ok &= good
        ev.append("%s %s: hovers +%d sel +%d refused +%d player %s%s%s" % (mode, movable[0], dh, ds, dr, p2,
                  " game hover on the tile" if mode == "hover" and game_hover else "", "" if good else " <-- BAD"))
    result("touch_map_neg", ok, "; ".join(ev))


def press_e2e(name, side="r", ms=300):
    """Fingertip (glove) 6 cm in front of the target's face, then pushed 1.5 cm through and back."""
    r = send("touchpose %s 6 %s" % (name, side))
    if "|" not in r:
        log("  touchpose: " + r)
        return False
    pos, press = [x.strip() for x in r.split("|")]
    x, y, z = pos.split()
    h = send("handto %s tip %s %s %s" % (side, x, y, z))
    if not h.startswith("move"):
        log("  handto: " + h)
        return False
    sim(h + " 400")
    time.sleep(0.5)
    p = press.split()
    back = "move %s %.4f %.4f %.4f %d" % (side, -float(p[2]), -float(p[3]), -float(p[4]), ms)
    sim("%s %d; wait 150; %s" % (press, ms, back))
    return True


def t_touch_map_e2e():
    """Map: the simulated hand puts its fingertip on a movable tile and presses -> the hero moves there."""
    ensure_map()
    rest_hands()
    player, movable = map_info()
    if not movable:
        return result("touch_map_e2e", False, "no movable tile")
    target = movable[0]
    h0, s0, r0 = touch_counts()
    press_e2e("MapLayout/" + target)
    time.sleep(2.5)
    h1, s1, r1 = touch_counts()
    p2, _ = map_info()
    ok = s1 == s0 + 1 and p2 == target
    result("touch_map_e2e", ok, "tile %s: player %s -> %s; touch +%d hovers +%d selects +%d refused | %s" % (target, player, p2, h1 - h0, s1 - s0, r1 - r0, st().get("touch")))
    rest_hands()


def t_dice_multi():
    """The second playtest (P2-3, mark 14: "Selecting multiple dice just doesn't work at all"): the dice laid out for the re-roll
    choice, the simulated hand taps them as a player does - the fingertip from above, down onto each die's top and back up:
    two dice marked, then the third, then one unmarked again; the Re-roll plate; the marked dice thrown again (two dice)."""
    ensure_map()
    rest_hands()
    if not start_mg("dice 99 0 0", dice_ready):
        return result("dice_multi", False, "dice never waited")
    time.sleep(0.5)
    send("dice throw 0 0.3 1.8 r game")
    if not wait(lambda s: s.get("dice", "").startswith("Marking"), 20):
        result("dice_multi", False, "no marking state: " + dice_status())
        return finish_task()
    time.sleep(1.5)

    def dice():
        out = []
        for part in send("reroll dice").split(" | "):
            m = re.match(r"(.+?) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (marked|-)", part.strip())
            if m:
                out.append((m.group(1), "%s %.4f %s" % (m.group(2), float(m.group(3)) + 0.05, m.group(4)), m.group(5) == "marked"))
        return out

    def tap(i):
        d = dice()
        if i >= len(d):
            return "no die %d" % i
        sim("hand r 0.25 1.2 -0.15 0 -30 0; ctouch r trigger 0; squeeze r 1")
        time.sleep(0.4)
        hand_to("r", "tip", d[i][1], ms=350)
        time.sleep(0.35)
        sim("move r 0 -0.065 0 220")
        time.sleep(0.45)
        sim("move r 0 0.08 0 220")
        time.sleep(0.6)
        return d[i][0]

    def marked():
        return [n for n, _, mk in dice() if mk]

    d0 = dice()
    if len(d0) < 3:
        result("dice_multi", False, "fewer than 3 dice: %s" % send("reroll dice"))
        return finish_task()
    sim("head 0 1.66 0 0 -50 0")
    a = tap(0)
    b = tap(1)
    two = marked()
    send("shot P2-3_dice_two_marked", 15)
    time.sleep(1.0)
    result("dice_multi_two", len(two) == 2, "tapped %s, %s -> marked %s | %s" % (a, b, two, st().get("touch", "")[:100]))
    c = tap(2)
    three = marked()
    result("dice_multi_three", len(three) == 3, "tapped %s -> marked %s" % (c, three))
    tap(2)
    back = marked()
    send("shot P2-3_dice_two_again", 15)
    time.sleep(1.0)
    result("dice_multi_unmark", len(back) == 2, "tapped %s again -> marked %s" % (c, back))
    sim("squeeze r 0")
    rest_hands()
    r = send("reroll press")
    s2 = wait(lambda s: s.get("dice", "").startswith("Catapault"), 10)
    if s2:
        time.sleep(0.5)
        send("dice throw 0 0.3 1.8 r game")
        landed = wait(lambda s: "landed" in s.get("dice", ""), 15)
    else:
        landed = None
    result("dice_multi_reroll", s2 is not None and landed is not None, "Re-roll: %s | then: %s" % (r, dice_status()[:140]))
    finish_task()


def t_touch_mark():
    """Dice marking: touching a resting die marks it, touching again unmarks it."""
    ensure_map()
    rest_hands()
    if not start_mg("dice 99 0 0", dice_ready):
        return result("touch_mark", False, "dice never waited")
    time.sleep(0.5)
    send("dice throw 0 0.3 1.8 r game")
    if not wait(lambda s: s.get("dice", "").startswith("Marking"), 20):
        result("touch_mark", False, "no marking state: " + dice_status())
        return finish_task()
    time.sleep(1.5)
    # a rolled die: they rest in the roller's result row (Dice Pool holds the unused ones)
    r1, c1 = touch_seam("PlayerDiceRoller/Container/D6", "tap")
    m1 = re.search(r"marked=(\d+)", dice_status())
    r2, c2 = touch_seam("PlayerDiceRoller/Container/D6", "tap", 0.5)
    m2 = re.search(r"marked=(\d+)", dice_status())
    ok = m1 is not None and m1.group(1) == "1" and m2 is not None and m2.group(1) == "0"
    result("touch_mark", ok, "tap 1: %s %s -> marked %s; tap 2: %s -> marked %s" % (r1, c1, m1.group(1) if m1 else "?", c2, m2.group(1) if m2 else "?"))
    finish_task()


def t_touch_wheel():
    """Wheel: touching a spinning wheel card stops it (end to end with the simulated hand)."""
    ensure_map()
    rest_hands()
    if not start_mg("wheel", wheel_ready, 40):
        return result("touch_wheel", False, "wheel never reached full speed")
    time.sleep(1.2)
    h0, s0, r0 = touch_counts()
    send("touch WheelSpinContainer/Card_CN r hold")   # the hand held into the wheel's path
    s = wait(lambda s: not s.get("wheel", "").startswith("Looping"), 5)
    h1, s1, r1 = touch_counts()
    result("touch_wheel", s is not None and s1 == s0 + 1, "wheel %s; touch +%d selects | %s" % (st().get("wheel"), s1 - s0, st().get("touch")))
    finish_task(60)


def t_touch_chance():
    """Chance cards (presented to the player): touched during the shuffle -> refused; after it -> picked."""
    ensure_map()
    rest_hands()
    r = send("mg chance")
    if not r.startswith("started"):
        return result("touch_chance", False, r)
    early = None
    picked = None
    deadline = time.time() + 40
    while time.time() < deadline:
        time.sleep(1.0)
        if not st().get("chance", "-").startswith("running"):
            continue
        rr, (dh, ds, dr) = touch_seam("CardChoiceContainer/Card_CN", "tap", 0.3)
        if ds == 1:
            picked = rr
            break
        if dr >= 1 and early is None:
            early = rr
    # the game reveals the cards and ends the task (the result is then in the log, the task gone)
    s = wait(lambda s: "result=Card" in s.get("chance", "") or "done ok" in s.get("mg", ""), 15)
    result("touch_chance", picked is not None and s is not None, "refused while shuffling: %s; picked: %s; then %s / %s" % (early, picked, st().get("chance"), st().get("mg")))
    finish_task(40)


def t_touch_chance_e2e():
    """Chance cards end to end: the simulated hand pokes the presented card."""
    ensure_map()
    rest_hands()
    r = send("mg chance")
    if not r.startswith("started"):
        return result("touch_chance_e2e", False, r)
    time.sleep(1.0)
    picked = False
    deadline = time.time() + 40
    tries = 0
    while time.time() < deadline and not picked:
        if st().get("chance", "-").startswith("running"):
            h0, s0, r0 = touch_counts()
            press_e2e("CardChoiceContainer/Card_CN")
            tries += 1
            time.sleep(1.5)   # status.json is written once a second
            h1, s1, r1 = touch_counts()
            picked = s1 == s0 + 1
            rest_hands()
        time.sleep(1.0)
    s = wait(lambda s: "result=Card" in s.get("chance", "") or "done ok" in s.get("mg", ""), 15)
    result("touch_chance_e2e", picked and s is not None, "%d pokes; %s / %s | %s" % (tries, st().get("chance"), st().get("mg"), st().get("touch")))
    finish_task(40)
    rest_hands()


def t_dice_grab2():
    """Hand feel Phase 2 with the dice (GrabThrow): an open hand at the dice gets ready (status, a tick); held dice stay
    when the grip relaxes to 0.45 (taken at 0.55, dropped under 0.35); a throw that peaks and slows down 40 ms before the
    hand opens is read at its peak (x ThrowBoost), not at the last 60 ms' mean."""
    ensure_map()
    rest_hands()
    send("cfg GrabThrow true")
    if not start_mg("dice 0", dice_ready):
        return result("dice_grab2", False, "dice never waited")
    time.sleep(0.5)
    hand_to("r", "palm", "dice", ms=500)
    time.sleep(0.8)
    ready = "ready R" in send("dice")   # live (status.json is written once a second)
    sim("squeeze r 1")
    held = wait(lambda s: "held=R" in s.get("dice", ""), 3) is not None
    sim("squeeze r 0.45")
    time.sleep(0.8)
    kept = "held=R" in dice_status()
    t0 = num(dice_status(), "throws")
    # a throw forward and up: 60 ms segments at about 0.5, 1.05, 1.53 and 1.53 m/s (a 120 ms peak, as a human throw), then
    # 40 ms at 0.7 m/s, and the hand opens
    # (`path` keeps one speed over all its segments: separate timed moves make the profile)
    # (`path` keeps one speed over all its segments: separate timed moves make the profile; the hand slows for the last 40 ms
    # before it opens, as in the design's test)
    sim("squeeze r 1; move r 0 0.01 -0.03 60; move r 0 0.02 -0.06 60; move r 0 0.03 -0.087 60; move r 0 0.03 -0.087 60; "
        "move r 0 0.007 -0.027 40; squeeze r 0.1")
    thrown = wait(lambda s: num(s.get("dice", ""), "throws") == t0 + 1, 6) is not None
    d = dice_status()
    m = re.search(r"estimate peak ([\d.]+) m/s \(x([\d.]+)\) vs mean ([\d.]+)", d)
    peak, boost, mean = (float(m.group(1)), float(m.group(2)), float(m.group(3))) if m else (0.0, 1.0, 0.0)
    # within 10 % of the motion's peak (x the boost), and well above the old 60 ms mean
    ok = ready and held and kept and thrown and m is not None and peak > mean * 1.15 and 0.9 * 1.53 * boost <= peak <= 1.1 * 1.53 * boost
    result("dice_grab2", ok, "ready at the dice %s; grabbed %s; kept at grip 0.45 %s; thrown %s; estimate peak %.2f m/s (x%.2f; the motion peaked at "
           "1.53 m/s) vs the old mean %.2f | %s" % (ready, held, kept, thrown, peak, boost, mean, d[:160]))
    finish_task()
    rest_hands()


def t_card_inspect():
    """Hand feel Phase 2 with cards (GrabThrow): in the inventory, the real hand at a card it hovers + a grip -> the game's
    close-up (Inspect); opening the hand -> back."""
    ensure_map()
    rest_hands()
    send("cfg GrabThrow true")
    send("press BACK")
    if not wait(lambda s: "Inventory" in (s.get("menu") or ""), 6):
        return result("card_inspect", False, "the inventory did not open: menu %s" % st().get("menu"))
    time.sleep(1.5)
    r = send("world list Equipment")
    names = re.findall(r"(\S*EquipmentCard\S*|\S*Card_EQ\S*)", send("log world: " + r))
    target = None
    for name in ("Weapon", "Armour", "Shield", "Card_EQ"):
        rr = send("touchpose %s 2 r" % name)
        if "|" in rr:
            target = name
            break
    if target is None:
        send("press B")
        return result("card_inspect", False, "no equipment card to touch (touchpose)")
    pos = rr.split("|")[0].strip()
    h = send("handto r tip %s" % pos)
    sim(h + " 400")
    time.sleep(1.2)
    c0 = st().get("cardGrab", "")
    sim("squeeze r 1")
    ok1 = wait(lambda s: "inspecting True" in s.get("cardGrab", ""), 5)
    c1 = st().get("cardGrab", "")
    sim("squeeze r 0")
    ok2 = wait(lambda s: "inspecting False" in s.get("cardGrab", "") and "free" in s.get("cardGrab", ""), 5)
    c2 = st().get("cardGrab", "")
    rest_hands()
    send("press B")
    time.sleep(1.5)
    result("card_inspect", ok1 is not None and ok2 is not None, "%s: before %s | gripped: %s | let go: %s" % (target, c0[:80], c1[:110], c2[:110]))


def t_card_pickup():
    """Hand feel Phase 2 rest (GrabThrow): on the map an open hand over a map card gets ready; a squeeze (the index on the
    trigger) picks the card up; it follows the hand 20 cm up; opening the hand sends it home; nothing else happens (no
    touch select, the hero stays, still the map). Shot CP_held (the card in the pinching glove)."""
    ensure_map()
    rest_hands()
    send("cfg GrabThrow true")
    time.sleep(1.0)
    w0 = send("tcard where")
    m = re.search(r"^(\S+) at (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) top (-?[\d.]+) (-?[\d.]+) (-?[\d.]+)", w0)
    if not m:
        return result("card_pickup", False, "no pickable map card: " + w0)
    card = m.group(1)
    home = [float(m.group(i)) for i in (2, 3, 4)]
    top = "%s %s %s" % (m.group(5), m.group(6), m.group(7))
    pl0, _ = map_info()
    t0 = touch_counts()
    sim("ctouch r trigger 1")   # the index on the trigger: a grabbing hand, not a pointing squeeze
    hand_to("r", "palm", top, ms=400)
    ready = wait(lambda s: re.search(r"ready L [\d.]+ (\S+ )?R 1\.0", s.get("tableCard", "")), 3)
    sim("squeeze r 1")
    held = wait(lambda s: "R holds " in s.get("tableCard", ""), 3)
    hc = re.search(r"R holds (\S+)", (held or {}).get("tableCard", ""))
    card = hc.group(1) if hc else card
    sim("move r 0 0.2 0.05 400")
    time.sleep(0.9)
    w1 = send("tcard where " + card)
    up = re.search(r"held, ([\d.]+) m from its place", w1)
    sim("head 0 1.75 0 0 -35 0")
    time.sleep(0.4)
    send("shot CP_held", 15)
    time.sleep(1.0)
    sim("squeeze r 0")
    free = wait(lambda s: s.get("tableCard", "").startswith("free") and "let go" in s.get("tableCard", ""), 3)
    time.sleep(1.2)
    w2 = send("tcard where " + card)
    m2 = re.search(r" at (-?[\d.]+) (-?[\d.]+) (-?[\d.]+)", w2)
    back = m2 is not None and sum((float(m2.group(i + 1)) - home[i]) ** 2 for i in range(3)) ** 0.5 < 0.01
    pl1, _ = map_info()
    t1 = touch_counts()
    # a pointing squeeze (the index lifted off the trigger) over the card is a touch, never a pick-up
    sim("head 0 1.75 0 0 0 0; ctouch r trigger 0")
    hand_to("r", "palm", top, ms=300)
    time.sleep(0.6)
    g0 = num(st().get("tableCard", ""), r"grabs")
    sim("squeeze r 1")
    time.sleep(0.8)
    g1 = num(st().get("tableCard", ""), r"grabs")
    sim("squeeze r 0")
    time.sleep(0.4)
    rest_hands()
    ok = ready is not None and held is not None and up is not None and float(up.group(1)) > 0.15 and free is not None and back and pl0 == pl1 and t1[1] == t0[1] and at_map()
    result("card_pickup", ok, "%s: ready %s, held %s, lifted %s m, let go %s, home again %s (%s), hero %s -> %s, touch selects %d -> %d" % (
        card, ready is not None, held is not None, up.group(1) if up else "-", free is not None, back, w2[:70], pl0, pl1, t0[1], t1[1]))
    result("card_pickup_not_when_pointing", g1 == g0 and g0 >= 0, "a squeeze with the index lifted over the card: grabs %s -> %s" % (g0, g1))


def t_figurine_pinch():
    """Playtest 2026-10-17 (T4, T5): a hand near the figurine outlines it; a pinch - the trigger and A together, as a Touch
    controller's pinch presses them - picks it up and the A never reaches the game (the encounter on the next tile does not
    open, `face buttons kept` counts it); let go of the trigger over the next place and the hero goes there. Shots
    FG_outline, FG_pinched."""
    ensure_map()
    rest_hands()
    send("cfg GrabThrow true")
    time.sleep(1.0)
    w = send("fig where")
    m = re.search(r"hero top (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) \| to (.+?) top (-?[\d.]+) (-?[\d.]+) (-?[\d.]+)", w)
    if not m:
        return result("figurine_pinch", False, "no figurine or no place to go: " + w)
    hero_above = "%s %.4f %s" % (m.group(1), float(m.group(2)) + 0.04, m.group(3))
    tile_above = "%s %.4f %s" % (m.group(5), float(m.group(6)) + 0.10, m.group(7))
    pl0, _ = map_info()
    menu0 = st().get("menu", "")
    sim("ctouch r trigger 1")
    hand_to("r", "palm", hero_above, ms=400)
    outlined = wait(lambda s: "outline on" in s.get("figurine", ""), 2)
    sim("head 0 1.75 0 0 -35 0")
    time.sleep(0.4)
    send("shot FG_outline", 15)
    time.sleep(1.0)
    result("figurine_outline", outlined is not None, "near: %s" % st().get("figurine", "-")[:160])
    # the pinch: A down, then the trigger pulled (a pinch closes them almost together)
    sim("hold r a 1; trig r 1")
    held = wait(lambda s: "R holds the hero" in s.get("figurine", ""), 3)
    time.sleep(0.6)
    fig = st().get("figurine", "")
    menu1 = st().get("menu", "")
    kept = re.search(r"face buttons kept (\d+)", fig)
    result("figurine_pinch_picks_up", held is not None and kept is not None and int(kept.group(1)) >= 1 and menu1 == menu0,
           "held %s | %s | menu %s -> %s" % (held is not None, fig[:160], menu0, menu1))
    hand_to("r", "palm", tile_above, ms=600)
    time.sleep(0.6)
    send("shot FG_pinched", 15)
    time.sleep(1.0)
    sim("hold r a 0; trig r 0")
    moved = wait(lambda s: "the hero goes there" in s.get("figurine", ""), 3)
    time.sleep(3.0)
    pl2, _ = map_info()
    rest_hands()
    result("figurine_pinch_set_down", moved is not None and pl2 != pl0, "let go: %s | hero %s -> %s" % (
        (moved or {}).get("figurine", "-")[:90] if moved else "-", pl0, pl2))


def t_figurine_real():
    """The second playtest (P2-2, mark 10: "I can't actually pick up the guy"): the grab inputs a Quest sends, the way a hand
    makes them - the fingertip at the figurine (not the palm on it), the index resting on the trigger, then the press ramped up
    over ~100 ms so the glove's index curls away from the figure before the click: a pinch (A + trigger), a squeeze (grip), the
    trigger alone. Each must pick it up; lifted 12 cm and let go in the air, it glides back (the hero does not move). Runs where
    the hero is shown and the table is calm: the dungeon map or the challenge's opening encounter (ChallengeSetup)."""
    rest_hands()
    send("cfg GrabThrow true")
    time.sleep(1.0)
    w = send("fig where")
    m = re.search(r"hero top (-?[\d.]+) (-?[\d.]+) (-?[\d.]+)", w)
    if not m:
        return result("figurine_real", False, "no figurine: " + w)
    # the fingertip at the figure's side, a little below its top, from the player's side
    side = "%s %.4f %s" % (m.group(1), float(m.group(2)) - 0.03, m.group(3))
    where = "%s / %s" % (st().get("state", ""), st().get("menu", ""))
    variants = [
        ("pinch", "ctouch r a 1; hold r a 1; trig r 0.3; wait 35; trig r 0.6; wait 35; trig r 0.85; wait 35; trig r 1",
         "trig r 0; hold r a 0; ctouch r a 0"),
        ("grip", "squeeze r 0.3; wait 35; squeeze r 0.6; wait 35; squeeze r 0.85; wait 35; squeeze r 1", "squeeze r 0"),
        ("trigger", "trig r 0.3; wait 35; trig r 0.6; wait 35; trig r 0.85; wait 35; trig r 1", "trig r 0"),
    ]
    for name, press, release in variants:
        rest_hands()
        time.sleep(0.8)
        sim("ctouch r trigger 1")
        hand_to("r", "tip", side, ms=400)
        time.sleep(0.5)
        near = st().get("figurine", "")
        menu0 = st().get("menu", "")
        sim(press)
        held = wait(lambda s: "R holds the hero" in s.get("figurine", ""), 3)
        sim("move r 0 0.12 0 300")
        time.sleep(0.6)
        if name == "pinch":
            sim("head 0 1.75 0 0 -35 0")
            time.sleep(0.4)
            send("shot P2-2_figurine_%s_held" % ("setup" if "Setup" in where else "map"), 15)
            time.sleep(1.0)
        fig = st().get("figurine", "")
        sim(release)
        back = wait(lambda s: "back to where it stood" in s.get("figurine", ""), 3)
        menu1 = st().get("menu", "")
        result("figurine_real_" + name, held is not None and back is not None and menu1 == menu0,
               "%s | before: %s | held %s: %s | let go: %s | menu %s -> %s" % (
                   where, near[:70], held is not None, fig[:90], (back or {}).get("figurine", "-")[60:140] if back else "-", menu0, menu1))
    rest_hands()


def t_figurine():
    """Feature A (hands-on table play): on the map an open hand at the hero's figurine gets ready; a squeeze picks it up;
    let go in the air and it glides back to where it stood; picked up again and let go over a place the hero can go to,
    the game moves the hero there (as a tap on that tile would). Shot FG_held. Leaves an encounter open: run last."""
    ensure_map()
    rest_hands()
    send("cfg GrabThrow true")
    time.sleep(1.0)
    w = send("fig where")
    m = re.search(r"hero top (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) \| to (.+?) top (-?[\d.]+) (-?[\d.]+) (-?[\d.]+)", w)
    if not m:
        return result("figurine", False, "no figurine or no place to go: " + w)
    hero = "%s %s %.4f" % (m.group(1), m.group(2), float(m.group(3)))
    hero_above = "%s %.4f %s" % (m.group(1), float(m.group(2)) + 0.04, m.group(3))
    tile = m.group(4)
    tile_above = "%s %.4f %s" % (m.group(5), float(m.group(6)) + 0.10, m.group(7))
    pl0, _ = map_info()
    # 1. picked up, let go in the air: back home
    sim("ctouch r trigger 1")
    hand_to("r", "palm", hero_above, ms=400)
    ready = wait(lambda s: "readies" in s.get("figurine", ""), 2)
    sim("squeeze r 1")
    held = wait(lambda s: "R holds the hero" in s.get("figurine", ""), 3)
    sim("move r 0 0.15 0.05 400")
    time.sleep(0.8)
    sim("head 0 1.75 0 0 -35 0")
    time.sleep(0.4)
    send("shot FG_held", 15)
    time.sleep(1.0)
    sim("squeeze r 0")
    back = wait(lambda s: "back to where it stood" in s.get("figurine", ""), 3)
    time.sleep(0.8)
    pl1, _ = map_info()
    result("figurine_lift_and_back", held is not None and back is not None and pl0 == pl1, "held %s, let go in the air: %s; hero %s -> %s" % (
        held is not None, (back or {}).get("figurine", "-")[:90] if back else "-", pl0, pl1))
    # 2. picked up and set down on the next place: the hero goes there
    rest_hands()
    time.sleep(0.5)
    sim("ctouch r trigger 1")
    hand_to("r", "palm", hero_above, ms=400)
    time.sleep(0.4)
    sim("squeeze r 1")
    held2 = wait(lambda s: "R holds the hero" in s.get("figurine", ""), 3)
    hand_to("r", "palm", tile_above, ms=600)
    time.sleep(0.8)
    over = st().get("figurine", "")
    sim("squeeze r 0")
    moved = wait(lambda s: "the hero goes there" in s.get("figurine", ""), 3)
    time.sleep(3.0)
    pl2, _ = map_info()
    rest_hands()
    result("figurine_set_down_moves_hero", held2 is not None and moved is not None and pl2 != pl0, "over the next place: %s | let go: %s | hero %s -> %s (%s)" % (
        over[:80], (moved or {}).get("figurine", "-")[:80] if moved else "-", pl0, pl2, tile))


def t_figurine_drop_beside():
    """figurine_drop's second half alone (a fresh map): set down just beside the next place, its outline 1-3 cm away."""
    t_figurine_drop(("beside",))


def t_figurine_drop(which=("on", "beside")):
    """The third playtest, mark 9 (at a challenge's opening, GameState_ChallengeSetup: "When I pick up the guy and move him on to
    the next spot, he just moves back to the first spot" - every drop went back): wherever the map takes moves (the dungeon map
    or the opening's map), the hero picked up and set down ON the next place goes there; picked up again and set down just
    BESIDE the next place (its outline 1.5 cm away), it goes there too. Leaves an encounter open: run on its own."""
    rest_hands()
    send("cfg GrabThrow true")
    time.sleep(1.0)
    where = "%s / %s" % (st().get("state", ""), st().get("menu", ""))
    for name, short in [v for v in (("on", 0.0), ("beside", -0.075)) if v[0] in which]:
        # (a challenge's opening shows its first encounter's pages first: answered with A until the map takes moves)
        for _ in range(8):
            if st().get("menu") == "Map":
                break
            if st().get("menu") == "Encounter":
                send("press A 6")
            time.sleep(3)
        if not wait(lambda s: s.get("menu") == "Map", 40):
            return result("figurine_drop_" + name, False, "the map takes no moves: %s / %s" % (st().get("state"), st().get("menu")))
        time.sleep(1.5)
        w = send("fig where")
        m = re.search(r"hero top (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) \| to (.+?) top (-?[\d.]+) (-?[\d.]+) (-?[\d.]+)", w)
        if not m:
            return result("figurine_drop_" + name, False, "no figurine or no place to go: " + w)
        hx, hy, hz = float(m.group(1)), float(m.group(2)), float(m.group(3))
        tx, ty, tz = float(m.group(5)), float(m.group(6)), float(m.group(7))
        # `short` (world) from the tile's middle toward the hero; beside = past its far edge, its outline 1-3 cm away (toward the
        # hero would be the hero's own place: set down there, the hero rightly stays)
        dx, dz = hx - tx, hz - tz
        d = max(1e-4, (dx * dx + dz * dz) ** 0.5)
        tx, tz = tx + dx / d * short, tz + dz / d * short
        hero_above = "%.4f %.4f %.4f" % (hx, hy + 0.04, hz)
        tile_above = "%.4f %.4f %.4f" % (tx, ty + 0.10, tz)
        pl0, _ = map_info()
        rest_hands()
        time.sleep(0.5)
        sim("ctouch r trigger 1")
        hand_to("r", "palm", hero_above, ms=400)
        time.sleep(0.4)
        sim("squeeze r 1")
        held = wait(lambda s: "R holds the hero" in s.get("figurine", ""), 3)
        hand_to("r", "palm", tile_above, ms=600)
        time.sleep(0.8)
        sim("squeeze r 0")
        moved = wait(lambda s: "the hero goes there" in s.get("figurine", ""), 3)
        time.sleep(3.0)
        pl1, _ = map_info()
        rest_hands()
        result("figurine_drop_" + name, held is not None and moved is not None and pl1 != pl0, "%s | let go %s: %s | hero %s -> %s (%s)" % (
            where, name, (moved or {}).get("figurine", "-")[-90:] if moved else st().get("figurine", "")[-90:], pl0, pl1, m.group(4)))
        # the move opens the place's encounter: through it back to the map
        for _ in range(10):
            if st().get("menu") == "Map":
                break
            send("press A 6")
            time.sleep(3)


def t_card_flip():
    """Feature A, the rest (2026-10-13): a face-down chance card (the shuffle done) picked up and let go unturned goes back and
    nothing is chosen; picked up again and turned over in the hand (its face toward the eyes), the game reveals it - the
    chance game's pick, as a touch makes it. Shot CF_held (the card in the hand, its back to the eyes)."""
    ensure_map()
    rest_hands()
    send("cfg GrabThrow true")
    r = send("mg chance")
    if not r.startswith("started"):
        return result("card_flip", False, r)
    wait(lambda s: s.get("chance", "-").startswith("running"), 30)
    w = ""
    for _ in range(30):
        w = send("tcard flipwhere")
        if " front " in w:
            break
        time.sleep(0.5)
    m = re.search(r"^(\S+) at .* front (-?[\d.]+) (-?[\d.]+) (-?[\d.]+)", w)
    if not m:
        result("card_flip", False, "no face-down card to turn over: " + w)
        return finish_task(40)
    card, front = m.group(1), "%s %s %s" % (m.group(2), m.group(3), m.group(4))
    # 1. picked up and let go unturned: back to its place, nothing chosen
    sim("ctouch r trigger 1")
    hand_to("r", "palm", front, ms=400)
    time.sleep(0.6)
    sim("squeeze r 1")
    held = wait(lambda s: "R holds " in s.get("tableCard", ""), 3)
    hc = re.search(r"R holds (\S+)", (held or {}).get("tableCard", ""))
    card = hc.group(1) if hc else card
    sim("move r 0 0.06 0.06 300")
    time.sleep(0.6)
    sim("head 0 1.75 0 0 -25 0")
    time.sleep(0.3)
    send("shot CF_held", 15)
    time.sleep(1.0)
    sim("squeeze r 0")
    back = wait(lambda s: "let go" in s.get("tableCard", ""), 3)
    time.sleep(1.0)
    still = st().get("chance", "-")
    result("card_flip_unturned_goes_back", held is not None and back is not None and still.startswith("running"),
           "%s held %s, let go unturned: %s; the chance game %s" % (card, held is not None, (back or {}).get("tableCard", "-")[:100], still[:60]))
    # 2. picked up again and turned over in the hand: the game reveals it
    rest_hands()
    time.sleep(0.8)
    w = send("tcard flipwhere")
    m = re.search(r"^(\S+) at .* front (-?[\d.]+) (-?[\d.]+) (-?[\d.]+)", w)
    if not m:
        result("card_flip_turned_reveals", False, "no face-down card the second time: " + w)
        return finish_task(40)
    card, front = m.group(1), "%s %s %s" % (m.group(2), m.group(3), m.group(4))
    f0 = num(st().get("tableCard", ""), "flips")
    sim("ctouch r trigger 1")
    hand_to("r", "palm", front, ms=400)
    time.sleep(0.6)
    sim("squeeze r 1")
    held2 = wait(lambda s: "R holds " in s.get("tableCard", ""), 3)
    hc = re.search(r"R holds (\S+)", (held2 or {}).get("tableCard", ""))
    card = hc.group(1) if hc else card
    faces = []
    for yaw in range(0, 200, 20):   # the wrist turns the card over (about the vertical: its face comes round to the eyes)
        sim("rot r %d 0 0" % yaw)
        time.sleep(0.12)
        tc = st().get("tableCard", "")
        mm = re.search(r"face (-?[\d.]+)", tc)
        if mm:
            faces.append(mm.group(1))
        if "turned over" in tc:
            break
    flipped = wait(lambda s: "turned over" in s.get("tableCard", ""), 3)
    sim("squeeze r 0")
    picked = wait(lambda s: "result=Card" in s.get("chance", "") or "done ok" in s.get("mg", ""), 15)
    tc = st().get("tableCard", "")
    result("card_flip_turned_reveals", held2 is not None and flipped is not None and num(tc, "flips") == f0 + 1 and picked is not None and
           ("turned over " + card + ": revealed") in tc,
           "%s held %s; face toward the eyes %s; %s; the chance game: %s / %s" % (card, held2 is not None, " ".join(faces[:10]),
                                                                                  re.sub(r".*last ", "last ", tc)[:110], st().get("chance", "-")[:60], st().get("mg", "-")[:40]))
    rest_hands()
    finish_task(40)


TESTS = {
    "stop_seam": t_stop_seam, "too_early": t_too_early, "slap": t_slap, "slap_slow": t_slap_slow, "grip": t_grip,
    "wheel_slap": lambda: t_wheel("slap"), "wheel_grip": lambda: t_wheel("grip"), "wheel_nudge": t_wheel_nudge,
    "dice_seam": t_dice_seam, "dice_drop": t_dice_drop, "dice_cone": t_dice_cone,
    "touch_map_neg": t_touch_map_neg, "touch_mark": t_touch_mark, "touch_wheel": t_touch_wheel,
    "touch_chance": t_touch_chance, "card_flip": t_card_flip, "touch_chance_e2e": t_touch_chance_e2e,
    "dice_reach": t_dice_reach, "dice_summon": t_dice_summon, "dice_trigger": t_dice_trigger, "dice_reroll": t_dice_reroll, "dice_classic": t_dice_classic,
    "dice_grab2": t_dice_grab2, "card_inspect": t_card_inspect, "card_pickup": t_card_pickup,
    "touch_map_e2e": t_touch_map_e2e,   # last: the hero walks, an encounter follows
    "figurine": t_figurine,             # (also leaves an encounter open: run on its own)
    "figurine_drop": t_figurine_drop,   # (playtest 3, mark 9: also at a challenge's opening; leaves an encounter open)
    "figurine_drop_beside": t_figurine_drop_beside,
    "figurine_real": t_figurine_real,
    "dice_multi": t_dice_multi,
    "figurine_pinch": t_figurine_pinch, # (also leaves an encounter open: run on its own)
    "dice_reroll_vr": t_dice_reroll_vr,
}


def main():
    args = sys.argv[1:]
    names = [a for a in args if not a.isdigit()] or [k for k in TESTS]
    log("interact_test: " + " ".join(names))
    for n in names:
        if n == "fairness":
            t_fairness(int(args[-1]) if args[-1].isdigit() else 30)
            continue
        try:
            TESTS[n]()
        except Exception as e:  # keep going
            result(n, False, "exception %r" % e)
    passed = sum(1 for _, ok in RESULTS if ok)
    log("interact_test: %d/%d passed (%s)" % (passed, len(RESULTS), ", ".join(n for n, ok in RESULTS if not ok) or "all ok"))
    return 0 if passed == len(RESULTS) else 1


if __name__ == "__main__":
    sys.exit(main())
