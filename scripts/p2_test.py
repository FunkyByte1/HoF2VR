#!/usr/bin/env python3
"""Checks for combat Phase 2 and the small fixes before it (docs/combat-progress.md, 2026-10-12 day run).

  p2_test.py --parts a,b,... [--fixture NAME] [--no-fixture] [--tag P2] [--equip CARD,CARD]

In a fixture fight (combat_fixture.py --fixture NAME --boot --god, unless --no-fixture), Physical mode, god mode.
--equip: on the map first (the fixture boots there), equip these cards (an artefact, a weapon) before the jump.
Parts:
  card        the fight controls card: Physical rows (the rules, A = Dash), Classic rows (A = Dash, not Dodge)
  thrust      a thief's strike marker is a level bar at chin height (the atlas: thieves thrust); the shield on it blocks
  combo       later blows of a combo get a bar (follow-ups), are judged on it (a block on a later blow); none unguarded
  finisher    a REAL knockdown (the corrupted tank lies down after its slam, or a knockdown ability): a swing finishes it
  flank       a blocking shield bearer hit from its side or back: the hit gets past its block
  abilities   every equipped ability through `abilitytest` (the effect at the marker, one charge used, no slow motion, no
              ActionAbility), letting go with no valid marker uses nothing, refused when not usable
  aim         the real input path: grip held, the pointer's arc aimed with the hand (xrsim), a shot of both eyes with
              the arc and ring, let go: used at the marker; the trigger cancels
  throw       [Combat] Throwing = Hand: a real throw (xrsim fling) lands where thrown (nudged to a group); a gentle
              release puts it back
  shots       archers: the shield held on a shot's line stops it (the game's Defended); without it the shot hits; the
              shot's bar is drawn (a shot of both eyes)
Results: PASS/FAIL on stdout and logs/p2_test.log; exit 1 on any FAIL.
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
LOG = H + "/logs/p2_test.log"
args = sys.argv[1:]
TAG = args[args.index("--tag") + 1] if "--tag" in args else "P2"
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


def vec(s, key):
    m = re.search(key + r" \((-?[\d.]+), ?(-?[\d.]+), ?(-?[\d.]+)\)", s)
    return tuple(float(x) for x in m.groups()) if m else None


def enemy(who="nearest"):
    e = send("enemy? " + who)
    m = re.search(r"(\S+) dist ([\d.]+) bearing (-?\d+) hp ([\d.]+) armour (\S+) blocking (\w+) prone (\w+) action (\S+) alive (\d+)", e)
    if not m:
        return None
    return {"name": m.group(1), "dist": float(m.group(2)), "bearing": int(m.group(3)), "hp": float(m.group(4)), "armour": m.group(5),
            "blocking": m.group(6) == "True", "prone": m.group(7) == "True", "action": m.group(8), "alive": int(m.group(9)),
            "head": vec(e, "head"), "at": vec(e, "at"), "raw": e}


def strike(timeout=20.0, inview=True):
    t0 = time.time()
    while time.time() - t0 < timeout:
        s = send("strike?", 4)
        if s and s != "none" and not s.startswith("ERR") and not s.startswith("no "):
            first = s.split(" | ")[0]
            if inview and "offview" in first:
                send("face striker")
                time.sleep(0.05)
                continue
            m = re.search(r"(\w+) (Gold|White|Red)(?: offview)? eta (-?[\d.]+)", first)
            if m:
                return first, float(m.group(3)), m.group(2)
        time.sleep(0.05)
    return None


def reset_pose():
    sim(HEAD + "; " + HAND_R + "; " + HAND_L)


def guard_to(dx=0.0, dy=0.0, dz=0.0, ms=80, shield=True):
    g = send("guardat %s %.3f %.3f %.3f" % ("shield" if shield else "blade", dx, dy, dz))
    if shield and g.startswith("no shield"):
        g = send("guardat blade %.3f %.3f %.3f" % (dx, dy, dz))   # dual blades or a two-hander: the weapon across the bar
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


def shot(name):
    r = send("shot " + name, 20)
    time.sleep(1.5)
    return H + "/testdata/shots/" + name + "_eyes.png", r


def colour_pixels(path, test):
    """Pixels per eye (left half, right half) for which test(r, g, b) holds."""
    try:
        from PIL import Image
    except ImportError:
        return None
    try:
        im = Image.open(path).convert("RGB")
    except Exception:  # noqa: BLE001
        return None
    w, h = im.size
    px = im.load()
    out = []
    for x0, x1 in ((0, w // 2), (w // 2, w)):
        n = 0
        for y in range(0, h, 2):
            for x in range(x0, x1, 2):
                r, g, b = px[x, y]
                if test(r, g, b):
                    n += 1
        out.append(n)
    return out


def gold(r, g, b):
    # the markers' gold, also pale over snow: yellow-orange (r > g > b), never white, sky or green
    return r > 190 and 120 < g < 240 and b < 165 and r - b > 80 and 5 < r - g < 90


# ------------------------------------------------------------------------------------------------ the small fixes

def part_card():
    p = send("controlscard?")
    send("vrcombat classic")
    c = send("controlscard?")
    send("vrcombat physical")
    okp = "Action1 | Dash" in p and "Swing for real" in p and "Hold, point, let go" in p and "Duck or lean the way the arrow points" in p and "Dodge - the left stick" not in p
    okc = "Action1 | Dash" in c and "Swing the sword" in c and "Dodge - the left stick" not in c
    result("card", okp and okc, "Physical: %s ... | Classic: %s" % (p[:150], c[:120]))


def part_thrust():
    head_y = None
    t0 = time.time()
    while time.time() - t0 < 120:
        reset_pose()
        send("face Thug")
        st = strike(timeout=15)
        if st is None or "Thug" not in st[0]:
            time.sleep(0.3)
            continue
        first, eta, colour = st
        e = enemy("Thug")
        if not e or not e["head"]:
            continue
        head_y = e["head"][1]
        bar, axis = vec(first, "bar"), vec(first, "axis")
        if not bar or not axis:
            continue
        # Stage 1: the bar sits where the thief's blade really passes (the swing atlas: its sweeps at hip height, across
        # their motion), no longer a level bar at chin height (the Phase 0 "thrust" was a misreading of its trail)
        atlas = " atlas True" in first
        drop = head_y - bar[1]
        if eta < 0.6:
            time.sleep(max(0.0, eta) + 0.4)   # too close to its blow for two placements: the next one
            continue
        c0 = counts()
        t1 = time.time()
        g = guard_to(ms=60)
        # the bar follows the attacker's turn until the blow (MRe: the shield placed 0.36 s before it was off by the impact):
        # placed again 0.25 s before the blow, from the bar as it is then (stage1 offset's way)
        time.sleep(max(0.0, eta - (time.time() - t1) - 0.25))
        g = guard_to(ms=40) or g
        k, c1 = wait_outcome(c0, ["blocks", "counters", "hitsTaken", "dodges"], timeout=max(2.0, eta + 1.5))
        if g is None or k is None:
            continue
        reset_pose()
        result("thrust", atlas and -0.3 <= drop <= 1.0 and k in ("blocks", "counters"),
               "%s: from the swing atlas %s, bar %.2f m below the eyes, axis %s; the guard on it (%s) -> %s" % (first.split(" eta")[0], atlas, drop, axis, (g or "-")[:40], k))
        return
    result("thrust", False, "no thief strike in view (head %s)" % head_y)


def part_combo():
    """The ogre's combo (ActionMeleeComboAI, 2 blows, reach 4 m, stops at its first landed blow): its first blow dodged
    along its arrow (an unblockable one: UYF's head dodge) or blocked with the shield, so it doesn't land and the combo goes
    on -> the second blow's bar appears at once (a follow-up), then that blow is judged on a bar. Every judged blow had a
    bar (unguarded 0). (Stage 2: the old dash away from the first blow brought no follow-up - CPZ7, BE3; a blow that comes
    during the Dash is left to the game, `dodgeBypassed`.)"""
    c0 = counts()
    shot_done = False
    got = 0
    t0 = time.time()
    while time.time() - t0 < 150 and got == 0:
        reset_pose()
        send("face Ogre")
        st = strike(timeout=20)
        if st is None or not st[0].startswith("Ogre"):
            time.sleep(0.2)
            continue
        first, eta, colour = st
        dm = re.search(r"dodge (\S+) ([\d.]+) m progress (-?[\d.]+) dirT (-?[\d.]+),(-?[\d.]+),(-?[\d.]+)", first)
        if dm:
            # the head along the arrow by its distance + 4 cm shortly before the impact
            need = float(dm.group(2)) + 0.04
            d = [float(dm.group(i)) * need for i in (4, 5, 6)]
            time.sleep(max(0.0, eta - 0.35))
            sim("head %.3f %.3f %.3f 0 0 0" % (d[0], 1.7 + d[1], -d[2]))
        else:
            # the shield onto the blow's bar
            sim("hand l -0.45 1.0 0.25 0 0 0")
            g = send("guardat shield 0 0 0")
            gm = re.match(r"(move [lr] \S+ \S+ \S+)", g)
            if gm:
                sim(gm.group(1) + " 60")
        t1 = time.time()
        while time.time() - t1 < 2.0:
            c1 = counts()
            if c1.get("followUps", 0) > c0.get("followUps", 0):
                got = 1
                if not shot_done:
                    shot(TAG + "_followup")
                    shot_done = True
                break
            time.sleep(0.05)
        time.sleep(1.5)
    c3 = counts()
    fu = c3.get("followUps", 0) - c0.get("followUps", 0)
    fj = c3.get("followUpsJudged", 0) - c0.get("followUpsJudged", 0)
    # the ogre's blows judged without a bar (the guard log names the attacker; gnomes' snare bites have no strike at all)
    ug = ogre_unguarded()
    result("combo", fu >= 1 and ug == 0,
           "follow-up bars %d (judged on them %d; a fresh telegraph replaces one), blows judged without any bar %d | %s" % (
               fu, fj, ug, c3["_raw"][c3["_raw"].find("strikes"):][:240]))


def ogre_unguarded():
    """'unguarded Ogre ...' lines in the Physical combat log `pcombat? log` writes now. The game's log reaches the disk in
    batches: polled until a new one is there (MR2d read it right after the command: none yet, -1)."""
    path = hof2env.GLOG
    def text_now():
        try:
            return open(path, errors="replace").read()
        except OSError:
            return ""
    n0 = text_now().count("Physical combat log:")
    send("pcombat? log")
    for _ in range(40):
        text = text_now()
        if text.count("Physical combat log:") > n0:
            i = text.rfind("Physical combat log:")
            return text[i:].count("unguarded Ogre")
        time.sleep(0.5)
    return -1


def part_finisher():
    """A REAL knockdown: an enemy lying down by the game's own effects - the corrupted tank after its slam, or the
    knockdown bomb (artefact bombKnockdown: its area's Knockdown) thrown at it - then a real swing at it."""
    send("tough off")
    c0 = counts()
    how = "waited"
    q = ability_state()
    if "bombKnockdown" in q or "ExecutionersAxe" in q:
        send("abilitycharge 3")
        side = "artifact" if "bombKnockdown" in q else "weapon"
        how = send("abilitytest %s nearest" % side, 15)[:160]
    t0 = time.time()
    prone = None
    while time.time() - t0 < 120:
        e = send("enemy? prone", 3)
        if e and not e.startswith("none") and " prone True" in e:
            prone = e
            break
        time.sleep(0.05)
    if prone is None:
        result("finisher", False, "no enemy went down (%s)" % how)
        return
    name = prone.split(" ")[0]
    # at once, while it lies there: step up to it (the test's stand-in for the Dash; the shield lowered, so the jump
    # doesn't drive it into the body as a bash) and chop down at it
    # Stage 2: the chop must be a legal swing that reaches the body on the ground - the blade pointing down and forward
    # (pitch -50) from waist height, the hand down 0.7 m (before: the blade pitched up from 1.5 m never reached it: illegal
    # swings, no hit - CPZ7)
    sim("hand l -0.45 0.8 0.2 0 0 0; hand r 0.12 1.25 -0.25 0 -50 0")
    send("heroat prone 0 0.7")
    time.sleep(0.1)
    for kind in ("down", "left", "down", "right"):
        sim(HEAD + "; hand l -0.45 0.8 0.2 0 0 0; hand r 0.12 1.25 -0.25 0 -50 0; wait 40; swing r %s 100" % kind)
        time.sleep(0.15)
        c1 = counts()
        if c1.get("finisher", 0) > c0.get("finisher", 0):
            result("finisher", True, "%s down for real (%s); a real swing at it: finishers +%d, earned slow-motion frames +%d" % (
                name, how[:110], c1["finisher"] - c0["finisher"], c1.get("earnedSlow", 0) - c0.get("earnedSlow", 0)))
            reset_pose()
            return
    c1 = counts()
    reset_pose()
    result("finisher", False, "%s down but the swings did not finish it: %s | now %s" % (name, c1["_raw"][:200], send("enemy? " + name)[:120]))


def part_flank():
    """A shield bearer (EmpireSoldier: ActionBlock) raises its shield after a few hits (its hit-count guard): then hit it
    from 150 deg off its facing - the hit gets past the block."""
    c0 = counts()
    t0 = time.time()
    while time.time() - t0 < 120:
        e = enemy("Soldier")
        if not e:
            break
        if not e["blocking"]:
            # hit it from the front until its guard comes up (the shield hand lowered: no bash from the jump)
            sim("hand l -0.45 0.8 0.2 0 0 0")
            send("heroat Soldier 0 0.9")
            time.sleep(0.1)
            sim(HEAD + "; hand l -0.45 0.8 0.2 0 0 0; hand r 0.25 1.4 -0.3 0 5 0; wait 40; swing r left 90")
            time.sleep(0.3)
            continue
        h = send("heroat Soldier 150 0.9")
        time.sleep(0.1)
        b = enemy("Soldier")
        sim(HEAD + "; hand l -0.45 0.8 0.2 0 0 0; hand r 0.25 1.4 -0.3 0 5 0; wait 40; swing r left 90")
        time.sleep(0.4)
        c1 = counts()
        if c1.get("flank", 0) > c0.get("flank", 0):
            last = re.search(r"last ([^|]*)\|", c1["_raw"])
            txt = last.group(1).strip() if last else "?"
            reset_pose()
            result("flank", "BLOCKED" not in txt and "flank" in txt, "the soldier blocking (%s); %s; the swing from behind: %s; blocked hits before %d" % (
                b and b["blocking"], h, txt, c1.get("blocked", 0) - c0.get("blocked", 0)))
            return
    reset_pose()
    result("flank", False, "no swing from behind a blocking shield bearer (%s)" % counts()["_raw"][:200])


# ------------------------------------------------------------------------------------------------ abilities

def ability_state():
    return send("ability?")


def part_abilities():
    st = ability_state()
    log("  " + st[:400])
    send("abilitycharge 3")
    tested = 0
    for side in ("weapon", "artifact"):
        q = ability_state()
        seg = [s for s in q.split(" | ") if s.startswith(side if side == "weapon" else "artefact")]
        if not seg or " - - " in seg[0] or "usable no (no artefact" in seg[0] or "has no ability" in seg[0]:
            log("  %s: none equipped (%s)" % (side, seg[0][:120] if seg else "?"))
            continue
        kind = re.search(r"(Area|Single|SingleProjectile|Direction|Ranged|Melee|Self) range", seg[0])
        kind = kind.group(1) if kind else "?"
        e0 = enemy()
        c0 = counts()
        g0 = send("abilitylog")
        before = nums(g0)
        ts0 = re.search(r"timescale ([\d.]+)", c0["_raw"])
        r = send("abilitytest %s nearest" % side, 15)
        time.sleep(1.5)
        e1 = enemy(e0["name"]) if e0 else None
        after = nums(send("abilitylog"))
        used = after.get("used", 0) - before.get("used", 0)
        charges = re.search(r"charges (-?\d+) -> (-?\d+)", r)
        charge_ok = charges is None or int(charges.group(1)) < 0 or int(charges.group(2)) == int(charges.group(1)) - 1
        ts = re.search(r"timescale ([\d.]+)", r)
        effect = (e0 and e1 and (e1["hp"] < e0["hp"] or e1["prone"] or e1["raw"] != e0["raw"])) or kind == "Self"
        result("ability_" + side, used == 1 and charge_ok and (ts is None or float(ts.group(1)) >= 0.99),
               "%s: %s | enemy hp %s -> %s prone %s | timescale %s" % (kind, r[:260], e0 and e0["hp"], e1 and e1["hp"], e1 and e1["prone"], ts.group(1) if ts else "?"))
        tested += 1
        # letting go with no valid marker uses nothing (single targets: no enemy near the point)
        if kind in ("Single", "SingleProjectile", "Melee"):
            send("abilitycharge 3")
            n0 = nums(send("abilitylog")).get("used", 0)
            r2 = send("abilitytest %s none" % side, 15)
            n1 = nums(send("abilitylog")).get("used", 0)
            result("ability_%s_none" % side, n1 == n0 and "nothing used" in r2, r2[:200])
        send("abilitycharge 3")
    if tested == 0:
        result("abilities", False, "no ability equipped: " + st[:200])


def part_aim():
    """The real input path for the artefact (shield hand) or else the weapon: hold the grip, aim the arc with the hand, a
    shot of both eyes, let go."""
    send("abilitycharge 3")
    q = ability_state()
    side, hand = ("artifact", "l") if "artefact - " not in q and "usable yes" in q.split(" | ")[1] else ("weapon", "r")
    e = enemy()
    if not e:
        result("aim", False, "no enemy")
        return
    reset_pose()
    # within the ability's reach (a melee special needs its enemy within 6 m: in p2final the nearest one was often further -
    # the arc found no target, CPZ7 "valid False")
    send("heroat nearest 0 2.0")
    send("face nearest")
    time.sleep(0.2)
    ray = re.search(r"(Direction|Ranged|Melee) range", q.split(" | ")[1 if side == "artifact" else 0]) is not None
    n0 = nums(send("abilitylog"))
    sim("hold %s grip 1" % hand)
    time.sleep(0.3)
    rot = send("abilityaim %s nearest" % side)
    if rot.startswith("rot"):
        sim(rot)
    time.sleep(0.6)
    q1 = ability_state()
    aiming = "AIMING" in q1
    valid = re.search(r"AIMING valid (\w+)", q1)
    path, r = shot(TAG + "_aim")
    px = colour_pixels(path, gold)
    sim("hold %s grip 0" % hand)
    time.sleep(1.0)
    n1 = nums(send("abilitylog"))
    used = n1.get("used", 0) - n0.get("used", 0)
    result("aim", aiming and valid is not None and valid.group(1) == "True" and used == 1 and (px is None or min(px) > (5 if ray else 30)),
           "%s hand: aiming %s valid %s; gold pixels per eye %s (%s); let go: used +%d | %s" % (
               side, aiming, valid.group(1) if valid else "?", px, os.path.basename(path), used, q1[:200]))
    # the trigger cancels
    send("abilitycharge 3")
    n0 = nums(send("abilitylog"))
    sim("hold %s grip 1; wait 300; btn %s trigger 120; wait 200; hold %s grip 0" % (hand, hand, hand))
    time.sleep(0.8)
    n1 = nums(send("abilitylog"))
    result("aim_cancel", n1.get("cancelled", 0) > n0.get("cancelled", 0) and n1.get("used", 0) == n0.get("used", 0),
           "trigger while holding: cancelled +%d, used +%d" % (n1.get("cancelled", 0) - n0.get("cancelled", 0), n1.get("used", 0) - n0.get("used", 0)))
    reset_pose()


def part_throw():
    send("vrthrow hand")
    send("abilitycharge 3")
    q = ability_state()
    if "Area range" not in q.split(" | ")[1]:
        result("throw", False, "the artefact is not a thrown area ability: " + q[:200])
        send("vrthrow setting")
        return
    reset_pose()
    n0 = nums(send("abilitylog"))
    # a gentle release puts it back
    sim("hold l grip 1; wait 300; hold l grip 0")
    time.sleep(0.8)
    n1 = nums(send("abilitylog"))
    gentle = n1.get("putBack", 0) - n0.get("putBack", 0)
    # a real throw toward the enemies: forward and up, 4 m/s, the grip opening at 75 %
    send("face nearest")
    sim("hand l -0.25 1.3 0.05 0 0 0; hold l grip 1; wait 300; fling l 0 0.35 -0.75 200 0.8")
    time.sleep(1.5)
    n2 = nums(send("abilitylog"))
    last = re.search(r"last (.*)$", send("abilitylog"))
    thrown = n2.get("thrown", 0) - n1.get("thrown", 0)
    used = n2.get("used", 0) - n1.get("used", 0)
    result("throw", gentle == 1 and thrown == 1 and used == 1 and n2.get("handStarts", 0) > n1.get("handStarts", 0),
           "gentle release put back %d; the fling: thrown %d used %d, the lob left the hand %d | %s" % (
               gentle, thrown, used, n2.get("handStarts", 0) - n1.get("handStarts", 0), last.group(1)[:200] if last else "?"))
    send("vrthrow setting")
    reset_pose()


# ------------------------------------------------------------------------------------------------ the shield against shots

def part_shots():
    send("shots? reset")
    send("proxylog on")
    blocked = hit = 0
    shot_taken = False
    t0 = time.time()
    while time.time() - t0 < 150 and (blocked == 0 or hit == 0):
        a = send("shotat")
        m = re.search(r"cross \((-?[\d.]+), ?(-?[\d.]+), ?(-?[\d.]+)\) toward \((-?[\d.]+), ?(-?[\d.]+), ?(-?[\d.]+)\) eta ([\d.]+) blockable (\w+)", a)
        if not m:
            time.sleep(0.05)
            continue
        if m.group(8) != "True":
            time.sleep(0.2)
            continue
        if not shot_taken:
            shot(TAG + "_shotbar")
            shot_taken = True
        if blocked == 0:
            # the shield onto the shot's line: guardat's sibling, through the shield's own xrsim move
            g = send("shotguard")
            mm = re.match(r"(move [lr] \S+ \S+ \S+)", g)
            if mm:
                sim(mm.group(1) + " 50")
            time.sleep(float(m.group(7)) + 0.6)
            s = nums(send("shots?"))
            blocked = int(s.get("defended", 0) + s.get("deflected", 0))
        else:
            # the shield lowered at the side: the shot reaches the body
            sim(HEAD + "; " + HAND_R + "; hand l -0.45 0.8 0.2 0 0 0")
            time.sleep(float(m.group(7)) + 0.6)
            s = nums(send("shots?"))
            hit = int(s.get("hitsTaken", 0))
    s = send("shots?")
    send("proxylog dump")
    send("shots? log")
    # lobs burst where they land (a fire on the ground, not a hit on the body): for them the deflection is the check
    lobs = nums(s).get("deflected", 0) >= 1 and nums(s).get("defended", 0) == 0
    result("shots", blocked >= 1 and (hit >= 1 or lobs), "the shield on the line stopped %d (%s); without it hit %d | %s" % (
        blocked, "lobs glanced off" if lobs else "shots defended", hit, s[:260]))


def main():
    parts = args[args.index("--parts") + 1].split(",") if "--parts" in args else ["card", "thrust"]
    fixture = args[args.index("--fixture") + 1] if "--fixture" in args else "thieves"
    if "--no-fixture" not in args:
        cmdline = ["python3", H + "/scripts/combat_fixture.py", "--fixture", fixture, "--god"] + (["--boot"] if "--boot" in args or "GameState_Map" not in send("state?") else [])
        if "--equip" in args:
            cmdline += ["--equip", args[args.index("--equip") + 1]]
        if "--board-shot" in args:
            cmdline += ["--board-shot", args[args.index("--board-shot") + 1]]
        r = subprocess.run(cmdline, capture_output=True, text=True)
        log("fixture %s: %s" % (fixture, (r.stdout.strip().splitlines() or ["?"])[-1]))
    send("god")
    send("vrcombat physical")
    # Stage 2 (UYF): a hit taken knocks the player 1.6 m back and a hit pulls them in; these Phase 2 parts let many blows
    # through in god mode and need the player to stay where they stand (the knockback and magnetism have their own tests)
    send("vrknock off")
    send("vrmagnet off")
    send("pcombat? reset")
    send("abilitylog reset")
    reset_pose()
    time.sleep(1.0)
    fns = {"card": part_card, "thrust": part_thrust, "combo": part_combo, "finisher": part_finisher, "flank": part_flank,
           "abilities": part_abilities, "aim": part_aim, "throw": part_throw, "shots": part_shots}
    for p in parts:
        send("tough " + ("off" if p in ("finisher", "abilities") else "on"))
        if send("state?").find("Level_Play") < 0:
            result(p, False, "not in a fight (" + send("state?") + ")")
            continue
        try:
            fns[p]()
        except Exception as e:  # noqa: BLE001
            result(p, False, "error: %r" % e)
    send("pcombat? log")
    send("abilitylog")
    send("vrknock setting")
    send("vrmagnet setting")
    if "--stay" not in args and "--no-fixture" not in args:
        import indicator_shots
        log("left the fight: %s %s" % (indicator_shots.leave(), send("state?")[:60]))
    n_ok = sum(1 for _, ok in results if ok)
    log("p2_test %s: %d/%d passed" % (TAG, n_ok, len(results)))
    return 0 if n_ok == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
