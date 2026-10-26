#!/usr/bin/env python3
"""Playtest 4 (2026-10-19, session 20261019-220935) blocking fight items, env 2: P4-12..P4-16 and the fight-exit error.

  p4b_test.py [--tag T] [--fixture NAME|--no-fixture] [--stay] [--parts a,b,...] [--equip CARDS] [--pics]

Fixtures: combat_fixture.py's names (thieves, behind = two assassins, ...).
Parts:
  shield   (P4-12) the held shield's up from its mesh (BasicShield: the mirror line toward the wide end, not the old +x guess); the
           hand held straight out (`shieldout`, and controller poses that don't use it) stands it upright - its up within 6 deg of
           the world's up in its face, the face level and ahead - for Index (the test headset), Quest Touch with the default glove
           fit, the user's fit (tilt 25, turn -9.5, roll -23, up -1.35, forward 0.73, side 1.75) and the coordinator's numbers
           (tilt 23, turn -22, roll 15); the old up's clock turn for comparison; --pics: both eyes before/after
  circle   (P4-13) the melee circle 0.30 m across (its inner edge 0.13-0.15 m), the rule's circle r 0.150 and its share 0.55, the
           line on the shield 0.08 (`ringframe`); live strikes show it so; --pics: waiting, and the shield in it (the line faint)
  edge     (P4-13/16, the 55 % rule of 2026-10-20) green means it blocks: the shield's centre 0.24 m off the circle's centre (outside
           the drawn circle: the old centre rule's miss) but its outline covering >= 55 % -> green before the blow and blocked;
           covering 30 % -> never green and not blocked
  cover    (2026-10-20, the user: "Any part of the shield has to cover at least 55% of the circle") with the equipped shield: frozen
           at a wind-up, the shield moved (`coverat`) to cover 0 / 40 / 52 / 58 / 60 / 100 % (along the bar, sideways, up) and with
           its centre 0.20 m off - the mod's exact coverage against its 200x200 grid, green exactly when the rule holds; the shield
           turned 0 / 45 / 70 / 80 / 90 deg toward edge-on with its centre on the circle's (the coverage falls with the projection);
           then live blows at 0 / 40 / 60 / 100 %, 0.20 m off and edge-on: the per-blow record (the coverage at the impact, the
           facing, green, blocked) - green == blocked and blocked == the rule for every blow; --pics: just over / under 55 %
  threat   (P4-15) a strike with its bar and circle up; the hero then 4.5 m away (`heroat`): within 0.4 s no bar and no circle
           (`strike?` threat False), and no hit; a strike still in reach keeps them (threat True)
  assassin (P4-14) two assassins: every vanish-and-strike landing (AssassinRoom) at least 0.6 m further than the game's (from
           the eyes), or where the ground ends; --pics: a landing in view
  green    (P4-16) the assassins' strikes with the shield held on the circle: every blow judged BLOCKED arrives blocked - no damage
           after the guard's block (`dmglog?` afterBlock 0) and no hit reaction; the damage log for the notes
  exit     (the fight-exit error) a won fight's exit waiting (paused), then a Dev scenario resets the game: the hold is dropped
           ("the game reset"), no "release failed", no exception
Results: PASS/FAIL lines on stdout; exit 1 on any FAIL.
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

sys_argv = sys.argv[:]
sys.argv = [sys.argv[0]]
import stage1_test as S1  # noqa: E402
sys.argv = sys_argv

H = hof2env.H
args = sys.argv[1:]
TAG = args[args.index("--tag") + 1] if "--tag" in args else "P4B"
PICS = "--pics" in args
OUT = []
HEAD = "head 0 1.7 0 0 0 0"
HAND_R = "hand r 0.25 1.35 -0.35 0 5 0"
SHIELD_OUT_POS = "hand l -0.15 1.35 -0.5"
CAP = os.environ.get("P4B_PICS", "/home/bara/Projects/flat2vr/HandOfFate2/captures/p4b-env2")
SHOTS = H + "/testdata/shots"
USER_FIT = "tilt 25 turn -9.5 roll -23 up -1.35 along 0.73 side 1.75"
COORD_FIT = "tilt 23 turn -22 roll 15"


def log(m):
    print(time.strftime("%H:%M:%S") + " " + m, flush=True)


def result(name, ok, detail):
    OUT.append(ok)
    log("%s %s: %s" % ("PASS" if ok else "FAIL", name, detail))
    return ok


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


def num(s, key, default=None):
    m = re.search(r"\b%s (-?[\d.]+)" % re.escape(key), s)
    return float(m.group(1)) if m else default


def vec(s):
    return [float(x) for x in s.strip("()").split(",")]


def shot(name):
    """Both eyes -> CAP/name.jpg (and name_left.jpg)."""
    os.makedirs(CAP, exist_ok=True)
    src = SHOTS + "/P4B_" + name + "_eyes.png"
    try:
        os.remove(src)
    except OSError:
        pass
    send("shot P4B_" + name, 20)
    t0 = time.time()
    while time.time() - t0 < 6 and not (os.path.exists(src) and os.path.getsize(src) > 0):
        time.sleep(0.05)
    time.sleep(0.3)
    if not os.path.exists(src):
        log("  NO SHOT " + name)
        return None
    from PIL import Image
    im = Image.open(src).convert("RGB")
    im.save(CAP + "/" + name + ".jpg", quality=90)
    w, h = im.size
    im.crop((0, 0, w // 2, h)).save(CAP + "/" + name + "_left.jpg", quality=90)
    log("  picture " + CAP + "/" + name + ".jpg")
    return CAP + "/" + name + ".jpg"


def pose():
    s = send("shieldpose?")
    d = {"raw": s}
    for k in ("clock", "faceEl", "faceVsHead", "fit"):
        d[k] = num(s, k)
    for k in ("up", "face", "meshUp", "oldUp"):
        m = re.search(r"\b%s (\([^)]*\))" % k, s)
        if m:
            d[k] = vec(m.group(1))
    m = re.search(r"\| (.*)$", s)
    d["how"] = m.group(1) if m else ""
    return d


def dot(a, b):
    return sum(x * y for x, y in zip(a, b))


def norm(a):
    n = math.sqrt(dot(a, a)) or 1.0
    return [x / n for x in a]


# ------------------------------------------------------------------------------------------------ P4-12: the shield's roll

def part_shield():
    p = pose()
    if p.get("meshUp") is None:
        return result("shield", False, "no shield held: " + p["raw"][:200])
    rows = []
    ok = True
    # BasicShield (the fixtures' gear): the offline mesh check (UnityPy, eq/shield/basicshield) found its mirror line at
    # 57.6 deg from +x, the wide end at 237.6 deg: up (-0.536, -0.844, 0)
    want = norm([-0.536, -0.844, 0.0])
    if "BasicShield" in p["raw"]:
        a = math.degrees(math.acos(max(-1.0, min(1.0, dot(norm(p["meshUp"]), want)))))
        good = a <= 5.0 and "wide end" in p["how"]
        ok &= good
        old = math.degrees(math.acos(max(-1.0, min(1.0, dot(norm(p["oldUp"]), want)))))
        rows.append("BasicShield's up from the mesh %s, %.1f deg from the offline check (the old guess %s: %.0f deg off) - %s" % (
            p["meshUp"], a, p["oldUp"], old, p["how"][:150]))
    configs = [
        # name, controller type, glove fit: the fitted hand straight out (`shieldout`, the runtime's frame offset zeroed first)
        ("index", "knuckles", "default"),
        ("touch_default", "touch", "default"),
        ("touch_userfit", "touch", USER_FIT),
        ("touch_coordfit", "touch", COORD_FIT),
    ]
    send("timescale 0")   # a still arena for the poses and pictures (the hands still follow)
    try:
        sim(HEAD + "; " + SHIELD_OUT_POS + " 0 0 0; " + HAND_R)
        time.sleep(0.4)
        z = send("shieldout l zero")
        rows.append("the runtime's controller frame at rot 0 0 0: " + z)
        for name, ct, fit in configs:
            send("ctrltype " + ct)
            send("glovefit " + fit)
            send("shieldup mesh")
            r = send("shieldout l")
            sim(SHIELD_OUT_POS + "; " + r)
            time.sleep(0.5)
            a = pose()
            good = a["clock"] is not None and abs(a["clock"]) <= 5.0 and abs(a["faceEl"]) <= 5.0 and abs(a["faceVsHead"]) <= 5.0
            ok &= good
            if PICS:
                shot("p412_%s_after" % name)
            send("shieldup old")
            time.sleep(0.4)
            b = pose()
            if PICS and name in ("touch_userfit", "touch_default"):
                shot("p412_%s_before" % name)
            send("shieldup mesh")
            time.sleep(0.2)
            rows.append("%s (%s, fit %s; %s): the shield's up %.1f deg from upright (before the fix: %.1f), face %.1f deg up, %.1f deg off the gaze, the fit turns the hold %.1f deg" % (
                name, ct, fit.split(" side")[0], r, a["clock"], b["clock"] if b["clock"] is not None else 999, a["faceEl"], a["faceVsHead"], a["fit"] or 0))
    finally:
        send("shieldup mesh")
        send("ctrltype auto")
        send("glovefit cfg")
        send("timescale 1")
        sim(HEAD + "; " + S1.HAND_R + "; " + S1.HAND_L)
    return result("shield", ok, " | ".join(rows))


# ------------------------------------------------------------------------------------------------ P4-13: the circle

def in_front(who):
    """The attacker 2 m in front of the player, the view kept on it."""
    send("face track " + who)
    send("heroat %s 0 2.0" % who)


def part_circle():
    r = send("ringframe")
    d = num(r, "d")
    inner = num(r, "inner")
    rule = re.search(r"covers >= ([\d.]+) of the circle r ([\d.]+)", r)
    m = re.search(r"line on shield ([\d.]+)", r)
    line = float(m.group(1)) if m else None
    static_ok = d is not None and abs(d - 0.30) < 1e-3 and inner is not None and 0.13 <= inner <= 0.15 and rule is not None and \
        abs(float(rule.group(1)) - 0.55) < 1e-3 and abs(float(rule.group(2)) - 0.15) < 1e-3 and line is not None and line <= 0.1
    rows = ["ringframe: " + r[:200]]
    live_ok = False
    send("vrlook Gilded")
    pics = 0
    try:
        for _ in range(8):
            if user_in_vr() or (live_ok and (pics >= 2 or not PICS)):
                break
            e = send("enemy? nearest")
            who = "#" + e.split(" id ")[1].split()[0] if " id " in e else "nearest"
            in_front(who)
            S1.reset_pose()
            sim(S1.SHIELD_AWAY + "; " + S1.SWORD_AWAY)
            st = S1.strike(timeout=25, fresh=0.9, colours=("White", "Gold", "Red"))
            if st is None:
                break
            s = send("strike?").split(" | ")[0]
            fin = num(s, "frameIn")
            rr, need = num(s, "r"), num(s, "need")
            if "ring True" in s and fin is not None and rr is not None:
                live_ok = live_ok or (0.13 <= fin <= 0.15 and abs(rr - 0.15) < 0.002 and abs(need - 0.55) < 1e-3)
                rows.append("live %s: ring True, inner %.3f m, rule r %.3f m share %.2f" % (st["who"], fin, rr, need))
                if PICS and pics == 0:
                    # frozen at the wind-up, the view on the attacker: the circle waiting, then the shield moved into it
                    send("timescale 0")
                    send("face track off")
                    send("face striker")
                    # the eyes on the circle: toward its centre (it sits in the guard zone, often low)
                    sc = send("strike?").split(" | ")[0]
                    bm = re.search(r"bar (\([^)]*\))", sc)
                    hm = re.search(r"head (\([^)]*\))", send("enemy? nearest"))
                    if bm and hm:
                        b, hh = vec(bm.group(1)), vec(hm.group(1))
                        dy, dz = b[1] - hh[1], math.hypot(b[0] - hh[0], b[2] - hh[2])
                        sim("head 0 1.7 0 0 %.1f 0" % max(-45.0, min(10.0, math.degrees(math.atan2(dy, max(0.05, dz))))))
                    time.sleep(0.4)
                    shot("p413_circle_waiting")
                    S1.guard_to("shield")
                    time.sleep(0.4)
                    s2 = send("strike?").split(" | ")[0]
                    shot("p413_shield_in_circle")
                    sim(HEAD)
                    send("timescale 1")
                    rows.append("pictures: the shield moved in: onShield %s" % ("onShield True" in s2))
                    pics = 2
            S1.wait_outcome(S1.counts())
            time.sleep(0.6)
    finally:
        send("timescale 1")
        send("face track off")
        send("vrlook setting")
    return result("circle", static_ok and live_ok, " | ".join(rows))


def green_counts():
    c = S1.counts()
    return {k: c.get(k, 0) for k in ("greenBlocked", "greenNotBlocked", "blockedNotGreen", "blocks", "hitsTaken", "counters")}


def guarded_outcome(off_m, tries=6, spec=None):
    """One strike in view: the shield's centre `off_m` along the bar's axis from the circle's centre (re-placed 0.3 s before
    the blow), or placed by `coverat <spec>`; returns (outcome, the circle's green on the frame before the impact (the mod's own
    record), who, the counts' changes, the blow's shield record) or None."""
    for _ in range(tries):
        if user_in_vr():
            return None
        S1.reset_pose()
        sim(S1.SHIELD_AWAY + "; " + S1.SWORD_AWAY)
        st = S1.strike(timeout=25, fresh=0.8, colours=("White", "Gold", "Red"))
        if st is None:
            return None
        ax = st["axis"]
        if spec:
            # (the fight held while the shield is placed: the seam calls take 0.1-0.25 s each at the test headset's 20 Hz, and
            # a thief's blow in view comes 1.0 s after its marker - unheld, the first placement came at eta 0.17)
            send("timescale 0 hold")
            try:
                g0 = green_counts()
                b0 = cover_q()["blows"]
                place(spec)
                time.sleep(0.3)
            finally:
                send("timescale 1")
            t0 = time.time()
            wait = st["eta"] - 0.05 - 0.3
        else:
            g0 = green_counts()
            b0 = cover_q()["blows"]
            t0 = time.time()
            S1.guard_to("shield", (ax[0] * off_m, ax[1] * off_m, ax[2] * off_m))
            wait = st["eta"] - (time.time() - t0) - 0.3
        if wait > 0:
            time.sleep(wait)
            if spec:
                place(spec, ms=30)
            else:
                st1 = S1.strike(timeout=0.2)
                if st1:
                    ax = st1["axis"]
                S1.guard_to("shield", (ax[0] * off_m, ax[1] * off_m, ax[2] * off_m), ms=30)
        what = None
        t1 = time.time()
        while time.time() - t1 < 3.0 and not what:
            g1 = green_counts()
            for k in ("blocks", "hitsTaken", "counters"):
                if g1[k] > g0[k]:
                    what = k
            time.sleep(0.05)
        if what:
            g1 = green_counts()
            green = g1["greenBlocked"] + g1["greenNotBlocked"] > g0["greenBlocked"] + g0["greenNotBlocked"]
            rec = new_rec(b0, 1.0)
            return what, green, st["who"], {k: g1[k] - g0[k] for k in g1}, rec
        time.sleep(0.8)
    return None


def part_edge():
    """The 55 % rule against the old centre rule: the centre 0.24 m off (outside the drawn circle) with the outline still
    covering >= 55 % blocks and is green; 30 % never green, a hit."""
    S1.reset_pose()
    outside_centre = guarded_outcome(0, spec="off 0.24")
    low = guarded_outcome(0, spec="0.30")
    if not outside_centre or not low or not outside_centre[4] or not low[4]:
        return result("edge", False, "too few blows judged: centre outside %s, 30 %% %s" % (outside_centre, low))
    a, b = outside_centre[4], low[4]
    # the centre-outside case blocks when its coverage at the impact holds the rule (it does for every shield >= 0.39 m across the
    # 0.24 m line: a test-side miss if the blow drifted under 55 %)
    ok = a["green"] == a["shield"] and b["green"] == b["shield"] and a["shield"] == rule_ok(a) and b["shield"] == rule_ok(b) and \
        a["shield"] and not b["shield"] and outside_centre[0] in ("blocks", "counters") and low[0] == "hitsTaken"
    return result("edge", ok, "centre 0.24 m off (%s): cover %.2f at the impact, green %s -> %s | 30 %% (%s): cover %.2f, green %s -> %s | %s" % (
        outside_centre[2], a["cover"], a["green"], outside_centre[0], low[2], b["cover"], b["green"], low[0], send("dmglog?")[:160]))


# ------------------------------------------------------------------------------------------------ 2026-10-20: the 55 % rule

NEED = 0.55
FACE_MAX = 65.0


def cover_q():
    """`shieldcover?`: the first live blow's exact coverage, the grid's estimate, the centre's offset, depth, face, green; the count
    of blow records and the last one."""
    s = send("shieldcover?")
    d = {"raw": s}
    m = re.search(r"\| (\w+)( offview)? cover ([\d.]+) grid ([\d.]+) centreOff ([\d.]+) depth ([\d.]+) face ([\d.]+) green (\w+) nf (-?[\d.]+)", s)
    if m:
        d.update(who=m.group(1), cover=float(m.group(3)), grid=float(m.group(4)), off=float(m.group(5)), depth=float(m.group(6)),
                 face=float(m.group(7)), green=m.group(8) == "True", nf=float(m.group(9)), offview=bool(m.group(2)))
    d["blows"] = int(num(s, "blows", 0) or 0)
    recs = s.split(" || ")[1:]
    d["recs"] = recs
    d["last"] = recs[-1] if recs else ""
    d["outline"] = (re.search(r"outline (.*?) \| shield", s) or re.search(r"(.{0,0})", s)).group(1)
    return d


def place(spec, ms=60):
    """`coverat <spec>`: the shield hand moved there; the seam's answer."""
    g = send("coverat " + spec)
    m = re.match(r"(move [lr] \S+ \S+ \S+)", g)
    if not m:
        return {"raw": g, "cover": None}
    sim(m.group(1) + " %d" % ms)
    return {"raw": g, "cover": num(g, "cover"), "off": num(g, "centreOff"), "face": num(g, "face"), "eta": num(g, "eta")}


def parse_rec(line):
    m = re.search(r"#(\d+) (.+?): cover (\S+) \(need ([\d.]+)\) face (\S+) deg \(max (\d+)\) depth (\S+) green (\w+) blocked (yes \(shield\)|yes \(weapon\)|no)", line or "")
    if not m:
        return None
    f = lambda x: float(x) if x != "-" else None  # noqa: E731
    return {"id": m.group(1), "result": m.group(2), "cover": f(m.group(3)), "face": f(m.group(5)), "depth": f(m.group(7)),
            "green": m.group(8) == "yes", "shield": m.group(9) == "yes (shield)", "raw": line}


def new_rec(b0, t=1.5):
    """The first blow record after the count `b0` that was judged on the circle (another attacker's blow from out of view has
    no circle - the arm-out rule decides it, not the 55 % one - and is skipped)."""
    t1 = time.time()
    while time.time() - t1 < t:
        q = cover_q()
        n = q["blows"] - b0
        if n > 0:
            for line in q["recs"][-n:]:
                r = parse_rec(line)
                if r and "off-view" not in line:
                    return r
        time.sleep(0.05)
    return None


def rule_ok(r):
    """The rule as the user gave it (and the kept facing and depth): what must decide green and the block."""
    return r.get("cover") is not None and r["cover"] >= NEED and r["face"] is not None and r["face"] < FACE_MAX and (r.get("depth") or 0) <= 0.4


def look_at_circle():
    """The eyes on the live blow's circle (it sits in the guard zone, often low and to one side): pitched to it, and turned
    toward it by up to 20 deg (the striker stays well inside the view's 40 deg half, so the blow stays in view)."""
    send("face track off")
    send("face striker")
    sc = send("strike?").split(" | ")[0]
    bm = re.search(r"bar (\([^)]*\))", sc)
    tm = re.search(r"tid (#-?\d+)", sc)
    who = tm.group(1) if tm and tm.group(1) != "#0" else "nearest"
    e = send("enemy? " + who)
    hm = re.search(r"head (\([^)]*\))", e)
    am = re.search(r" at (\([^)]*\))", e)
    if not (bm and hm):
        return
    b, hh = vec(bm.group(1)), vec(hm.group(1))
    dy, dz = b[1] - hh[1], math.hypot(b[0] - hh[0], b[2] - hh[2])
    pitch = max(-45.0, min(10.0, math.degrees(math.atan2(dy, max(0.05, dz)))))
    sim("head 0 1.7 0 0 %.1f 0" % pitch)
    if not am:
        return
    a = vec(am.group(1))
    ta, tb = (a[0] - hh[0], a[2] - hh[2]), (b[0] - hh[0], b[2] - hh[2])
    # the circle's bearing from the striker's, seen from above (Unity's SignedAngle about up: + is to the right)
    ang = math.degrees(math.atan2(ta[1] * tb[0] - ta[0] * tb[1], ta[0] * tb[0] + ta[1] * tb[1]))
    want = max(-20.0, min(20.0, 0.8 * ang))
    if abs(want) < 3.0:
        return
    bear0 = num(send("enemy? " + who), "bearing", 0.0)
    for yaw in (-want, want):   # (xrsim's yaw sign against the game's bearing: the one that moves the striker the other way)
        sim("head 0 1.7 0 %.1f %.1f 0" % (yaw, pitch))
        time.sleep(0.15)
        bear = num(send("enemy? " + who), "bearing", None)
        if bear is not None and (bear - bear0) * want < 0 and abs(bear) < 35.0:
            return
    sim("head 0 1.7 0 0 %.1f 0" % pitch)


def shield_name():
    return (re.search(r"^(\w+?)(?:Model)?(?:\(Clone\))?:", cover_q()["outline"]) or re.search(r"(\w*)", "")).group(1) or "shield"


def part_cover():
    rows, ok = [], True
    send("vrlook Gilded")
    q0 = cover_q()
    name = shield_name()
    rows.append("outline " + q0["outline"][:220])
    # the shield squarely ahead (the fitted hand straight out), its rotation kept for every placement
    sim(HEAD + "; " + SHIELD_OUT_POS + " 0 0 0; " + HAND_R)
    time.sleep(0.3)
    out = send("shieldout l")
    mo = re.match(r"rot l (-?[\d.]+) (-?[\d.]+) (-?[\d.]+)", out)
    y0, p0, r0 = (float(mo.group(1)), float(mo.group(2)), float(mo.group(3))) if mo else (0.0, 0.0, 0.0)
    straight = "rot l %.2f %.2f %.2f" % (y0, p0, r0)
    edge_rot = "rot l %.2f %.2f %.2f" % (y0 + 90, p0, r0)
    static_rows = 0
    misses = 0
    try:
        # ---- frozen at a wind-up: the coverage table
        for _ in range(6):
            if user_in_vr():
                break
            e = send("enemy? nearest")
            who = "#" + e.split(" id ")[1].split()[0] if " id " in e else "nearest"
            in_front(who)
            send("face track off")   # (S1.strike turns to whichever attacker strikes; a track would turn the view back)
            S1.reset_pose()
            sim(S1.SHIELD_AWAY + "; " + S1.SWORD_AWAY)
            # (the hold below stands the fight still, so a short lead is enough: candidate 11.1's U run saw every in-view
            # thief blow first at eta 0.82-0.90 - the run attack's 1.0 s lead at the test headset's poll - and fresh=1.0
            # took none in 25 s: no frozen row)
            st = S1.strike(timeout=25, fresh=0.6, colours=("White", "Gold"))
            if st is None:
                misses += 1
                if misses >= 2:
                    break
                continue
            send("timescale 0 hold")   # (the fight clock held: the blow keeps its eta for the whole table)
            look_at_circle()
            sim(SHIELD_OUT_POS + "; " + straight)
            time.sleep(0.35)
            q0 = cover_q()
            if q0.get("cover") is None or q0.get("offview"):
                send("timescale 1")
                S1.wait_outcome(S1.counts())
                time.sleep(0.6)
                continue
            cases = [("0", 0.0), ("0.40", 0.40), ("0.52", 0.52), ("0.58", 0.58), ("0.60", 0.60), ("1.0", 1.0), ("0.40 right", 0.40),
                     ("0.60 up", 0.60), ("off 0.20", None), ("off 0.20 right", None)]
            for spec, want in cases:
                pl = place(spec)
                time.sleep(0.35)
                q = cover_q()
                if q.get("cover") is None:
                    rows.append("%s: no live row (%s)" % (spec, q["raw"][:120]))
                    ok = False
                    continue
                r = {"cover": q["cover"], "face": q["face"], "depth": q["depth"]}
                good = q["green"] == rule_ok(r) and abs(q["grid"] - q["cover"]) <= 0.01
                if want is not None:
                    reach = pl["cover"] if pl["cover"] is not None else 0.0
                    # (a shield that can't cover the whole circle at its best: the most it covers)
                    good &= abs(q["cover"] - min(want, reach if want >= 0.99 else want)) <= 0.03
                ok &= good
                static_rows += 1
                rows.append("frozen %s: cover %.3f (grid %.3f) centre %.3f m off, face %.0f deg, depth %.2f -> green %s%s" % (
                    spec, q["cover"], q["grid"], q["off"], q["face"], q["depth"], q["green"], "" if good else " MISMATCH"))
                if PICS and spec in ("0.52", "0.58"):
                    shot("cover_%s_%s_%s" % (name, spec.replace(".", ""), "green" if q["green"] else "not_green"))
            # the shield turned toward edge-on (its face's level direction `rel` deg from the blow's), its centre on the circle's
            yaw = y0
            for rel in (0, 45, 70, 80, 90):
                q = turn_shield(rel, yaw, p0, r0)
                if q is None or q.get("cover") is None:
                    continue
                yaw = q["yaw"]
                if rel == 90:
                    edge_rot = "rot l %.2f %.2f %.2f" % (yaw, p0, r0)
                r = {"cover": q["cover"], "face": q["face"], "depth": q["depth"]}
                good = q["green"] == rule_ok(r) and abs(q["grid"] - q["cover"]) <= 0.01
                if rel == 90:
                    good &= q["cover"] <= 0.15 and not q["green"]
                ok &= good
                rows.append("turned %d deg (level %.1f), centre on the circle's: face %.0f deg, cover %.3f (grid %.3f) -> green %s%s" % (
                    rel, abs(q["nf"]), q["face"], q["cover"], q["grid"], q["green"], "" if good else " MISMATCH"))
                if PICS and rel == 80:
                    shot("cover_%s_turned80_%s" % (name, "green" if q["green"] else "not_green"))
            sim(HEAD)
            send("timescale 1")
            S1.reset_pose()
            sim(S1.SHIELD_AWAY + "; " + S1.SWORD_AWAY)
            S1.wait_outcome(S1.counts())
            break
    finally:
        send("timescale 1")
        send("face track off")
        sim(HEAD)
    ok &= static_rows >= 8
    # ---- live blows: the per-blow record decides
    live = []
    for spec, edge in (("0", False), ("0.40", False), ("0.60", False), ("1.0", False), ("off 0.20", False), ("off 0", True)):
        if user_in_vr():
            break
        got = None
        for _ in range(4):
            got = cover_live(spec, straight, edge_rot if edge else straight)
            if got and got[0]:
                break
        if not got or not got[0]:
            rows.append("live %s: no record" % spec)
            ok = False
            continue
        rec, who, what, planned = got
        consistent = rec["green"] == rec["shield"] and rec["shield"] == rule_ok(rec)
        ok &= consistent
        live.append((spec + (" edge-on" if edge else ""), rec, what))
        rows.append("live %s%s (%s): placed %s, at the impact cover %s face %s depth %s green %s blocked %s -> %s%s" % (
            spec, " edge-on" if edge else "", who, planned, rec["cover"], rec["face"], rec["depth"], rec["green"], rec["shield"], what,
            "" if consistent else " INCONSISTENT"))
    ok &= len(live) >= 5
    return result("cover", ok, name + ": " + " | ".join(rows))


def turn_shield(rel, yaw, p0, r0):
    """The shield hand turned (xrsim yaw) until the shield's face, seen from above, is `rel` deg from the blow's direction (the
    `nf` of shieldcover?; xrsim's yaw sign against it found by a trial), its centre put on the circle's: shieldcover?'s row + the yaw."""
    def measure(y):
        sim("rot l %.2f %.2f %.2f" % (y, p0, r0))
        time.sleep(0.25)
        place("off 0")
        time.sleep(0.35)
        return cover_q()
    q = measure(yaw)
    sign = None
    for _ in range(4):
        if q.get("nf") is None:
            return None
        err = rel - abs(q["nf"])
        if abs(err) <= 2.0:
            break
        if sign is None:
            q2 = measure(yaw + err)
            sign = 1.0 if q2.get("nf") is not None and abs(rel - abs(q2["nf"])) < abs(err) else -1.0
            if sign > 0:
                yaw += err
                q = q2
                continue
        yaw += sign * err
        q = measure(yaw)
    q["yaw"] = yaw
    return q


def cover_live(spec, rest_rot, hold_rot):
    """One live blow: at its first frame in view the fight is held (`timescale 0 hold`), the shield placed by `coverat <spec>`
    held turned `hold_rot`, then the blow plays out (re-placed 0.3 s before the impact, as a player keeps the shield on the
    moving circle): (its record - the coverage at the impact, green, blocked -, who, the outcome, the coverage placed) or None.
    A blow seen from out of view, or none: (None, ...) - tried again."""
    e = send("enemy? nearest")
    in_front("#" + e.split(" id ")[1].split()[0] if " id " in e else "nearest")
    send("face track off")   # (S1.strike turns to whichever attacker strikes; a track would turn the view back)
    S1.reset_pose()
    sim(S1.SHIELD_AWAY + "; " + S1.SWORD_AWAY + "; " + rest_rot)
    st = S1.strike(timeout=25, fresh=0.6, colours=("White", "Gold"))
    if st is None:
        return None
    planned, eta, b0, c0 = None, st["eta"], 0, {}
    send("timescale 0 hold")
    try:
        send("face striker")
        sim(SHIELD_OUT_POS + "; " + hold_rot)
        time.sleep(0.3)
        place(spec)
        time.sleep(0.35)
        q = cover_q()
        b0 = q["blows"]
        c0 = S1.counts()
        planned = "%.2f" % q["cover"] if q.get("cover") is not None and not q.get("offview") else None
        eta = num(send("strike?").split(" | ")[0], "eta", st["eta"])
    finally:
        send("timescale 1")
    if planned is None:
        S1.wait_outcome(S1.counts(), timeout=3.0)
        time.sleep(0.6)
        return None, st["who"], None, None
    wait = eta - 0.3
    if wait > 0:
        time.sleep(wait)
        place(spec, ms=30)
    what, _ = S1.wait_outcome(c0, timeout=3.5)
    rec = new_rec(b0)
    time.sleep(0.6)
    return rec, st["who"], what, planned


# ------------------------------------------------------------------------------------------------ P4-15: the circle follows the threat

def back_room():
    fm = re.search(r"free ([\d.]+)(\+?) m", send("dash? free 180"))
    return 99.0 if fm and fm.group(2) else (float(fm.group(1)) if fm else 0.0)


def make_room():
    """The hero 2 m from the nearest enemy, facing it, on a side with at least 3.5 m of floor behind (for a full dash back)."""
    e = send("enemy? nearest")
    who = "#" + e.split(" id ")[1].split()[0] if " id " in e else "nearest"
    for deg in (0, 45, -45, 90, -90, 135, -135, 180):
        if send("heroat %s %d 2.0" % (who, deg)).startswith("hero at"):
            time.sleep(0.3)
            if back_room() >= 3.5:
                return True
    return False


def part_threat():
    gone = kept = None
    send("vrlook Gilded")
    try:
        for _ in range(10):
            if user_in_vr() or (gone and kept):
                break
            S1.reset_pose()
            sim(S1.SHIELD_AWAY + "; " + S1.SWORD_AWAY)
            st = S1.strike(timeout=25, fresh=0.9, colours=("White", "Gold", "Red"))
            if st is None:
                break
            c0 = S1.counts()
            s0 = send("strike?").split(" | ")[0]
            t_s0 = time.time()
            if not ("ring True" in s0 and "threat True" in s0):
                time.sleep(max(0.0, st["eta"]) + 0.6)
                continue
            if not kept:
                # (candidate 11: the thieves' running blow has a 1.0 s lead and each seam call takes 0.1-0.25 s at the test
                # headset's 20 Hz - timed from the first sighting's eta, the "halfway" sample came after the impact: "none"
                # four times in a row. Now from this sample's own eta, less the time since)
                em = re.search(r" eta (-?[\d.]+)", s0)
                left = (float(em.group(1)) if em else st["eta"]) - (time.time() - t_s0)
                if left < 0.45:
                    S1.wait_outcome(c0, timeout=3.0)
                    time.sleep(0.6)
                    continue
                # in reach: still up halfway to the blow (this blow's own line, by its attacker's id; a blow that has
                # already ended by then - judged, cancelled - says nothing about the rule: the next blow)
                tm = re.search(r"tid (#-?\d+)", s0)
                tid = tm.group(1) if tm else None
                time.sleep(max(0.0, left * 0.5 - 0.12))
                s1all = send("strike?")
                mine = [x for x in s1all.split(" | ") if tid and ("tid " + tid) in x] or [x for x in s1all.split(" | ") if x.startswith(st["who"])]
                if not mine:
                    log("  threat: %s's blow already over halfway to it (%s): the next blow" % (st["who"], s1all[:80]))
                    S1.wait_outcome(c0, timeout=3.0)
                    time.sleep(0.6)
                    continue
                s1 = mine[0]
                kept = ("threat True" in s1 and "ring True" in s1, "%s in reach halfway to its blow: %s" % (st["who"], s1[:120]))
                S1.wait_outcome(c0, timeout=3.0)
            else:
                # a dash back out of its reach (mark 222216_12: the circle stayed by the player after a dash): the bar and the
                # circle go while the blow is still on its way
                tm = re.search(r"tid (#-?\d+)", s0)
                tid = tm.group(1) if tm else None
                room = back_room()
                moved = ""
                if room < 3.2 and tid and st["eta"] >= 0.7:
                    # (2026-10-20: the thieves' floor ended behind the player for every blow of four turns, and a place made
                    # after a blow was gone by the next. Now, held at this blow: the hero set 2 m in front of its striker,
                    # up to 60 deg round, where 3.2 m of floor lies behind and the blow is still in reach (`inrange`: the
                    # threat's own hide waits 0.12 s of the held clock, so it is not read here); then the dash)
                    send("timescale 0 hold")
                    try:
                        for deg in (0, 30, -30, 60, -60):
                            if not send("heroat %s %d 2.0" % (tid, deg)).startswith("hero at"):
                                continue
                            send("face striker")
                            time.sleep(0.25)
                            room = back_room()
                            sx = [x for x in send("strike?").split(" | ") if ("tid " + tid) in x]
                            if room >= 3.2 and sx and "inrange True" in sx[0] and "ring True" in sx[0]:
                                moved = " (held: the hero set %d deg round its striker, %.1f m behind)" % (deg, room)
                                break
                            room = 0.0
                    finally:
                        send("timescale 1")
                if st["eta"] < 0.7 or room < 3.2:
                    S1.wait_outcome(c0, timeout=3.0)
                    time.sleep(0.6)
                    if room < 3.2:
                        make_room()
                    continue   # no time or no floor behind for the full dash: the next blow
                v0 = send("vrthreat")
                h0, m0 = num(v0, "hides", 0), num(v0, "hiddenThenHit", 0)
                sim("stick l 0 -1 0")
                sim("btn r a 80")
                time.sleep(0.25)
                sim("stick l 0 0 0")
                first, live, hidden = "none", False, False
                for _ in range(4):
                    s1 = send("strike?", 3)
                    mine = [x for x in s1.split(" | ") if tid and ("tid " + tid) in x] or [x for x in s1.split(" | ") if x.startswith(st["who"])]
                    if mine:
                        first, live = mine[0], True
                        if "ring False" in first and "barShown False" in first and "threat False" in first:
                            hidden = True
                            break
                d = send("dash?")
                if PICS and hidden:
                    shot("p415_after_the_dash")
                time.sleep(max(0.0, st["eta"]) + 0.5)
                v1 = send("vrthreat")
                h1, m1 = num(v1, "hides", 0), num(v1, "hiddenThenHit", 0)
                if not live:
                    time.sleep(0.6)
                    continue   # the blow ended at once (judged): not this check
                # (another thug's hit may land meanwhile: the hidden blow's own outcome is the hiddenThenHit count)
                gone = (hidden and h1 > h0 and m1 == m0, "%s%s: shown, then a dash back (%s): %s (hides %d -> %d, hidden then hit %d -> %d)" % (
                    st["who"], moved, (re.search(r"last (\w+ [\d.]+ m)", d) or re.search(r"(.{0,40})", d)).group(1),
                    re.sub(r" (bar|axis) \([^)]*\)", "", first)[:260], h0, h1, m0, m1))
            time.sleep(0.6)
    finally:
        send("vrlook setting")
    if not gone or not kept:
        return result("threat", False, "too few strikes: gone %s kept %s | %s" % (gone, kept, send("vrthreat")))
    v = send("vrthreat")
    missed = num(v, "hiddenThenHit", 0)
    return result("threat", gone[0] and kept[0] and missed == 0, " | ".join([gone[1], kept[1], v]))


# ------------------------------------------------------------------------------------------------ P4-14: the assassins' landing

def part_assassin():
    a0 = send("assassin?")
    l0 = num(a0, "landings", 0)
    sim(HEAD + "; " + S1.HAND_R + "; " + S1.HAND_L)
    t0 = time.time()
    lasts = []
    pic = False
    while time.time() - t0 < 120 and not user_in_vr():
        a = send("assassin?")
        if num(a, "landings", 0) > l0 + len(lasts):
            m = re.search(r"last (.*)$", a)
            lasts.append(m.group(1) if m else a)
            if PICS and len(lasts) <= 3:
                # every landing (up to 3): the first may be one where the ground ended (the game's own landing kept)
                send("face nearest")
                time.sleep(0.05)
                pic = shot("p414_assassin_landed_%d" % len(lasts)) is not None
                log("  landing %d: %s" % (len(lasts), lasts[-1][:160]))
            if len(lasts) >= 4:
                break
        time.sleep(0.05)
    if not lasts:
        return result("assassin", False, "no assassin landing in 120 s: " + send("assassin?"))
    good = 0
    rows = []
    for x in lasts:
        m = re.search(r"landed ([\d.]+) m from the eyes \(the game's ([\d.]+) m", x)
        if m:
            # the 0.7 m out along the ground; where the ground ends first (AssassinRoom: "(the ground ends there)") as far as it
            # goes, never nearer than the game's own landing
            edge = "the ground ends there" in x
            ok = float(m.group(1)) >= float(m.group(2)) + (0.0 if edge else 0.6)
            good += ok
            rows.append("%s m (game %s)%s%s" % (m.group(1), m.group(2), " the ground ends" if edge else "", "" if ok else " SHORT"))
        else:
            rows.append(x[:80])
            good += "ground ends" in x
    return result("assassin", good == len(lasts), "%d landings: %s | %s" % (len(lasts), "; ".join(rows), send("assassin?")[:120]))


# ------------------------------------------------------------------------------------------------ P4-16: green must block

def part_green():
    """The assassins (two, 'behind'): the shield held on every in-view circle for ~2 min. The mod's own record at each impact:
    a circle green on the frame before its blow is a shield block (greenNotBlocked 0); a block costs no health (the hero's
    health after the guard's block: 0); the blows that did land and why (off view, another attacker)."""
    send("dmglog? clear")
    g0 = green_counts()
    judged = []
    t0 = time.time()
    while time.time() - t0 < 120 and len(judged) < 5 and not user_in_vr():
        r = guarded_outcome(0.0, tries=2)
        if r:
            judged.append(r)
    dl = send("dmglog?")
    g1 = green_counts()
    d = {k: g1[k] - g0[k] for k in g1}
    th = num(dl, "toHealthAfterBlock", -1)
    ok = d["greenBlocked"] >= 2 and d["greenNotBlocked"] == 0 and th == 0
    return result("green", ok, "%d judged with the shield on the circle: %s | over the part: %s | %s" % (
        len(judged), ", ".join("%s %s (green %s)" % (r[2], r[0], r[1]) for r in judged), d, dl[:900]))


def part_bladedmg():
    """A blow blocked with the weapon (run it without a shield too: --equip HugeHammer): the damage that still reaches the player
    after the guard's block (`dmglog?` afterBlock lines) must be 0 - the design's "a block costs no health"."""
    send("dmglog? clear")
    got = []
    for _ in range(6):
        if len(got) >= 2 or user_in_vr():
            break
        r = S1.block_once("blade", colours=("White", "Gold", "Red"))
        if r is None:
            break
        got.append(r[0])
    dl = send("dmglog?")
    blocks = sum(1 for w in got if w in ("blocks", "counters"))
    lines = [x for x in dl.split(" | ")[1:] if "AFTER THE GUARD'S BLOCK" in x]
    dmg = [float(m.group(1)) for x in lines for m in [re.search(r" (-?[\d.]+)(?: blocked)? AFTER", x)] if m]
    after = num(dl, "afterBlock", 0)
    ok = (blocks >= 1 or after >= 1) and num(dl, "toHealthAfterBlock", -1) == 0
    return result("bladedmg", ok, "weapon blocks: %s | %s | shields? %s" % (got, dl[:500], send("shots?")[:60]))


# ------------------------------------------------------------------------------------------------ the fight-exit error

def part_exit():
    """A won fight's exit waiting (the portal), the pause menu open, then the game's sign-out (its Quit to title / the Scenarios'
    reload: the reset of playtest 4, 22:18): the hold is dropped ("the game reset"), no "release failed", no exception."""
    logf = hof2env.G + "/BepInEx/LogOutput.log"
    n0 = len(open(logf, errors="replace").read().splitlines())
    send("killall")
    t0 = time.time()
    pending = False
    ei = ""
    while time.time() - t0 < 25:
        ei = send("exitcard info")
        if "no exit pending" not in ei and not ei.startswith("ERR"):
            pending = True
            break
        time.sleep(0.5)
    if not pending:
        return result("exit", False, "no exit pending after killall: " + ei)
    time.sleep(2.5)
    send("pad l menu 150")
    time.sleep(1.5)
    paused = "Level_Pause" in send("state?")
    r = send("exitcard signout", 10)
    time.sleep(15.0)
    lines = open(logf, errors="replace").read().splitlines()[n0:]
    failed = [x for x in lines if "release failed" in x]
    dropped = [x for x in lines if "hold dropped" in x]
    exc = [x for x in lines if "Exception" in x]
    ok = bool(paused and not failed and dropped and "reset" in dropped[0] and not exc)
    return result("exit", ok, "paused %s; %s | %s | release failed %d, exceptions %d%s | state now %s" % (
        paused, r[:60], dropped[0][dropped[0].find("fight exit"):][:120] if dropped else "no hold dropped", len(failed), len(exc),
        (": " + exc[0][:200]) if exc else "", send("state?")[:40]))


PARTS = {"shield": part_shield, "circle": part_circle, "edge": part_edge, "threat": part_threat, "assassin": part_assassin,
         "green": part_green, "bladedmg": part_bladedmg, "exit": part_exit, "cover": part_cover}


def main():
    parts = args[args.index("--parts") + 1].split(",") if "--parts" in args else ["shield", "circle", "edge", "threat"]
    fixture = args[args.index("--fixture") + 1] if "--fixture" in args else "thieves"
    equip = args[args.index("--equip") + 1] if "--equip" in args else "BasicSword_noToken,BasicShield"
    if "--no-fixture" not in args:
        boot = ["--boot"] if "GameState_Map" not in send("state?") else []
        r = subprocess.run(["python3", H + "/scripts/combat_fixture.py", "--fixture", fixture] + boot + ["--god", "--physical", "--equip", equip],
                           capture_output=True, text=True)
        log("fixture %s: %s" % (fixture, ((r.stdout or "").strip().splitlines() or ["?"])[-1]))
    if "Level_Play" not in send("state?"):
        result("fixture", False, "no fight: " + send("state?"))
        return 1
    send("god on")
    log("state %s | %s" % (send("state?")[:60], send("shieldpose?")[:200]))
    for p in parts:
        if user_in_vr():
            log("the user is in VR: stopping")
            break
        try:
            PARTS[p]()
        except Exception as ex:
            result(p, False, "error " + repr(ex))
    sim(HEAD)
    if "--stay" not in args and "exit" not in parts:
        subprocess.run(["python3", "-c", "import sys; sys.path.insert(0, %r); import indicator_shots; indicator_shots.leave()" % (H + "/scripts")])
    log("%s: %d/%d PASS" % (TAG, sum(OUT), len(OUT)))
    return 0 if OUT and all(OUT) else 1


if __name__ == "__main__":
    sys.exit(main())
