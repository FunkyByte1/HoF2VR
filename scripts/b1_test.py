#!/usr/bin/env python3
"""B1 end to end (2026-10-13; src/HoF2VR/Game/FightCard.cs): into a fight by touching the glowing encounter card at the table.

  b1_test.py [--start] [--seated] [--parts touch,button,off]

From the dungeon map of a test session (--start: session.py start --snapshot magician_mid --to table; --seated: the game seated
and the simulated head 0.55 m lower, as playtest.py --seated). Each part jumps a goblin ambush (the dev jump) and checks:
  touch  - the player stays at the Dealer's table through his call to battle (the place is the card table while the game is
           in Level_In; no waiting space yet); the arena is held asleep (nothing of the fight wakes); the encounter card is
           offered within reach (standing reach; with --seated, a seated player's reach); a real-hand poke on it -> touched;
           then the campfire waiting space with the arena still asleep and the game's music ducked; A -> the fight, no frame
           >= 40 ms while the campfire was in view, the arena's activation behind the mist; nothing too close at the table.
           Item 30 (2026-10-18, the user: once the card says ready, touching it skips the waiting space): the simulator
           loads within the card's glide, so this touch comes once the fight has loaded - straight into the fight: no
           waiting space, the arena activates in the place change's dark, the fight waits in Level_In until the reveal,
           the recentre in the dark, the listener's move, no campfire duck, the progress lines, the weapon in hand by the
           reveal, no frame >= 40 ms in view, nothing too close.
  button - the same with A at the table instead of a touch (the fallback, as everywhere).
  early, early_button - the touch / A before the fight has loaded (`fightcard slowload 20`: the fight counts as loaded 20 s
           after its arena hold began): the campfire waiting space as before, its gate once loaded, A / Continue -> the fight.
  after  - straight in again right after a fight entered through the campfire.
  loss   - straight in, the hero dies, a fresh dev run's map (`scen reload mid`), straight in again.
  off    - [Comfort] TouchToFight false (this run only): the campfire right away, as before.
--shots: both-eye pictures of the direct parts' way in (`stateshot` B1S_<tag>: the touch, the way out of the table, the
state changes; `relshot`: the reveal at 0 / 0.3 / 0.6 / 1.2 / 2.4 s).
Shots: testdata/shots/B1_*. Results: PASS/FAIL in logs/b1_test.log.
"""
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import playtest as pt  # noqa: E402

H = pt.H
pt.LOG = H + "/logs/b1_test.log"
log, sim, send, st, result, shot = pt.log, pt.sim, pt.send, pt.st, pt.result, pt.shot
REST = pt.REST


def fc():
    send("state")
    return st().get("fightCard", "")


def win_and_back():
    send("jump win")
    pt.wait_state(["GameState_Map"], 90)
    time.sleep(3)
    for _ in range(10):
        if pt.map_ready():
            return True
        pt.answer_page()
        time.sleep(2)
    return pt.map_ready()


def into_level_in():
    """The dev jump into a goblin ambush; answers the fight's page at the table by touch; returns once in Level_In."""
    pt.sim(REST + "; head 0 1.75 0 0 0 0")
    # an encounter left open (a story's pages, an equip question): finished by touch as a player would (the sweep's way)
    import table_sweep as ts
    send("gate auto 1")
    if not ts.map_ready(90, act=True):
        log("      the map is not ready: %s / menu %s" % (pt.state(), st().get("menu")))
    send("gate auto 0")
    r = send("jump fight EL_Ambush_Goblins")
    for _ in range(20):
        time.sleep(1.5)
        s = pt.state()
        if "Level_In" in s or "Level_Play" in s:
            return True
        pt.answer_page()
    log("      the jump did not reach Level_In: %s / %s" % (r, pt.state()))
    return False


def stalls_now():
    send("state")
    m = re.search(r"stalls >= 40 ms (\d+)", st().get("fightflow", ""))
    return int(m.group(1)) if m else 0


SLOW = 20.0   # (the test's touch comes 7-9 s after the card: its shots, the reach check)
SHOTS = [False]
DARK = []   # (part, the whole dark of the way in in s, of it after the activation in s)


def wait_level_play(by_button, tag, timeout=45):
    """Into Level_Play: the enemies' description (the game's showcase), if any, closed by its Continue plate (A with --button)."""
    show_touched, show_seen = None, False
    t0 = time.time()
    while time.time() - t0 < timeout:
        time.sleep(1)
        if "Level_Play" in pt.state():
            break
        send("state")
        s = st()
        if "Showcase" in (s.get("menu") or ""):
            show_seen = True
            if by_button:
                sim("btn r a 150")
            elif s.get("goPlates", "").split("|")[-1].strip().startswith("showcase shown"):
                time.sleep(0.6)
                shot("B1_%s_showcase_continue" % tag)
                pt.poke_pose("goplate show pose 6", settle=0.5)
                show_touched = True
    return show_seen, show_touched


def clock(line):
    """The mod's own clock (Diag.Clock, 'HH:MM:SS.fff') in a log line -> seconds, or None."""
    m = re.search(r"(\d\d):(\d\d):(\d\d\.\d+)", line)
    return int(m.group(1)) * 3600 + int(m.group(2)) * 60 + float(m.group(3)) if m else None


def check_direct(tag, lines, by_button):
    """Item 30's way straight in, from the mod's log since the touch."""
    body = [l.split("] ", 1)[-1] for l in lines]
    def first(sub, after=0):
        for i in range(after, len(body)):
            if sub in body[i]:
                return i
        return -1
    i_touch = first("fight card: touched (")
    i_direct = first("fight flow: straight into the fight (")
    i_act = first("the arena activates now, in the dark")
    i_rel = first("fade release 'place'", max(i_act, 0))
    i_rev = first("fight flow: straight in - the fight is revealed")
    i_wait = first("gate: the fight waits in Level_In")
    i_play = first("game state -> GameState_Level_Play")
    space = [b for b in body if "limbo: waiting space on" in b]
    touched = body[i_touch] if i_touch >= 0 else "no touch logged"
    result("b1_%s_straight_in" % tag, "straight into the fight (it has loaded)" in touched and i_direct > i_touch and not space and "Level_Play" in pt.state(),
           "%s | %s | waiting space: %s | %s" % (touched[:110], body[i_direct][:90] if i_direct >= 0 else "no straight-in line", space[0][:60] if space else "never", pt.state()))
    hold = re.search(r"after ([\d.]+) s", body[i_rel]) if i_rel >= 0 else None
    rev = re.search(r"\(([\d.]+) s after", body[i_rev]) if i_rev >= 0 else None
    t_act, t_rel = (clock(body[i_act]) if i_act >= 0 else None), None
    m_rel = re.search(r"at (\d\d:\d\d:\d\d\.\d+)", body[i_rel]) if i_rel >= 0 else None
    if m_rel:
        t_rel = clock(m_rel.group(1))
    act_to_rel = (t_rel - t_act) if t_act is not None and t_rel is not None else -1
    result("b1_%s_arena_in_the_dark" % tag, i_act > i_direct >= 0 and i_rel > i_act and i_rev >= 0,
           "activation in the dark: %s | the dark (the place hold) %s s, of it %.1f s after the activation | revealed %s s after the flare" % (
               body[i_act][:100] if i_act >= 0 else "none", hold.group(1) if hold else "?", act_to_rel, rev.group(1) if rev else "?"))
    # (the fight's start - its music, input, Level_Play - waits for the reveal: WaitGate.HoldFight holds Level_In's last wait
    # while the place is dark; the game usually reaches that wait only after the reveal, so the hold's own lines are rare)
    result("b1_%s_fight_starts_after_reveal" % tag, 0 <= i_rel < i_play,
           "the reveal at line %d, Level_Play at line %d%s" % (i_rel, i_play, " | " + body[i_wait][:70] if i_wait >= 0 else " (the fight hold was not needed)"))
    rec = [b for b in body[max(i_touch, 0):] if b.startswith("recenter:")]
    snap = [b for b in body[max(i_touch, 0):] if "fade-snap" in b and "combat start" in b]
    result("b1_%s_recenter_in_dark" % tag, bool(rec) and not snap, "%s%s" % (rec[0][:140] if rec else "no recentre", " | " + snap[0][:60] if snap else ""))
    listener = first("the audio listener goes into the tunnel")
    duck = [b for b in body if "fade out at the campfire" in b]
    send("state")
    ff = st().get("fightflow", "")
    result("b1_%s_audio" % tag, listener > i_touch and not duck and "ducked False" in ff,
           "listener: %s | campfire duck: %s | %s" % ("into the tunnel after the touch" if listener > i_touch else "not moved", duck[0][:60] if duck else "none",
                                                      re.search(r"ducked \w+", ff).group(0) if "ducked" in ff else ff[:60]))
    pb = [b for b in body if b.startswith("progress: fight_in")]
    result("b1_%s_progress_lines" % tag, any(" begins " in b for b in pb) and any(" done after " in b for b in pb) and not any("aborted" in b for b in pb),
           " / ".join(b[:120] for b in pb[:3]) or "no progress lines")
    held = [b for b in body[:i_rel if i_rel >= 0 else len(body)] if b.startswith("CombatVR: holding")]
    cb = st().get("combat", "")
    mh = re.search(r"held (\d+)", cb)
    result("b1_%s_weapon_at_reveal" % tag, bool(held) or (mh is not None and int(mh.group(1)) > 0 and i_rel >= 0),
           "%s | combat: %s" % ("; ".join(h[:70] for h in held[:2]) or "no 'holding' line before the reveal", cb[:90]))
    # the way in: from the touch until the reveal's fade-in is over (VRFade.HoldInTime 0.35 s); a test's own eye capture
    # stalls frames by itself (not counted)
    t_end = (t_rel + 0.5) if t_rel is not None else 1e9
    caps = [clock(b) for b in body if b.startswith("captured eyes")]
    stalls, after = [], 0
    for b in body[max(i_touch, 0):]:
        if "perf stall" not in b or "view " not in b:
            continue
        t = clock(b)
        if t is None or any(c is not None and 0 <= t - c < 0.6 for c in caps):
            continue
        if t <= t_end:
            stalls.append(b)
        elif re.search(r"view (open|fading)", b):
            after += 1
    seen = [b for b in stalls if re.search(r"view (open|space|fading 0\.[0-8])", b)]
    if SHOTS[0]:
        log("      (the pictures' run: its captures stall frames - %d of the way in's %d long frames in view, not judged)" % (len(seen), len(stalls)))
    else:
        result("b1_%s_no_stall_in_view" % tag, not seen, "%d frames >= 40 ms on the way in, %d in view: %s | after the reveal %d in view (the fight's own start)" % (
            len(stalls), len(seen), " / ".join(re.sub(r" \| hold.*", "", x)[:110] for x in seen[:3]) or "views " + ", ".join(sorted(set(re.search(r"view (\w+)", b).group(1) for b in stalls))), after))
    return hold.group(1) if hold else "?", act_to_rel


def part_touch(seated, by_button=False, early=False, after="", end=True):
    tag = ("button" if by_button else "touch") + ("_early" if early else "") + ("_" + after if after else "") + ("_seated" if seated else "")
    n0 = pt.nlines()
    stalls0 = stalls_now()   # (the counter is the session's: this part's own are the difference)
    send("fightcard slowload %s" % (SLOW if early else 0))
    if SHOTS[0]:
        send("stateshot B1S_%s" % tag)
        send("relshot B1S_%s_reveal" % tag)
    ok = False
    try:
        ok = _part_touch(seated, by_button, early, tag, n0, stalls0)
    finally:
        send("fightcard slowload 0")
        send("stateshot off")
        send("relshot off")
    if end:
        win_and_back()
    return ok


def _part_touch(seated, by_button, early, tag, n0, stalls0):
    if not into_level_in():
        result("b1_%s_level_in" % tag, False, pt.state())
        return
    # the call to battle at the table: the place stays the card table, no waiting space, the arena asleep
    places, offered, t0 = [], None, time.time()
    shots = {1.0: "deal_1s", 2.5: "deal_2s", 4.0: "deal_4s"} if not SHOTS[0] else {}
    while time.time() - t0 < 14:
        send("state")
        s = st()
        pl = s.get("place", "")
        if not places or places[-1] != pl:
            places.append(pl)
        for at in sorted(shots):
            if time.time() - t0 >= at:
                shot("B1_%s_%s" % (tag, shots.pop(at)))
                break
        if s.get("fightCard", "").startswith("offered"):
            offered = s.get("fightCard", "")
            break
        time.sleep(0.25)
    ff = st().get("fightflow", "")
    lines, _ = pt.gamelog_since(n0)
    space = [l for l in lines if "limbo: waiting space on" in l]
    stayed = all(p.startswith("Caravan") for p in places) and not space
    result("b1_%s_stays_at_table" % tag, stayed and offered is not None,
           "places while in Level_In: %s; waiting space before the touch: %s; the card: %s" % (" > ".join(places), "yes" if space else "no", (offered or fc())[:150]))
    result("b1_%s_arena_asleep_at_table" % tag, ff.startswith("arena held"), "at the table: %s" % ff[:140])
    if offered is None:
        return
    time.sleep(1.2)   # the card's glide
    if not SHOTS[0]:
        sim("head 0 1.75 0 0 -25 0")
        time.sleep(0.5)
        shot("B1_%s_card" % tag)
        sim("head 0 1.75 0 0 0 0")
    # within reach: the card's face from the neutral eyes (standing: < 0.65 m from the eyes; seated: SeatedRate) - and brought
    # up no nearer than the spawn rule's 0.53 m (the bug hunt of 2026-10-18, finding 4)
    pose = send("fightcard pose 0")
    pos = pose.split("|")[0].strip()
    rr = send("seatreach " + pos) if re.match(r"-?[\d.]+ -?[\d.]+ -?[\d.]+", pos) else pose
    m = re.search(r"eye ([\d.]+)", rr)
    eye_d = float(m.group(1)) if m else 9.0
    ok_reach = (rr.startswith(("OK", "LEAN")) and "LAP" not in rr.split()[0] if seated else eye_d < 0.65) and eye_d >= 0.53
    result("b1_%s_card_in_reach" % tag, ok_reach, "the card's face: %s" % rr[:150])
    # loaded or not, as the card shows it (its bar, its glow), just before the touch
    bar = re.search(r"bar ([\d.]+) (loaded|loading)", fc())
    want = "loading" if early else "loaded"
    result("b1_%s_card_%s" % (tag, want), bar is not None and bar.group(2) == want, "the card's bar before the %s: %s" % ("A" if by_button else "touch", bar.group(0) if bar else fc()[:120]))
    if SHOTS[0]:
        sim("head 0 1.75 0 0 -25 0")
        time.sleep(0.5)
        shot("B1S_%s_card_%s" % (tag, "loading" if early else "ready"))
    # the touch (or A)
    n_touch = pt.nlines()
    if by_button:
        sim("btn r a 150")
    else:
        pt.poke_pose("fightcard pose 6 r", settle=0.5)
    t_touch = time.time()
    touched = None
    for _ in range(20):
        c = fc()
        if not c.startswith("offered"):
            touched = c
            break
        time.sleep(0.2)
    # (the touch's own log line - the log is written a moment later - or the status' counters: by the time the status is read
    # the card may already be gone, the space up)
    went = []
    for _ in range(6):
        lines, _ = pt.gamelog_since(n0)
        went = [l.split("] ", 1)[-1] for l in lines if "fight card: touched (" in l]
        if went:
            break
        time.sleep(0.5)
    cnt = re.search(r"touches (\d+) buttons (\d+)", fc())
    cnt0 = re.search(r"touches (\d+) buttons (\d+)", offered or "")
    by_count = cnt is not None and cnt0 is not None and (int(cnt.group(2)) > int(cnt0.group(2)) if by_button else int(cnt.group(1)) > int(cnt0.group(1)))
    how_ok = (bool(went) and (("touched (A)" in went[-1]) if by_button else ("fingertip)" in went[-1]))) or by_count
    result("b1_%s_%s" % (tag, "button_goes" if by_button else "touched"), how_ok,
           "after the %s: %s | %s" % ("A" if by_button else "poke", went[-1][:150] if went else "no touch logged", (touched or fc())[:100]))
    if not early:
        # item 30: loaded at the touch - straight into the fight
        if SHOTS[0]:
            sim("head 0 1.75 0 0 0 0")
        show_seen, show_touched = wait_level_play(by_button, tag)
        time.sleep(3.0)
        lines, _ = pt.gamelog_since(n_touch)
        dark, act_rel = check_direct(tag, lines, by_button)
        DARK.append((tag, dark, act_rel))
    else:
        # then the campfire, the arena still asleep, the game's music ducked
        space_on = None
        for _ in range(40):
            time.sleep(0.25)
            send("state")
            lm = st().get("limbo", "")
            if not lm.startswith("off"):
                space_on = time.time() - t_touch
                break
        time.sleep(2.5)
        send("state")
        ff = st().get("fightflow", "")
        result("b1_%s_then_campfire" % tag, space_on is not None and ff.startswith("arena held") and "ducked True" in ff,
               "the waiting space %s s after the %s; at the campfire: %s" % ("%.1f" % space_on if space_on is not None else "never", "A" if by_button else "touch", ff[:150]))
        sim("head 0 1.75 0 0 -15 0")
        time.sleep(0.4)
        shot("B1_%s_campfire" % tag)
        sim("head 0 1.75 0 0 0 0")
        # at the campfire once the place is ready: its Continue plate by touch (H17), or A
        gate_plate, n_go = None, None
        for _ in range(60):
            g = send("gate", 4)
            if g.startswith("gate waiting") and "encounter card" not in g:
                time.sleep(0.8)
                n_go = pt.nlines()
                if by_button:
                    sim("btn r a 150")
                else:
                    send("state")
                    gate_plate = st().get("goPlates", "")
                    sim("head 0 1.75 0 0 -25 0")
                    time.sleep(0.4)
                    shot("B1_%s_campfire_continue" % tag)
                    sim("head 0 1.75 0 0 0 0")
                    pt.poke_pose("goplate gate pose 6", settle=0.5)
                break
            time.sleep(0.5)
        if not by_button:
            send("state")
            gp = st().get("goPlates", "")
            result("b1_%s_campfire_continue_by_touch" % tag, gate_plate is not None and gate_plate.startswith("gate shown") and re.search(r"touched [1-9]", gp.split("|")[0] or "") is not None,
                   "the campfire's Continue plate: %s -> %s" % ((gate_plate or "-")[:60], gp[:80]))
        show_seen, show_touched = wait_level_play(by_button, tag)
        # (read once in the fight: the log is written a moment after the lines happen)
        lines, _ = pt.gamelog_since(n0)
        rdy = [l.split("] ", 1)[-1] for l in lines if "gate: 'fight_in" in l and "is ready" in l]
        result("b1_%s_gate_once_loaded" % tag, bool(rdy) and n_go is not None,
               "the gate: %s" % (rdy[0][:120] if rdy else "never opened"))
        send("state")
        ff = st().get("fightflow", "")
        mm = re.search(r"stalls >= 40 ms (\d+) \(worst (\d+) ms\)", ff)
        lines, _ = pt.gamelog_since(n0)
        act = [l for l in lines if "the arena activates now, behind the mist" in l]
        stalls = (int(mm.group(1)) - stalls0) if mm else -1
        result("b1_%s_fight_no_stall_at_campfire" % tag, "Level_Play" in pt.state() and stalls == 0 and bool(act),
               "%s; %d frames >= 40 ms while this fight's campfire was in view (%s); %s" % (pt.state(), stalls, mm.group(0) if mm else ff[:100],
                                                                                        act[-1].split("] ", 1)[-1][:110] if act else "no activation line"))
        # the old way's dark after A (to compare with the straight way's whole dark)
        if n_go is not None:
            gl, _ = pt.gamelog_since(n_go)
            body = [l.split("] ", 1)[-1] for l in gl]
            ia = next((i for i, b in enumerate(body) if "the arena activates now, behind the mist" in b), -1)
            ir = next((i for i, b in enumerate(body) if "fade release 'place'" in b and i > ia >= 0), -1)
            ta = clock(body[ia]) if ia >= 0 else None
            mr = re.search(r"at (\d\d:\d\d:\d\d\.\d+)", body[ir]) if ir >= 0 else None
            tr = clock(mr.group(1)) if mr else None
            go = next((b for b in body if b.startswith("gate: go (")), "")
            tg = clock(go) if go else None
            DARK.append((tag + " (after A/Continue)", "%.1f" % (tr - tg) if tg is not None and tr is not None else "?", (tr - ta) if ta is not None and tr is not None else -1))
    if not by_button and show_seen:
        send("state")
        gp = st().get("goPlates", "")
        m2 = re.search(r"showcase .*touched (\d+)", gp)
        result("b1_%s_showcase_continue_by_touch" % tag, show_touched is not None and m2 is not None and int(m2.group(1)) >= 1 and "Level_Play" in pt.state(),
               "the enemies' description's Continue plate: %s; %s" % (gp[:120], pt.state()))
    # nothing came too close at the table during the deal and the offer (the watchdog: listed touch rows don't count)
    lines, _ = pt.gamelog_since(n0)
    prox = [l.split("] ", 1)[-1] for l in lines if "PROXIMITY:" in l]
    result("b1_%s_nothing_too_close" % tag, not prox, "; ".join(p[:150] for p in prox[:3]) or "no proximity violation during the deal, the offer and the way in")
    return True


def part_loss(seated):
    """Straight in, the hero dies, a fresh dev run's map, straight in again (the first fight after a loss)."""
    tag = "loss" + ("_seated" if seated else "")
    if not part_touch(seated, after="before_loss", end=False):
        return
    if "Level_Play" not in pt.state():
        result("b1_%s_died" % tag, False, "not in the fight (%s)" % pt.state())
        return
    time.sleep(1.5)
    send("die")
    t0 = time.time()
    while time.time() - t0 < 90 and "Level_Play" in pt.state():
        time.sleep(1)
    died = pt.state()
    # (the death ends the run: the end-of-run screens, then the side table; a fresh dev run's dungeon map for the next fight)
    time.sleep(8)
    for _ in range(30):
        if any(k in pt.state() for k in ("ChallengeMap", "Hub", "GameState_Map")):
            break
        pt.answer_page()
        time.sleep(2)
    after = pt.state()
    send("scen reload mid", 10)
    ok = False
    for _ in range(60):
        time.sleep(2)
        if "GameState_Map" in pt.state() and pt.map_ready():
            ok = True
            break
    result("b1_%s_died" % tag, "Level_Play" not in died and ok, "after 'die': %s, then %s; a fresh dev run's map: %s" % (died.strip()[:40], after.strip()[:40], ok))
    if ok:
        part_touch(seated, after="after_loss")


def part_off(seated):
    tag = "off" + ("_seated" if seated else "")
    send("cfg TouchToFight false")
    n0 = pt.nlines()
    ok = into_level_in()
    time.sleep(5)
    lines, _ = pt.gamelog_since(n0)
    space = any("limbo: waiting space on (Plain, state GameState_Level_In)" in l for l in lines)
    starts = [l for l in lines if "fight card: the player stays" in l]
    result("b1_%s_old_way" % tag, ok and space and not starts, "TouchToFight false: the campfire at once %s, the table stay %s" % (space, bool(starts)))
    send("gate go")
    for _ in range(30):
        time.sleep(1)
        if "Level_Play" in pt.state():
            break
        send("gate go")
        if "Showcase" in (st().get("menu") or ""):
            sim("btn r a 150")
    send("cfg TouchToFight true")
    win_and_back()


def main():
    a = sys.argv[1:]
    seated = "--seated" in a
    parts = a[a.index("--parts") + 1].split(",") if "--parts" in a else ["touch", "button", "early", "after", "early_button", "loss", "off"]
    SHOTS[0] = "--shots" in a
    open(pt.LOG, "a").close()
    log("==== B1 test%s: %s" % (" (seated)" if seated else "", ",".join(parts)))
    if seated:
        os.environ["HOF2VR_SEATED"] = "1"
        pt.SEATED[0] = True
    if "--start" in a:
        subprocess.run(["python3", H + "/scripts/session.py", "start", "--snapshot", "magician_mid", "--to", "table"], capture_output=True, text=True, timeout=4200)
    sim("seat %s" % ("0.55" if seated else "0"))
    send("posture " + ("seated" if seated else "standing"))
    time.sleep(2)
    send("cfg Scenarios true")
    for p in parts:
        try:
            if p == "touch":
                part_touch(seated)
            elif p == "button":
                part_touch(seated, by_button=True)
            elif p == "early":
                part_touch(seated, early=True)
            elif p == "early_button":
                part_touch(seated, by_button=True, early=True)
            elif p == "after":
                part_touch(seated, after="after_campfire")
            elif p == "loss":
                part_loss(seated)
            elif p == "off":
                part_off(seated)
        except Exception as e:  # noqa: BLE001
            result("b1_" + p, False, "error: %s" % e)
    with open(pt.GAMELOG, errors="replace") as f:
        exc = sum(1 for l in f if "Exception" in l)
    result("b1_no_exceptions", exc == 0, "%d exception lines in the mod's log" % exc)
    if seated:
        sim("seat 0")
    for tag, dark, act_rel in DARK:
        log("  dark: %s: %s s in all, %.1f s of it after the activation" % (tag, dark, act_rel))
    ok = sum(1 for _, v in pt.RESULTS if v)
    log("==== %d/%d PASS" % (ok, len(pt.RESULTS)))
    sys.exit(0 if ok == len(pt.RESULTS) else 1)


if __name__ == "__main__":
    main()
