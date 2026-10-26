#!/usr/bin/env python3
"""The call to battle at the Dealer's table, repeated, seated or standing (env 1, 2026-10-19): the spawn rule at every one.

  cb_seated_test.py [--start] [--standing] [--reps N] [--deal] [--pics N]

From the dungeon map of a test session (--start: session.py start --snapshot magician_mid --to table; seated unless
--standing: the game seated, the simulated head 0.55 m lower and the seated eyes taken at 1.20 m, as b1_test --seated's
failing runs). Each repetition: `prox reset` (every renderer counts as seen for the first time again, so the watchdog's
first-frame rule covers each call to battle), the dev jump into a goblin ambush, and while the game deals: the head at rest,
then down 25 deg (b1's card shot), down 45, down-left 40, left 60 (the stat cards' pile), down-right 40 (the map's near row in view),
back to rest; the fight card touched once offered, the fight, `jump win`, back to the map. Checks per repetition:
  nothing_too_close - no PROXIMITY line from the jump to the fight's start;
  pile_clear        - the stat cards hidden at the call to battle (StatHand's hidden pile) lie at least 0.53 m from the
                      resting eyes and outside the body's 0.22 m (TableHud's status: the pile where the fan stands);
  table_known       - the table's top is known all through the call to battle (KeepOut's status: no 'top unknown').
--deal: then a fresh dev run's map (`scen reload mid`) dealt with the head looking down at the near row: no PROXIMITY line.
--pics N: both-eye pictures of the first N repetitions' looks (testdata/shots/PT_CB_*). Results: logs/cb_seated_test.log.
"""
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import b1_test as B  # noqa: E402
import playtest as pt  # noqa: E402

H = pt.H
pt.LOG = H + "/logs/cb_seated_test.log"
log, sim, send, st, result = pt.log, pt.sim, pt.send, pt.st, pt.result
EYE = "1.75"   # (the tour's standing eyes; with `seat 0.55` the seated 1.20 m)
LOOKS = [("rest", 0, 0), ("down25", 0, -25), ("down45", 0, -45), ("downleft", 35, -40), ("left60", 60, -35), ("downright", -35, -40), ("rest2", 0, 0)]


def look(yaw, pitch):
    sim("head 0 %s 0 %g %g 0" % (EYE, yaw, pitch))


def prox_lines(n0):
    lines, _ = pt.gamelog_since(n0)
    return [l.split("] ", 1)[-1] for l in lines if "PROXIMITY:" in l]


def pile_check(tag, seated):
    """The hidden pile (StatHand's hidden container) as it lies now: its cards' nearest point from the resting eyes."""
    send("state")
    hud = st().get("tableHud", "")
    m = re.search(r"hidden pile at ([-\d.]+) right, ([-\d.]+) ahead, ([-\d.]+) below the eyes, ([\d.]+) m", hud)
    if not m:
        result("cb_%s_pile_clear" % tag, False, "no hidden pile in the status: %s" % hud[-200:])
        return
    right, ahead, below, dist = (float(m.group(i)) for i in range(1, 5))
    body = (right * right + ahead * ahead) ** 0.5
    # (the pile's middle; a card reaches 0.06-0.10 m from it: its nearest corner kept >= 0.53 m by the middle's distance
    # less the card's half length - measured from the cards by the watchdog's first-frame rule above)
    ok = dist - 0.10 >= 0.53 and body - 0.10 >= 0.22
    result("cb_%s_pile_clear" % tag, ok, "the pile's middle %.2f m from the resting eyes (%.2f right, %.2f ahead, %.2f below; %.2f m from the body's axis) | %s"
           % (dist, right, ahead, below, body, hud.split("| hidden pile ", 1)[-1][:160]))


def boxes(key):
    """`bounds <key>`: the renderers' world boxes (path, min, max) from the log."""
    n0 = pt.nlines()
    send("bounds %s 60" % key)
    lines = []
    for _ in range(10):   # (the log is written a moment after the command answers)
        time.sleep(0.5)
        lines = pt.gamelog_since(n0)[0]
        if any("bounds %s 60 =>" % key in l for l in lines):
            break
    out = []
    for l in lines:
        m = re.match(r"\s+(\S.*?) active=True \| R min=\(([-\d.]+),([-\d.]+),([-\d.]+)\) max=\(([-\d.]+),([-\d.]+),([-\d.]+)\)", l)
        if m:
            out.append((m.group(1), [float(m.group(k)) for k in (2, 3, 4)], [float(m.group(k)) for k in (5, 6, 7)]))
    return out


def pile_vs_decks(tag):
    """The hidden pile's cards (lying where the fan stands) against the player's decks beside them ('Player Decks Left'): their
    boxes on the table must not overlap (cards lying through each other)."""
    pile = [b for b in boxes("OffscreenStatHand") if b[0].endswith("c_cardmodel")]
    decks = [b for b in boxes("Player Decks Left") if "OffscreenStatHand" not in b[0] and "c_cardmodel" in b[0]]
    hits = []
    for pn, pmin, pmax in pile:
        for dn, dmin, dmax in decks:
            if pmin[0] < dmax[0] and pmax[0] > dmin[0] and pmin[2] < dmax[2] and pmax[2] > dmin[2]:
                hits.append("%s over %s" % (pn.split("/")[-3], "/".join(dn.split("/")[-4:-1])))
    result("cb_%s_pile_beside_decks" % tag, bool(pile) and not hits, "%d pile cards, %d deck cards lying beside them%s" % (len(pile), len(decks), ("; overlapping: " + "; ".join(hits[:3])) if hits else ", none overlapping"))


def one(i, seated, pics):
    tag = "%s%d" % ("seated" if seated else "standing", i)
    send("prox reset")
    n0 = pt.nlines()
    if not B.into_level_in():
        result("cb_%s_level_in" % tag, False, pt.state())
        return
    for name, yaw, pitch in LOOKS:
        look(yaw, pitch)
        time.sleep(0.9)
        if i <= pics:
            send("shot PT_CB_%s_%s" % (tag, name))
            time.sleep(1.8)
    look(0, 0)
    if i == 1:
        pile_vs_decks(tag)
    n_list = pt.nlines()
    send("prox list")
    time.sleep(0.5)
    # the fight card offered, touched (straight into the fight once loaded)
    offered = None
    t1 = time.time()   # (the looks and their pictures take up to 20 s themselves)
    while time.time() - t1 < 20:
        c = B.fc()
        if c.startswith("offered"):
            offered = c
            break
        time.sleep(0.3)
    pile_check(tag, seated)
    if offered is None:
        result("cb_%s_offered" % tag, False, "the fight card was not offered: %s" % B.fc()[:120])
    else:
        time.sleep(1.2)
        if i == 1:
            send("kotrace Ambush 240")   # (the touch's frames: KeepOut's and the watchdog's view of the fight card, in the log)
        pt.poke_pose("fightcard pose 6 r", settle=0.5)
        B.wait_level_play(False, "cb_" + tag)
    prox = prox_lines(n0)
    result("cb_%s_nothing_too_close" % tag, not prox, "; ".join(p[:170] for p in prox[:4]) or "no proximity violation from the jump to the fight (%s)" % send("prox")[:150])
    # the map's near row lies on the table: the watchdog measures it so (no body distance: 'closest X m eyes, - m body')
    lines, _ = pt.gamelog_since(n_list)
    rows = [l.strip() for l in lines if re.match(r"\s+(ok|FIRST|BAD)\s+\(\d, \d\)/", l)]
    near = [r for r in rows if "(Level_In" in r]
    flying = [r for r in near if re.search(r"m eyes, [\d.]+ m body \(Level_In", r)]
    kept = [l.split("] ", 1)[-1] for l in pt.gamelog_since(n0)[0] if "table's collider is switched off" in l]
    result("cb_%s_table_known" % tag, bool(near) and not flying, "%d map cards seen in Level_In, %d of them measured as in the air%s | %s"
           % (len(near), len(flying), (": " + flying[0][:160]) if flying else "", kept[0][:120] if kept else "(the collider stayed on)"))
    B.win_and_back()


def deal():
    """A fresh dev run's map dealt with the head looking down at the near row."""
    send("prox reset")
    look(0, -40)
    n0 = pt.nlines()
    send("scen reload mid", 10)
    t0 = time.time()
    left = False
    while time.time() - t0 < 150:
        time.sleep(1)
        s = pt.state()
        if "GameState_Map" not in s:
            left = True
        elif left and pt.map_ready():
            break
    time.sleep(3)
    send("shot PT_CB_deal_down40")
    time.sleep(1.8)
    look(0, 0)
    prox = prox_lines(n0)
    result("cb_deal_nothing_too_close", left and not prox, ("; ".join(p[:170] for p in prox[:4]) or "no proximity violation in the deal (%s)" % send("prox")[:150]) +
           ("" if left else " | the reload never left the map"))


def main():
    a = sys.argv[1:]
    seated = "--standing" not in a
    reps = int(a[a.index("--reps") + 1]) if "--reps" in a else 10
    pics = int(a[a.index("--pics") + 1]) if "--pics" in a else 1
    open(pt.LOG, "a").close()
    log("==== call to battle x%d (%s)" % (reps, "seated" if seated else "standing"))
    if seated:
        os.environ["HOF2VR_SEATED"] = "1"
        pt.SEATED[0] = True
    if "--start" in a:
        subprocess.run(["python3", H + "/scripts/session.py", "start", "--snapshot", "magician_mid", "--to", "table"], capture_output=True, text=True, timeout=4200)
    sim("seat %s" % ("0.55" if seated else "0"))
    sim(pt.REST + "; head 0 %s 0 0 0 0" % EYE)
    time.sleep(0.5)
    log("posture: " + send("posture " + ("seated" if seated else "standing"))[:200])
    time.sleep(2)
    send("cfg Scenarios true")
    for i in range(1, reps + 1):
        try:
            one(i, seated, pics)
        except Exception as e:  # noqa: BLE001
            result("cb_%d" % i, False, "error: %s" % e)
    if "--deal" in a:
        try:
            deal()
        except Exception as e:  # noqa: BLE001
            result("cb_deal", False, "error: %s" % e)
    with open(pt.GAMELOG, errors="replace") as f:
        exc = sum(1 for l in f if "Exception" in l)
    result("cb_no_exceptions", exc == 0, "%d exception lines in the mod's log" % exc)
    sim("head 0 %s 0 0 0 0; seat 0" % EYE)
    ok = sum(1 for _, v in pt.RESULTS if v)
    log("==== %d/%d PASS" % (ok, len(pt.RESULTS)))
    sys.exit(0 if ok == len(pt.RESULTS) else 1)


if __name__ == "__main__":
    main()
