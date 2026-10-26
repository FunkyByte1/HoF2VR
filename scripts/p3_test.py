#!/usr/bin/env python3
"""Playtest 3 (2026-10-18) fight fixes, env 2: the user's rulings on candidate 9 and the coordinator's follow-ups.

  p3_test.py [--tag T] [--fixture NAME|--no-fixture] [--stay] [--parts a,b,...] [--equip CARDS]

Fixtures (EL_Ambush_Thral with): mix = a thug (basic), an Empire soldier (basic, shield), a musketeer (advanced), a juggernaut
(elite); lobs = two molotov throwers; others: combat_fixture.py's names.
Parts:
  tiers    hits to break each enemy's guard with standard sword hits (the seam's `bladetest`), against its tier's count (basic
           3, advanced 5, elite 7, boss 10; the axe: basic 2) - the soldier's shield-raise does not stop the break; a table row
           per enemy (UYF's guard before -> now)
  refill   one hit on a thug, then the guard measured until full again: the refill takes about 10 s (basic), never under 4 s;
           every enemy's full-refill time (poise? refill) >= 4 s
  damage   hands down, an enemy hit lands: the game's damage reached the player x0.33 (vrdamagein: in -> out)
  reverse  (mark 22) an open crit: a cut against its arrow is wrong (critsWrong +1, no crit, still open); along it: a crit
  lined    (note A) the bar while lined up is green: the premo seam's still bar, valid vs live, green pixels in both eyes
  ring     (note B) a blow with a shield held shows the circle; the shield on the circle blocks (onShield, blocks +1); the
           shield moved to cover only 30 % of the circle (`coverat 0.30`, under the 55 % rule of 2026-10-20) does not
  ground   (note C) without a shield (lobs fixture, --equip HugeHammer): a lob's wind-up shows the red ground arrow, no circle
  groundgold (the ruling of 17:40) with a shield: a lob shows the gold-white ground arrow, filling, and the circle; no bar
  glow     (note D) the super full: the weapon hand glows; used (a stick tap): the glow goes
  stick    (mark 11) the left stick held 1.5 s opens no pause; the weapon hand's stick held 1 s aims (stickHolds +1) and the
           ability is used when it is let go
  pips     (mark 10) an enemy no weapon can kill (the tutorial's skeletons) shows the bomb mark, not pips (enemybars? bombOnly)
  shield   (mark 29) the held shield's face stays within 25 deg below / 55 deg above level with the wrist pointing at the floor
  mirror   (mark 16) the swing trace on the soldiers: each blow's bar on the side its blade passes (swingmatch?), per clip
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

H = hof2env.H
args = sys.argv[1:]
TAG = args[args.index("--tag") + 1] if "--tag" in args else "P3"
OUT = []
HEAD = "head 0 1.7 0 0 0 0"
HANDS_DOWN = "hand r 0.32 0.85 0.05 0 -80 0; hand l -0.32 0.85 0.05 0 -80 0"
HAND_R = "hand r 0.25 1.35 -0.35 0 5 0"
HAND_L = "hand l -0.25 1.2 -0.3 0 0 0"
SHIELD_AWAY = "hand l -0.45 1.0 0.25 0 0 0"
OUTCOMES = ["blocks", "counters", "hitsTaken", "dodges", "outOfRange"]
FIXTURES = {"mix": "ThievesThugREF*1,EmpireSoldierREF*1,EmpireMusketeerREF*1,CorruptedJuggernautREF*1",
            "tut": "Tutorial*2,ThievesMolotovThrowerREF*2",
            "dodge2": "CorruptedJuggernautREF*1,ShadowWarriorREF*1"}
TIER_HITS = {"Basic": 3, "Advanced": 5, "Elite": 7, "boss": 10}


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


def nums(s):
    d = {"_raw": s}
    for k, v in re.findall(r"([A-Za-z]+) (-?[\d.]+)", s):
        if k not in d:
            try:
                d[k] = float(v)
            except ValueError:
                pass
    return d


def counts():
    return nums(send("pcombat?"))


def poise(who="nearest"):
    p = send("poise? " + who)
    m = re.search(r"(\w+) (\w[\w ]*?) guard ([\d.]+)/(\d+) phase (\w+) step (\d+) crit (.*?) stunned (\w+)", p)
    if not m:
        return {"raw": p}
    d = {"who": m.group(1), "guard": float(m.group(3)), "phase": m.group(5), "crit": m.group(7), "raw": p}
    for k in ("tier", "hitsToBreak", "refill", "regen", "max"):
        mm = re.search(r"\b%s (\S+)" % k, p)
        if mm:
            d[k] = mm.group(1)
    im = re.search(r"id (#-?\d+)", p)
    if im:
        d["id"] = im.group(1)
    d["blocking"] = "blocking True" in p
    return d


def strike(timeout=25.0, fresh=0.6, colours=("White", "Gold", "Red")):
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
                vec = lambda v: [float(x) for x in v.strip("()").split(",")]
                return {"who": m.group(1), "colour": m.group(2), "eta": float(m.group(3)), "bar": vec(m.group(5)), "axis": vec(m.group(6)), "raw": first}
        time.sleep(0.04)
    return None


def wait_outcome(c0, timeout=4.0):
    t0 = time.time()
    while time.time() - t0 < timeout:
        c = counts()
        for k in OUTCOMES:
            if c.get(k, 0) > c0.get(k, 0):
                return k, c
        time.sleep(0.05)
    return None, counts()


def guard_to(kind="shield", off=(0.0, 0.0, 0.0), ms=50):
    g = send("guardat %s %.3f %.3f %.3f" % (kind, off[0], off[1], off[2]))
    m = re.match(r"(move [lr] \S+ \S+ \S+)", g)
    if m:
        sim(m.group(1) + " %d" % ms)
    return g


def recovered(who, timeout=15.0):
    t0 = time.time()
    while time.time() - t0 < timeout:
        p = poise(who)
        if p.get("phase") == "Up" and p.get("max") and abs(p["guard"] - float(p["max"])) < 0.6:
            return p
        time.sleep(0.25)
    return poise(who)


# ------------------------------------------------------------------------------------------------ the guard (rulings 1-2)

def hits_to_break(who, cap=14):
    # full guard first: since the regrow rule (68ab8b4) an enemy is Up at once with a nearly empty guard that refills over its
    # tier's time (elite 8 s after the stagger) - 15 s was not always enough (the hud regression: the juggernaut's axe "1")
    p = recovered(who, 25.0)
    if p.get("phase") != "Up":
        return None, p
    n = 0
    shield_up = False
    g = p.get("guard", 0)
    for _ in range(cap):
        send("bladetest %s" % who)
        time.sleep(0.45)
        q = poise(who)
        shield_up |= q.get("blocking", False)
        if q.get("phase") == "Broken" or q.get("phase") == "Recovering":
            return (n + 1, shield_up), q
        # a blow that took nothing off (it missed or was dodged) is not a hit (the hud regression: the thug's sword "4")
        if q.get("guard", g) < g - 0.01:
            n += 1
        g = q.get("guard", g)
    return (None, shield_up), poise(who)


def part_tiers():
    rows = []
    ok_all = True
    names = ["ThievesThug", "EmpireSoldier", "EmpireMusketeer", "CorruptedJuggernaut", "EmpirePhalanx", "ThievesMolotovThrower", "SkeletonNEW"]
    seen = 0
    for name in names:
        e = send("enemy? " + name)
        if not e.startswith(name):
            continue
        seen += 1
        p0 = poise(name)
        who = p0.get("id", name)
        tier = p0.get("tier", "?")
        want = TIER_HITS.get(tier, 3)
        et = send("enemytable? %s" % who)
        um = re.search(r"UYF's guard ([\d.]+)", et)
        uyf = float(um.group(1)) if um else -1
        # sword (the fixture's arming sword)
        send("aside %s 4" % who)
        (sw, up1), q1 = hits_to_break(who)
        send("vrweapon heavyaxe")
        (ax, up2), q2 = hits_to_break(who)
        send("vrweapon game")
        hm = re.search(r"hitMult (\S+)", et)
        ok = sw == want and ax is not None and ax <= max(2, want - 1) and ax >= max(1, math.ceil(want * 7 / 11.0) - 1)
        ok_all &= ok
        rows.append("%s %s guard UYF %s -> %s: sword %s (want %d) axe %s%s" % (name, tier, uyf, p0.get("max", "?"), sw, want, ax, " (its shield went up meanwhile)" if (up1 or up2) else ""))
        log("  tiers: " + rows[-1])
    if seen == 0:
        return result("tiers", False, "no known enemy in the fight")
    return result("tiers", ok_all, " | ".join(rows))


def part_refill():
    name = "ThievesThug" if send("enemy? ThievesThug").startswith("ThievesThug") else "nearest"
    p = recovered(name)
    who = p.get("id", name)
    full = float(p.get("max", 0) or 0)
    if full <= 0:
        return result("refill", False, "no guard: " + p.get("raw", "")[:160])
    send("aside %s 4" % who)
    sim(HEAD + "; " + HANDS_DOWN + "; " + SHIELD_AWAY)
    send("bladetest %s" % who)
    t_hit = time.time()
    g1 = poise(who)["guard"]
    # the refill rate over a clean rise (a chip meanwhile - a block, a stray hit - starts the measure again)
    t_start = t_full = None
    g_start = last = g1
    drops = 0
    while time.time() - t_hit < 30:
        time.sleep(0.15)
        q = poise(who)
        if q.get("phase") != "Up":
            break
        g = q["guard"]
        now = time.time()
        if g < last - 0.05:
            drops += 1
            t_start = None
        elif t_start is None and g > last + 0.05:
            t_start, g_start = now, g
        if t_start is not None and g >= full - 0.05:
            t_full = now
            break
        last = g
    if t_start is None or t_full is None or t_full - t_start < 0.3:
        return result("refill", False, "no clean refill seen (guard %.1f of %.1f after the hit, %d drops)" % (g1, full, drops))
    span = t_full - t_start
    whole = span * full / max(0.01, full - g_start)
    claimed = float(p.get("refill", 0) or 0)
    # every enemy in the fight: its full refill >= 4 s
    worst = []
    for name2 in ["ThievesThug", "EmpireSoldier", "EmpireMusketeer", "CorruptedJuggernaut", "EmpirePhalanx", "ThievesMolotovThrower"]:
        if send("enemy? " + name2).startswith(name2):
            r = float(poise(name2).get("refill", 0) or 0)
            worst.append("%s %.1f s" % (name2, r))
            if r < 4.0:
                claimed = -1
    ok = claimed >= 4.0 and abs(whole - float(p.get("refill", 0) or 0)) <= 0.15 * float(p.get("refill", 1) or 1)
    return result("refill", ok, "%s: one hit took the guard to %.1f of %.1f; the rise from %.1f took %.1f s: a whole refill %.1f s (claimed %s s; %d drops meanwhile) | %s" % (
        name, g1, full, g_start, span, whole, p.get("refill"), drops, ", ".join(worst)))


def part_damage():
    sim(HEAD + "; " + HANDS_DOWN + "; " + SHIELD_AWAY)
    near = "ThievesThug" if send("enemy? ThievesThug").startswith("ThievesThug") else "nearest"
    send("heroat %s 0 1.7" % near)
    send("face track " + near)
    d0 = send("vrdamagein")
    m0 = re.search(r"cut (\d+)", d0)
    n0 = int(m0.group(1)) if m0 else 0
    for _ in range(6):
        st = strike(timeout=25, fresh=0.3)
        if st is None:
            break
        c0 = counts()
        what, c1 = wait_outcome(c0, 4)
        if what != "hitsTaken":
            log("  damage: outcome %s" % what)
            continue
        send("face track off")
        time.sleep(0.2)
        d1 = send("vrdamagein")
        m = re.search(r"x([\d.]+) \(cut (\d+), last ([\d.]+) -> ([\d.]+)\)", d1)
        if not m or int(m.group(2)) <= n0:
            return result("damage", False, "a hit landed but no damage passed the hook: " + d1)
        k, din, dout = float(m.group(1)), float(m.group(3)), float(m.group(4))
        ratio = dout / din if din > 0 else -1
        return result("damage", abs(ratio - 0.33) < 0.02 and abs(k - 0.33) < 0.01, "%s's blow: the game's %.2f -> %.2f reached the player (x%.3f; setting x%.2f)" % (st["who"], din, dout, ratio, k))
    send("face track off")
    return result("damage", False, "no blow landed in 6 tries")


# ------------------------------------------------------------------------------------------------ mark 22

def open_crit(name):
    p = recovered(name)
    who = p.get("id", name)
    send("poiseset %s 0" % who)
    t0 = time.time()
    while time.time() - t0 < 2.5:
        q = poise(who)
        if q.get("phase") == "Broken" and q.get("crit", "-") != "-":
            return who, q
        time.sleep(0.1)
    return who, None


def part_reverse():
    name = "ThievesThug" if send("enemy? ThievesThug").startswith("ThievesThug") else "nearest"
    who, q = open_crit(name)
    if q is None:
        return result("reverse", False, "no open crit")
    send("face " + who)
    time.sleep(0.2)
    c0 = counts()
    send("bladetest %s against" % who)
    time.sleep(0.15)
    c1 = counts()
    q1 = poise(who)
    wrong = c1.get("critsWrong", 0) - c0.get("critsWrong", 0)
    landed = c1.get("critsLanded", 0) - c0.get("critsLanded", 0)
    still = q1.get("phase") == "Broken" and q1.get("crit", "-") != "-"
    send("bladetest %s along" % who)
    time.sleep(0.15)
    c2 = counts()
    landed2 = c2.get("critsLanded", 0) - c1.get("critsLanded", 0)
    last = re.search(r"poise (.*)$", c2["_raw"])
    return result("reverse", wrong >= 1 and landed == 0 and still and landed2 >= 1,
                  "against the arrow: wrong +%d, crits +%d, still open %s | along it: crits +%d | %s" % (wrong, landed, still, landed2, (last.group(1) if last else "")[:120]))


# ------------------------------------------------------------------------------------------------ notes A-D

def shot(name):
    p = H + "/testdata/shots/%s_%s_eyes.png" % (TAG, name)
    try:
        os.remove(p)
    except OSError:
        pass
    send("shot %s_%s" % (TAG, name), 20)
    t0 = time.time()
    while time.time() - t0 < 5.0 and not (os.path.exists(p) and os.path.getsize(p) > 0):
        time.sleep(0.1)
    time.sleep(0.3)
    return p if os.path.exists(p) else None


def green_pixels(path, base=None):
    """Green pixels in `path` (every other one); with `base`, only those not green there (the scene is frozen: the game's own
    green attack glow on an enemy stays out of the count)."""
    from PIL import Image
    im = Image.open(path).convert("RGB")
    px = im.load()
    bp = Image.open(base).convert("RGB").load() if base else None
    w, h = im.size
    green = lambda c: c[1] > 150 and c[1] > c[0] + 50 and c[1] > c[2] + 40
    n = 0
    for y in range(0, h, 2):
        for x in range(0, w, 2):
            if green(px[x, y]) and not (bp and green(bp[x, y])):
                n += 1
    return n


def part_lined():
    rows = []
    ok = True
    sim(HEAD + "; " + HANDS_DOWN)
    send("face nearest")
    send("timescale 0")
    try:
        for look in ("Gilded", "Pill"):
            send("vrlook " + look)
            send("premo bar 0.6 live 1 60 0 -0.12")
            time.sleep(0.3)
            a = shot("lined_%s_live" % look)
            send("premo bar 0.6 valid 1 60 0 -0.12")
            time.sleep(0.3)
            b = shot("lined_%s_valid" % look)
            send("premo off")
            if not a or not b:
                ok = False
                rows.append(look + ": no shot")
                continue
            ga, gb = green_pixels(a), green_pixels(b, a)
            good = gb >= 150   # (every other pixel; the bar half behind an enemy still gives ~240)
            ok &= good
            rows.append("%s: green pixels live %d, new green when lined up %d" % (look, ga, gb))
    finally:
        send("premo off")
        send("timescale 1")
        send("vrlook setting")
    return result("lined", ok, " | ".join(rows))


def part_ring():
    """Note B: the shield on the circle (`coverat 1.0`: its outline over all of it) blocks; moved along the bar until its
    outline covers only 30 % of the circle (`coverat 0.30`; the 55 % rule of 2026-10-20 - before it, 0.35 m along the bar,
    which a 0.8 m shield still covers by about 75 %) it does not. Both placements are made while the fight is held
    (`timescale 0 hold`: a thief's blow in view comes 1.0 s after its marker), then re-placed 0.3 s before the impact
    (candidate 11.1: stage1's unheld block_once came too late once)."""
    sys_argv = sys.argv[:]
    sys.argv = [sys.argv[0]]
    import stage1_test as S1
    sys.argv = sys_argv

    def cover_to(share, ms=60):
        g = send("coverat %.2f" % share)
        m = re.match(r"(move [lr] \S+ \S+ \S+)", g)
        if m:
            sim(m.group(1) + " %d" % ms)
        return g

    def held_blow(share):
        """One blow in view with the shield placed (`coverat <share>`) while the fight is held, re-placed 0.3 s before the
        impact: (outcome, who, the seam's answer, the circle shown) or None (no strike); outcome None = not judged."""
        st0 = S1.strike(timeout=25, fresh=0.6, colours=("White", "Gold", "Red"))
        if st0 is None:
            return None
        S1.reset_pose()
        S1.sim(S1.SHIELD_AWAY + "; " + S1.SWORD_AWAY)   # the sword off every bar (P3S: the soldiers' low bar met the guarding sword)
        send("timescale 0 hold")
        try:
            c0 = S1.counts()
            g = cover_to(share)
            time.sleep(0.3)
        finally:
            send("timescale 1")
        if not g.startswith("move"):
            S1.wait_outcome(c0)
            time.sleep(0.6)
            return None, st0["who"], g, "ring True" in st0["raw"]
        wait = st0["eta"] - 0.05 - 0.3
        if wait > 0:
            time.sleep(wait)
            cover_to(share, ms=30)
        what, c1 = S1.wait_outcome(c0)
        return (what if what in ("blocks", "hitsTaken", "counters") else None), st0["who"], g, "ring True" in st0["raw"]

    on = off = None
    rings = []
    placed = ""
    for _ in range(8):
        if on and off:
            break
        # on the circle: the outline over all of it (candidate 11.1: block_once's unheld placement came at eta 0.12 after a
        # thief's run attack, the shield 0.77 m off the circle at the impact - a hit, rightly, but not the case this tests)
        # / off the circle: the outline over 30 % of it
        r = held_blow(1.0 if not on else 0.30)
        if r is None:
            break
        what, who, g, ring = r
        if not what:
            continue
        if not on:
            rings.append(ring)
            on = (what, who)
        else:
            off = (what, who)
            placed = (re.search(r"cover [\d.]+ t [\d.]+", g) or re.search(r"(.{0,0})", g)).group(0)
    if not on or not off:
        return result("ring", False, "too few blows judged: on %s off %s" % (on, off))
    ok = on[0] in ("blocks", "counters") and off[0] == "hitsTaken" and all(rings)
    return result("ring", ok, "the circle shown %s | the shield on the circle (%s) -> %s | covering 30 %% of it (%s, %s) -> %s" % (
        all(rings), on[1], on[0], off[1], placed, off[0]))


def part_ground():
    s = send("shots?")
    if "shield none" not in s:
        return result("ground", False, "a shield is held (run with --fixture lobs --equip HugeHammer): " + s[-80:])
    sim(HEAD + "; " + HANDS_DOWN)
    seen = None
    t0 = time.time()
    g0 = nums(send("shots?")).get("grounds", 0)
    while time.time() - t0 < 60 and not seen:
        send("face nearest")
        s = send("shots?", 4)
        for part in s.split(" | ")[1:]:
            if " eta " in part and "ground True" in part:
                # an arrow made before g0 was read (a lob already coming when the part began: p3i's "grounds +0") is not
                # counted; wait for a fresh one
                if nums(s).get("grounds", 0) > g0:
                    seen = part
                break
        time.sleep(0.05)
    if not seen:
        return result("ground", False, "no fresh lob wind-up with the ground arrow in 60 s")
    g1 = nums(send("shots?")).get("grounds", 0)
    ok = "bar False" in seen and "ring False" in seen and "blockable False" in seen and g1 > g0
    return result("ground", ok, "a lob without a shield: %s | grounds +%d" % (seen[:200], g1 - g0))


def part_groundgold():
    """The ruling of 17:40: with a shield, a lob (or shot) shows the ground arrow in gold-white and the shield's circle, no bar;
    the arrow fills until it arrives (shots? lastGround)."""
    s = send("shots?")
    if "shield none" in s:
        return result("groundgold", False, "no shield held: " + s[-60:])
    sim(HEAD + "; " + HANDS_DOWN)
    seen = None
    seen_at = 0.0
    fills = []
    t0 = time.time()
    # (each poll is two seam calls, about 0.5 s at the test headset's 20 Hz, and a lob flies about 1 s: waiting for more than
    # three fill samples before the sighting missed every lob on 2026-10-20 - now the lob seen, then its fill followed to
    # its arrival, 1.5 s)
    while time.time() - t0 < 60:
        if seen and time.time() - seen_at > 1.5:
            break
        send("face nearest")
        s = send("shots?", 4)
        m = re.search(r"lastGround \[([^\]]*) fill ([\d.]+)\]", s)
        if m:
            v = float(m.group(2))
            if fills and v < fills[-1] - 0.05:
                fills = []   # a new shot's arrow: its own fill from here
            fills.append(v)
        if not seen:
            for part in s.split(" | ")[1:]:
                if " eta " in part and "ground True" in part and "ring True" in part:
                    seen, seen_at = part, time.time()
                    break
        time.sleep(0.05)
    if not seen:
        return result("groundgold", False, "no shot with the gold arrow and the circle in 60 s")
    rising = len(fills) >= 2 and fills[-1] > fills[0]
    ok = "bar False" in seen and "blockable True" in seen and rising and "gold" in send("shots?")
    return result("groundgold", ok, "with a shield: %s | fills %s" % (seen[:200], " ".join("%.2f" % f for f in fills[-6:])))


def part_postbreak():
    """Audit item 2: after a guard break's stagger the guard grows back from empty at its refill rate (full in its refill time,
    >= 4 s), the enemy fighting again at once."""
    name = "ThievesThug" if send("enemy? ThievesThug").startswith("ThievesThug") else "nearest"
    p = recovered(name)
    who = p.get("id", name)
    disturbed = 0
    for attempt in range(3):
        send("aside %s 4" % who)
        sim(HEAD + "; " + HANDS_DOWN + "; " + SHIELD_AWAY)
        send("poiseset %s 0" % who)
        t0 = time.time()
        up_at = None
        samples = []
        drop = False
        while time.time() - t0 < 30:
            q = poise(who)
            if q.get("phase") == "Up" and q.get("max"):
                if up_at is None:
                    up_at = time.time()
                # (candidate 11.1: the thief struck the hero while it refilled and the shield's off-view block chipped its
                # guard 12 -> 1.6, so the refill began again - full 19.7 s after it stood up; the measurement is redone)
                if samples and q["guard"] < samples[-1][1] - 1.0:
                    drop = True
                    break
                samples.append((time.time() - up_at, q["guard"], float(q["max"])))
                if q["guard"] >= float(q["max"]) - 0.05:
                    break
            time.sleep(0.25)
        if not drop:
            break
        disturbed += 1
        time.sleep(2.0)
    if disturbed:
        log("postbreak: the refill was cut by a hit or a block %d time(s) - measured again" % disturbed)
    if up_at is None or len(samples) < 3:
        return result("postbreak", False, "no recovery seen in 30 s: " + poise(who).get("raw", "")[:160])
    g0, full = samples[0][1], samples[0][2]
    t_full = samples[-1][0]
    claimed = float(p.get("refill", 0) or 0)
    # from (near) empty to full in about its refill time
    ok = g0 <= 0.25 * full and samples[-1][1] >= full - 0.05 and t_full >= 4.0 and abs(t_full - claimed * (1 - g0 / full)) <= 0.2 * claimed
    return result("postbreak", ok, "%s: back in the fight with guard %.1f of %.1f, full %.1f s later (its refill %s s)" % (name, g0, full, t_full, p.get("refill")))


def part_pause():
    """Audit item 3: a pause keeps the fight - a chipped guard stays chipped through it (no refill meanwhile), a broken enemy
    stays broken (its crit still open), and the poise state is not rebuilt (same max)."""
    name = "ThievesThug" if send("enemy? ThievesThug").startswith("ThievesThug") else "nearest"
    p = recovered(name)
    who = p.get("id", name)
    send("aside %s 4" % who)
    sim(HEAD + "; " + HANDS_DOWN + "; " + SHIELD_AWAY)
    send("bladetest %s" % who)
    g1 = poise(who)["guard"]
    send("pad l menu 150")
    time.sleep(1.0)
    paused = "Level_Pause" in send("state?")
    time.sleep(5.0)   # longer than the 5 s refill delay: in the old build the state was gone anyway
    send("pad l menu 150")
    time.sleep(1.0)
    back = "Level_Play" in send("state?")
    g2 = poise(who)["guard"]
    # a broken one through a pause
    send("poiseset %s 0" % who)
    time.sleep(0.8)
    b1 = poise(who)
    send("pad l menu 150")
    time.sleep(3.0)
    send("pad l menu 150")
    time.sleep(0.8)
    b2 = poise(who)
    ok = paused and back and abs(g2 - g1) <= 0.6 and g1 < float(p.get("max", 99)) - 1 and b1.get("phase") == "Broken" and b2.get("phase") == "Broken"
    return result("pause", ok, "paused %s, back %s | chipped guard %.1f before, %.1f after 6 s paused | broken: %s -> %s (crit %s)" % (
        paused, back, g1, g2, b1.get("phase"), b2.get("phase"), b2.get("crit", "-")[:30]))


def part_leanarrow():
    """Audit item 5: with LeanDodge on, a blow the game can't block shows the red ground arrow - no bar, no circle."""
    send("vrlean on")
    try:
        sim(HEAD + "; " + HANDS_DOWN + "; " + SHIELD_AWAY)
        for _ in range(6):
            st = strike(timeout=25, fresh=0.3, colours=("Red",))
            if st is None:
                break
            time.sleep(0.1)
            s = send("strike?", 4)
            row = [r for r in s.split(" | ") if " Red " in r]
            if row:
                r = row[0]
                ok = "ground True" in r and "barShown False" in r and "ring False" in r
                return result("leanarrow", ok, "%s" % r[:220])
        return result("leanarrow", False, "no red blow in view")
    finally:
        send("vrlean setting")


def abil():
    return nums(send("ability?"))


def part_glow():
    sim(HEAD + "; " + HAND_R + "; " + HAND_L)
    send("face nearest")
    send("super? set 100")
    time.sleep(0.4)
    s1 = send("super?")
    on = "handGlow True" in s1
    a0 = abil()
    send("face nearest")
    sim("btn %s stick 150" % ("r",))
    time.sleep(0.8)
    a1 = abil()
    s2 = send("super?")
    off = "handGlow False" in s2
    used = a1.get("used", 0) > a0.get("used", 0)
    at = re.search(r"handGlowAt (\([^)]*\))", s1)
    return result("glow", on and used and off, "full: handGlow %s at %s | a weapon stick tap: used +%d | after: handGlow %s (%s)" % (
        on, at.group(1) if at else "-", a1.get("used", 0) - a0.get("used", 0), not off, s2[:90]))


def part_stick():
    sim(HEAD + "; " + HAND_R + "; " + HAND_L)
    st0 = send("state?")
    sim("btn l stick 1500")
    time.sleep(1.9)
    st1 = send("state?")
    paused = "Level_Pause" in st1 or "Pause" in send("pcombat?")[:0]
    if "Level_Pause" in st1:
        send("pad l menu 150")
        time.sleep(0.8)
    send("abilitycharge 3")
    send("super? set 100")
    send("face nearest")
    time.sleep(0.3)
    # point the hand at the nearest enemy (a melee ability needs one under its ring: P3M on 53ddace7 pointed past it)
    m = re.match(r"(rot [lr] \S+ \S+ \S+)", send("abilityaim weapon nearest"))
    if m:
        sim(m.group(1))
        time.sleep(0.15)
    a0 = abil()
    sim("hold r stick 1")
    time.sleep(1.0)
    a1 = abil()
    aiming = a1.get("stickHolds", 0) > a0.get("stickHolds", 0)
    sim("hold r stick 0")
    time.sleep(0.6)
    a2 = abil()
    used = a2.get("used", 0) > a0.get("used", 0)
    last = re.search(r"last (.*)$", a2["_raw"])
    return result("stick", not paused and aiming and used, "left stick held 1.5 s: %s -> %s (no pause: %s) | right stick held 1 s: stickHolds +%d, used on release +%d (%s)" % (
        st0[:28], st1[:28], not paused, a1.get("stickHolds", 0) - a0.get("stickHolds", 0), a2.get("used", 0) - a0.get("used", 0), (last.group(1) if last else "")[:110]))


def part_pips():
    s = send("enemybars?") if False else send("bars?")
    if "bombOnly" in s:
        m = re.findall(r"(\S+)[^|]*pips (\d+) bombOnly", s)
        return result("pips", True, "bomb-only meters: %s | %s" % (m[:4], s[:160]))
    return result("pips", False, "no bomb-only meter (an enemy no weapon can kill must be in the fight): " + s[:200])


def part_shield():
    rows = []
    ok = True
    # the wrist pointing at the floor (the shield hand low and ahead, as in mark 29), level, and raised
    for name, pose in (("down80", "hand l -0.2 1.15 -0.45 0 -80 0"), ("down45", "hand l -0.2 1.2 -0.45 0 -45 0"), ("level", "hand l -0.2 1.3 -0.4 0 0 0"),
                       ("up60", "hand l -0.2 1.4 -0.4 0 60 0")):
        sim(HEAD + "; " + pose)
        time.sleep(0.35)
        m = re.search(r"shieldFace (-?\d+)", send("pcombat?"))
        el = int(m.group(1)) if m else 999
        good = -25 <= el <= 55
        ok &= good
        rows.append("%s: face %s deg" % (name, el))
    return result("shield", ok, " | ".join(rows))


def part_mirror():
    """Mark 16: every sweep's bar on the side its blade is heading (the side it lands): the swing trace's bar centre (x right)
    against the blade's sideways motion where it passed the bar's plane, for the soldiers (and any other enemy traced)."""
    send("swingtrace clear")
    send("swingtrace on")
    sim(HEAD + "; " + HANDS_DOWN + "; " + SHIELD_AWAY)
    who = "EmpireSoldier" if send("enemy? EmpireSoldier").startswith("EmpireSoldier") else "nearest"
    send("face track " + who)
    send("heroat %s 0 1.9" % who)
    t0 = time.time()
    while time.time() - t0 < 70 and not user_in_vr():
        time.sleep(1.0)
    send("face track off")
    d = send("swingtrace dump %s_mirror" % TAG, 15)
    send("swingtrace off")
    import csv
    path = H + "/testdata/probes/%s_mirror.tsv" % TAG
    try:
        rows = list(csv.DictReader(open(path), delimiter="\t"))
    except OSError:
        return result("mirror", False, "no trace dump: " + d[:160])
    good = bad = 0
    per = {}
    for r in rows:
        try:
            bx = float(r["bar_c"].split(",")[0])
            vel = [float(x) for x in r["blade_vel"].split(",")]
        except (KeyError, ValueError, IndexError):
            continue
        vm = math.sqrt(sum(c * c for c in vel)) or 1.0
        if abs(vel[0]) < 0.3 * vm or abs(bx) < 0.05:
            continue   # not a sweep, or a bar in the middle
        ok = (bx > 0) == (vel[0] > 0)
        good += ok
        bad += not ok
        k = r["enemy"].replace("Model", "") + " " + r["clip"].split("/")[-2] if "/" in r["clip"] else r["enemy"] + " " + r["clip"][:20]
        per.setdefault(k, [0, 0])[0 if ok else 1] += 1
    n = good + bad
    if n < 3:
        return result("mirror", False, "too few sweeps traced (%d): %s" % (n, d[:160]))
    return result("mirror", good >= 0.8 * n, "sweeps with the bar on the side the blade heads: %d/%d | %s" % (
        good, n, ", ".join("%s %d/%d" % (k, v[0], v[0] + v[1]) for k, v in sorted(per.items()))))


PARTS = {"tiers": part_tiers, "refill": part_refill, "damage": part_damage, "reverse": part_reverse, "lined": part_lined, "ring": part_ring,
         "ground": part_ground, "groundgold": part_groundgold, "postbreak": part_postbreak, "pause": part_pause, "leanarrow": part_leanarrow, "glow": part_glow, "stick": part_stick, "pips": part_pips, "shield": part_shield, "mirror": part_mirror}


def main():
    parts = args[args.index("--parts") + 1].split(",") if "--parts" in args else ["tiers", "refill", "damage", "reverse", "lined", "ring", "shield"]
    fixture = args[args.index("--fixture") + 1] if "--fixture" in args else "mix"
    equip = args[args.index("--equip") + 1] if "--equip" in args else "BasicSword_noToken,BasicShield"
    if "--no-fixture" not in args:
        boot = ["--boot"] if "GameState_Map" not in send("state?") else []
        how = ["--jump", "EL_Ambush_Thral", "--with", FIXTURES[fixture]] if fixture in FIXTURES else ["--fixture", fixture]
        r = subprocess.run(["python3", H + "/scripts/combat_fixture.py"] + how + boot + ["--god", "--physical", "--equip", equip], capture_output=True, text=True)
        log("fixture %s: %s" % (fixture, ((r.stdout or "").strip().splitlines() or ["?"])[-1]))
    if "Level_Play" not in send("state?"):
        result("fixture", False, "no fight: " + send("state?"))
        return 1
    send("god on")
    log("DLL %s | %s" % (send("state?")[:60], send("vrdamagein")))
    for p in parts:
        if user_in_vr():
            log("the user is in VR: stopping")
            break
        try:
            PARTS[p]()
        except Exception as ex:
            result(p, False, "error " + repr(ex))
    sim(HEAD)
    if "--stay" not in args:
        subprocess.run(["python3", "-c", "import sys; sys.path.insert(0, %r); import indicator_shots; indicator_shots.leave()" % (H + "/scripts")])
    log("%s: %d/%d PASS" % (TAG, sum(OUT), len(OUT)))
    return 0 if OUT and all(OUT) else 1


if __name__ == "__main__":
    sys.exit(main())
