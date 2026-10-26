#!/usr/bin/env python3
"""Combat Stage 1 checks (docs/combat-progress.md, 2026-10-12 night): Until You Fall's premonitions, block and dodge.

  stage1_test.py [--parts a,b,...] [--fixture NAME] [--no-fixture] [--tag S1T] [--captures]

In a fixture fight (Physical mode, god mode, enemies kept alive), through the simulated headset (xrsim):
  block       the shield on the bar early: blocked; UYF's knockback moved the player 4-8 cm (6 cm) away from the
              attacker, the view shook (one shake), the bar turned valid (lock-in) before the impact
  bladeblock  the sword laid along the bar (within 30 deg), its middle on the bar: blocked
  offset      the sword on the bar's line but 0.25 m off it in the bar's plane (UYF: 0.165 m): not blocked (hit)
  across      the sword on the bar but square to it (90 deg; UYF: 30 deg): not blocked
  nocounter   [Combat] Counter = Off (UYF): the shield meeting the blow at the last moment only blocks
  counter     Counter = Meet (the earlier rule, a setting): meeting the blow counters again
  nolean      a blockable blow, the head 0.3 m sideways before it (UYF: no head dodge for blockable blows): hit
  lean        LeanDodge = true (the earlier rule): the same lean dodges it
Dodge fixture (--fixture dodge: unblockable blows):
  arrow       an unblockable blow shows the arrow (strike? "dodge"); the head moved along it by its distance + 3 cm
              before the impact: dodged, the "succeeding" cue fired first
  short       the head moved 40 % of the distance: hit
  wrong       the head moved the other way: hit
Seated (--seated: the simulated head and hands 0.5 m lower, as a player in a chair; UYF's seated rules, SeatedRules.cs):
  seatedon    [Comfort] Seated on (`combatseated on`, then a recenter): the view lifted to EyeHeight, the duck 5 cm, every
              captain's and the Aether Horror's agent radius x seated / standing (UYF 1.0/1.1, 1.45/1.6), the rest unchanged
  seatedduck  a duck arrow (the shadow warrior's sweep): the head 6.5 cm down - more than UYF's seated 5 cm, less than its
              standing 8 cm: dodged
  reach       45 s of blows: where the bars stand against the eyes (a seated player's hands reach ~0.7 m below them in front)
  seatedoff   Seated off again: the duck 8 cm, every agent its own radius
Both:
  match       the swing trace over the run: the bar within 0.15 m of the blade in its plane (0.4 m in depth) and
              square to its motion within 25 deg, now vs before Stage 1 (Guard.LegacyBar)
  shots       stereo stills of the live bar and arrow (testdata/shots/<tag>_*_eyes.png; --captures copies them, and a
              frame strip of one strike from the `premo` seam, to the main worktree's HandOfFate2/captures/)
Results: PASS/FAIL lines on stdout and in logs/stage1_test.log; exit 1 on any FAIL.
"""
import os
import re
import shutil
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cmd  # noqa: E402
import hof2env  # noqa: E402

H = hof2env.H
LOG = H + "/logs/stage1_test.log"
CAPTURES = "/home/bara/Projects/flat2vr/HandOfFate2/captures"
args = sys.argv[1:]
TAG = args[args.index("--tag") + 1] if "--tag" in args else "S1T"
results = []
HEAD = "head 0 1.7 0 0 0 0"
HANDS_DOWN = "hand r 0.32 0.85 0.05 0 -80 0; hand l -0.32 0.85 0.05 0 -80 0"
HAND_R = "hand r 0.25 1.35 -0.35 0 5 0"
HAND_L = "hand l -0.25 1.2 -0.3 0 0 0"
SHIELD_AWAY = "hand l -0.45 1.0 0.25 0 0 0"   # beside and behind the body: more than 0.4 m behind any bar's plane
SWORD_AWAY = "hand r 0.45 0.95 0.3 0 -80 0"    # the same on the right, the blade down (gnG2: the lowered sword met low red blows)
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


SEAT_DY = -0.5 if "--seated" in args else 0.0   # a seated player's head and hands, against the standing poses below


def seat(c):
    """Absolute head and hand poses (`head x y z ...`, `hand r|l x y z ...`) moved by SEAT_DY; relative moves unchanged."""
    if not SEAT_DY:
        return c
    out = []
    for seg in c.split(";"):
        w = seg.split()
        try:
            if w and w[0] == "head" and len(w) >= 4:
                w[2] = "%.3f" % (float(w[2]) + SEAT_DY)
            elif w and w[0] == "hand" and len(w) >= 5:
                w[3] = "%.3f" % (float(w[3]) + SEAT_DY)
        except ValueError:
            pass
        out.append(" ".join(w))
    return "; ".join(out)


def sim(c):
    subprocess.run(["python3", H + "/scripts/xrsim.py", seat(c)], capture_output=True, text=True)


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


def vec(s):
    return [float(x) for x in s.strip("()").split(",")]


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
                d = {"who": m.group(1), "colour": m.group(2), "eta": float(m.group(3)), "bar": vec(m.group(5)), "axis": vec(m.group(6)), "raw": first}
                dm = re.search(r"dodge (\S+) ([\d.]+) m progress (-?[\d.]+) dirT (-?[\d.]+),(-?[\d.]+),(-?[\d.]+)", first)
                if dm:
                    d["dodge"] = dm.group(1)
                    d["need"] = float(dm.group(2))
                    d["dirT"] = [float(dm.group(4)), float(dm.group(5)), float(dm.group(6))]
                return d
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


def guard_to(kind="shield", off=(0.0, 0.0, 0.0), ms=60):
    g = send("guardat %s %.3f %.3f %.3f" % (kind, off[0], off[1], off[2]))
    m = re.match(r"(move [lr] \S+ \S+ \S+)", g)
    if not m:
        return g
    sim(m.group(1) + " %d" % ms)
    return g


def head_pos():
    e = send("enemy? nearest")
    m = re.search(r"head (\([^)]*\))", e)
    return vec(m.group(1)) if m else None


_BLADE0 = None


def blade_rest():
    """The blade's direction (tracking) with the controller unrotated (`rot r 0 0 0`), measured once: the fist's pitch-up and
    any yaw of the grip (the game's own hold)."""
    global _BLADE0
    if _BLADE0 is None:
        import math
        sim("rot r 0 0 0")
        time.sleep(0.2)
        m = re.search(r"dirT \((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\)", send("blade?"))
        d = [float(m.group(i)) for i in (1, 2, 3)] if m else [0.0, math.sin(math.radians(20)), math.cos(math.radians(20))]
        n = math.sqrt(sum(c * c for c in d)) or 1.0
        _BLADE0 = [c / n for c in d]
        log("  the blade with the controller level: dirT %.3f,%.3f,%.3f" % tuple(_BLADE0))
    return _BLADE0


def blade_along(axis_t, square=False):
    """Turn the right hand so the sword lies exactly along the bar's axis (tracking space), or square to it in the bar's
    plane. xrsim's `rot` (OpenXR: yaw about +Y, then pitch about +X): with the controller unrotated the blade points d0
    (blade_rest: elevation g, yaw offset f0); a tracking direction a (Unity, +z ahead) then needs pitch asin(a.y) - g and
    yaw atan2(-a.x, a.z) - f0. Before 2026-10-13 the sword was only ever upright or level: with the thieves' bar tilted
    ~36 deg by the bend, a "level along" sword crossed the bar and its end blocked the offset blow (env 1's stage1
    `offset` fail on main, 464395e)."""
    import math
    ax, ay, az = axis_t
    if square:
        # rotate the axis 90 deg about the view (z): (x, y) -> (-y, x)
        ax, ay = -ay, ax
    n = math.sqrt(ax * ax + ay * ay + az * az) or 1.0
    ax, ay, az = ax / n, ay / n, az / n
    # the bar is a line: point the blade up (level: to the left)
    if ay < -1e-3 or (abs(ay) <= 1e-3 and ax > 0):
        ax, ay, az = -ax, -ay, -az
    d0 = blade_rest()
    g = math.degrees(math.asin(max(-1.0, min(1.0, d0[1]))))
    f0 = math.degrees(math.atan2(-d0[0], d0[2]))
    pitch = math.degrees(math.asin(max(-1.0, min(1.0, ay)))) - g
    yaw = math.degrees(math.atan2(-ax, az)) - f0
    sim("rot r %.1f %.1f 0" % (yaw, pitch))
    time.sleep(0.12)
    m = re.search(r"dirT \((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\)", send("blade?"))
    if m:
        got = [float(m.group(i)) for i in (1, 2, 3)]
        dot = abs(sum(got[i] * (ax, ay, az)[i] for i in range(3)))
        log("  blade_along%s: want %.2f,%.2f,%.2f got %.2f,%.2f,%.2f (%.1f deg off)" % (
            " (square)" if square else "", ax, ay, az, got[0], got[1], got[2], math.degrees(math.acos(min(1.0, dot)))))


# ------------------------------------------------------------------------------------------------ blocks

PRE = [{}]


SHIELD_REGUARD = os.environ.get("S1_SHIELD_REGUARD", "1") != "0"


def block_once(kind="shield", off=(0, 0, 0), square=False, settle=0.1, colours=("White", "Gold")):
    """Guard one blockable strike: returns (outcome, before, after, strike) or None."""
    reset_pose()
    if kind == "shield":
        sim(SHIELD_AWAY)   # off every bar until the test moves it (the rest pose lay on the thieves' low-left bar)
    if kind == "blade":
        blade_rest()   # measured before the strike: its window is short
        sim("hand l -0.3 0.9 -0.1 0 -60 0; rot r 0 70 0")
    PRE[0] = counts()   # the lock-ins before the guard moved (round 2: block "lock-ins +0" twice)
    st = strike(timeout=25, fresh=0.75, colours=colours)
    if st is None:
        return None
    if kind == "blade":
        g = send("guardat blade")
        m = re.search(r"axis \((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\)", g)
        if m:
            blade_along([float(m.group(1)), float(m.group(2)), float(m.group(3))], square)
    c0 = counts()
    t0 = time.time()
    g = guard_to(kind, off)
    if kind == "blade" or (kind == "shield" and SHIELD_REGUARD):
        # and once more 0.25 s before the blow, from the bar as it is then: a thief's running attack carries its bar along
        # until the blow (MR2b: the sword placed at the telegraph was 0.5 m off the bar at the impact - a hit). Only moved:
        # the orientation set above still lies along the bar. The shield too since candidate 11 (P4-13: the shield's reach is
        # the drawn 30 cm circle, 0.165 m, no longer up to 0.26 m - a shield placed once at the telegraph was left 0.2-1 m off
        # a lunging or running attacker's bar at its impact; a player keeps it on the circle, which moves with the bar).
        # S1_SHIELD_REGUARD=0: placed once, as before (the A/B)
        wait = st["eta"] - (time.time() - t0) - 0.25
        if wait > 0:
            time.sleep(wait)
            st2 = strike(timeout=0.2)
            if st2 and st2.get("who") == st.get("who"):
                g = guard_to(kind, off, ms=30)
    time.sleep(settle)
    what, c1 = wait_outcome(c0)
    return what, c0, c1, st, g


def floor_behind():
    """The floor behind the player (`dash? free 180`: from the gaze), m; 99 = more than the query looks."""
    fm = re.search(r"free ([\d.]+)(\+?) m", send("dash? free 180"))
    return 99.0 if fm and fm.group(2) else (float(fm.group(1)) if fm else -1.0)


def room_behind(need=1.0):
    """The hero 2 m from the nearest enemy, facing it, on a side with at least `need` m of floor behind (for the block's push)."""
    e = send("enemy? nearest")
    who = "#" + e.split(" id ")[1].split()[0] if " id " in e else "nearest"
    for deg in (0, 45, -45, 90, -90, 135, -135, 180):
        if send("heroat %s %d 2.0" % (who, deg)).startswith("hero at"):
            send("face " + who)
            time.sleep(0.3)
            b = floor_behind()
            if b >= need:
                return "the hero set %d deg round %s, %.2f m of floor behind" % (deg, who, b)
    return "no side of %s with %.1f m of floor behind" % (who, need)


def part_block():
    short = 0
    for _ in range(7):
        r = block_once("shield")
        if r is None:
            break
        what, c0, c1, st, g = r
        if what == "blocks":
            time.sleep(0.4)   # the push travels 6 cm at 0.5 m/s: about 0.12 s
            c1 = counts()
            kb = c1.get("knockbacks", 0) - c0.get("knockbacks", 0)
            knocked = c1.get("knocked", 0)
            shakes = c1.get("shakes", 0) - c0.get("shakes", 0)
            # the lock-ins from before the strike was seen: the away pose (beside, behind) lies on a bar of a blow from that
            # side, and the lock-in came on the bar's first frame, before the guard moved (round 2's chain r2: "lock-ins +0
            # (before the guard moved +1)")
            lock = c1.get("lockIns", 0) - PRE[0].get("lockIns", 0)
            ok = kb >= 1 and 0.035 <= knocked <= 0.085 and shakes >= 1 and lock >= 1
            behind = floor_behind() if not ok and kb >= 1 and knocked < 0.035 else None
            if behind is not None and short < 4:
                # the push stops 0.35 m short of any body and at the floor's edge: another thief behind the player cut it short
                # (CPZ1S: 0.023 m), or the floor ended behind (2026-10-20 17:15, mountain_peak_snow: 9 pushes of 0.00-0.04 m
                # in a row) - another blow; from the second, the hero first set 2 m from the nearest enemy on a side with floor
                short += 1
                log("  block: blocked, the push stopped at %.3f m (the floor behind %.2f m; a body or the edge): again" % (knocked, behind))
                if short >= 2:
                    log("  block: " + room_behind())
                continue
            return result("block", ok, "%s strike (%s), shield on the bar -> blocked; knockback %d, moved %.3f m (UYF 0.06), shakes +%d, lock-ins +%d "
                          "(before the guard moved +%d; the bar's eta %.2f s)%s" % (
                st["colour"], st["who"], kb, knocked, shakes, lock, c0.get("lockIns", 0) - PRE[0].get("lockIns", 0), st.get("eta", -1),
                "" if behind is None else "; the floor behind %.2f m" % behind))
        log("  block: outcome %s (%s)" % (what, st["who"]))
    result("block", False, "no strike blocked by the shield on the bar in 7 tries")


def part_bladeblock():
    for _ in range(5):
        r = block_once("blade")
        if r is None:
            break
        what, c0, c1, st, g = r
        if what in ("blocks", "hitsTaken"):
            return result("bladeblock", what == "blocks", "%s strike (%s), the sword along the bar -> %s | %s" % (st["colour"], st["who"], what, send("blade?")[:100]))
        log("  bladeblock: outcome %s" % what)
    result("bladeblock", False, "no clear outcome in 5 tries")


def plane_offset(st, dist):
    """A world offset `dist` m from the bar's centre, in the bar's plane, square to its axis."""
    h = head_pos()
    if h is None:
        return (0.0, dist, 0.0)
    b, a = st["bar"], st["axis"]
    f = [b[0] - h[0], 0.0, b[2] - h[2]]
    n = (f[0] ** 2 + f[2] ** 2) ** 0.5 or 1.0
    f = [f[0] / n, 0.0, f[2] / n]
    # square to the axis in the plane: f x axis
    o = [f[1] * a[2] - f[2] * a[1], f[2] * a[0] - f[0] * a[2], f[0] * a[1] - f[1] * a[0]]
    on = (o[0] ** 2 + o[1] ** 2 + o[2] ** 2) ** 0.5 or 1.0
    return tuple(c / on * dist for c in o)


def part_offset():
    blade_rest()
    for _ in range(5):
        reset_pose()
        sim("hand l -0.3 0.9 -0.1 0 -60 0; rot r 0 70 0")
        st = strike(timeout=25, fresh=0.75, colours=("White", "Gold"))
        if st is None:
            break
        g = send("guardat blade")
        m = re.search(r"axis \((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\)", g)
        if m:
            blade_along([float(m.group(1)), float(m.group(2)), float(m.group(3))])
        c0 = counts()
        # late, once the attacker has committed (the bar follows its facing until then)
        t0 = time.time()
        time.sleep(max(0.0, st["eta"] - 0.45))
        st2 = strike(timeout=0.3) or st
        guard_to("blade", plane_offset(st2, 0.25), ms=40)
        # and once more just before the blow, from the bar as it is then (main 14:12: the bar moved ~0.1 m toward the sword
        # in the last 0.45 s and the blow was blocked; the coordinator: re-place it just before the impact)
        wait = st["eta"] - (time.time() - t0) - 0.22
        if wait > 0:
            time.sleep(wait)
            st3 = strike(timeout=0.2)
            if st3 and st3.get("who") == st.get("who"):
                # only moved (the orientation set at the start still lies along the bar; turning again costs ~0.25 s)
                guard_to("blade", plane_offset(st3, 0.25), ms=30)
        what, c1 = wait_outcome(c0)
        if what in ("blocks", "hitsTaken"):
            return result("offset", what == "hitsTaken", "the sword 0.25 m off the bar in its plane (UYF 0.165) -> %s (%s)" % (what, st["who"]))
        log("  offset: outcome %s" % what)
    result("offset", False, "no clear outcome in 5 tries")


def part_across():
    for _ in range(5):
        r = block_once("blade", square=True)
        if r is None:
            break
        what, c0, c1, st, g = r
        if what in ("blocks", "hitsTaken"):
            return result("across", what == "hitsTaken", "the sword on the bar but square to it (UYF 30 deg) -> %s (%s)" % (what, st["who"]))
        log("  across: outcome %s" % what)
    result("across", False, "no clear outcome in 5 tries")


def meet_once():
    """The shield brought onto the bar in the blow's last 0.25 s (the earlier counter move)."""
    reset_pose()
    sim(SHIELD_AWAY)   # (a -60 deg pitch here turned the shield's face away from the attacker: never a block)
    st = strike(timeout=25, fresh=0.7, colours=("Gold", "White"))
    if st is None:
        return None
    t0 = time.time()
    # held off the bar, 0.65 m across it in its plane (MR2b/MR2c: "0.35 m lower" lay along a thief's upright running-attack
    # bar - on it a second early: a block, never the meet; MR2d: 0.35 m across was still within the shield's own disc)
    guard_to("shield", plane_offset(st, 0.65), ms=60)
    c0 = counts()
    # the eta read again shortly before the move: a strike can be slowed after it shows (the lead floor, the off-view
    # bonus), and a shield that arrives more than the meet window early is a block, not a counter (CPZ1S, BA1S)
    wait = st["eta"] - (time.time() - t0) - 0.75
    if wait > 0:
        time.sleep(wait)
    t1 = time.time()
    st2 = strike(timeout=0.3)
    eta2 = st2["eta"] if st2 and st2.get("who") == st.get("who") else st["eta"] - (t1 - t0)
    wait = eta2 - (time.time() - t1) - 0.40    # then up into the blow: the held shield settles in ~0.15 s
    if wait > 0:
        time.sleep(wait)
    guard_to("shield", ms=60)
    what, c1 = wait_outcome(c0)
    return what, st


def part_nocounter():
    send("vrcounter setting")
    for _ in range(6):
        r = meet_once()
        if r is None:
            break
        what, st = r
        if what in ("blocks", "counters"):
            return result("nocounter", what == "blocks", "Counter = %s: the shield met a %s blow -> %s" % (send("vrcounter").split()[-1], st["colour"], what))
        log("  nocounter: outcome %s" % what)
    result("nocounter", False, "no block in 6 tries")


def part_counter():
    send("vrcounter meet")
    try:
        for _ in range(6):
            r = meet_once()
            if r is None:
                break
            what, st = r
            if st["colour"] == "Gold" and what in ("blocks", "counters"):
                return result("counter", what == "counters", "Counter = Meet: the shield met a gold blow -> %s" % what)
            log("  counter: %s blow -> %s" % (st["colour"], what))
        result("counter", False, "no gold blow met in 6 tries")
    finally:
        send("vrcounter setting")


def lean_once():
    reset_pose()
    sim(HANDS_DOWN)
    st = strike(timeout=25, fresh=0.8, colours=("White", "Gold"))
    if st is None:
        return None
    send("face striker")
    c0 = counts()
    time.sleep(max(0.0, st["eta"] - 0.45))
    sim("head 0.3 1.7 0 0 0 0")
    what, c1 = wait_outcome(c0)
    sim(HEAD)
    return what


def part_nolean():
    send("vrlean setting")
    for _ in range(5):
        what = lean_once()
        if what in ("dodges", "hitsTaken"):
            return result("nolean", what == "hitsTaken", "LeanDodge off (UYF): the head 0.3 m sideways before a blockable blow -> %s" % what)
        log("  nolean: outcome %s" % what)
    result("nolean", False, "no clear outcome in 5 tries")


def part_lean():
    send("vrlean on")
    try:
        for _ in range(5):
            what = lean_once()
            if what in ("dodges", "hitsTaken"):
                return result("lean", what == "dodges", "LeanDodge on (the earlier rule): the head 0.3 m sideways -> %s" % what)
            log("  lean: outcome %s" % what)
        result("lean", False, "no clear outcome in 5 tries")
    finally:
        send("vrlean setting")


# ------------------------------------------------------------------------------------------------ dodges

def dodge_once(scale, sign=1.0, extra=0.0):
    reset_pose()
    sim(HANDS_DOWN)
    st = strike(timeout=30, fresh=0.7, colours=("Red",))
    if st is None or "dirT" not in st:
        return None, st
    c0 = counts()
    time.sleep(max(0.0, st["eta"] - 0.4))
    d = [c * (st["need"] * scale + extra) * sign for c in st["dirT"]]
    sim("head %.3f %.3f %.3f 0 0 0" % (d[0], 1.7 + d[1], -d[2]))   # (seat() lowers it with --seated)
    what, c1 = wait_outcome(c0)
    time.sleep(0.2)
    sim(HEAD)
    return (what, c0, c1), st


def part_arrow():
    for _ in range(6):
        r, st = dodge_once(1.0, 1.0, 0.03)   # the arrow's distance + 3 cm (exactly the distance sat on the line: ST1 hit at 5 cm)
        if r is None:
            if st is None:
                break
            continue
        what, c0, c1 = r
        # the arrow's distance + 3 cm (the head eases there over the xrsim move; the impact judges where it is then)
        ready = c1.get("dodgeReadies", 0) - c0.get("dodgeReadies", 0)
        if what in ("dodges", "hitsTaken"):
            return result("arrow", what == "dodges" and ready >= 1, "%s: the arrow says %s (%.3f m); the head moved along it -> %s, succeeding cue +%d" % (
                st["who"], st["dodge"], st["need"], what, ready))
        log("  arrow: outcome %s" % what)
    result("arrow", False, "no unblockable blow judged in 6 tries")


def part_short():
    for _ in range(6):
        r, st = dodge_once(0.4, 1.0)
        if r is None:
            if st is None:
                break
            continue
        what = r[0]
        if what in ("dodges", "hitsTaken"):
            return result("short", what == "hitsTaken", "%s: 40 %% of the arrow's %.3f m -> %s" % (st["who"], st["need"], what))
    result("short", False, "no unblockable blow judged in 6 tries")


def part_wrong():
    for _ in range(6):
        r, st = dodge_once(1.5, -1.0)
        if r is None:
            if st is None:
                break
            continue
        what = r[0]
        if what in ("dodges", "hitsTaken"):
            return result("wrong", what == "hitsTaken", "%s: the head moved against the arrow (%s) -> %s" % (st["who"], st["dodge"], what))
    result("wrong", False, "no unblockable blow judged in 6 tries")


# ------------------------------------------------------------------------------------------------ red blows (F5)
# The user's playtest 2026-10-17: no dodge arrows. A red (unblockable) blow shows a bar like every blow: the shield on it
# blocks it (no counter), leaning does nothing, a step out of its reach avoids it.

def part_redbar():
    for _ in range(6):
        r = block_once("shield", colours=("Red",))
        if r is None:
            break
        what, c0, c1, st, g = r
        if what in ("blocks", "hitsTaken", "counters"):
            red = c1.get("redBlocked", 0) - c0.get("redBlocked", 0)
            ok = what == "blocks" and red >= 1 and "dodge" not in st
            return result("redbar", ok, "%s: a red blow shows a bar (%s), the shield on it -> %s, red blocked +%d, counters +%d" % (
                st["who"], "no arrow" if "dodge" not in st else "AN ARROW: " + st.get("dodge", ""), what, red, c1.get("counters", 0) - c0.get("counters", 0)))
        log("  redbar: outcome %s" % what)
    result("redbar", False, "no red blow judged in 6 tries")


def part_redlean():
    for _ in range(6):
        reset_pose()
        # the shield fully away (candidate 8 retest: a lowered shield is a real guard for a low blow since drawn and judged
        # agree - 'blocks' with the hands down)
        sim(HANDS_DOWN + "; " + SHIELD_AWAY + "; " + SWORD_AWAY)
        st = strike(timeout=30, fresh=0.7, colours=("Red",))
        if st is None:
            break
        c0 = counts()
        time.sleep(max(0.0, st["eta"] - 0.45))
        sim("head 0.3 1.7 0 0 0 0")
        what, c1 = wait_outcome(c0)
        sim(HEAD)
        # the blow itself decides (gn2r r2, gnG r1: blow 0 hit, its combo's follow-up 0.27 s later met the lowered guard -
        # both in one poll, "blocks" read first); a block without a hit says nothing about leaning: another blow
        if c1.get("hitsTaken", 0) > c0.get("hitsTaken", 0):
            return result("redlean", True, "%s: the head 0.3 m sideways before a red blow (no arrows: leaning does nothing) -> hitsTaken%s" % (
                st["who"], " (a later blow of its combo met the lowered guard)" if what == "blocks" else ""))
        if what == "dodges":
            return result("redlean", False, "%s: the head 0.3 m sideways before a red blow (no arrows: leaning does nothing) -> dodges" % st["who"])
        log("  redlean: outcome %s" % what)
    result("redlean", False, "no red blow judged in 6 tries")


# ------------------------------------------------------------------------------------------------ match, shots

def part_match():
    s = send("swingmatch?", 10)
    m = re.search(r"now: match (\d+)/(\d+)", s)
    b = re.search(r"before: match (\d+)/(\d+)", s)
    if not m or int(m.group(2)) < 5:
        return result("match", False, "too few blows traced: " + s[:200])
    now = int(m.group(1)) / int(m.group(2))
    result("match", now >= 0.85, "%s | now %.0f %% (before %s)" % (s[:420], 100 * now, (b.group(1) + "/" + b.group(2)) if b else "?"))


def shot(name):
    p = H + "/testdata/shots/%s_%s_eyes.png" % (TAG, name)
    try:
        os.remove(p)
    except OSError:
        pass
    r = send("shot %s_%s" % (TAG, name), 20)
    t0 = time.time()
    while time.time() - t0 < 4.0 and not os.path.exists(p):   # the PNG is written after the command returns
        time.sleep(0.1)
    return p if os.path.exists(p) else None, r


def copy_capture(path, name):
    if path and "--captures" in args:
        os.makedirs(CAPTURES, exist_ok=True)
        shutil.copy(path, os.path.join(CAPTURES, name))


def part_shots():
    took = []
    # one live strike as a frame strip: the bar as it appears, filling, the shield on it (white), the result
    for _ in range(4):
        reset_pose()
        sim(HANDS_DOWN)
        st = strike(timeout=25, fresh=1.0, colours=("White", "Gold"))
        if st is None:
            break
        send("face striker")
        c0 = counts()
        p1, _ = shot("live_1_appear")
        guard_to("shield", ms=40)
        time.sleep(0.08)
        p2, _ = shot("live_2_locked")
        p3, _ = shot("live_3_filling")
        what, _ = wait_outcome(c0, timeout=2.5)
        time.sleep(0.03)
        p4, _ = shot("live_4_" + (what or "none"))
        for i, p in enumerate((p1, p2, p3, p4)):
            copy_capture(p, "stage1_live_%d_%s_eyes.png" % (i + 1, ("appear", "locked", "locked_later", what or "none")[i]))
        took += [x for x in (p1, p2, p3, p4) if x]
        log("  shots: live strip of %s (%s) -> %s" % (st["who"], st["colour"], what))
        break
    # an arrow live (an unblockable blow), if one comes within 20 s
    sim(HANDS_DOWN)
    st = strike(timeout=20, fresh=0.6, colours=("Red",))
    if st:
        send("face striker")
        p5, _ = shot("live_arrow")
        copy_capture(p5, "stage1_live_arrow_eyes.png")
        if p5:
            took.append(p5)
    # the frame strip and the stills from the premo seam (a still bar in each state, in front of the eyes)
    sim(HEAD + "; " + HANDS_DOWN)
    send("timescale 0")
    strip = [("bar 0.05 live 0.05", "1_appear"), ("bar 0.3 live 0.15", "2_fill30"), ("bar 0.6 live 1", "3_fill60"),
             ("bar 0.8 valid 1", "4_locked"), ("bar 1 ok 0.06", "5_blocked"), ("bar 1 ok 0.25", "6_after"),
             ("bar 1 fail 0.1", "7_hit"), ("bar 0.6 out 1", "8_out_of_range"),
             ("arrow 0.4 0.2 live down", "9_arrow_duck"), ("arrow 0.8 1.1 live right", "10_arrow_lean_ok"),
             ("arrow 1 1.2 ok right 0.2", "11_arrow_dodged")]
    for spec, name in strip:
        send("premo " + spec)
        time.sleep(0.25)
        p, r = shot("strip_" + name)
        copy_capture(p, "stage1_strip_%s_eyes.png" % name)
        if p:
            took.append(p)
    send("premo off")
    send("timescale 1")
    result("shots", len(took) >= 10, "%d stereo shots (%s)" % (len(took), ", ".join(os.path.basename(t) for t in took[:4]) + " ..."))


# ------------------------------------------------------------------------------------------------ seated (--seated)

def seated_rows(raw):
    """`seatedrules?` / `combatseated` -> (head dict, [enemy rows])."""
    head = {}
    m = re.search(r"seated (True|False) duck ([\d.]+) m", raw)
    if m:
        head = {"seated": m.group(1) == "True", "duck": float(m.group(2))}
    e = re.search(r"eye height ([\d.]+) \(setting ([\d.]+)\)", raw)
    if e:
        head["eye"], head["want"] = float(e.group(1)), float(e.group(2))
    rows = [{"who": r[0], "arch": r[1], "st": float(r[2]), "se": float(r[3]), "agent": float(r[4]), "own": float(r[5]), "near": float(r[6])}
            for r in re.findall(r"\| (\S+) #-?\d+ (\w+) uyf ([\d.]+)->([\d.]+) agent ([\d.]+) own ([\d.]+) near ([\d.]+)", raw)]
    return head, rows


def agents_ok(rows, seated):
    bad = []
    for r in rows:
        want = r["own"] * (r["se"] / r["st"] if seated else 1.0)
        if abs(r["agent"] - want) > 0.002:
            bad.append("%s %s agent %.3f want %.3f" % (r["who"], r["arch"], r["agent"], want))
    return bad


def part_seatedon():
    sim(HEAD)
    time.sleep(0.5)
    raw = send("combatseated on")
    time.sleep(1.0)   # SeatedRules re-applies every 0.5 s
    raw = send("combatseated")
    head, rows = seated_rows(raw)
    bad = agents_ok(rows, True)
    shrunk = [r for r in rows if r["se"] < r["st"]]
    ok = head.get("seated") is True and abs(head.get("duck", 0) - 0.05) < 1e-4 and abs(head.get("eye", 0) - head.get("want", -1)) < 0.03 and rows and not bad
    result("seatedon", ok, "seated %s, eye height %s (setting %s; the real head at %.2f m), duck %s m; %d enemies, %d shrunk: %s%s" % (
        head.get("seated"), head.get("eye"), head.get("want"), 1.7 + SEAT_DY, head.get("duck"), len(rows), len(shrunk),
        "; ".join("%s %s %.3f/%.3f near %.2f" % (r["who"], r["arch"], r["agent"], r["own"], r["near"]) for r in rows)[:500],
        (" | WRONG: " + "; ".join(bad)) if bad else ""))


def part_seatedduck():
    for _ in range(8):
        reset_pose()
        sim(HANDS_DOWN)
        st = strike(timeout=30, fresh=0.7, colours=("Red",))
        if st is None:
            break
        if "dirT" not in st or st.get("dodge") != "duck":
            time.sleep(0.5)
            continue
        c0 = counts()
        time.sleep(max(0.0, st["eta"] - 0.4))
        d = [c * 0.065 for c in st["dirT"]]
        sim("head %.3f %.3f %.3f 0 0 0" % (d[0], 1.7 + d[1], -d[2]))
        what, c1 = wait_outcome(c0)
        time.sleep(0.2)
        sim(HEAD)
        if what in ("dodges", "hitsTaken"):
            return result("seatedduck", what == "dodges" and abs(st["need"] - 0.05) < 1e-4, "%s: the arrow says %s %.3f m (UYF seated 0.05, standing 0.08); the head 0.065 m down -> %s" % (
                st["who"], st["dodge"], st["need"], what))
        log("  seatedduck: outcome %s" % what)
    result("seatedduck", False, "no duck arrow judged in 8 tries")


def part_reach():
    """45 s of blows with the head still: each blow's bar once, 0.3 s before its impact (the bar follows the attacker until
    it commits), its centre and its upper end against the eyes."""
    reset_pose()
    sim(HANDS_DOWN)
    seen = {}
    t0 = time.time()
    while time.time() - t0 < 45:
        if user_in_vr():
            break
        h = head_pos()
        s = send("strike?", 4)
        if h and s and s != "none" and not s.startswith(("ERR", "no ")):
            for seg in s.split(" | "):
                m = re.search(r"(\w+) (Gold|White|Red)( offview)? eta (-?[\d.]+) valid \w+ bar (\([^)]*\)) axis (\([^)]*\))", seg)
                if not m or float(m.group(4)) > 0.3:
                    continue
                key = (m.group(1), round(time.time() - float(m.group(4)), 0))
                if key in seen:
                    continue
                b, a = vec(m.group(5)), vec(m.group(6))
                cy = b[1] - h[1]
                seen[key] = (m.group(1), cy, cy + 0.3 * abs(a[1]))
        time.sleep(0.1)
    if not seen:
        return result("reach", False, "no blow in 45 s")
    v = sorted(seen.values(), key=lambda x: x[1])
    low = [x for x in v if x[2] < -0.7]
    result("reach", True, "%d blows: the bar's centre %.2f .. %.2f m from the eyes (median %.2f); %d with even its upper end more than 0.7 m below the eyes%s" % (
        len(v), v[0][1], v[-1][1], v[len(v) // 2][1], len(low), (": " + ", ".join("%s %.2f/%.2f" % x for x in low[:8])) if low else ""))


def part_seatedoff():
    raw = send("combatseated off")
    time.sleep(1.0)
    raw = send("combatseated")
    head, rows = seated_rows(raw)
    bad = agents_ok(rows, False)
    ok = head.get("seated") is False and abs(head.get("duck", 0) - 0.08) < 1e-4 and not bad
    result("seatedoff", ok, "seated %s, eye height %s, duck %s m; %d enemies at their own radius%s" % (
        head.get("seated"), head.get("eye"), head.get("duck"), len(rows), (" | WRONG: " + "; ".join(bad)) if bad else ""))


PARTS = {"block": part_block, "bladeblock": part_bladeblock, "offset": part_offset, "across": part_across, "nocounter": part_nocounter,
         "counter": part_counter, "nolean": part_nolean, "lean": part_lean, "arrow": part_arrow, "short": part_short, "wrong": part_wrong, "redbar": part_redbar, "redlean": part_redlean,
         "match": part_match, "shots": part_shots, "seatedon": part_seatedon, "seatedduck": part_seatedduck, "reach": part_reach,
         "seatedoff": part_seatedoff}
DEFAULT = ["block", "bladeblock", "offset", "across", "nocounter", "counter", "nolean", "lean", "shots", "match"]
# (no dodge arrows since 2026-10-17: the dodge fixture's red blows are blocked on their bars; arrow / short / wrong stay for
# DodgeArrows = true)
DODGE = ["redbar", "redlean"]
FIXTURES = {"dodge": "CorruptedJuggernautREF*1,ShadowWarriorREF*1"}


def main():
    fixture = args[args.index("--fixture") + 1] if "--fixture" in args else "thieves"
    parts = args[args.index("--parts") + 1].split(",") if "--parts" in args else (DODGE if fixture == "dodge" else DEFAULT)
    if "--no-fixture" not in args:
        how = ["--jump", "EL_Ambush_Thral", "--with", FIXTURES[fixture]] if fixture in FIXTURES else ["--fixture", fixture]
        # boot only when the game isn't on the map already (one session for the whole run: the one-game slot stays ours)
        boot = ["--boot"] if "--boot" in args or "GameState_Map" not in send("state?") else []
        # the magician_mid hero carries no shield: equip one on the map first (as p2_boss.py does)
        r = subprocess.run(["python3", H + "/scripts/combat_fixture.py"] + how + boot + ["--god", "--physical", "--equip", "BasicShield"], capture_output=True, text=True)
        log("fixture %s: %s" % (fixture, ((r.stdout or "").strip().splitlines() or ["?"])[-1]))
    if "Level_Play" not in send("state?"):
        result("fixture", False, "no fight: " + send("state?"))
        return 1
    send("god on")
    send("tough on")
    send("vrcombat physical")
    send("pcombat? reset")
    send("swingtrace clear")
    send("swingtrace on")
    for p in parts:
        if user_in_vr():
            log("the user is in VR: stopping")
            break
        try:
            PARTS[p]()
        except Exception as e:  # noqa: BLE001
            result(p, False, "exception " + repr(e))
    if SEAT_DY:
        send("combatseated off")   # never leave the posture changed (in memory only; the settings file is not written)
        sim(HEAD)
    send("swingtrace dump %s_%s" % (TAG, fixture), 15)
    log("pcombat: " + send("pcombat?")[:700])
    if "--stay" not in args:
        # the safe leave (indicator_shots.leave: through a stray encounter instead of into it; every step logged)
        import indicator_shots
        left = indicator_shots.leave()
        log("left the fight: %s %s" % (left, send("state?")[:60]))
    ok = all(o for _, o in results)
    log("%s: %d/%d PASS" % (TAG, sum(1 for _, o in results if o), len(results)))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
