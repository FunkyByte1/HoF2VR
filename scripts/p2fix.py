#!/usr/bin/env python3
"""Checks for the second playtest's table fixes (2026-10-17 evening, env 1; docs/queue.md P2-1..P2-8).

  p2fix.py still [TAG]        P2-1: nothing on the table moves with the head. At the table (the map, a deal, a zoom...):
                              marks where every card / token / stack / die / the figurine is drawn (`prox still mark`),
                              walks and leans the simulated head round the table (forward, down, left, right, a crouch,
                              a step to each side, head turns), and after each pose compares (`prox still check`).
                              PASS when no draw-time move of the mod changed by more than 1 mm at any pose (drawn minus
                              the game's own pose of the same frame; the game's idle motion is listed, not counted). Shots captures/fixes-p2/P2-1_<TAG>_*.
  p2fix.py events [TAG...]    P2-1 at the moments things appear: zoom_life, zoom_weapon, choose3, end_coins (the still walk
                              while each is shown; leaves it open)
  p2fix.py swipe [FILTER [TAG]]  P2-5: a card of a scrolling row pressed 1 cm in then slid 18 cm (scrolls, selects nothing);
                              pressed and lifted (selects on the lift)
  p2fix.py hubwalk [TAG [map|deck]]  from the challenge map: probes + shots there, in the deck builder (select, build),
                              and at the challenge's opening (the figurine); stops after map / deck if asked
  p2fix.py shot NAME [yaw pitch [x y z]]   a both-eye shot -> captures/fixes-p2/NAME.jpg (yaw/pitch from the map's line;
                              x y z: the head's offset from the resting spot in the station's frame: right, up, forward)
  p2fix.py look yaw pitch [x y z]
  p2fix.py pin [TAG]          P2-8: a fingertip straight down onto a challenge map pin opens its deck builder (then B back)
  p2fix.py walk TAG [PART]    the picture walk: both-eye shots with the head moving round the place (PART: the headlook target the
                              walk faces, default the dungeon map; Map_Pivot/MapBoard at the side table)
  p2fix.py pushes [TAG]       any build: KeepOut's push counter at rest, leaning in, leaning down close, at rest again
  p2fix.py rest               both hands down at the sides
Results: logs/p2fix.log.
"""
import math
import os
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cmd as C  # noqa: E402
import hof2env  # noqa: E402
import pt17  # noqa: E402

H = hof2env.H
LOG = os.path.join(H, "logs", "p2fix.log")
pt17.OUT = os.path.join(H, "captures", "fixes-p2")
EYE = 1.66


def log(msg):
    line = time.strftime("%H:%M:%S ") + msg
    print(line, flush=True)
    with open(LOG, "a") as f:
        f.write(line + "\n")


def base_yaw(part="Scale/Map/MapLayout"):
    """The yaw (stage space) of the line from the resting eyes to the map (each boot faces the station another way)."""
    r = C.send("headlook %s from 0 1.66 0" % part, 5)
    try:
        return float(r.split()[4])
    except Exception:
        return 0.0


def pose(base, yaw, pitch, right=0.0, up=0.0, fwd=0.0):
    """The head at the resting spot moved by (right, up, forward) in the station's frame, looking yaw/pitch from the map's line."""
    y = math.radians(base)
    # stage space: forward is -z turned by the yaw about +y; right is +x turned the same way
    fx, fz = -math.sin(y), -math.cos(y)
    rx, rz = math.cos(y), -math.sin(y)
    x = rx * right + fx * fwd
    z = rz * right + fz * fwd
    return pt17.sim("head %.4f %.4f %.4f %g %g 0" % (x, EYE + up, z, base + yaw, pitch))


def prox(op):
    C.send("prox still " + op, 5)
    for _ in range(20):
        time.sleep(0.25)
        r = C.send("prox still", 5)
        if r not in ("marking", "checking"):
            return r
    return r


POSES = [
    # name, yaw, pitch, right, up, forward
    ("rest", 0, -35, 0, 0, 0),
    ("lean_in", 0, -55, 0, -0.10, 0.28),
    ("lean_down_close", 0, -70, 0, -0.30, 0.35),
    ("lean_left", 25, -40, -0.25, -0.05, 0.10),
    ("lean_right", -25, -40, 0.25, -0.05, 0.10),
    ("crouch", 0, -10, 0, -0.45, 0),
    ("step_left", 30, -35, -0.40, 0, 0),
    ("step_right", -30, -35, 0.40, 0, 0),
    ("turn_left", 60, -20, 0, 0, 0),
    ("turn_right", -60, -20, 0, 0, 0),
    ("back_at_rest", 0, -35, 0, 0, 0),
]
SHOTS = {"rest", "lean_in", "lean_down_close", "lean_left", "step_right"}


def still(tag):
    pt17.sim(pt17.REST)
    base = base_yaw()
    pose(base, 0, -35)
    time.sleep(2.0)
    m = prox("mark")
    log("still %s: %s (state %s)" % (tag, m, C.send("state?", 3)))
    worst, bad = 0.0, []
    for name, yaw, pitch, right, up, fwd in POSES:
        pose(base, yaw, pitch, right, up, fwd)
        time.sleep(1.2)
        r = prox("check")
        mm = re.search(r"by the mod.s moves \d+, most ([0-9.]+) mm", r)
        v = float(mm.group(1)) if mm else 999.0
        worst = max(worst, v)
        if v > 1.0:
            bad.append(name)
        log("  %-16s %s" % (name, r[:400]))
        if name in SHOTS:
            log("    " + pt17.shot("P2-1_%s_%s" % (tag, name)))
    ok = not bad and "compared 0 " not in r
    log("RESULT still_%s %s: worst %.1f mm%s" % (tag, "PASS" if ok else "FAIL", worst, (" at " + ", ".join(bad)) if bad else ""))
    pose(base, 0, -35)
    return ok


def pushes(tag):
    """Any build (no seam needed): KeepOut's own push counter at rest and leaning in - a keep-out that follows the head pushes
    cards only while the head is near them."""
    pt17.sim(pt17.REST)
    base = base_yaw()
    for name, yaw, pitch, right, up, fwd in [POSES[0], POSES[1], POSES[2], POSES[0]]:
        pose(base, yaw, pitch, right, up, fwd)
        time.sleep(1.5)
        ko = C.status().get("keepout", "")
        m = re.search(r"pushed (\d+) \((\d+) frames\)", ko)
        log("pushes %s %-16s %s | %s" % (tag, name, m.group(0) if m else "?", ko[-160:]))
        if name == "lean_down_close":
            log("    " + pt17.shot("P2-1_%s_%s" % (tag, name)))


EVENTS = {
    # tag: (seam, seconds to settle) - each from the dungeon map; the still walk runs while the thing is shown
    "zoom_life": ("gain Health+15", 3.0),
    "zoom_weapon": ("gain RustedAxe", 3.5),
    "choose3": ("gain Equipment+1From3", 4.0),
    "end_coins": ("wincoins", 6.0),
    # the Dealer's call to battle: the monster cards gathered and shown, the encounter card offered to touch (B1)
    "battle": ("jump fight EL_Ambush_Goblins", -30.0),
}


def events(tags):
    """P2-1 at the moments something appears or is brought up: the thing appears with the head at rest, then the head walks
    and leans (still); a shot at rest first. Then A / Continue until the map takes jumps again."""
    for tag in tags:
        seam, settle = EVENTS[tag]
        pt17.sim(pt17.REST)
        pose(base_yaw(), 0, -20)
        if settle < 0:
            log("  test gate's auto touch off: " + C.send("gate auto 0", 5))
        log("event %s: %s -> %s" % (tag, seam, C.send(seam, 10)))
        if settle < 0:
            # until the encounter card is offered (the call to battle is over), with shots on the way
            time.sleep(3.0)
            log("  the encounter's option (A): " + pt17.sim("btn r a 200"))
            t0, k = time.time(), 0
            while time.time() - t0 < -settle and not C.status().get("fightCard", "").startswith("offered"):
                time.sleep(1.0)
                if k < 2 and time.time() - t0 > 1 + 1.5 * k:
                    log("  keepout " + C.status().get("keepout", "")[:240])
                    log("  " + pt17.shot("P2-1_%s_during_%d" % (tag, k)))
                    k += 1
            log("  fightCard " + C.status().get("fightCard", "")[:160])
            log("  keepout " + C.status().get("keepout", "")[:240])
            still(tag)   # (at once: the card is offered only a while)
        else:
            time.sleep(settle)
            log("  keepout " + C.status().get("keepout", "")[:240])
            log("  " + pt17.shot("P2-1_%s_appeared" % tag))
            still(tag)
        log("  keepout after " + C.status().get("keepout", "")[:240])


def touch_counts():
    t = C.status().get("touch", "")
    m = re.search(r"selects (\d+) .*swipes (\d+) steps (\d+) coast (\d+)", t)
    h = re.search(r"row presses held (\d+) \(selected on lift (\d+), became swipes (\d+)\)", t)
    return (tuple(int(x) for x in m.groups()) if m else None), (tuple(int(x) for x in h.groups()) if h else None)


def swipe_press(flt, tag):
    """P2-5 (mark 6): on a row that scrolls, a fingertip that PRESSES a card (1 cm into it) and then slides 18 cm along the row
    must scroll and select nothing; a press lifted straight off without sliding selects (on the lift)."""
    out = []
    for name, slide in (("press_then_slide", True), ("press_and_lift", False)):
        p = C.send("touchpose %s 6" % flt, 5)
        if "|" not in p:
            log("RESULT %s_%s FAIL: no card to touch: %s" % (tag, name, p))
            out.append(False)
            continue
        pos, mv = [x.strip() for x in p.split("|")]
        d = [float(v) for v in mv.split()[2:5]]
        c0, h0 = touch_counts()
        pt17.sim("hand r 0.25 1.2 -0.15 0 -30 0; ctouch r trigger 0; squeeze r 1")
        time.sleep(0.4)
        h = C.send("handto r tip " + pos, 5)
        k = 7.0 / 7.5   # from 6 cm in front to 1 cm into the card: a press
        seq = h + " 400; wait 200; move r %.4f %.4f %.4f 250; wait 250; " % (d[0] * k, d[1] * k, d[2] * k)
        if slide:
            seq += "move r -0.18 0 0 450; wait 250; "
        seq += "move r %.4f %.4f %.4f 300" % (-d[0], -d[1], -d[2])
        pt17.sim(seq)
        time.sleep(3.0)
        c1, h1 = touch_counts()
        pt17.sim("squeeze r 0")
        if slide:
            # the row steps with the finger, or coasts on after it (a swipe that leaves the card ends the drag and coasts:
            # candidate 9's side table, steps 0 coast 4)
            ok = c0 and c1 and c1[0] == c0[0] and c1[1] > c0[1] and c1[2] + c1[3] > c0[2] + c0[3]
        else:
            ok = c0 and c1 and c1[0] == c0[0] + 1 and h0 and h1 and h1[1] == h0[1] + 1
        log("RESULT %s_%s %s: selects %s -> %s, swipes %s -> %s, row steps %s -> %s, coast %s -> %s | held presses %s -> %s" % (
            tag, name, "PASS" if ok else "FAIL", c0 and c0[0], c1 and c1[0], c0 and c0[1], c1 and c1[1], c0 and c0[2], c1 and c1[2],
            c0 and c0[3], c1 and c1[3], h0, h1))
        out.append(bool(ok))
        time.sleep(1.0)
    return all(out)


def wait_menu(part, timeout=30):
    t0 = time.time()
    while time.time() - t0 < timeout:
        st = C.status()
        if part in st.get("menu", "") or part in st.get("state", ""):
            return True
        time.sleep(1.0)
    return False


def logs(label, cmdline, n=1400):
    r = C.send(cmdline, 10)
    log("%s: %s" % (label, r[:n]))
    return r


def hubwalk(tag, stop_at=None):
    """The side table, step by step (P2-5..P2-8, and P2-2/P2-4 at the challenge's opening), from a session at the challenge map
    (session.py start --snapshot magician --to table): probes to the log and both-eye shots at each step."""
    pt17.sim(pt17.REST)
    log("hub %s: %s / %s" % (tag, C.status().get("state"), C.status().get("menu")))
    if "Attract" in C.status().get("state", ""):
        C.send("press A 6", 5)   # the caravan's Start
        if not wait_menu("ChallengeMap", 30):
            log("  no challenge map: %s / %s" % (C.status().get("state"), C.status().get("menu")))
            return
        time.sleep(4.0)
        log("  at %s / %s, place %s" % (C.status().get("state"), C.status().get("menu"), C.status().get("place", "")[:60]))
    logs("  boards", "boards")
    C.send("hubgeo", 10)
    time.sleep(0.5)
    log("  hubgeo -> the log (" + C.status().get("tableHud", "")[:60] + ")")
    for name, yaw, pitch in (("ahead", 0, -40), ("left", 40, -25), ("down", 0, -65)):
        pose(0.0, 0, 0)   # (the map's line is not the hub's: absolute head turns from the station's facing)
        pt17.look(yaw, pitch)
        time.sleep(0.8)
        log("  " + pt17.shot("%s_challengemap_%s" % (tag, name)))
    if stop_at == "map":
        return
    C.send("press A 6", 5)
    if not wait_menu("Deckbuilder", 20):
        log("  no deck builder: " + C.status().get("menu", ""))
        return
    time.sleep(3.0)
    C.send("hubgeo", 10)
    log("  deck builder: " + C.status().get("menu", "") + " | " + C.status().get("deckrows", "")[:200])
    for name, yaw, pitch in (("ahead", 0, -45), ("down", 0, -65)):
        pt17.look(yaw, pitch)
        time.sleep(0.8)
        log("  " + pt17.shot("%s_deckbuilder_select_%s" % (tag, name)))
    C.send("press Y 6", 5); time.sleep(3)
    C.send("press A 6", 5); time.sleep(3)
    log("  after Recommend + Fill: " + C.status().get("menu", ""))
    for name, yaw, pitch in (("ahead", 0, -45), ("down", 0, -65)):
        pt17.look(yaw, pitch)
        time.sleep(0.8)
        log("  " + pt17.shot("%s_deckbuilder_build_%s" % (tag, name)))
    swipe_press("Card_EN", tag + "_deck_swipe")
    if stop_at == "deck":
        return
    C.send("press X 6", 5)
    if not wait_menu("ChallengeSetup", 40):
        log("  no challenge setup: %s / %s" % (C.status().get("state"), C.status().get("menu")))
        return
    time.sleep(8.0)
    log("  setup: %s / %s | fig %s | %s" % (C.status().get("state"), C.status().get("menu"), C.send("fig where", 5), C.status().get("figurine", "")[:200]))
    base = base_yaw()
    for name, yaw, pitch in (("ahead", 0, -40), ("left", 35, -45)):
        pose(base, yaw, pitch)
        time.sleep(0.8)
        log("  " + pt17.shot("%s_setup_%s" % (tag, name)))
    inventory(tag)


def inventory(tag):
    """P2-4: the inventory opened (its touch plate), its parts measured (`invgeo`, to the log) and shot."""
    log("  inventory: " + C.send("ptouch nav:Inventory", 5))
    if not wait_menu("Inventory", 15):
        C.send("press L 6", 5)
        if not wait_menu("Inventory", 10):
            log("  no inventory: " + C.status().get("menu", ""))
            return
    time.sleep(2.5)
    C.send("invgeo", 10)
    pt17.sim(pt17.REST)
    for name, yaw, pitch in (("ahead", 0, -10), ("down", 0, -30)):
        pt17.look(yaw, pitch)
        time.sleep(0.8)
        log("  " + pt17.shot("%s_inventory_%s" % (tag, name)))


WALK = [
    # name, yaw, pitch, right, up, forward: the head moving round the place as a player's does
    ("rest", 0, -40, 0, 0, 0),
    ("lean_in", 0, -55, 0, -0.10, 0.25),
    ("lean_left", 20, -45, -0.20, -0.05, 0.10),
    ("lean_right", -20, -45, 0.20, -0.05, 0.10),
    ("step_left", 25, -35, -0.35, 0, 0),
    ("step_right", -25, -35, 0.35, 0, 0),
    ("crouch", 0, -15, 0, -0.35, 0),
    ("look_up", 0, -5, 0, 0, 0),
]


def walk(tag, part="Scale/Map/MapLayout", poses=None):
    """The picture walk (step 6 of the env 1 job): both-eye shots of the place with the head moving round it (fixes-p2/<TAG>_<pose>);
    the mod's moves checked as in `still` (prox still mark at rest, check at each pose)."""
    pt17.sim(pt17.REST)
    base = base_yaw(part)
    pose(base, 0, -40)
    time.sleep(1.5)
    log("walk %s (%s, base yaw %.1f): %s | %s / %s" % (tag, part, base, prox("mark"), C.status().get("state"), C.status().get("menu")))
    worst = 0.0
    for name, yaw, pitch, right, up, fwd in (poses or WALK):
        pose(base, yaw, pitch, right, up, fwd)
        time.sleep(1.0)
        r = prox("check")
        mm = re.search(r"by the mod.s moves \d+, most ([0-9.]+) mm", r)
        v = float(mm.group(1)) if mm else 999.0
        worst = max(worst, v)
        log("  %-12s mod's move %s mm | %s" % (name, mm.group(1) if mm else "?", pt17.shot("%s_%s" % (tag, name))))
    pose(base, 0, -40)
    log("RESULT walk_%s: the mod's moves at most %.1f mm" % (tag, worst))


def pin(tag="P2-8_pin"):
    """P2-8: at the challenge map, a fingertip straight down onto a pin's top (touchpose finds its pressed face): PASS when the
    pin is pressed (the deck builder opens); then B back to the map."""
    pt17.sim(pt17.REST)
    if not wait_menu("ChallengeMap", 5):
        log("RESULT %s FAIL: not at the challenge map (%s)" % (tag, C.status().get("menu")))
        return False
    p = C.send("touchpose ChallengeMap 6", 5)
    if "|" not in p:
        log("RESULT %s FAIL: no pin to touch: %s" % (tag, p))
        return False
    pos, mv = [x.strip() for x in p.split("|")]
    d = [float(v) for v in mv.split()[2:5]]
    c0, _ = touch_counts()
    pt17.sim("hand r 0.25 1.2 -0.15 0 -30 0; ctouch r trigger 0; squeeze r 1")
    time.sleep(0.4)
    h = C.send("handto r tip " + pos, 5)
    pt17.sim(h + " 400; wait 200; move r %.4f %.4f %.4f 300; wait 300; move r %.4f %.4f %.4f 300" % (d[0] * 7 / 7.5, d[1] * 7 / 7.5, d[2] * 7 / 7.5, -d[0], -d[1], -d[2]))
    ok = wait_menu("Deckbuilder", 6)
    c1, _ = touch_counts()
    pt17.sim("squeeze r 0")
    pt17.sim(pt17.REST)
    log("RESULT %s %s: pose %s | selects %s -> %s | menu %s" % (tag, "PASS" if ok else "FAIL", p, c0 and c0[0], c1 and c1[0], C.status().get("menu")))
    if ok:
        C.send("press B 6", 5)
        wait_menu("ChallengeMap", 10)
        time.sleep(1.5)
    return ok


def main(a):
    if not a:
        print(__doc__)
        return
    if a[0] == "still":
        still(a[1] if len(a) > 1 else "map")
    elif a[0] in ("shot", "look"):
        v = a[2:] if a[0] == "shot" else a[1:]
        if len(v) >= 2:
            off = [float(x) for x in v[2:5]] + [0.0] * (3 - len(v[2:5]))
            pose(base_yaw(), float(v[0]), float(v[1]), *off)
            time.sleep(0.7)
        if a[0] == "shot":
            print(pt17.shot(a[1]))
    elif a[0] == "events":
        events(a[1:] or list(EVENTS))
    elif a[0] == "swipe":
        swipe_press(a[1] if len(a) > 1 else "Card_EN", a[2] if len(a) > 2 else "P2-5")
    elif a[0] == "hubwalk":
        hubwalk(a[1] if len(a) > 1 else "P2hub", a[2] if len(a) > 2 else None)
    elif a[0] == "inventory":
        inventory(a[1] if len(a) > 1 else "P2-4")
    elif a[0] == "pin":
        sys.exit(0 if pin(a[1] if len(a) > 1 else "P2-8_pin") else 1)
    elif a[0] == "walk":
        walk(a[1] if len(a) > 1 else "walk", a[2] if len(a) > 2 else "Scale/Map/MapLayout")
    elif a[0] == "pushes":
        pushes(a[1] if len(a) > 1 else "map")
    elif a[0] == "rest":
        print(pt17.sim(pt17.REST))
    else:
        print(__doc__)


if __name__ == "__main__":
    main(sys.argv[1:])
