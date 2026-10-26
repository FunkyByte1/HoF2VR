#!/usr/bin/env python3
"""The Dealer's table proximity sweep (round 5 item 5; user 2026-10-12: "Can you try to check every kind of thing that
can happen at the dealer table to make sure none of it has anything too close to the player?").

  table_sweep.py [--start] [--heights 1.60,1.75,1.90] [--lean] [--only label,label] [--seated] [--reach]

--seated (2026-10-13, seated mode): the game seated (HOF2VR_SEATED=1, `cfg Seated true`) and the simulated head and hands
0.55 m lower (xrsim `seat 0.55`): the heights 1.60 / 1.75 / 1.90 become seated eyes at 1.05 / 1.20 / 1.35 m. Seated is
chosen again at each height with the head there (2026-10-16), so each height is a seated player of that height; the dice's
marking phase is answered by a fingertip poke on a die, its end point rated for seated reach.

From the dungeon map (session.py start --snapshot magician_mid --to table): every table scenario of the Scenarios
catalog that is safe to run in place (`scen here`, the test's own save) is run with the proximity watchdog
(Diag/Proximity.cs) reset before it: rewards and losses (the game's own fortune cards), equipment (the equip question:
answered No), blessing, curse, the chance cards, the pendulum, the wheel, the dice, the shop, a story with choices, the
inventory, a fight's entry and return; last the end-of-challenge coins (it ends the run). Per player height (and leaning
over the table with --lean). The result per event: the closest distance to the eyes and the body, first-frame
violations, the objects - logs/table_sweep.log, the PROX_* shots in testdata/shots.
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
LOG = H + "/logs/table_sweep.log"
REST = "hand r 0.2 1.0 -0.3 0 -60 0; hand l -0.2 1.0 -0.3 0 -60 0"
# (catalog label, how long the event takes, what answers it)
EVENTS = [
    ("Gain life", 9, None), ("Gain gold", 9, None), ("Lose gold", 9, None), ("Gain food", 9, None), ("Lose food", 9, None),
    ("Max health up", 9, None),
    # Blessing and Curse: their card waits for Continue; until 2026-10-13 a button press answered it and a second one landed on
    # the map once it was back (the hero walked into a fight every time) - now only a touch on the Continue plate, found by
    # its label in the game's own frame (never a button, never a map plate)
    ("Blessing", 16, "touch"), ("Curse", 16, "touch"),
    ("Gain a weapon", 8, "B"), ("Gain a shield", 8, "B"), ("Gain armour", 8, "B"), ("Gain a helm", 8, "B"), ("Gain an artifact", 8, "B"),
    ("Random equipment", 10, "B"),
    ("Choose a card", 22, "chance"), ("Pendulum", 14, "stop"), ("Wheel", 14, "stop"), ("Dice", 12, "dice"),
    ("Shop", 10, "shop"), ("Dialogue", 12, "story"),
]
LAST = [("End coins", 40, "coins"), ("Challenge map", 70, "hub")]


def log(msg):
    line = time.strftime("%H:%M:%S ") + msg
    print(line, flush=True)
    with open(LOG, "a") as f:
        f.write(line + "\n")


def send(c, t=10):
    try:
        return cmd.send(c, t)
    except SystemExit as e:
        return "ERR " + str(e)


def sim(c):
    subprocess.run(["python3", H + "/scripts/xrsim.py", c], capture_output=True, text=True)


def st():
    send("state")
    try:
        return cmd.status()
    except Exception:
        return {}


def state():
    return send("state?", 5)


def jump_ready():
    """The dev jump's own test: it answers 'no encounter like' only when the map takes jumps (nothing left open)."""
    return send("jump encounter zz_ready_probe").startswith("no encounter like")


def map_ready(timeout=40, act=False):
    """Waits for the dungeon map with nothing open; with act, finishes what is open as a player would (No to an equip
    question, the first option of a story, A for a Continue)."""
    t0 = time.time()
    acted = 0
    while time.time() - t0 < timeout:
        s = st()
        if "GameState_Map" in state() and s.get("menu") == "Map" and jump_ready():
            return True
        # an event that went on into a fight (a press that reached the map): won at once, back to the table
        stt = state()
        if "Level_Play" in stt:
            log("      (a fight began: won at once to get back to the map)")
            send("jump win")
            time.sleep(4)
            continue
        if "Level_In" in stt:
            send("gate go")
            send("press A 6")   # the campfire's A (the arena waits for it)
            time.sleep(2)
            continue
        if act == "touch":
            # (Blessing, Curse) only the Continue plate, by its label: never a button that could reach the map
            if s.get("menu") not in ("Map", None, "") and "nav:Continue" in s.get("boards", ""):
                send("ptouch nav:Continue")
            time.sleep(1.5)
            continue
        # (every act is a touch on a plate shown now, found by its label: no limit is needed - the old two-press limit kept A
        # presses from reaching the map, and stalled the chains of a story: choice, gain, choice, page)
        # never on the dungeon map itself: its plates are the next places to go (a tap walks the hero on - into a fight)
        if act and time.time() - t0 > 2 and s.get("menu") not in ("Map", None, ""):
            # by touch, the plate found by its label in the game's own frame (2026-10-13: an A press on the wheel's last page
            # was its Redraw, and the wheel spun again and again; a numbered touch can land on what took its number)
            b = s.get("boards", "")
            if s.get("menu") == "Inventory":
                send("ptouch nav:Back")   # (its Equip would equip the hovered card)
                time.sleep(1.5)
                continue
            if s.get("menu") == "DiceMenu":
                dice_step(s)
                time.sleep(1.5)
                continue
            if s.get("menu") == "Choice" and not any(k in b for k in ("nav:Continue", "nav:No", "nav:Select", "nav:Yes")):
                # a choice of cards with nothing chosen yet (a story's reward): a card touched, then its Continue (the chosen
                # card comes forward first: 3 s)
                send("touch CardChoiceContainer/Card_ r tap")
                time.sleep(3.0)
                continue
            if s.get("menu") == "Choice" and "nav:Select" in b and not re.search(r"\[\d+ (?!nav:)", b):
                send("ptouch nav:Select")
                time.sleep(1.5)
                continue
            if "nav:No" in b:
                send("ptouch nav:No")
                acted += 1
            elif s.get("menu") == "Encounter" and re.search(r"\[\d+ (?!nav:)", b):
                send("ptouch opt")
                acted += 1
            elif s.get("chance", "-").startswith("running"):
                send("touch CardChoiceContainer/Card_CN r tap")   # the chance game's pick by a fingertip
                acted += 1
            else:
                w = next((w for w in ("nav:Continue", "nav:OK", "nav:Confirm", "nav:Equip", "nav:Reserve", "nav:Store", "nav:Yes") if w in b), None)
                if w:
                    send("ptouch " + w)
                    acted += 1
        time.sleep(1.5)
    return False


# the marking phase's real-hand pokes (2026-10-16): (die, its seated rating) per poke; dice marked; marking phases left unmarked
DICE = {"pokes": [], "marked": 0, "unmarked": 0}
DIE_NAMES = ["D6 (%d)" % i for i in range(1, 10)] + ["D6"]   # (the plain name last: it finds the shortest-named die)


def poke_die(name):
    """A real-hand poke on a die, as a player marks one: the fingertip from 6 cm above its face 1.5 cm into it (`touchpose`,
    `handto`), the end point rated for seated reach in a seated run (`seatreach tip r`). None = no touchable die of that name now."""
    r = send("touchpose PlayerDiceRoller/Container/%s 6 r" % name)
    if "|" not in r:
        return None
    pos, press = [x.strip() for x in r.split("|")]
    h = send("handto r tip " + pos)
    if not h.startswith("move"):
        return None
    sim(h + " 400; wait 300; " + press + " 300")
    rate = send("seatreach tip r") if SEAT[0] else "standing"
    p = press.split()
    sim("wait 150; move r %.4f %.4f %.4f 300" % (-float(p[2]), -float(p[3]), -float(p[4])))
    return rate


def mark_die():
    """One die marked by a fingertip, die after die until one is (2026-10-16: the sweep's laser click on D6 (6) came from the
    resting hand - seated, 0.45 m above the real floor, under the table top - and met a map card lying next to the dice, so the
    seated dice never got marked; a seated player lifts the hand over the table and touches the die, lasers off)."""
    sim("hand r 0.2 %.2f -0.3 0 -60 0" % (1.0 + (0.5 if SEAT[0] else 0.0)))   # the hand up over the table first
    time.sleep(0.5)
    for name in DIE_NAMES:
        rate = poke_die(name)
        if rate is None:
            continue
        time.sleep(0.8)
        d = st().get("dice", "") or ""
        DICE["pokes"].append((name, rate))
        log("      dice: %s touched (%s) -> %s" % (name, rate[:70], d[:24]))
        if d.startswith("Marking") and "marked=0" not in d:
            DICE["marked"] += 1
            return True
    DICE["unmarked"] += 1
    return False


def dice_step(s):
    """The dice game the way a player finishes it (2026-10-13: the sweep only threw, and the game sat in its marking phase):
    thrown (by the hand seam); in the marking phase one die marked by a fingertip and re-rolled (X - the dice's own button);
    once they lie, Continue touched by its label."""
    d = s.get("dice", "") or ""
    b = s.get("boards", "")
    if d.startswith("Marking"):
        if "marked=0" in d:
            mark_die()
        send("press X 6")
    elif d.startswith("Ready") and "nav:Continue" in b:
        send("ptouch nav:Continue")
    elif d.startswith("Catapault") or d.startswith("Ready") and "landed" not in d:
        send("dice throw 0 0.5 2.0")


def answer(kind, t_end):
    """Plays the event out the way a player would, quickly. A and B at most twice each per event: a press that lands just
    after the game went back to the map selects the next place there and walks the hero into a fight."""
    presses = 0
    while time.time() < t_end:
        s = st()
        b = s.get("boards", "")
        # never on the dungeon map itself (its controls bar has Select: A there walks the hero on, into a fight)
        onmap = s.get("menu") in ("Map", None, "")
        if kind in ("A", "B") and s.get("menu") in ("Activation", "Encounter", "Choice") and "nav:" in b and presses < 1:
            if kind == "B" and "nav:No" in b:
                probe("question")
                send("ptouch nav:No")
                presses += 1
            else:
                w = next((w for w in ("nav:Continue", "nav:OK", "nav:Confirm", "nav:Equip", "nav:Reserve", "nav:Store", "nav:Yes") if w in b), None)
                if w:
                    send("ptouch " + w)
                    presses += 1
                    time.sleep(1.5)
        elif kind == "touch":
            if not onmap and "nav:Continue" in b:
                probe("card")
                send("ptouch nav:Continue")
                time.sleep(1.5)
        elif kind == "chance" and s.get("chance", "-").startswith("running"):
            probe("cards")
            send("touch CardChoiceContainer/Card_CN r tap")   # a card picked by a fingertip (no button: it could reach the map)
        elif kind == "stop" and s.get("menu") in ("WheelMenu", "PendulumMenu") and "nav:Continue" in b:
            send("ptouch nav:Continue")   # the result's page (the wheel's also has Redraw - never pressed)
        elif kind == "stop" and (s.get("pendulum", "").find("swinging") >= 0 or "wheel" in s.get("mg", "")):
            probe("swinging")
            send("stop")
        elif kind == "dice":
            probe("dice")
            dice_step(s)
        elif kind == "story" and s.get("menu") == "Encounter":
            if re.search(r"\[\d+ (?!nav:)", b):
                probe("choices")
                send("ptouch opt")
        elif kind == "shop":
            m = re.findall(r"\[(\d+) ([^\]]*)\]", b)
            leave = next((i for i, t in m if t.lower().startswith("leave")), None)
            if leave:
                probe("shop")
                send("ptouch %s tap" % leave)
            elif s.get("menu") == "Shop":
                send("press B 6")
        time.sleep(1.5)


def prox_detail():
    send("prox list")
    with open(hof2env.GLOG, errors="replace") as f:
        lines = f.read().splitlines()
    for i in range(len(lines) - 1, -1, -1):
        if "proximity: " in lines[i] and "] proximity:" in lines[i]:
            return [l.strip() for l in lines[i + 1:i + 60] if l.startswith("  ")]
    return []


PREV = [None]   # the last event's kind (a Blessing or Curse left open is finished by touch only)


SEAT = [0.0]   # --seated: the simulated head and hands are this much lower than written
# --reach: a reach dump (`reach SW_<event>_<what>`, Diag/ReachProbe.cs) right before the first touch of each kind in each event;
# seated runs rate every touch target for a seated player (OK / LEAN / FAR / in the lap)
REACH = {"on": False, "event": "", "done": set(), "rows": []}


def probe(what):
    if not REACH["on"]:
        return
    key = (REACH["event"], what)
    if key in REACH["done"]:
        return
    REACH["done"].add(key)
    tag = "SW_%s_%s" % (REACH["event"].replace(" ", "_"), what)
    send("reach " + tag)
    time.sleep(0.3)
    try:
        with open(H + "/testdata/reach/%s.txt" % tag) as f:
            txt = f.read()
    except OSError:
        return
    m = re.search(r"seated summary \(touch targets now\): (.*)", txt)
    summary = m.group(1) if m else "(standing run)"
    REACH["rows"].append((tag, summary))
    log("      reach %-34s %s" % (tag, summary[:200]))


def posture_now():
    return send("posture")


def wait_posture(seated, timeout=10):
    t0 = time.time()
    r = posture_now()
    while time.time() - t0 < timeout:
        r = posture_now()
        if r.startswith("seated" if seated else "standing") and "busy False" in r:
            break
        time.sleep(0.4)
    return r


SEATED_AT = [None]   # the height the seated posture was chosen at


def seat_at(height):
    """A seated sweep's player of this height (2026-10-16, the seated round's item 3): the head there first, then Seated chosen
    (standing first if seated), as a player sits down and then touches Seated - the table is placed for the seated eyes at that
    moment. Until 2026-10-16 the posture was chosen once, at whatever height the simulator's head had before (a fresh
    session's 1.60 m -> seated 1.05 m), and the events ran at other heights: the 1.20 m events then measured a 1.05 m player
    (candidate 6's 'first frame' trap: the map's near row 0.39 m from the eyes), and the 1.05 / 1.35 m events of a full sweep
    a 1.20 m player leaning back or sitting up."""
    if SEATED_AT[0] == height:
        return
    sim(REST + "; head 0 %.2f 0 0 0 0" % height)
    time.sleep(0.5)
    if posture_now().startswith("seated"):
        send("posture standing")
        wait_posture(False)
    send("posture seated")
    r = wait_posture(True)
    SEATED_AT[0] = height
    log("      seated at eyes %.2f: %s" % (height - SEAT[0], r[:200]))


def hlabel(height):
    return ("h %.2f" % height) if not SEAT[0] else ("seated eyes %.2f" % (height - SEAT[0]))


def run_event(label, secs, kind, height, lean):
    REACH["event"] = "%s_%d%s" % (label, round((height - SEAT[0]) * 100), "_lean" if lean else "")
    head = "head 0 %.2f %.2f 0 %d 0" % (height, -0.15 if lean else 0.0, -35 if lean else -25)
    sim(REST + "; " + head)
    send("prox reset")
    r = send("scen here " + label.replace(" ", "_"))
    if r.startswith("busy"):
        # the last event left something open (its scenario still waits for the map): stopped, finished, tried again
        send("scen stop")
        map_ready(30, act="touch" if PREV[0] == "touch" else True)
        r = send("scen here " + label.replace(" ", "_"))
    PREV[0] = kind
    if not r.startswith("running"):
        log("SKIP %-18s %s%s: %s" % (label, hlabel(height), " lean" if lean else "", r))
        return None
    t_end = time.time() + secs
    if kind in ("coins", "hub") or kind is None:
        # the gains and losses (a card shown), the end coins, the challenge map: a dump once it is up
        time.sleep(6 if kind is None else 14)
        probe("shown")
    if kind:
        answer(kind, t_end)
    else:
        time.sleep(max(0.0, t_end - time.time()))
    # the event's whole course counts (the card flying to its place after the answer too); the last ones end the run
    # (a story can chain a choice, a gained card, another choice and its page: up to 90 s)
    back = map_ready(90 if kind == "story" else 40, act="touch" if kind == "touch" else True) if kind not in ("coins", "hub") else True
    s = st()
    p = s.get("proximity", "")
    m = re.search(r"violations (\d+) \(first frame (\d+)\) \| closest (.*)", p)
    viol, first, closest = (int(m.group(1)), int(m.group(2)), m.group(3)) if m else (-1, -1, p)
    # a row the player touches is placed for reach (listed, not counted) - but never closer than 0.20 m to the eyes, where it
    # cuts the near clip (the checklist's A4; 2026-10-14: the wheel's cards 0.12 m from seated eyes)
    mm = re.search(r"([\d.]+) m from the eyes", closest)
    if mm and float(mm.group(1)) < 0.20:
        viol = max(viol, 0) + 1
        closest = "TOO CLOSE (< 0.20 m, even for a touch row): " + closest
    detail = prox_detail()
    log("%s %-18s %s%s: violations %d (first frame %d) | closest %s" % ("FAIL" if viol else "ok  ", label, hlabel(height), " lean" if lean else "",
                                                                          viol, first, closest[:150]))
    # the violators first (all of them), then the nearest of the rest (2026-10-14: a seated end coins' 3 first-frame
    # violators were cut off behind three touch rows, and the game log that named them was gone with the next session)
    bad = [d for d in detail if not d.startswith("ok")]
    for d in (bad + [d for d in detail if d.startswith("ok")])[:max(3, len(bad))]:
        log("      " + d[:200])
    if not back:
        log("      (the map did not come back: %s)" % state())
    return viol, first, closest


def main():
    a = sys.argv[1:]
    open(LOG, "a").close()
    seated = "--seated" in a
    if seated:
        os.environ["HOF2VR_SEATED"] = "1"
        SEAT[0] = 0.55
    log("==== table sweep" + (" (seated: eyes 0.55 m lower than the heights written)" if seated else ""))
    if "--start" in a:
        subprocess.run(["python3", H + "/scripts/session.py", "start", "--snapshot", "magician_mid", "--to", "table"], capture_output=True, text=True, timeout=4200)
    heights = [float(x) for x in a[a.index("--heights") + 1].split(",")] if "--heights" in a else [1.60, 1.75, 1.90]
    only = a[a.index("--only") + 1].split(",") if "--only" in a else None
    lean = "--lean" in a
    REACH["on"] = "--reach" in a
    sim("seat %.2f" % SEAT[0])
    if seated:
        seat_at(heights[0])   # (a game started standing: placed again, seated - at the first height)
    send("cfg Scenarios true")
    send("gate auto 1.5")
    if not map_ready(30):
        # elsewhere (the challenge map, an open encounter): the dev save's fresh mid-run map (Scenarios "Fresh dev run")
        log("not on the dungeon map (%s): a fresh dev run" % state())
        sim(REST + "; head 0 1.75 0 0 0 0")   # forward: arriving recentres on the head's turn
        time.sleep(0.5)
        send("scen run Fresh_dev_run")
        if not map_ready(240):
            raise SystemExit("not on the dungeon map: " + state())
        SEATED_AT[0] = None   # (arriving at the table again with the head elsewhere: seated again at the first height)
    rows = []
    for h in heights:
        if seated:
            seat_at(h)
        for label, secs, kind in EVENTS:
            if only and label not in only:
                continue
            r = run_event(label, secs, kind, h, False)
            if r:
                rows.append((label, h, False) + r)
        if lean:
            for label, secs, kind in EVENTS[:6]:
                r = run_event(label, secs, kind, h, True)
                if r:
                    rows.append((label, h, True) + r)
    if not only or any(x[0] in only for x in LAST):
        if seated:
            seat_at(heights[len(heights) // 2])
        for label, secs, kind in LAST:
            r = run_event(label, secs, kind, heights[len(heights) // 2], False)
            if r:
                rows.append((label, heights[len(heights) // 2], False) + r)
    bad = [r for r in rows if r[3]]
    log("==== %d events%s, %d with violations: %s" % (len(rows), " (seated)" if seated else "", len(bad), ", ".join("%s@%.2f" % (r[0], r[1] - SEAT[0]) for r in bad) or "none"))
    if REACH["rows"]:
        log("==== reach (%s): %s" % ("seated" if seated else "standing", " | ".join("%s: %s" % (t, r[:60]) for t, r in REACH["rows"])))
    if DICE["pokes"] or DICE["unmarked"]:
        far = [(n, r) for n, r in DICE["pokes"] if seated and (not r.startswith(("OK", "LEAN")) or "LAP" in r.split()[0])]
        rated = ("%d OK, %d with a lean, %d out of reach or in the lap%s" % (
            sum(1 for _, r in DICE["pokes"] if r.startswith("OK") and "LAP" not in r.split()[0]),
            sum(1 for _, r in DICE["pokes"] if r.startswith("LEAN") and "LAP" not in r.split()[0]), len(far),
            (": " + ", ".join("%s %s" % (n, r[:40]) for n, r in far[:4])) if far else "")) if seated else "a standing run (not rated)"
        log("==== dice marked by a fingertip: %d marking phases, %d left unmarked; %d pokes, %s" % (
            DICE["marked"] + DICE["unmarked"], DICE["unmarked"], len(DICE["pokes"]), rated))
        if DICE["unmarked"]:
            bad.append(("Dice (a marking phase left unmarked)", 0, False, 1, 0, ""))
    if seated:
        sim("seat 0")
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()
