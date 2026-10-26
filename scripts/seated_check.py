#!/usr/bin/env python3
"""Seated mode (2026-10-13; VRRig's posture, Places.PostureChanged): the posture switch and the eye height per place.

  seated_check.py [--start] [--no-fight]

From the dungeon map of a STANDING test session (session.py start --snapshot magician_mid --to table; --start does it):
 1. standing at the table: the eyes at the head's real height (no lift) - a standing player sees nothing new;
 2. the hand check's first page: Standing / Seated by a real-hand poke; Seated -> the player is placed again in a short
    dark, the check stays open and its board comes along; the table's top then sits SeatedTableHeight (0.75 m) above the
    real floor: with the head lowered to a seated 1.20 m the table top is 0.45 m below the eyes;
 3. the VR settings page's Comfort tab: Posture and the seated table's - / + (2.5 cm, a blink), the page stays open;
 3b. (2026-10-16) the seated view at the tables: Comfort, At the table = Lifted -> Until You Fall's seated lift (0.4572 m)
    at the card table too (the table top 0.73 m below seated 1.20 m eyes; the reach dump's seated summary logged), Real
    table (the default) -> the table top 0.45 m below them again;
 3c. (2026-10-16) a shorter and a taller seated player (eyes 1.05 / 1.35 m): the tables 0.15 m lower / higher (the table
    top 0.45 m below the eyes for both, nothing within the keep-out from the first frame on);
 4. (unless --no-fight) a fight seated: the waiting space and the arena lifted by SeatedLift (0.4572 m); back at the table
    the table's lift again;
 5. the settings file is never written (a test run).
Results: PASS/FAIL in logs/seated_check.log; shots testdata/shots/SC_*.
"""
import hashlib
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import playtest as pt  # noqa: E402  (its helpers: sim, send, st, poke_pose, wait_vrset, poke_vrset, wait_boards, shot...)

H = pt.H
pt.LOG = H + "/logs/seated_check.log"
log, sim, send, st, result = pt.log, pt.sim, pt.send, pt.st, pt.result
REST = pt.REST


def posture():
    """(seated?, the neutral eye height, the table height, the lift, the seam's text, the live head height) from `posture`."""
    r = send("posture")
    m = re.search(r"eye ([\d.]+) head (-?[\d.]+) \| table ([\d.]+)", r)
    send("state")
    lift = re.search(r"lift (-?[\d.]+)", st().get("posture", ""))
    return (r.startswith("seated"), float(m.group(1)) if m else -1.0, float(m.group(3)) if m else -1.0, float(lift.group(1)) if lift else 0.0, r,
            float(m.group(2)) if m else -1.0)


def wait_posture(seated, timeout=8):
    t0 = time.time()
    while time.time() - t0 < timeout:
        p = posture()
        if p[0] == seated and "busy False" in p[4]:
            return p
        time.sleep(0.4)
    return posture()


def surface():
    """The card table's top above the room's floor in real metres (the reach probe's table line)."""
    send("reach SC_surface")
    time.sleep(0.5)
    try:
        with open(H + "/testdata/reach/SC_surface.txt") as f:
            m = re.search(r"top ([\d.]+) m above the floor", f.read())
        return float(m.group(1)) if m else -1.0
    except OSError:
        return -1.0


def file_sha1(p):
    try:
        with open(p, "rb") as f:
            return hashlib.sha1(f.read()).hexdigest()[:12]
    except OSError:
        return "-"


def main():
    a = sys.argv[1:]
    open(pt.LOG, "a").close()
    log("==== seated check")
    if "--start" in a:
        os.environ.pop("HOF2VR_SEATED", None)
        subprocess.run(["python3", H + "/scripts/session.py", "start", "--snapshot", "magician_mid", "--to", "table"], capture_output=True, text=True, timeout=4200)
    sim("seat 0; " + REST + "; head 0 1.75 0 0 0 0")
    send("posture standing")
    time.sleep(2)
    cfg0 = file_sha1(pt.GAMECFG)
    # 1. standing: no lift - the live head 1.75 m above the floor
    p = wait_posture(False)
    result("standing_no_lift", not p[0] and abs(p[5] - 1.75) < 0.02 and abs(p[3]) < 1e-3, "standing at the table, head 1.75 m: %s" % p[4][:160])
    top = surface()
    log("      the card table's top: %.3f m above the room's floor (real metres)" % top)
    # 2. the hand check's first page: Seated by a real-hand poke
    send("handcheck start")
    time.sleep(1.5)
    hc = st().get("handcheck", "")
    result("handcheck_posture_buttons", "17 buttons" in hc and hc.startswith("Fit"), hc[:200])
    pt.shot("SC_fit_standing")
    # the player sits down, then touches Seated (the hand check's board stays where it was put: now above the seated eyes)
    sim("seat 0.55; head 0 1.75 0 0 0 0")
    time.sleep(1.0)
    pt.poke_pose("handcheck pose 17 6", settle=2.5)   # Seated
    p = wait_posture(True)
    hc = st().get("handcheck", "")
    want = 1.20 + (top - 0.75)
    gap = p[5] - top
    result("handcheck_seated", p[0] and abs(p[1] - want) < 0.03 and hc.startswith("Fit"),
           "Seated touched (seated head 1.20 m): eye %.3f m above the room's floor (want %.3f = 1.20 + the table top %.3f - 0.75); the check %s" % (p[1], want, top, hc.split(" |")[0]))
    result("seated_table_gap", abs(gap - 0.45) < 0.03, "seated head 1.20 m: the live eyes %.3f m above the room's floor, the table top %.3f m below them" % (p[5], gap))
    sim("head 0 1.75 0 0 -30 0")
    time.sleep(0.6)
    pt.shot("SC_fit_seated")
    sim("head 0 1.75 0 0 0 0")
    # the board came along: its buttons within a seated reach
    r = send("handcheck pose 16 6")
    pos = r.split("|")[0].strip()
    rr = send("seatreach " + pos) if re.match(r"-?[\d.]+ -?[\d.]+ -?[\d.]+", pos) else r
    result("handcheck_board_in_seated_reach", rr.startswith(("OK", "LEAN")) and "LAP" not in rr.split()[0], "the Standing button after the switch: %s" % rr[:140])
    pt.poke_pose("handcheck pose 16 6", settle=2.5)   # Standing
    p = wait_posture(False)
    result("handcheck_standing_again", not p[0] and abs(p[3]) < 1e-3 and abs(p[5] - 1.20) < 0.02 and st().get("handcheck", "").startswith("Fit"), p[4][:160])
    send("handcheck close")
    sim("seat 0; head 0 1.75 0 0 0 0")
    time.sleep(1)
    # 3. the VR settings page: Posture, the seated table's - / +
    send("press START 6")
    b = pt.wait_boards(lambda b: "menu board pause" in b, 10)
    ok_page = False
    if b and pt.wait_vrset(lambda v: v.startswith("entry"), 6):
        pt.poke_vrset("VR_Settings")
        if pt.wait_vrset(lambda v: v.startswith("page"), 6):
            pt.poke_vrset("Comfort")
            ok_page = pt.wait_vrset(lambda v: v.startswith("page Comfort"), 6) is not None
    result("vrsettings_comfort_open", ok_page, pt.vrset_now()[:160])
    pt.shot("SC_comfort_tab")
    pt.poke_vrset("Posture/Seated")
    p1 = wait_posture(True)
    v = pt.vrset_now()
    result("vrsettings_seated", p1[0] and abs(p1[3] - (top - 0.75)) < 0.005 and v.startswith("page Comfort"), "after Posture / Seated: %s | the page: %s" % (p1[4][:120], v[:60]))
    pt.poke_vrset("Seated_table/+")
    time.sleep(1.5)
    p2 = posture()
    result("vrsettings_table_up", abs(p2[2] - (p1[2] + 0.025)) < 1e-3 and abs((p1[5] - p2[5]) - 0.025) < 0.005,
           "Seated table +: the table %.3f -> %.3f m, the live eyes %.3f -> %.3f m above the room's floor (the table comes up toward them)" % (p1[2], p2[2], p1[5], p2[5]))
    pt.poke_vrset("Seated_table/-")
    time.sleep(1.5)
    p3 = posture()
    result("vrsettings_table_back", abs(p3[2] - p1[2]) < 1e-3, "Seated table -: %.3f m" % p3[2])
    pt.poke_vrset("Posture/Standing")
    p4 = wait_posture(False)
    result("vrsettings_standing", not p4[0] and abs(p4[3]) < 1e-3 and pt.vrset_now().startswith("page Comfort"), p4[4][:120])
    pt.poke_vrset("Back")
    time.sleep(1)
    send("press START 6")
    time.sleep(2)
    # 3b. the seated view at the tables (2026-10-16, the seated round's item 1): Comfort, At the table = Lifted puts Until You
    # Fall's seated lift (SeatedLift 0.4572 m above the real seated head) at the card table too; Real table (the default) puts
    # the table's top back at SeatedTableHeight. Seated first at 1.20 m (the place taken again there), so the reach dump's
    # seated summary is a 1.20 m seated player's
    sim("seat 0.55; head 0 1.75 0 0 0 0")
    time.sleep(0.5)
    send("posture seated")
    p0 = wait_posture(True)
    result("table_view_default_real", "view Table" in p0[4] and abs((p0[5] - top) - 0.45) < 0.03,
           "seated at 1.20 m, At the table = Real table (the default): the table top %.3f m below the eyes (want 0.45) | %s" % (p0[5] - top, p0[4][:120]))
    send("press START 6")
    b = pt.wait_boards(lambda b: "menu board pause" in b, 10)
    ok_page = False
    if b and pt.wait_vrset(lambda v: v.startswith("entry"), 6):
        pt.poke_vrset("VR_Settings")
        if pt.wait_vrset(lambda v: v.startswith("page"), 6):
            pt.poke_vrset("Comfort")
            ok_page = pt.wait_vrset(lambda v: v.startswith("page Comfort"), 6) is not None
    pt.poke_vrset("At_the_table/Lifted")
    time.sleep(1.5)
    pl = wait_posture(True)
    result("table_view_lifted", ok_page and "view Lifted" in pl[4] and abs(pl[3] - 0.4572) < 1e-3 and abs(pl[5] - (1.20 + 0.4572)) < 0.03 and
           pt.vrset_now().startswith("page Comfort"),
           "At the table: Lifted -> the lift %.4f (want SeatedLift 0.4572), the eyes %.3f m above the room's floor (want 1.657), the table top %.3f m below them | %s" % (
               pl[3], pl[5], pl[5] - top, pl[4][:110]))
    pt.poke_vrset("At_the_table/Real_table")
    time.sleep(1.5)
    pr = wait_posture(True)
    result("table_view_real_again", "view Table" in pr[4] and abs((pr[5] - top) - 0.45) < 0.03,
           "At the table: Real table -> the table top %.3f m below the eyes again (want 0.45) | %s" % (pr[5] - top, pr[4][:110]))
    pt.poke_vrset("Back")
    time.sleep(1)
    send("press START 6")
    time.sleep(2)
    # what the Lifted view means for a seated player's reach at the table (the page closed: the map's next places and the
    # figurine are the touch targets), next to the Real table's
    for view in ("lifted", "table"):
        send("posture view " + view)
        time.sleep(1.5)
        wait_posture(True)
        send("reach SC_view_" + view)
        time.sleep(0.6)
        try:
            with open(H + "/testdata/reach/SC_view_%s.txt" % view) as f:
                m = re.search(r"seated summary \(touch targets now\): (OK \d+, LEAN \d+, FAR \d+, in the lap \d+)", f.read())
            log("      seated reach at the table, At the table = %s: %s" % ("Lifted" if view == "lifted" else "Real table", m.group(1) if m else "no summary"))
        except OSError:
            log("      no reach dump SC_view_" + view)
        if view == "lifted":
            sim("head 0 1.75 0 0 -30 0")
            time.sleep(0.6)
            pt.shot("SC_table_lifted")
            sim("head 0 1.75 0 0 0 0")
    send("posture standing")
    sim("seat 0; head 0 1.75 0 0 0 0")
    wait_posture(False)
    # 3c. (2026-10-16, the seated round's item 3) a shorter and a taller seated player: seated eyes 1.05 m -> the tables 0.15 m
    # lower (the card table's top 0.60 m above the real floor, 0.45 m below the eyes; it was 0.30 m, the map's near row 0.39 m
    # from the eyes), nothing too close from the first frame on (the proximity watchdog); 1.35 m -> 0.15 m higher (0.90 m;
    # it was 0.60 m below the eyes, the figurine and the near row in the lap)
    for seat, eyes, want_gap in ((0.70, 1.05, 0.45), (0.40, 1.35, 0.45)):
        sim("seat %.2f; head 0 1.75 0 0 0 0" % seat)
        time.sleep(0.5)
        send("posture seated")
        ps = wait_posture(True)
        send("prox reset")
        time.sleep(2.0)
        send("state")
        prox = st().get("proximity", "")
        pm = re.search(r"violations (\d+) \(first frame (\d+)\)", prox)
        result("seated_table_for_eyes_%d" % round(eyes * 100),
               ps[0] and abs((ps[5] - top) - want_gap) < 0.03 and bool(pm) and pm.group(1) == "0",
               "seated eyes %.2f m: the table top %.3f m below the eyes (want %.2f), %s | %s" % (eyes, ps[5] - top, want_gap, prox[:110], ps[4][:150]))
        send("posture standing")
        wait_posture(False)
    sim("seat 0; head 0 1.75 0 0 0 0")
    time.sleep(0.5)
    # 4. a fight seated: the waiting space and the arena lifted by SeatedLift
    if "--no-fight" not in a:
        sim("seat 0.55; head 0 1.75 0 0 0 0")
        time.sleep(0.5)
        send("posture seated")
        wait_posture(True)
        send("gate auto 0")
        # (item 30, 2026-10-18: a card touched once its fight has loaded goes straight in - the simulator loads within the
        # card's glide; this fight counts as loaded only 20 s into its hold, so the touch comes before: the waiting space)
        send("fightcard slowload 20")
        n0 = pt.nlines()
        send("jump fight EL_Ambush_Goblins")
        eye_space = None
        for _ in range(40):
            time.sleep(1)
            send("state")
            s = st()
            if s.get("fightCard", "").startswith("offered"):
                send("gate go")   # B1: the encounter card at the table (touched by the B1 test; here: go)
            lm = re.search(r"eye ([\d.]+)", s.get("limbo", ""))
            if lm:
                eye_space = float(lm.group(1))
                break
            pt.answer_page()
        send("fightcard slowload 0")
        result("seated_waiting_space_lift", eye_space is not None and abs(eye_space - (1.20 + 0.4572)) < 0.03,
               "the waiting space's eyes %s (want 1.20 + 0.4572)" % eye_space)
        send("gate go")
        for _ in range(30):
            time.sleep(1)
            if "Level_Play" in pt.state():
                break
            send("gate go")
            if "Showcase" in (st().get("menu") or ""):
                sim("btn r a 150")   # the enemies' description: A (B1's tests touch its plate)
        p = posture()
        result("seated_fight_lift", abs(p[3] - 0.4572) < 1e-3 and abs(p[5] - (1.20 + 0.4572)) < 0.03,
               "in the fight (%s): %s" % (pt.state(), p[4][:160]))
        sim("head 0 1.75 0 0 -10 0")
        time.sleep(0.5)
        pt.shot("SC_fight_seated")
        send("jump win")
        pt.wait_state(["GameState_Map"], 90)
        time.sleep(4)
        p = posture()
        result("seated_table_after_fight", p[0] and abs((p[5] - top) - 0.45) < 0.03, "back at the table: %s" % p[4][:160])
        send("posture standing")
        sim("seat 0; head 0 1.75 0 0 0 0")
        wait_posture(False)
    cfg1 = file_sha1(pt.GAMECFG)
    result("settings_file_untouched", cfg0 == cfg1, "the player's settings file sha1 %s -> %s (a test run never writes it)" % (cfg0, cfg1))
    ok = sum(1 for _, v in pt.RESULTS if v)
    log("==== %d/%d PASS" % (ok, len(pt.RESULTS)))
    sys.exit(0 if ok == len(pt.RESULTS) else 1)


if __name__ == "__main__":
    main()
