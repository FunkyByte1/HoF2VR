#!/usr/bin/env python3
"""The flat world's sounds in 3D (2026-10-13, VRAudio's world sounds: SoundEffectController.Play at the table, the side
table and the title).

  audio_check.py [--start] [--attract]

--attract: a session from the `magician` save, which arrives at the caravan's attract screen (the Dealer's attract magic,
the challenge cards); one watch there.

From the dungeon map of a test session: `audio watch` windows (VRAudio: every source that plays, 2D or 3D, with its mixer
group and path) during the table's everyday events (gains and losses: the cards, the stat cards' sounds), the dice, the
end coins (the challenge tokens' whoosh and unlock; they end the run), the caravan's attract screen and the challenge map
that follow (the Dealer's attract magic, the deck builder's cards), and the title. Check: no source that belongs to a thing
in the room (under Models/, TokenManager/, the 3D UI's cards) plays flat; music, stings, the Dealer's voice and the pooled
menu sounds may. Results: PASS/FAIL in logs/audio_check.log.
"""
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import playtest as pt  # noqa: E402

H = pt.H
pt.LOG = H + "/logs/audio_check.log"
log, sim, send, st, result = pt.log, pt.sim, pt.send, pt.st, pt.result
WORLD = ("Models/", "TokenManager/", "UIManager/UI Root (3D)/")
FLAT_OK = ("CardTable", "Music", "EncounterStings", "Stings", "Dealer", "Narration")


def watch(tag, secs, fn=None):
    n0 = pt.nlines()
    send("audio watch %d %s" % (secs, tag))
    t0 = time.time()
    if fn:
        fn()
    while time.time() - t0 < secs + 2:
        time.sleep(0.5)
    lines, _ = pt.gamelog_since(n0)
    block, out = False, []
    group = ""
    for l in lines:
        if ("audio watch '%s'" % tag) in l:
            block = True
            continue
        if not block:
            continue
        m = re.match(r"\s+group '([^']*)':", l)
        if m:
            group = m.group(1)
            continue
        m = re.match(r"\s+(2D|3D|mix) blend ([\d.]+)-([\d.]+) dist ([\d.]+)-([\d.]+) frames \d+ starts \d+ clip (.*?) \| (.*)", l)
        if m:
            out.append((group, m.group(1), m.group(6), m.group(7).strip()))
        elif out and not l.startswith(" "):
            break
    flat = [o for o in out if o[1] != "3D" and o[3].startswith(WORLD) and o[0] not in FLAT_OK]
    made = [l.split("] ", 1)[-1] for l in lines if "plays 3D at its object" in l]
    result("world_sounds_3d_%s" % tag, not flat, "%d sources played (%d flat, %d made 3D at their objects this window)%s" % (
        len(out), sum(1 for o in out if o[1] != "3D"), len(made), (": still flat: " + " / ".join("%s %s (%s)" % (o[0], o[3][-60:], o[2]) for o in flat[:4])) if flat else ""))
    for o in out:
        if o[1] != "3D":
            log("      flat: %-16s %-30s %s" % (o[0], o[2][:30], o[3][-90:]))
    for m in made[:12]:
        log("      made 3D: " + m[:160])
    return out


def table_events():
    for label in ("Gain_gold", "Lose_food", "Gain_life"):
        send("scen here " + label)
        time.sleep(6)
        s = st()
        if "nav:Continue" in s.get("boards", ""):
            send("ptouch nav:Continue")
        time.sleep(1.5)


def dice():
    send("scen here Dice")
    time.sleep(3)
    send("dice throw 0 0.5 2.0")
    time.sleep(5)


def coins():
    send("scen here End_coins")


def main():
    a = sys.argv[1:]
    open(pt.LOG, "a").close()
    log("==== audio check")
    if "--attract" in a:
        # the caravan's attract screen (a save at the start of a challenge): the Dealer's attract magic, the challenge cards
        subprocess.run(["python3", H + "/scripts/session.py", "start", "--snapshot", "magician", "--to", "table"], capture_output=True, text=True, timeout=4200)
        sim("seat 0; " + pt.REST + "; head 0 1.75 0 0 -10 0")
        log("at %s" % pt.state())
        watch("attract", 45)
        ok = sum(1 for _, v in pt.RESULTS if v)
        log("==== %d/%d PASS" % (ok, len(pt.RESULTS)))
        sys.exit(0 if ok == len(pt.RESULTS) else 1)
    if "--start" in a:
        subprocess.run(["python3", H + "/scripts/session.py", "start", "--snapshot", "magician_mid", "--to", "table"], capture_output=True, text=True, timeout=4200)
    sim("seat 0; " + pt.REST + "; head 0 1.75 0 0 -10 0")
    send("cfg Scenarios true")
    send("gate auto 1.5")
    log("world sounds: " + send("worldsound"))
    watch("table", 28, table_events)
    # the dice game, then the end coins (they end the run: the caravan's attract and the challenge map follow)
    import table_sweep as ts
    watch("dice", 14, dice)
    ts.answer("dice", time.time() + 12)
    ts.map_ready(40, act=True)
    watch("coins", 40, coins)
    watch("after", 40)   # the challenge's end, the caravan, the challenge map
    log("world sounds: " + send("worldsound"))
    ok = sum(1 for _, v in pt.RESULTS if v)
    log("==== %d/%d PASS" % (ok, len(pt.RESULTS)))
    sys.exit(0 if ok == len(pt.RESULTS) else 1)


if __name__ == "__main__":
    main()
