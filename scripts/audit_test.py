#!/usr/bin/env python3
"""Checks for the UYF parity audit's items (docs/uyf-parity-audit.md; docs/combat-progress.md, 2026-10-14 night).

  audit_test.py --parts a,b,... [--fixture NAME] [--no-fixture] [--tag AT] [--equip CARDS] [--stay]

In a fixture fight (Physical mode, god mode, enemies kept alive), through the simulated headset (xrsim):
  commit      (item 2) the attacker commits: in the last 0.3 s before its blow its facing is held - the bar moves less
              than 6 cm then (it followed the attacker's turn before); facing holds counted
  sidestep    (item 2) 0.25 s before a blow the head steps 1.5 m aside: committed (facing held), out of its reach (3 of 4
              at least); the same with the hold off is reported (`vrcommit off`; at this distance and time HoF2's
              thieves don't turn after the step either: MR2d 4/4 and 4/4)
  dash        (item 3) UYF's dash: a thief 4.5 m ahead - A with the stick forward homes on it (ends ~0.5 m from its body,
              well under 0.3 s), hits it (its guard worn, pushed back ~3 m, the others within 2 m knocked away); back: 3 m;
              three dashes, the fourth refused; a charge back 4 s after use
  flick       (item 3) the right stick flicked forward: a dash forward (flicks +1); back: 3 m back; left / right still turn
  shortdash   (item 3) `[Combat] Dash = Short`: the earlier 2 m dash
  pauses      (merge-ready #4, E14; --fixture shield) 50 s: each enemy's pause after an attack drawn from its short / med /
              long (two kinds at least of 4 draws or more); VariedPauses off 25 s: none drawn
  idlebonus   (merge-ready #4) 60 s with three thieves: the turns go round (at most a quarter are the same enemy's turn back
              within 6 s); 60 s without the bonus reported
  backdash    (merge-ready #4) UYF's back dash covers what it plans: in the open its 3 m; with the floor's end 2.4 m behind it
              lands UYF's 1 m before the obstacle and covers that; 0.9 m from it the dash fails (no move, no charge); an
              enemy 1.5 m behind is nudged aside as the dash passes (`dash? free`, `dash? edge` place and read the way)
  health      (item 3) EnemyHealth's default 0.15 (2026-10-17; the old 0.25 and 1.0 carried over once: DefaultsVersion 2); walking 1.8 m/s
  trail       (item 4) a fast swing draws the blade's trail (peak alpha > 0.5); a slow move (1.5 m/s) draws none
  smartsnap   (item 5) a snap turn toward the nearest enemy on that side (`vrsmartsnap probe`): the step is its angle (8-190
              deg); with the smart snap off the fixed angle
  shotstop    (item 7) a shot stopped by the shield: UYF's feedback (stopFeedback +1, super +1)
  bladeshot   (item 11; --fixture shots) the shield hand down at the side, the sword's middle put on a shot's line
              (`shotblade`): the blade stops it (bladeStops +1, no hit); then the sword down too: the next shot hits
  super       (item 13) the meter full: the weapon glows (glowFrames grow); using the special: activations +1
  leadhold    (item 9) `[Combat] Lead = Hold` (UYF's): 40 s of thieves' blows - the attacks waited (holds released),
              each blow landed at its lead (lead >= the table's floor - 0.03 s, at most 0.3 s over), at the table's attack
              speed at the impact (not slowed); then `Lead = Slow` for comparison (no holds)
  launch      (item 10) a killing blow (big) throws the body along the swing: kill launches +1 (`launch?`: 600 along the
              swing, the ragdoll or the puppet)
  diffflash   (item 14) changing the difficulty flashes the view 1.5 s at full strength in the step's colour (UYF's
              DifficultySwap flashes); with `[Combat] DifficultyFlash` off, no flash
  exitdash    (last: it wins the fight) a won fight's exit waits for A held a second (FightExit): the A press itself is no
              dash any more (MR2b: every leave began with a 3 m dash back)
  circle      (item 14, UYF's C6, off by default) `vrcircle probe` 2.4 m from a thief it faces: one second of the stick
              pushed right - round it at the same distance (within 8 cm, >= 20 deg round) with `[Combat] CircleTarget`
              on, a straight side step (further away) with it off
  glovehud    (item 14, UYF's I13) `[Combat] GloveHud` off hides the glove's health gems and the super meter; on shows them
  weaponsound (item 14, UYF's F5) the weapon's movement loop: quiet at rest (0.2 of its volume), louder in a fast swing; off:
              silent
  worldtouch  (item 16, UYF's F16) the sword hanging point down, brought down into the floor at ~6 m/s: an impact (sparks,
              a clank, a knock); then dragged along it at ~1.7 m/s: scrapes (`world?`)
  bossdeath   (item 17; --fixture boss_knight, last: it ends the fight) the boss killed: UYF's boss-death flashes (a pale
              cyan burst, then a pale rose flash) and a long rumble (`bossdeath?`)
  bodylean    (item 18) `bodylean? test <enemy>`: a recoil on the nearest enemy - its upper spine found, thrown back (> 3 deg
              0.05 s in), gone after 0.4 s; the block lean's count after a fight with shields (stage2 `deflect`) reported
  waiting     (item 8, UYF's E13; three thieves = UYF's husk: on deck 4 m, standby 6 m) 45 s with `[Combat] WaitingEnemies`
              on, standing: the enemy next in turn keeps 3.7-4.3 m, the others 5.5-6.5 m (`waiting?`; samples 4 s into a
              role, within 0.4 m: 60 %); then 45 s off: the game's ring data back (no copies left); the strike rate on at least
              half the rate off
  approach    (item 8, UYF's E12) the turn holder kept 5 m away (`heroat` it every 0.2 s): it never closes in, so it gives
              the turn up after UYF's approach time-out (a husk: 1 s after its wait before engaging) - approachTimeouts +1,
              demotions +1, and the next turn goes to another enemy
  combogaps   (audit E3; --fixture shadows) 75 s of the shadows' attacks, standing: their runs of blows keep their own
              rhythm - later blows shown ahead (`ahead?`: made, adopted, judged), each marker up its full lead (none short
              by more than 0.1 s), two or more markers of one attacker up at once, the blows of a run closer together than
              a lead (gapMax under it; a blow whose telegraph came out of view waits the off-view lead by design and is
              left out of the gaps: offHeld), Threats holding none of them (laterBlows); then 45 s with `vrcombogaps
              off`: none
  looks       shots of the night's visuals for the report (testdata/shots/<TAG>_look_*_eyes.png): the trail mid-swing,
              the dash's landing ring (the right stick leaning), the super's glow, the difficulty flash (Hard's orange)
  alloc       (main's GC work) 30 s of a fight with the new per-frame parts busy (the trail in fast swings, the super's
              glow, the dash's pips, the seated check, the facing hold): `profile alloc` - the new per-frame parts
              (WeaponTrail, SuperMeter, DashMarker, SeatedRules) under 40 KB/s each (the profiler's attribution is coarse);
              the rest of the combat side is reported (main's cut: the gloves' 150 KB/s -> 0.6)
  neverreach  (item 12; --fixture boss_knight) 90 s standing in front of the Black Knight: every blow whose blade never
              comes near the player (its atlas reach over 1 m: Templar_attack_1's first hit, the counter combo's) is never a
              hit - a phantom since item 12 (no bar, no lead, no damage; `phantoms` counts them) - the rest is reported
Results: PASS/FAIL lines on stdout and in logs/audit_test.log; exit 1 on any FAIL.
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
LOG = H + "/logs/audit_test.log"
args = sys.argv[1:]
TAG = args[args.index("--tag") + 1] if "--tag" in args else "AT"
results = []
HEAD = "head 0 1.7 0 0 0 0"
HANDS_DOWN = "hand r 0.32 0.85 0.05 0 -80 0; hand l -0.32 0.85 0.05 0 -80 0"
HAND_R = "hand r 0.25 1.35 -0.35 0 5 0"
HAND_L = "hand l -0.25 1.2 -0.3 0 0 0"


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


def nums(s):
    d = {}
    for k, v in re.findall(r"([A-Za-z]+) (-?[\d.]+)", s):
        if k not in d:
            try:
                d[k] = float(v)
            except ValueError:
                pass
    return d


def counts():
    s = send("pcombat?")
    d = nums(s)
    d["_raw"] = s
    return d


def reset_pose():
    sim(HEAD + "; " + HAND_R + "; " + HAND_L)


def vec(s):
    return [float(x) for x in s.strip("()").split(",")]


def strike(timeout=20.0, fresh=0.0):
    """A live in-view strike: dict (who, colour, eta, bar) or None."""
    t0 = time.time()
    while time.time() - t0 < timeout:
        s = send("strike?", 4)
        if s and s != "none" and not s.startswith(("ERR", "no ")):
            first = s.split(" | ")[0]
            if "offview" in first:
                send("face striker")
                time.sleep(0.05)
                continue
            m = re.search(r"(\w+) (Gold|White|Red) eta (-?[\d.]+) valid (\w+) bar (\([^)]*\))", first)
            if m and float(m.group(3)) >= fresh:
                return {"who": m.group(1), "colour": m.group(2), "eta": float(m.group(3)), "bar": vec(m.group(5)), "raw": first}
        time.sleep(0.05)
    return None


def enemy(who="nearest"):
    e = send("enemy? " + who)
    m = re.search(r"^(\w+) dist ([\d.]+).* at (\([^)]*\)) head (\([^)]*\)) id (-?\d+)", e)
    if not m:
        return None
    return {"who": m.group(1), "dist": float(m.group(2)), "at": vec(m.group(3)), "head": vec(m.group(4)), "id": "#" + m.group(5), "raw": e}


def poise(who):
    p = send("poise? " + who)
    m = re.search(r"guard ([\d.]+)/(\d+) phase (\w+)", p)
    return {"guard": float(m.group(1)), "max": float(m.group(2)), "phase": m.group(3), "raw": p} if m else {"raw": p}


# ------------------------------------------------------------------------------------------------ item 2: the enemy commits

def part_commit():
    """The bar's centre over the last 0.3 s before a blow: held (moves < 6 cm) now that the attacker's facing is held."""
    send("vrcommit on")
    rows, moves = [], []
    t0 = time.time()
    while time.time() - t0 < 90 and len(moves) < 5:
        if user_in_vr():
            return
        reset_pose()
        sim(HANDS_DOWN)
        st = strike(timeout=25, fresh=0.6)
        if st is None:
            break
        who = st["who"]
        # sample its bar from 0.3 s before the blow until 0.05 s before
        first = None
        last = None
        while True:
            s = send("strike?", 4)
            m = re.search(who + r" \w+(?: offview)? eta (-?[\d.]+) valid \w+ bar (\([^)]*\))", s or "")
            if not m:
                break
            eta = float(m.group(1))
            if eta <= 0.3 and first is None:
                first = vec(m.group(2))
            if eta <= 0.05:
                break
            if first is not None:
                last = vec(m.group(2))
            time.sleep(0.03)
        if first and last:
            d = sum((a - b) ** 2 for a, b in zip(first, last)) ** 0.5
            moves.append(d)
            rows.append("%s %.3f" % (who, d))
        time.sleep(0.6)
    c = send("vrcommit")
    holds = re.search(r"holds (\d+)", c)
    ok = len(moves) >= 3 and max(moves) < 0.06 and holds and int(holds.group(1)) >= len(moves)
    result("commit", bool(ok), "the bar's move in the last 0.3 s: %s m (max %.3f) | %s" % (", ".join(rows), max(moves) if moves else -1, c))


def sidestep_phase(commit, tries=4):
    send("vrcommit " + ("on" if commit else "off"))
    got = {}
    for _ in range(tries * 2):
        reset_pose()
        # the hands where the head will step to, behind it at knee height, the sword pointing back: no guard anywhere near
        # a blow, before the step or after it (MR2b: HANDS_DOWN's sword hung on the run attack's upright bar; MR2c: from
        # behind the knees a sword pointing forward still reached it; MR2d: after the step the attacker stood beyond the
        # 40 deg edge, and off-view any guard held out toward it blocks - the hands left behind did)
        sim("hand r 1.75 0.45 0.45 0 180 0; hand l 1.25 0.45 0.45 0 180 0")
        st = strike(timeout=25, fresh=0.6)
        if st is None:
            break
        t_read = time.time()
        c0 = counts()
        time.sleep(max(0.0, st["eta"] - (time.time() - t_read) - 0.25))
        sim("head 1.5 1.7 0 0 0 0")
        what = None
        t1 = time.time()
        while time.time() - t1 < 3:
            c1 = counts()
            for k in ("outOfRange", "hitsTaken", "blocks", "dodges"):
                if c1.get(k, 0) > c0.get(k, 0):
                    what = k
                    break
            if what:
                break
            time.sleep(0.08)
        sim(HEAD)
        if what:
            got[what] = got.get(what, 0) + 1
        if sum(got.values()) >= tries:
            break
        time.sleep(0.6)
    return got


def part_sidestep():
    """0.25 s before a blow the head steps 1.5 m to the side (from ~1.3 m: outside UYF's +-45 deg sector): with the attacker
    committed (its facing held) the blow misses (out of its reach); without the hold the attacker turns after the player."""
    on = sidestep_phase(True)
    off = sidestep_phase(False)
    send("vrcommit on")
    # the late step escapes with the hold (UYF's point); the free phase is reported for comparison only - MR2d: 4/4 out of
    # reach either way at 1.5 m (HoF2's thieves don't turn after a step that late in time), so the hold's own effect is
    # `commit`'s bar standing still
    ok = on.get("outOfRange", 0) >= 3
    result("sidestep", ok, "a 1.5 m side step 0.25 s before the blow - committed: %s; not committed: %s" % (on, off))


# ------------------------------------------------------------------------------------------------ item 3: UYF's dash

def dash_state():
    return send("dash?")


def press_a():
    sim("btn r a 80")


def part_dash():
    send("vrdash uyf")
    send("vrknock off")
    send("vrmagnet off")
    # the waiting enemies off: the thief placed 4.5 m out is a waiter, and a waiter inside or outside its band now moves to
    # its middle (Standby.FleePostfix) while the scenario is set up - its distance at the press changed (round 2's chain r3:
    # homed at 3.0 m, then not homed). The dash is tested here; the waiters have `waiting`
    send("vrwaiting off")
    rows = []
    ok = True
    # homing and the impact: a thief straight ahead 4.5 m away
    e = enemy("nearest")
    if not e:
        return result("dash", False, "no enemy")
    who = e["id"]
    time.sleep(1.2)   # the charges back (4 s after any earlier dash) - before the thief is set up, not after
    # set up just before the press (MR4h: placed, then 1.2 s of waiting - the thief moved meanwhile, the dash went 19 deg off
    # the gaze and took its 7.0 off another one, while this one's guard refilled 43.4 -> 50.0), with this thief the nearest:
    # `aside` puts the others 5 m from it - none can then stand in a 45 deg cone between the hero (4.5 m out) and it (MR4i, MR4j
    # with 3 m: another thief 1.4 m, 2.0 m away took the dash) - and if one is still nearer, the hero stands on another side
    # and with the floor going on 8.5 m along the gaze - the thief 4.5 m out (it may step 0.7 m away before the press) and its
    # 3 m push (round 2: pushed 1.37 m - the thief stood near the arena's end; tail: the hero placed at the floor's end, the
    # dash failed "the floor ends 0.00 m out"); else the side with the most floor
    def room_ahead():
        fm = re.search(r"free ([\d.]+)(\+?) m", send("dash? free 0"))
        return 99.0 if fm and fm.group(2) else (float(fm.group(1)) if fm else 0.0)
    tries = []
    for attempt in range(3):
        best = None   # (room, deg) of the side with the most floor where this thief was the nearest
        for deg in (0, 90, -90, 180, 45, -45, 135, -135, "best"):
            if deg == "best":   # no side had 8.5 m: the best one again
                if best is None:
                    break
                deg = best[1]
            send("aside %s 5" % who)
            send("heroat %s %d 4.5" % (who, deg))
            send("face " + who)
            reset_pose()
            n = enemy("nearest")
            room = room_ahead()
            if n and n["id"] == who:
                if room >= 8.5:
                    break
                if best is None or room > best[0]:
                    best = (room, deg)
        g0 = poise(who).get("guard")
        e0 = enemy(who)
        d0 = send("dash?")
        sim("stick l 0 1 0")
        press_a()
        time.sleep(0.5)
        sim("stick l 0 0 0")
        time.sleep(0.6)
        d1 = send("dash?")
        e1 = enemy(who)
        g1 = poise(who).get("guard")
        imp0 = int(nums(d0).get("impacts", 0))
        imp1 = int(nums(d1).get("impacts", 0))
        homed = "homed on" in d1
        hm = re.search(r"homed on \S+ ([\d.]+) m away", d1)
        homed_at = float(hm.group(1)) if hm else -1.0
        # the guard the impact took, from the dash's own line (read 1.1 s later the guard has refilled part of it)
        im = re.search(r"\| impact \S+ after [\d.]+ m: guard (-?[\d.]+)", d1)
        took = float(im.group(1)) if im else 0.0
        # the push as it really went (MR2b: 0.6 s after it the thief had run most of the way back - its distance then says little)
        pm = re.search(r"\| push ([\d.]+) m of ([\d.]+)", d1)
        pushed = float(pm.group(1)) if pm else -1.0
        interposed = homed and 0.0 <= homed_at < 3.5   # another thief hopped into the line before the press (placed at 4.5 m)
        # or, not homed, the dash met another one well short of this one (p2c3: "impact ThievesThug after 2.1 m", the placed
        # thief 4.5 m out)
        ia = re.search(r"\| impact \S+ after ([\d.]+) m", d1)
        if not homed and ia and float(ia.group(1)) < 3.0:
            interposed = True
        good = homed and not interposed and imp1 == imp0 + 1 and took < 0.0 and pushed >= 1.5
        tries.append("%s at %.1f m, impact guard %.1f (read %s -> %s), pushed %.2f m (floor %.1f m ahead)" % ("homed" if homed else "NOT homed", homed_at, took, g0, g1, pushed, room))
        if not interposed:
            break
        time.sleep(4.2)   # the charge back
    ok = ok and good
    rows.append("forward at a thief 4.5 m away: %s; its distance 0.6 s later %.2f m | %s" % (
        "; ".join(tries), e1["dist"] if e1 else -1, d1.split("| last ")[-1][:240]))
    # back: 3 m
    time.sleep(0.5)
    p0 = enemy("nearest")
    sim("stick l 0 -1 0")
    press_a()
    time.sleep(0.4)
    sim("stick l 0 0 0")
    time.sleep(0.3)
    d2 = send("dash?")
    m = re.search(r"last \w+ ([\d.]+) m of ([\d.]+)", d2)
    moved = float(m.group(1)) if m else -1
    good = m is not None and (abs(moved - 3.0) < 0.2 or "edge" in d2 or moved > 1.0)
    ok = ok and good
    rows.append("back: %.2f m of %s" % (moved, m.group(2) if m else "?"))
    # charges: three, then refused; back 4 s after use. Each press with 6 m of floor behind: UYF's landing fails a dash
    # with under 0.5 m of room and uses no charge (MR4b: after two dashes back the hero stood at the arena's end - 1 dash)
    time.sleep(4.5)
    n0 = nums(send("dash?"))
    for _ in range(4):
        send("dash? edge 6 180")
        press_a()
        time.sleep(0.35)
    n1 = nums(send("dash?"))
    dashes, refused = n1.get("dashes", 0) - n0.get("dashes", 0), n1.get("refused", 0) - n0.get("refused", 0)
    failed = n1.get("failed", 0) - n0.get("failed", 0)
    time.sleep(4.2)
    n2 = nums(send("dash?"))
    good = dashes == 3 and refused == 1 and failed == 0 and n2.get("charges", 0) >= 3
    ok = ok and good
    rows.append("four presses 0.35 s apart: %d dashes, %d refused, %d failed; 4.2 s later charges %s" % (dashes, refused, failed, n2.get("charges")))
    send("vrknock setting")
    send("vrmagnet setting")
    send("vrwaiting setting")
    result("dash", ok, "; ".join(rows))


def dash_last(d):
    m = re.search(r"\| last \w+ ([\d.]+) m of ([\d.]+)", d)
    return (float(m.group(1)), float(m.group(2))) if m else (-1.0, -1.0)


def find_edge(room):
    """Turns the simulated head (0, 90, 180, 270 deg) until the floor ends within 30 m behind the gaze, then moves the hero
    so it ends `room` m behind it (`dash? edge`). Returns the seam's answer."""
    e = ""
    for yaw in (0, 90, 180, 270):
        sim("head 0 1.7 0 %d 0 0" % yaw)
        time.sleep(0.3)
        e = send("dash? edge %.2f 180" % room)
        if "edge now" in e:
            return e
    return e


def part_backdash():
    """(merge-ready #4) UYF's back dash covers what it plans: in the open its 3 m; near the floor's end it lands 1 m before
    the obstacle (UYF's `_minDistanceToObstacle`) and covers that; with under 0.5 m of room it fails - no move, no charge;
    an enemy in its line is nudged aside (1 m)."""
    send("vrdash uyf")
    send("vrknock off")
    send("vrmagnet off")
    rows = []
    ok = True
    reset_pose()
    time.sleep(0.5)
    rows.append("where the fight began: " + send("dash? free 180"))
    for name, room in (("open", 6.0), ("edge", 2.4), ("wall", 0.9)):
        time.sleep(4.3)   # every charge back (4 s after use)
        placed = find_edge(room) if name != "open" else send("dash? edge %.2f 180" % room)
        if name == "open" and "edge now" not in placed and "no edge" not in placed:
            placed = find_edge(room)
        f = send("dash? free 180")
        n0 = nums(send("dash?"))
        press_a()   # the stick centred: straight back from the gaze
        time.sleep(0.6)
        d = send("dash?")
        n1 = nums(d)
        moved, of = dash_last(d)
        fails = n1.get("failed", 0) - n0.get("failed", 0)
        dashes = n1.get("dashes", 0) - n0.get("dashes", 0)
        short = n1.get("shortened", 0) - n0.get("shortened", 0)
        last = d.split("| last ")[-1].split(" | impact")[0]
        gm = re.search(r"gap ([\d.]+)", f)
        fm = re.search(r"free ([\d.]+)", f)
        gap = float(gm.group(1)) if gm else 0.7
        free = float(fm.group(1)) if fm else 99.0
        if name == "wall":
            good = fails == 1 and dashes == 0 and n1.get("charges", 0) == n0.get("charges", -1) and last.startswith("failed")
        else:
            want = min(3.0, free - gap)
            good = fails == 0 and dashes == 1 and abs(of - want) < 0.1 and abs(moved - of) < 0.1 and (short == (1 if name == "edge" else 0))
        ok = ok and good
        rows.append("%s (%s; %s): %s; failed +%d, dashes +%d, shortened +%d, charges %s -> %s: %s" % (
            name, placed[:70], f[:60], "%.2f m of %.2f" % (moved, of) if name != "wall" else "no move", fails, dashes, short,
            n0.get("charges"), n1.get("charges"), "ok" if good else "WRONG | " + last[:160]))
    # an enemy straight behind, 1.5 m: the dash passes it, the enemy nudged aside
    time.sleep(4.3)
    e = enemy("nearest")
    if e:
        who = e["id"]
        send("aside %s 3" % who)
        send("heroat %s 0 1.5" % who)
        sim("head 0 1.7 0 180 0 0")   # the gaze away from it: it stands straight behind
        time.sleep(0.4)
        e0 = enemy(who)
        n0 = nums(send("dash?"))
        press_a()
        time.sleep(0.6)
        d = send("dash?")
        e1 = enemy(who)
        n1 = nums(d)
        moved, of = dash_last(d)
        nudged = n1.get("nudges", 0) - n0.get("nudges", 0)
        shift = sum((a - b) ** 2 for a, b in zip(e1["at"], e0["at"])) ** 0.5 if e0 and e1 else -1
        good = nudged >= 1 and shift >= 0.5 and of > 1.5
        ok = ok and good
        rows.append("an enemy 1.5 m behind: nudges +%d, it moved %.2f m; the dash %.2f m of %.2f%s" % (nudged, shift, moved, of, "" if good else " WRONG"))
    reset_pose()
    send("vrknock setting")
    send("vrmagnet setting")
    result("backdash", ok, "; ".join(rows))


def part_flick():
    send("vrdash uyf")
    send("vrdashflick on")
    time.sleep(4.2)
    n0 = nums(send("dash?"))
    sim("stick r 0 1 120")
    time.sleep(0.6)
    sim("stick r 0 -1 120")
    time.sleep(0.6)
    d = send("dash?")
    n1 = nums(d)
    flicks = n1.get("flicks", 0) - n0.get("flicks", 0)
    dashes = n1.get("dashes", 0) - n0.get("dashes", 0)
    snaps0 = send("sget CombatVR SnapTurns")
    sim("stick r 1 0 150")
    time.sleep(0.4)
    snaps1 = send("sget CombatVR SnapTurns")
    result("flick", flicks == 2 and dashes == 2 and "flick back" in d, "the right stick forward, then back: flicks +%d, dashes +%d (%s); right: snap turns %s -> %s" % (
        flicks, dashes, d.split("| last ")[-1][:160], snaps0, snaps1))


def part_shortdash():
    send("vrdash short")
    time.sleep(2.2)
    sim("stick l 0 -1 0")
    press_a()
    time.sleep(0.5)
    sim("stick l 0 0 0")
    time.sleep(0.3)
    d = send("dash?")
    m = re.search(r"last \w+ ([\d.]+) m of ([\d.]+)", d)
    send("vrdash setting")
    result("shortdash", m is not None and abs(float(m.group(2)) - 2.0) < 0.05, "Dash = Short: %s" % d.split("| last ")[-1][:160])


def part_health():
    eh = send("sget Settings EnemyHealth")
    ws = send("sget CombatSettings WalkSpeed")
    m1 = re.search(r"([\d.]+)\s*$", eh)
    m2 = re.search(r"([\d.]+)\s*$", ws)
    # EnemyHealth 0.15 since the user's playtest of 2026-10-17 (the old defaults 0.25 -> 1.0 -> 0.15 carried over once each)
    ok = m1 and abs(float(m1.group(1)) - 0.15) < 0.01 and m2 and abs(float(m2.group(1)) - 1.8) < 0.01
    result("health", bool(ok), "EnemyHealth %s, WalkSpeed %s (the old defaults 0.25 -> 1.0 -> 0.15 and 2.2 carried over once)" % (eh, ws))


# ------------------------------------------------------------------------------------------------ item 4: the trail

def part_trail():
    send("vrtrail on")
    reset_pose()
    # the hand placed first and left to settle (a placement is one frame's jump: no swing), then the count reset
    sim("hand r 0.8 1.4 -0.3 0 5 0")
    time.sleep(0.4)
    send("vrtrail reset")
    sim("move r -1.6 -0.2 -0.15 170")   # a fast swing (~9 m/s at the tip)
    time.sleep(0.4)
    fast = send("vrtrail")
    sim("hand r 0.8 1.4 -0.3 0 5 0")
    time.sleep(0.4)
    send("vrtrail reset")
    sim("move r -1.0 0 0 900")   # slow (~1.1 m/s)
    time.sleep(1.2)
    slow = send("vrtrail")
    reset_pose()
    pf = re.search(r"peak alpha ([\d.]+)", fast)
    ps = re.search(r"peak alpha ([\d.]+)", slow)
    ok = pf and float(pf.group(1)) > 0.5 and ps and float(ps.group(1)) < 0.05
    result("trail", bool(ok), "fast swing: %s | slow move: %s" % (fast, slow))


# ------------------------------------------------------------------------------------------------ item 5: smart snap turn

def part_smartsnap():
    e = enemy("nearest")
    if not e:
        return result("smartsnap", False, "no enemy")
    who = e["id"]
    send("aside %s 4" % who)
    send("face " + who)
    reset_pose()
    time.sleep(0.3)
    # turn the head 70 deg left: the enemy now 70 deg to the right
    sim("head 0 1.7 0 70 0 0")
    time.sleep(0.3)
    send("vrsmartsnap on")
    right = send("vrsmartsnap probe right")
    send("vrsmartsnap off")
    fixed = send("vrsmartsnap probe right")
    send("vrsmartsnap setting")
    sim(HEAD)
    mr = re.search(r"step (-?\d+)", right)
    mf = re.search(r"step (-?\d+)", fixed)
    ok = mr and mf and 8 <= int(mr.group(1)) <= 190 and abs(int(mr.group(1)) - int(mf.group(1))) > 5
    result("smartsnap", bool(ok), "an enemy ~70 deg right: smart %s | fixed %s" % (right, fixed))


# ------------------------------------------------------------------------------------------------ item 7: a stopped shot

def part_shotstop():
    send("shots? reset")
    s0 = nums(send("super?"))
    t0 = time.time()
    stopped = 0
    while time.time() - t0 < 120 and stopped == 0:
        a = send("shotat")
        if "cross" not in a:
            time.sleep(0.05)
            continue
        g = send("shotguard")
        mm = re.match(r"(move [lr] \S+ \S+ \S+)", g)
        if mm:
            sim(mm.group(1) + " 50")
        time.sleep(1.2)
        sh = nums(send("shots?"))
        stopped = int(sh.get("defended", 0) + sh.get("deflected", 0))
    sh = send("shots?")
    s1 = nums(send("super?"))
    fb = nums(sh).get("stopFeedback", 0)
    result("shotstop", stopped >= 1 and fb >= 1 and s1.get("super", 0) > s0.get("super", 0), "stopped %d, stop feedback %d, super %s -> %s | %s" % (
        stopped, fb, s0.get("super"), s1.get("super"), sh[:200]))


def part_bladeshot():
    send("shots? reset")
    stopped = hit = 0
    t0 = time.time()
    while time.time() - t0 < 150 and (stopped == 0 or hit == 0):
        if user_in_vr():
            return
        a = send("shotat")
        m = re.search(r"eta ([\d.]+) blockable (\w+)", a)
        if not m:
            time.sleep(0.05)
            continue
        if m.group(2) != "True":
            time.sleep(0.2)
            continue
        if stopped == 0:
            # the shield hand down at the side (out of the line), the sword's middle onto the shot's crossing point
            sim("hand l -0.45 0.8 0.2 0 0 0")
            g = send("shotblade")
            mm = re.match(r"(move [lr] \S+ \S+ \S+)", g)
            if mm:
                sim(mm.group(1) + " 50")
            time.sleep(float(m.group(1)) + 0.6)
            stopped = int(nums(send("shots?")).get("bladeStops", 0))
        else:
            sim(HANDS_DOWN.replace("hand l -0.32 0.85 0.05", "hand l -0.45 0.8 0.2"))
            time.sleep(float(m.group(1)) + 0.6)
            hit = int(nums(send("shots?")).get("hitsTaken", 0))
    sh = send("shots?")
    reset_pose()
    result("bladeshot", stopped >= 1 and hit >= 1, "the blade on the line stopped %d; with it down, hits %d | %s" % (stopped, hit, sh[:240]))


# ------------------------------------------------------------------------------------------------ item 13: the super

def part_super():
    send("super? set 100")
    time.sleep(1.0)
    a = nums(send("super?"))
    time.sleep(1.0)
    b = nums(send("super?"))
    send("abilitycharge 3")
    r = send("abilitytest weapon nearest", 10)
    time.sleep(0.8)
    c = send("super?")
    nc = nums(c)
    ok = b.get("glowFrames", 0) > a.get("glowFrames", 0) and nc.get("activations", 0) >= 1
    result("super", ok, "full: glow frames %s -> %s; the special used (%s): %s" % (a.get("glowFrames"), b.get("glowFrames"), r[:80], c[:200]))


# ------------------------------------------------------------------------------------------------ garbage (main's GC work)

COMBAT_TYPES = ("WeaponTrail", "SuperMeter", "DashMarker", "SeatedRules", "Guard", "Poise", "Tickets", "BladeHits", "CombatMove",
                "Physical", "ThreatWarning", "Threats", "EnemyBars", "Premonition", "ShieldShots", "KillLaunch", "CombatVR",
                "IndicatorLooks", "CombatFeel", "CameraShake", "ScreenFlash", "WeaponFeel", "EnemyPush")


def part_alloc():
    send("god on")
    send("vrtrail on")
    send("super? set 100")
    log0 = os.path.getsize(hof2env.GLOG) if os.path.exists(hof2env.GLOG) else 0
    send("profile alloc", 60)
    time.sleep(1.0)
    t0 = time.time()
    n = 0
    while time.time() - t0 < 30:
        if user_in_vr():
            return
        send("face nearest")
        # fast swings both ways (the trail draws, the blade hits record) and a look round
        sim("hand r 0.6 1.4 -0.35 0 5 0; wait 150; move r -1.2 -0.1 -0.1 160; wait 250; move r 1.2 0.1 0.1 160; wait 250")
        n += 1
        time.sleep(1.4)
    r = send("profile allocs 40", 20)
    # the report goes to the game's log, which is written to disk in batches (MR2c: read after 1 s, nothing there yet)
    rows = []
    for _ in range(40):
        time.sleep(0.5)
        with open(hof2env.GLOG, errors="replace") as f:
            f.seek(log0)
            text = f.read()
        i = text.rfind("alloc window")
        rep = text[i:i + 6000] if i >= 0 else ""
        rows = [(float(m.group(1)), m.group(2)) for m in re.finditer(r"^\s+([\d.]+) KB/s \[mod\] (\S+)", rep, re.M)]
        if rows:
            break
    ours = [(kb, name) for kb, name in rows if name.split(".")[0] in COMBAT_TYPES]
    # the night's new per-frame parts must make little: the profiler credits each new heap block to whatever runs at the
    # time (MR2c: WeaponTrail 20 KB/s, EnemyBars 150, Guard 61), so only a gross allocator fails; the rest is reported
    NEW = ("WeaponTrail", "SuperMeter", "DashMarker", "SeatedRules")
    bad = [(kb, name) for kb, name in ours if name.split(".")[0] in NEW and kb > 40.0]
    send("super? set 0")
    result("alloc", bool(rows) and not bad, "%d swings; %s | the combat side's: %s | the mod's top: %s" % (
        n, r[:120], ", ".join("%s %.1f KB/s" % (nm, kb) for kb, nm in ours[:12]) or "none",
        ", ".join("%s %.1f" % (nm, kb) for kb, nm in rows[:8])))


# ------------------------------------------------------------------------------------------------ shots for the report

def look_shot(name):
    p = H + "/testdata/shots/%s_look_%s_eyes.png" % (TAG, name)
    try:
        os.remove(p)
    except OSError:
        pass
    send("shot %s_look_%s" % (TAG, name), 20)
    for _ in range(40):
        if os.path.exists(p):
            return p
        time.sleep(0.2)
    return None


def part_looks():
    rows = []
    send("vrtrail on")
    reset_pose()
    # the shield hand down at the side, out of the view (MR2c: the shield covered half of every shot)
    away = "hand l -0.6 0.7 0.15 0 0 0"
    # the trail: a long, steady sweep across the view (~4.5 m/s), the shot a third of the way in
    sim(away + "; hand r 0.9 1.6 -0.55 0 5 0")
    time.sleep(0.5)
    sim("move r -2.0 -0.4 0 450")
    time.sleep(0.15)
    rows.append("trail %s" % look_shot("trail"))
    reset_pose()
    time.sleep(0.6)
    # the dash's landing ring: the right stick leaning forward short of a flick (the preview)
    send("vrdash uyf")
    send("vrdashflick on")
    sim(away + "; stick r 0 0.5 0; head 0 1.7 0 0 -20 0")
    time.sleep(0.5)
    rows.append("dashring %s" % look_shot("dashring"))
    sim("stick r 0 0 0; " + HEAD)
    time.sleep(0.4)
    # the super's glow: the meter full, the sword held up in view
    send("super? set 100")
    sim(away + "; hand r 0.2 1.45 -0.45 0 30 0")
    time.sleep(0.8)
    rows.append("superglow %s" % look_shot("superglow"))
    send("super? set 0")
    reset_pose()
    # the difficulty flash: Hard's orange, 1.5 s
    sim(away)
    send("vrdiffflash on")
    send("vrdiff hard")
    time.sleep(0.15)
    rows.append("diffflash %s" % look_shot("diffflash"))
    send("vrdiff setting")
    send("vrdiffflash setting")
    ok = all(" None" not in r for r in rows)
    result("looks", ok, "; ".join(rows))


def part_exitdash():
    send("vrdash uyf")
    send("killall")
    pending = False
    t0 = time.time()
    while time.time() - t0 < 15:
        if "no exit pending" not in send("exitcard info"):
            pending = True
            break
        time.sleep(0.5)
    d0 = nums(send("dash?"))
    sim("btn r a 120")
    time.sleep(0.6)
    d1 = nums(send("dash?"))
    dashes = int(d1.get("dashes", 0) - d0.get("dashes", 0))
    result("exitdash", pending and dashes == 0, "the won fight's exit pending: %s; a short A press: dashes +%d" % (pending, dashes))


# ------------------------------------------------------------------------------------------------ item 14: C6, I13, F5

def part_circle():
    e = enemy("nearest")
    if not e:
        return result("circle", False, "no enemy")
    who = e["id"]
    send("aside %s 4" % who)
    send("heroat %s 0 2.4" % who)
    send("face " + who)
    reset_pose()
    time.sleep(0.3)
    send("vrcircle on")
    on = send("vrcircle probe")
    send("vrcircle off")
    off = send("vrcircle probe")
    send("vrcircle setting")
    rx = r"at ([\d.]+) m; after 1 s: round it ([\d.]+) m \(([\d.]+) deg round\), straight ([\d.]+) m"
    m, mo = re.search(rx, on), re.search(rx, off)
    ok = bool(m and mo)
    if ok:
        d0, rnd, deg, st = (float(m.group(i)) for i in (1, 2, 3, 4))
        ok = abs(rnd - d0) < 0.08 and st - d0 > 0.2 and deg >= 20 and abs(float(mo.group(2)) - float(mo.group(4))) < 0.01
    result("circle", ok, "on: %s | off: %s" % (on, off))


def part_glovehud():
    reset_pose()
    send("vrglovehud on")
    time.sleep(0.4)
    a, sa = send("hands health"), send("super?")
    send("vrglovehud off")
    time.sleep(0.4)
    b, sb = send("hands health"), send("super?")
    send("vrglovehud setting")
    time.sleep(0.4)
    c = send("hands health")
    glove = lambda t: t.split("on the glove")[-1]
    ok = "hidden" not in a and "hidden" not in glove(sa) and "hidden" in b and "hidden" in glove(sb) and "hidden" not in c
    result("glovehud", ok, "on: %s / super%s | off: %s / super%s | back: %s" % (a, glove(sa)[:40], b, glove(sb)[:40], c))


def part_weaponsound():
    send("vrweaponsound on")
    reset_pose()
    time.sleep(0.8)
    rest = send("vrweaponsound")
    sim("hand r 0.8 1.5 -0.4 0 5 0")
    time.sleep(0.4)
    send("vrweaponsound reset")
    # (xrsim's move returns when it ends: the loudest during it is read - MR3b read 0.12 s after it, the rush settled)
    sim("move r -1.6 -0.3 0 200")   # ~8 m/s at the tip
    fast = send("vrweaponsound")
    send("vrweaponsound off")
    time.sleep(0.2)
    off = send("vrweaponsound")
    send("vrweaponsound setting")
    reset_pose()
    vol = lambda t: float(re.search(r"volume ([\d.]+)", t).group(1)) if re.search(r"volume ([\d.]+)", t) else -1.0
    peak = lambda t: float(re.search(r"peak ([\d.]+)", t).group(1)) if re.search(r"peak ([\d.]+)", t) else -1.0
    vr, vf, vo = vol(rest), peak(fast), vol(off)
    ok = 0.0 < vr <= 0.075 and vf >= vr * 1.4 and vo == 0.0
    result("weaponsound", ok, "rest: %s | fast: %s | off: %s" % (rest, fast, off))


def part_worldtouch():
    send("world? reset")
    reset_pose()
    # the shield hand out of the way; the sword hanging point down above the floor, then down into it, then along it
    sim("hand l -0.6 0.7 0.15 0 0 0; hand r 0.35 1.3 -0.45 0 -80 0")
    time.sleep(0.5)
    sim("move r 0 -0.9 0 150")
    time.sleep(0.3)
    a = send("world?")
    sim("move r -0.6 0 0 350")
    time.sleep(0.5)
    b = send("world?")
    reset_pose()
    imp = int(re.search(r"impacts (\d+)", a).group(1)) if re.search(r"impacts (\d+)", a) else 0
    scr = int(re.search(r"scrapes (\d+)", b).group(1)) if re.search(r"scrapes (\d+)", b) else 0
    result("worldtouch", imp >= 1 and scr >= 2, "down into the floor: %s | dragged along it: %s" % (a, b))


def part_bossdeath():
    b0 = send("bossdeath?")
    send("tough off")
    send("killall")
    time.sleep(0.5)
    mid = send("bossdeath?")   # merge-ready #4: UYF's slow motion, 0.1x for 1.5 s
    time.sleep(1.5)
    b1 = send("bossdeath?")
    num = lambda t: int(re.search(r"boss deaths (\d+)", t).group(1)) if re.search(r"boss deaths (\d+)", t) else -1
    m, e = nums(mid), nums(b1)
    slow = e.get("motions", 0) > nums(b0).get("motions", 0) and m.get("now", 1) <= 0.15 and e.get("now", 0) >= 0.9
    ok = num(b1) > num(b0) >= 0 and "boss death" in b1.split("last flash")[-1] and slow
    result("bossdeath", ok, "%s -> 0.5 s: %s -> 2 s: %s" % (b0, mid, b1))


# ------------------------------------------------------------------------------------------------ item 8: waiting enemies

def waiting_rows(s):
    return [{"who": m.group(1), "id": m.group(2), "role": m.group(3), "dist": float(m.group(4)), "lo": float(m.group(5)),
             "hi": float(m.group(6)), "age": float(m.group(7)), "act": (m.group(8) or "").strip()}
            for m in re.finditer(r"\| (\S+)#(-?\d+) (\w+) (-?[\d.]+) m band ([\d.]+)-([\d.]+) for ([\d.]+) s( act [^|]*)?", s)]


def guard_strikes():
    m = re.search(r"\| strikes (\d+) blocks", send("pcombat?"))
    return int(m.group(1)) if m else 0


def median(v):
    v = sorted(v)
    return ("%.2f" % v[len(v) // 2]) if v else "-"


def waiting_phase(on, secs):
    send("vrwaiting " + ("on" if on else "off"))
    sim(HEAD + "; " + HANDS_DOWN)
    s0, t0 = guard_strikes(), time.time()
    tk0 = nums(send("tickets?"))
    inband = {"ondeck": [0, 0], "standby": [0, 0]}
    near = {}
    near_acts = {}   # what a too-near waiter's body ran (its actions, ActionMoveAI's state and blocks)
    far, far_acts, seen = {}, {}, []   # the same for a too-far one; a few such samples whole
    dists = {}
    # a stunned body cannot walk to its band (guards now break in 2-3 blows: the env 3 regression of 2026-10-19, p3i's
    # "too near" standbys were ActionStunned 12/19 and then their walk back): a stunned waiter, and the same one for 4 s
    # after it, is "recovering" - counted apart, not judged
    stunned_at, recovering = {}, 0
    while time.time() - t0 < secs and not user_in_vr():
        now = time.time()
        for r in waiting_rows(send("waiting?")):
            dists.setdefault(r["role"], []).append(r["dist"])
            if "ActionStunned" in r["act"]:
                stunned_at[r["id"]] = now
            if r["role"] in inband and r["age"] >= 4.0 and now - stunned_at.get(r["id"], -99.0) < 4.0:
                recovering += 1
                continue
            if r["role"] in inband and r["age"] >= 4.0:
                inband[r["role"]][1] += 1
                if r["lo"] - 0.4 <= r["dist"] <= r["hi"] + 0.4:
                    inband[r["role"]][0] += 1
                else:
                    side, acts = (near, near_acts) if r["dist"] < r["lo"] - 0.4 else (far, far_acts)
                    side[r["role"]] = side.get(r["role"], 0) + 1
                    k = r["role"] + " " + re.sub(r" in [\d.]+.*$", "", re.sub(r"^act ", "", r["act"]))
                    acts[k] = acts.get(k, 0) + 1
                    if len(seen) < 12:
                        seen.append("%s %.2f%s" % (r["role"], r["dist"], re.sub(r"^act \S+", "", r["act"])))
        time.sleep(0.5)
    tk1 = nums(send("tickets?"))
    return {"strikes": guard_strikes() - s0, "secs": time.time() - t0, "inband": inband, "dists": dists,
            "timeouts": tk1.get("approachTimeouts", 0) - tk0.get("approachTimeouts", 0),
            "demotions": tk1.get("demotions", 0) - tk0.get("demotions", 0), "raw": send("waiting?"), "near": near,
            "near_acts": near_acts, "far": far, "far_acts": far_acts, "seen": seen, "recovering": recovering}


def part_waiting():
    """UYF's waiting enemies (item 8): the next in turn at its GuardedRange, the others at their InWaitingRange."""
    send("vrknock off")   # the hero stays put: each blow taken pushed it 1.6 m, and every distance jumped with it (MR3i)
    try:
        on = waiting_phase(True, 45)
        off = waiting_phase(False, 45)
    finally:
        send("vrwaiting setting")
        send("vrknock setting")
    (ad, nd), (as_, ns) = on["inband"]["ondeck"], on["inband"]["standby"]
    fd, fs = (ad / nd if nd else 0.0), (as_ / ns if ns else 0.0)
    left = waiting_rows(off["raw"])
    r_on, r_off = on["strikes"] / max(on["secs"], 1.0), off["strikes"] / max(off["secs"], 1.0)
    ok = ns >= 10 and fs >= 0.6 and (nd < 10 or fd >= 0.6) and not left and on["strikes"] >= 3 and r_on >= 0.5 * r_off
    result("waiting", ok, "on: standby in its band %d/%d, on deck %d/%d (medians: standby %s, on deck %s, attacker %s m); %d strikes in %.0f s, "
           "approach time-outs %d, demotions %d; recovering from a stun (not judged) %d; too near (on deck / standby) %d / %d (%s); too far %d / %d (%s); samples: %s | off: %d copies left, %d strikes in %.0f s | %s"
           % (as_, ns, ad, nd, median(on["dists"].get("standby", [])), median(on["dists"].get("ondeck", [])),
              median(on["dists"].get("attacker", [])), on["strikes"], on["secs"], on["timeouts"], on["demotions"], on["recovering"],
              on["near"].get("ondeck", 0), on["near"].get("standby", 0),
              ", ".join("%s %d" % kv for kv in sorted(on["near_acts"].items(), key=lambda kv: -kv[1])[:6]) or "-",
              on["far"].get("ondeck", 0), on["far"].get("standby", 0),
              ", ".join("%s %d" % kv for kv in sorted(on["far_acts"].items(), key=lambda kv: -kv[1])[:6]) or "-",
              "; ".join(on["seen"]) or "-", len(left),
              off["strikes"], off["secs"], on["raw"][:400]))


def part_pauses():
    """(merge-ready #4, audit E14) UYF's pause after each attack, drawn from the enemy's short / med / long pause (med after
    an attack stopped part-way); `vrpauses off` (VariedPauses = false): nothing drawn, Med as before."""
    send("vrknock off")
    sim(HEAD + "; " + HANDS_DOWN)
    try:
        send("vrpauses on")
        send("vrpauses reset")
        time.sleep(50)
        on = send("vrpauses")
        send("vrpauses off")
        send("vrpauses reset")
        time.sleep(25)
        off = send("vrpauses")
    finally:
        send("vrpauses setting")
        send("vrknock setting")
        reset_pose()
    n, f = nums(on), nums(off)
    draws = sum(n.get(k, 0) for k in ("short", "med", "long"))
    kinds = sum(1 for k in ("short", "med", "long") if n.get(k, 0) > 0)
    offdraws = sum(f.get(k, 0) for k in ("short", "med", "long", "stopped"))
    ok = draws >= 4 and kinds >= 2 and offdraws == 0
    result("pauses", ok, "on (50 s): %s | off (25 s): %s | %s" % (on, off, send("enemytable? nearest")[:220]))


def part_idlebonus():
    """(merge-ready #4) UYF's idle bonus: an enemy whose turn ended under 6 s ago ranks below the others - with three
    thieves the turns go round (few repeats: the same enemy's turn back within 6 s); without it (`tickets? idle off`)
    the repeats are reported."""
    send("vrknock off")
    send("vrwaiting on")
    sim(HEAD + "; " + HANDS_DOWN)
    try:
        # short turns (2 s each, UYF's are 4-10 s): an enemy's turn back within 6 s is possible, and the bonus tells (MR4c: with
        # UYF's 7 s turns, longer than the bonus's 6 s, neither way repeated)
        send("tickets? possession 2")
        send("tickets? reset")
        send("tickets? idle on")
        time.sleep(60)
        on = send("tickets?")
        why_on = send("tickets? why")
        send("tickets? reset")
        send("tickets? idle off")
        time.sleep(60)
        off = send("tickets?")
        why_off = send("tickets? why")
    finally:
        send("tickets? idle on")
        send("tickets? possession off")
        send("vrwaiting setting")
        send("vrknock setting")
        reset_pose()
    log("idlebonus why (with): " + why_on[:900])
    log("idlebonus why (without): " + why_off[:900])
    n, f = nums(on), nums(off)
    ron = n.get("repeats", 0) / max(1.0, n.get("turns", 0))
    roff = f.get("repeats", 0) / max(1.0, f.get("turns", 0))
    ok = n.get("turns", 0) >= 8 and ron <= 0.25
    result("idlebonus", ok, "with the bonus: %d turns, %d repeats (%.0f %%) | without: %d turns, %d repeats (%.0f %%) | %s" % (
        n.get("turns", 0), n.get("repeats", 0), 100 * ron, f.get("turns", 0), f.get("repeats", 0), 100 * roff, on[:300]))


def engaged():
    m = re.search(r"engaged (\S+)#(-?\d+)", send("tickets?"))
    return ("#" + m.group(2), m.group(1)) if m else (None, None)


def part_approach():
    """UYF's approach time-out (item 8): a holder kept away gives the turn up; its priority drops (the next turn: another)."""
    send("vrwaiting on")
    sim(HEAD + "; " + HANDS_DOWN)
    rows = []
    ok = False
    try:
        for attempt in range(6):
            # a fresh turn (a holder that has come into reach has ended its approach: no time-out for it any more)
            who, name = None, None
            t0 = time.time()
            while time.time() - t0 < 25 and who is None:
                m = re.search(r"engaged (\S+)#(-?\d+) for ([\d.]+) s", send("tickets?"))
                if m and float(m.group(3)) <= 0.4:
                    who, name = "#" + m.group(2), m.group(1)
                else:
                    time.sleep(0.05)
            if who is None:
                rows.append("no fresh turn in 25 s")
                break
            tk0 = nums(send("tickets?"))
            # 8 m off (beyond a thief's running attack, which from 5 m reaches the hero: its turn then ends in a blow, not a
            # time-out - MR3e), placed again every 0.15 s: it never comes nearer
            t1, fired, acts, place = time.time(), False, set(), "-"
            while time.time() - t1 < 8:
                for ang in (0, 40, -40, 80, -80, 120, -120):
                    r = send("heroat %s %d 8" % (who, ang))
                    if "no ground" not in r:
                        place = "%d deg" % ang
                        break
                am = re.search(r" action (\S+)", send("enemy? " + who))
                if am:
                    acts.add(am.group(1))
                tk = nums(send("tickets?"))
                if tk.get("approachTimeouts", 0) > tk0.get("approachTimeouts", 0):
                    fired = True
                    break
                time.sleep(0.15)
            took = time.time() - t1
            tk1 = nums(send("tickets?"))
            last = re.search(r"last (.*?)( \| |$)", send("tickets?"))
            nxt = None
            t2 = time.time()
            while time.time() - t2 < 3:
                nxt, _ = engaged()
                if nxt is not None:
                    break
                time.sleep(0.1)
            rows.append("%s %s: timed out %s after %.1f s kept 8 m away (%s; its actions %s), demotions +%d, last '%s', next turn %s"
                        % (name, who, fired, took, place, ",".join(sorted(acts)), tk1.get("demotions", 0) - tk0.get("demotions", 0), last.group(1) if last else "?", nxt))
            # the next turn is reported, not judged: the priority drop lasts 1-2 s and HoF2 gives the turn back to the last
            # attacker when nobody else may attack (MR3g: the same soldier again)
            if fired and tk1.get("demotions", 0) > tk0.get("demotions", 0):
                ok = True
                break
            time.sleep(1.0)   # no time-out (it attacked - a turn that ends in a blow - or blocked): another try
    finally:
        send("vrwaiting setting")
        send("heroat nearest 0 3")
    result("approach", ok, "; ".join(rows))


# ------------------------------------------------------------------------------------------------ audit E3: combo density

def part_combogaps():
    """UYF's combo density (audit E3): a run's later blows keep their gaps, their markers shown ahead at their own lead."""
    sim(HEAD + "; " + HANDS_DOWN)
    # the off-view warning off for this run (`cfg`, not saved): a later blow whose telegraph came out of view waits the
    # off-view lead by design and is left out of the gaps (offHeld) - with it on, the shadows' runs were judged in view in
    # 1 of 5 runs (round 2, 2026-10-17: 2 of 14 judged in 151 s, all held; 6 of 20, all held, facing the striker). The
    # off-view lead has its own tests; this one samples the runs' rhythm
    ovw = re.search(r"=(\w+)", send("cfg OffViewWarning"))
    try:
        send("cfg OffViewWarning false")
        send("vrcombogaps on")
        send("ahead? reset")
        lead0 = nums(send("pcombat?")).get("leadShort", 0)
        # its blows in view: off view a marker stays hidden and its blow is not counted (MR3i); the turn holder faced (MR4e: the
        # nearest is often a waiter). A run still began off view in 3 of 5 runs (MR4e/MR4f: nothing judged in view); the view
        # kept on the holder every frame (`face track holder`, MR4g) brought fewer runs, none judged in view. So: 75 s, then
        # on (up to 150 s) until a run that began in view has been judged - every run samples the rhythm
        # round 2 (2026-10-17): the view on the attacker whose blow is live (`face striker`), else the holder, every 0.3 s - a
        # run's later blows then come in view, so their gaps are sampled: with the holder faced every 1 s, 2 of 14 blows made
        # ahead were judged in 151 s, both held for an off-view telegraph (rel17 r1; round 2's diagnostics twice)
        t0 = time.time()
        next_holder = 0.0
        while time.time() - t0 < 150 and not user_in_vr():
            if "facing" not in send("face striker") and time.time() >= next_holder:
                h, _ = engaged()
                send("face " + (h if h else "nearest"))
                next_holder = time.time() + 1.0
            time.sleep(0.3)
            if time.time() - t0 >= 75:
                a0 = nums(send("ahead?"))
                if a0.get("judged", 0) > a0.get("offHeld", 0) and a0.get("gapMax", 0) > 0:
                    break
        took = time.time() - t0
        on = send("ahead?")
        a = nums(on)
        pc1 = nums(send("pcombat?"))
        short_on = pc1.get("leadShort", 0) - lead0
        replans = pc1.get("replans", 0)
        send("vrcombogaps off")
        send("ahead? reset")
        t1 = time.time()
        while time.time() - t1 < 45 and not user_in_vr():
            time.sleep(1.0)
        off = send("ahead?")
        b = nums(off)
    finally:
        send("face track off")
        send("vrcombogaps setting")
        send("cfg OffViewWarning " + (ovw.group(1) if ovw else "true"))
    ok = (a.get("made", 0) >= 2 and a.get("adopted", 0) >= 1 and a.get("judged", 0) >= 1 and a.get("short", 1) == 0 and a.get("overlap", 0) >= 2
          and a.get("judged", 0) > a.get("offHeld", 0)   # a run that began in view was judged (its gaps sampled)
          and a.get("laterBlows", 0) >= 1 and 0 < a.get("gapMax", 0) < 0.85 and b.get("made", 1) == 0 and b.get("laterBlows", 1) == 0
          and short_on == 0)   # MR4 item 2: a fresh blow's own lead too (candidate 6b: one 0.70-0.87 s of 0.90 per run)
    result("combogaps", ok, "on (%.0f s): %s (short leads of fresh telegraphs %d, holds planned again %d) | off: %s" % (took, on, short_on, replans, off))


def ondeck():
    m = re.search(r"ondeck (\S+)#(-?\d+)", send("tickets?"))
    return ("#" + m.group(2), m.group(1)) if m else (None, None)


def part_shovewaiter():
    """(merge-ready #4, UYF's E7: its shove comes before the ticket checks) An enemy waiting its turn shoves too: the hero kept
    1 m in front of the one on deck (not the turn holder) past its time - it attacks for its shove although the turn is
    another's: shoves? waiterShoves +1, no damage, the hero pushed; the turn stays where it was."""
    send("vrknock setting")
    send("vrshoves on")
    send("vrwaiting on")
    sim(HEAD + "; " + HANDS_DOWN)
    try:
        t0 = time.time()
        who = None
        while time.time() - t0 < 20 and who is None and not user_in_vr():
            h, _ = engaged()
            w, _ = ondeck()
            who = w if (w and w != h) else None
            time.sleep(0.3)
        if not who:
            return result("shovewaiter", False, "no waiter on deck: " + send("tickets?")[:200])
        send("shoves? reset")
        p0 = nums(send("pcombat?"))
        t0 = time.time()
        due = shoved = False
        took = seen_holder = 0
        while time.time() - t0 < 40 and not user_in_vr():
            h, _ = engaged()
            if h == who:
                seen_holder += 1   # its turn came: not a waiter's shove any more - another waiter
                if nums(send("shoves?")).get("waiterShoves", 0) >= 1:
                    shoved = True   # its shove landed just before its turn came: the reset below would have lost it
                    break
                w, _ = ondeck()
                if w and w != h:
                    who = w
                    send("shoves? reset")
            send("heroat %s 0 1.0" % who)
            send("face " + who)
            p0 = nums(send("pcombat?"))
            time.sleep(0.3)
            n = nums(send("shoves?"))
            if n.get("due", 0) >= 1 and not due:
                due, took = True, time.time() - t0
            if n.get("waiterShoves", 0) >= 1:
                shoved = True
                break
        time.sleep(1.0)
        p1 = nums(send("pcombat?"))
        sh = send("shoves?")
        hits = p1.get("hitsTaken", 0) - p0.get("hitsTaken", 0)
        ok = due and shoved and hits == 0 and p1.get("knocked", 0) >= 1.5
        result("shovewaiter", ok, "waiter %s: due %s after %.1f s, shoved while waiting %s, hits taken +%d, pushed %.2f m, its turn came %d times | %s" % (
            who, due, took, shoved, hits, p1.get("knocked", 0), seen_holder, sh[:260]))
    finally:
        send("vrshoves setting")
        send("vrwaiting setting")


def part_shove():
    """(merge-ready #4, UYF's E7) Shoves: the hero kept inside the nearest enemy's range (1 m in front of it) past its time -
    its next attack shoves: no damage (hitsTaken unchanged), the hero pushed back (pcombat's knocked, a soldier's 4 m; walls
    may stop it short); off: nothing comes due."""
    # no knockback until the shove is under way: the holder's own blows taken pushed the hero 1.6 m, out of its reach, and
    # called the due shove off (UYF's timer starts afresh outside the reach) - MR4h: due after 12.5 s, called off, and no
    # other due in the 17 s left, each blow taken starting the time again. Its push needs the knockback: on at its telegraph.
    send("vrknock off")
    send("vrshoves on")
    sim(HEAD + "; " + HANDS_DOWN)
    rows = []
    ok = True
    try:
        # the turn holder: its next attack comes soon (a waiter's waits for its turn, backing off meanwhile - MR4e: called off)
        t0 = time.time()
        who = None
        while time.time() - t0 < 15 and who is None:
            who, _ = engaged()
            time.sleep(0.3)
        if who is None:
            e = enemy("nearest")
            who = e["id"] if e else None
        if not who:
            return result("shove", False, "no enemy")
        send("shoves? reset")
        t0 = time.time()
        due = False
        p0 = nums(send("pcombat?"))
        shoved = False
        knock = False
        while time.time() - t0 < 30 and not user_in_vr():
            send("heroat %s 0 1.0" % who)   # kept 1 m in front of it until the shove lands
            send("face " + who)
            p0 = nums(send("pcombat?"))     # hits taken just before: the shove lands within the next 0.4 s
            time.sleep(0.15 if due else 0.4)
            n = nums(send("shoves?"))
            if n.get("due", 0) >= 1 and not due:
                due, took = True, time.time() - t0
                t1 = time.time()
            if n.get("shoved", 0) >= 1:
                if not knock:
                    # it landed before the knockback was back on (taken and landed within one look): its push was the test's
                    # to lose - once more, from a fresh time inside
                    log("  shove: landed with the knockback still off - again")
                    send("shoves? reset")
                    due = False
                    continue
                shoved = True
                break
            # under way (taken at its telegraph): more due than called off or whiffed, none waiting - the knockback on for its
            # push; off again if it whiffed
            under_way = n.get("due", 0) > n.get("cancelled", 0) + n.get("whiffed", 0) and n.get("waiting", 1) == 0
            if under_way != knock:
                send("vrknock setting" if under_way else "vrknock off")
                knock = under_way
        if not due:
            took, t1 = time.time() - t0, time.time()
        time.sleep(1.0)   # the push runs out
        p1 = nums(send("pcombat?"))
        sh = send("shoves?")
        hits = p1.get("hitsTaken", 0) - p0.get("hitsTaken", 0)
        knocked = p1.get("knocked", 0)
        good = due and shoved and hits == 0 and knocked >= 1.5
        ok = ok and good
        rows.append("on: due %s after %.1f s, shoved %s %.1f s later, hits taken +%d, pushed %.2f m | %s" % (due, took, shoved, time.time() - t1, hits, knocked, sh[:240]))
        # off: nothing comes due
        send("vrshoves off")
        send("shoves? reset")
        t2 = time.time()
        while time.time() - t2 < 8 and not user_in_vr():
            send("heroat %s 0 1.0" % who)
            time.sleep(0.5)
        n = nums(send("shoves?"))
        good = n.get("due", 1) == 0 and n.get("shoved", 1) == 0
        ok = ok and good
        rows.append("off: due %d shoved %d in 8 s inside" % (n.get("due", -1), n.get("shoved", -1)))
    finally:
        send("vrshoves setting")
        send("vrknock setting")
        reset_pose()
    result("shove", ok, "; ".join(rows))


def part_strafe():
    """(merge-ready #4, UYF's E13) Waiting enemies step aside after UYF's StrafeWaitTimer (a thug 4 s): HoF2's idle step timed by
    it and sideways only; off (WaitingEnemies false): no waiter, no strafe."""
    send("vrknock off")
    send("vrwaiting on")
    sim(HEAD + "; " + HANDS_DOWN)
    try:
        send("waiting? reset")
        t0 = time.time()
        while time.time() - t0 < 45 and not user_in_vr():
            send("face nearest")
            time.sleep(1.0)
        on = send("waiting?")
        send("vrwaiting off")
        send("waiting? reset")
        time.sleep(20)
        off = send("waiting?")
    finally:
        send("vrwaiting setting")
        send("vrknock setting")
        reset_pose()
    n, f = nums(on), nums(off)
    ok = n.get("strafes", 0) >= 2 and n.get("strafesTimed", 0) >= 2 and n.get("strafeGapMin", 0) >= 2.5 and f.get("strafes", 1) == 0
    m = re.search(r"strafes .*?lastStrafe [^|]*", on)
    result("strafe", ok, "on (45 s): %s | off (20 s): strafes %d" % (m.group(0) if m else on[:200], f.get("strafes", -1)))


def part_bodylean():
    e = enemy("nearest")
    if not e:
        return result("bodylean", False, "no enemy")
    r = send("bodylean? test %s" % e["id"])
    time.sleep(0.05)
    mid = send("bodylean?")
    time.sleep(0.45)
    end = send("bodylean?")
    ang = re.search(r"angle now ([\d.]+)", mid)
    live_mid = re.search(r"live (\d+)", mid)
    live_end = re.search(r"live (\d+)", end)
    bone = re.search(r"\(([^)]*)\) roll", r)
    ok = "no spine bone" not in r and bone is not None and "cloth" not in bone.group(1).lower() and ang is not None and float(ang.group(1)) > 3.0 and live_mid and int(live_mid.group(1)) >= 1 and live_end and int(live_end.group(1)) == 0
    result("bodylean", bool(ok), "test: %s | 0.05 s: %s | 0.5 s: %s" % (r, mid, end))


def part_neverreach():
    send("swingtrace clear")
    send("swingtrace on")
    log0 = os.path.getsize(hof2env.GLOG) if os.path.exists(hof2env.GLOG) else 0
    reset_pose()
    sim(HANDS_DOWN)
    t0 = time.time()
    while time.time() - t0 < 90:
        if user_in_vr():
            return
        send("face nearest")
        time.sleep(3)
    with open(hof2env.GLOG, errors="replace") as f:
        f.seek(log0)
        text = f.read()
    rows = {}
    for m in re.finditer(r"swing (\w+) \w+ clip (\S+)@([\d.]+) \w+ ([A-Z ]+?) part", text):
        k = "%s %s@%s" % (m.group(1).replace("Model", ""), m.group(2), m.group(3))
        rows.setdefault(k, {}).setdefault(m.group(4).strip(), 0)
        rows[k][m.group(4).strip()] += 1
    far = [k for k in rows if ("Templar_attack_1@0.7" in k or "counter_combo" in k)]
    bad = [k for k in far if rows[k].get("HIT", 0) > 0]
    # item 12's phantoms (the wind-up's hit events: no bar, no damage) and the real blows after them (judged as any blow)
    ph = re.search(r"phantoms (\d+)", send("pcombat?"))
    result("neverreach", bool(rows) and not bad, "%d kinds of blow in 90 s: %s%s | phantoms %s" % (
        len(rows), "; ".join("%s %s" % (k, rows[k]) for k in sorted(rows))[:700], (" | HIT from afar: " + ", ".join(bad)) if bad else "",
        ph.group(1) if ph else "?"))


def lead_rows(raw):
    """`leads?` -> [(model, lead, want, speed)]."""
    return [(m.group(1), float(m.group(2)), float(m.group(3)), float(m.group(4)))
            for m in re.finditer(r"(\w+)/\w+@\w+ ([\d.]+)/([\d.]+) x([\d.]+);", raw)]


LEAD_CUTS0 = [""]


def cut_reasons(cuts):
    """`vrlead cuts` -> {reason: count}."""
    return dict((k, int(v)) for k, v in re.findall(r"([a-z][a-z ]*?) (\d+)(?:,|\))", cuts.split(" last ")[0].split("(", 1)[-1] + ")"))


def lead_phase(mode, secs):
    send("vrlead " + mode)
    send("leads? reset")
    h0 = send("vrlead")
    LEAD_CUTS0[0] = send("vrlead cuts")   # the reasons are counted per game session: the phase's own are the difference
    reset_pose()
    sim(HANDS_DOWN)
    t0 = time.time()
    while time.time() - t0 < secs:
        if user_in_vr():
            break
        h, _ = engaged()   # the turn holder's blows in view (MR4e: the nearest was often a waiter stepping aside)
        send("face " + (h if h else "nearest"))
        time.sleep(1)
    h1 = send("vrlead")
    rows = lead_rows(send("leads?"))
    holds = int(re.search(r"holds (\d+)", h1).group(1)) - int(re.search(r"holds (\d+)", h0).group(1))
    rel = int(re.search(r"released (\d+)", h1).group(1)) - int(re.search(r"released (\d+)", h0).group(1))
    cut = int(re.search(r"cut (\d+)", h1).group(1)) - int(re.search(r"cut (\d+)", h0).group(1))
    return rows, holds, rel, cut, send("vrlead cuts")


def part_leadhold():
    rows, holds, rel, cut, cuts = lead_phase("hold", 40)
    # a blow begun off view gets the off-view lead on top by design (ThreatWarning): not an overshoot
    bad = [r for r in rows if r[1] < r[2] - 0.03 or r[2] + 0.3 < r[1] < r[2] + 0.6]
    speeds = sorted(set("%.2f" % r[3] for r in rows))
    # every hold accounted for (MR4e/MR4f: holds +13, released +10): released, or ended with its strike before its release -
    # the attack stopped short of its blow, the attacker dead, the fight over (`vrlead cuts`). Never one whose blow came
    # while it held (the hit inside the hold) or one left to time out; one may still run at the phase's end
    # this phase's own (gn2r r2: the gnome fight before it in the same game session had left "hit while held 4")
    why, why0 = cut_reasons(cuts), cut_reasons(LEAD_CUTS0[0])
    lost = sum(v - why0.get(k, 0) for k, v in why.items() if k in ("hit while held", "never resolved"))
    ok_hold = len(rows) >= 3 and holds >= 3 and rel + cut >= holds - 1 and lost == 0 and not bad
    v0 = send("vrlead")
    rows_s, holds_s, rel_s, cut_s, _ = lead_phase("slow", 40)
    v1 = send("vrlead")
    send("vrlead setting")
    # Lead = Slow gives its full lead too (test reliability 2026-10-17: MR4g's Slow leads 0.87 and 0.78 of 0.90 were not judged)
    bad_s = [r for r in rows_s if r[1] < r[2] - 0.034]
    fixes = int(nums(v1).get("slowFixes", 0) - nums(v0).get("slowFixes", 0))
    ok = ok_hold and holds_s == 0 and len(rows_s) >= 3 and not bad_s
    result("leadhold", ok, "Hold: %d blows, holds +%d, released +%d, cut +%d (%s), leads %s (want %s), speeds at the impact %s%s | Slow: %d blows, holds +%d, slow fixes +%d, leads %s%s" % (
        len(rows), holds, rel, cut, cuts[:300], " ".join("%.2f" % r[1] for r in rows[:10]), " ".join("%.2f" % r[2] for r in rows[:3]), speeds,
        (" | OFF: " + ", ".join("%s %.2f/%.2f" % (r[0], r[1], r[2]) for r in bad)) if bad else "", len(rows_s), holds_s, fixes, " ".join("%.2f" % r[1] for r in rows_s[:12]),
        (" | SHORT: " + ", ".join("%s %.2f/%.2f" % (r[0], r[1], r[2]) for r in bad_s)) if bad_s else ""))


def part_launch():
    l0 = send("launch?")
    e = enemy("nearest")
    if not e:
        return result("launch", False, "no enemy")
    who = e["id"]
    send("vrdamage everyhit")
    send("tough off")
    try:
        # EnemyHealth 1.0 (MR2b: twelve big hits left a thug alive, its guard broken and the cuts "the wrong way"): cut
        # along its open crit when one is open
        for _ in range(40):
            send("heroat %s 0 1.2" % who)
            r = send("bladetest %s along" % who)
            if "KILL" in r or "no enemy" in r:
                break
            time.sleep(0.3)
        time.sleep(1.5)
    finally:
        send("tough on")
        send("vrdamage setting")
    l1 = send("launch?")
    n0 = int(re.search(r"launches (\d+)", l0).group(1))
    n1 = int(re.search(r"launches (\d+)", l1).group(1))
    result("launch", n1 > n0, "%s -> %s" % (l0, l1))


def part_diffflash():
    send("vrdiffflash on")
    a = send("vrdiffflash")
    send("vrdiff hard")
    time.sleep(0.2)
    b = send("vrdiffflash")
    send("vrdiffflash off")
    send("vrdiff nightmare1")
    time.sleep(0.2)
    c = send("vrdiffflash")
    send("vrdiff setting")
    send("vrdiffflash setting")
    na = int(re.search(r"flashes (\d+)", a).group(1)) if "flashes" in a else -1
    nb = int(re.search(r"flashes (\d+)", b).group(1)) if "flashes" in b else -1
    nc = int(re.search(r"flashes (\d+)", c).group(1)) if "flashes" in c else -1
    ok = nb == na + 1 and "difficulty Hard 1.00/1.50" in b and nc == nb
    result("diffflash", ok, "on: %s | off: %s" % (b, c))


PARTS = {"strafe": part_strafe, "shove": part_shove, "shovewaiter": part_shovewaiter, "combogaps": part_combogaps, "waiting": part_waiting, "approach": part_approach, "bodylean": part_bodylean, "bossdeath": part_bossdeath, "worldtouch": part_worldtouch, "circle": part_circle, "glovehud": part_glovehud, "weaponsound": part_weaponsound, "exitdash": part_exitdash, "looks": part_looks, "alloc": part_alloc, "bladeshot": part_bladeshot, "launch": part_launch, "leadhold": part_leadhold, "diffflash": part_diffflash, "neverreach": part_neverreach, "commit": part_commit, "sidestep": part_sidestep, "dash": part_dash, "flick": part_flick, "shortdash": part_shortdash, "backdash": part_backdash, "pauses": part_pauses, "idlebonus": part_idlebonus,
         "health": part_health, "trail": part_trail, "smartsnap": part_smartsnap, "shotstop": part_shotstop, "super": part_super}


def main():
    fixture = args[args.index("--fixture") + 1] if "--fixture" in args else "thieves"
    parts = args[args.index("--parts") + 1].split(",") if "--parts" in args else ["commit", "sidestep", "dash", "flick", "shortdash", "health", "trail", "smartsnap"]
    if "--no-fixture" not in args:
        boot = ["--boot"] if "--boot" in args or "GameState_Map" not in send("state?") else []
        equip = ["--equip", args[args.index("--equip") + 1]] if "--equip" in args else ["--equip", "BasicShield"]
        r = subprocess.run(["python3", H + "/scripts/combat_fixture.py", "--fixture", fixture] + boot + ["--god", "--physical"] + equip, capture_output=True, text=True)
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
    log("pcombat: " + send("pcombat?")[:600])
    if "--stay" not in args:
        import indicator_shots
        log("left the fight: %s %s" % (indicator_shots.leave(), send("state?")[:60]))
    log("%s: %d/%d PASS" % (TAG, sum(1 for _, o in results if o), len(results)))
    return 0 if all(o for _, o in results) else 1


if __name__ == "__main__":
    sys.exit(main())
