#!/usr/bin/env python3
"""The wheel (Games/Wheel) seen standing and seated (2026-10-14: the seated sweep listed a wheel card's highlight 0.12 m from
seated eyes; standing 0.23 m): shots while it spins and the closest the cards come, per posture.

  wheel_look.py [--start] [--no-hub | --hub-only]

--hub-only skips the wheel (the end coins then the side table: the challenge map and the deck builder, seated).

From the dungeon map (session.py start --snapshot magician_mid --to table with --start). Per posture (standing at 1.75 m,
seated at 1.20 m): `scen here Wheel`, the proximity watchdog reset, 2.5 s of spinning, shots WL_<posture>_ahead / _down, the
reach dump WL_<posture>, the closest the wheel's cards came (status proximity), then the wheel stopped and finished.
"""
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import playtest as pt  # noqa: E402
import table_sweep as ts  # noqa: E402

H = pt.H
pt.LOG = H + "/logs/wheel_look.log"
log, sim, send, st = pt.log, pt.sim, pt.send, pt.st


def look(posture, seat):
    sim("seat %.2f; %s; head 0 1.75 0 0 -10 0" % (seat, pt.REST))
    send("posture " + posture)
    time.sleep(2.5)
    ts.map_ready(40, act=True)
    send("prox reset")
    r = send("scen here Wheel")
    time.sleep(2.5)
    sim("head 0 1.75 0 0 0 0")
    time.sleep(0.4)
    pt.shot("WL_%s_ahead" % posture)
    sim("head 0 1.75 0 0 -20 0")
    time.sleep(0.4)
    pt.shot("WL_%s_down" % posture)
    send("reach WL_%s" % posture)
    time.sleep(2.0)
    send("state")
    p = st().get("proximity", "")
    log("%-8s %s | %s" % (posture, r[:40], p[:220]))
    # (the bug hunt of 2026-10-18, finding 5: the wheel's cards sweep no nearer than the spawn rule's 0.53 m - WheelSeat keeps
    # their circle out; the watchdog counts them since)
    ws = st().get("wheelSeat", "")
    m = re.search(r"violations (\d+)", p)
    mc = re.search(r"closest ([\d.]+) m", ws)
    mo = re.search(r"out ([\d.]+) m", ws)
    # (and clear of what it must miss: the Dealer's bones DealerGap and the figurine FigurineGap from the ring's disc)
    md = re.search(r"(-?[\d.]+) m beyond the gap from", ws)
    pt.result("wheel_%s_kept_out" % posture, m is not None and int(m.group(1)) == 0 and mc is not None and float(mc.group(1)) >= 0.53 and mo is not None and float(mo.group(1)) < 0.6
              and md is not None and float(md.group(1)) >= 0,
              "wheelSeat: %s | watchdog: %s" % (ws[:520], p[:170]))
    ts.answer("stop", time.time() + 14)
    ts.map_ready(40, act=True)
    # (the stop, the chosen card shown, the rest discarded, the carousel back in its place: nothing brought up near the eyes)
    send("state")
    p2 = st().get("proximity", "")
    m2 = re.search(r"violations (\d+)", p2)
    pt.result("wheel_%s_end_kept_out" % posture, m2 is not None and int(m2.group(1)) == 0, "after the stop: %s | wheelSeat: %s" % (p2[:200], st().get("wheelSeat", "")[:80]))


def hub(posture, seat):
    """The side table seated: the challenge map's pins and plates, then the deck builder (a challenge picked by touch, its
    Begin / Continue) - reach dumps and shots."""
    sim("seat %.2f; %s; head 0 1.75 0 0 -20 0" % (seat, pt.REST))
    send("posture " + posture)
    time.sleep(2.0)
    # the run ends first (the end coins), as in the sweep; then the challenge map at the side table
    send("scen here End_coins")
    t0 = time.time()
    while time.time() - t0 < 45:
        time.sleep(1.5)
        if "ChallengeMap" in pt.state():
            break
        b = st().get("boards", "")
        for k in ("nav:Continue", "nav:Open"):   # (the end coins: Open the token, then Continue)
            if k in b:
                send("ptouch " + k)
                break
    send("scen here Challenge_map")
    for _ in range(70):
        time.sleep(1.0)
        if "ChallengeMap" in pt.state() and st().get("place", "").startswith("Hub"):
            break
    time.sleep(3.0)
    pt.shot("WL_%s_hub" % posture)
    send("reach WL_%s_hub" % posture)
    log("hub: %s | %s | boards %s" % (pt.state(), st().get("place", ""), st().get("boards", "")[:200]))
    # a challenge by touch, then on to its deck builder
    for step in range(12):
        s = st()
        menu = s.get("menu") or ""
        b = s.get("boards", "")
        if "DeckBuilder" in menu or "Deckbuilder" in menu:
            break
        labels = re.findall(r"\[(\d+) ([^\]]*)\]", b)
        nav = next((l for i, l in labels if l.startswith("nav:") and any(k in l for k in ("Begin", "Start", "Continue", "Select", "Confirm", "Play", "Open"))), None)
        opt = next((i for i, l in labels if not l.startswith("nav:")), None)
        if nav and step > 0:
            send("ptouch " + nav)
        elif opt:
            send("ptouch %s tap" % opt)
        else:
            send("press A 6")
        log("      hub step %d: menu %s -> %s" % (step, menu, (nav or opt or "A")))
        time.sleep(2.5)
    time.sleep(2.0)
    pt.shot("WL_%s_deckbuilder" % posture)
    send("reach WL_%s_deckbuilder" % posture)
    log("deck builder: %s | menu %s | boards %s" % (pt.state(), st().get("menu"), st().get("boards", "")[:200]))


def main():
    a = sys.argv[1:]
    open(pt.LOG, "a").close()
    log("==== wheel look")
    if "--start" in a:
        subprocess.run(["python3", H + "/scripts/session.py", "start", "--snapshot", "magician_mid", "--to", "table"], capture_output=True, text=True, timeout=4200)
    send("cfg Scenarios true")
    send("gate auto 1.5")
    if "--hub-only" not in a:
        look("standing", 0.0)
        look("seated", 0.55)
    if "--no-hub" not in a:
        hub("seated", 0.55)
    sim("seat 0")
    send("posture standing")
    ok = sum(1 for _, v in pt.RESULTS if v)
    log("==== %d/%d PASS" % (ok, len(pt.RESULTS)))
    sys.exit(0 if ok == len(pt.RESULTS) else 1)


if __name__ == "__main__":
    main()
