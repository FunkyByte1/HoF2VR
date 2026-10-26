#!/usr/bin/env python3
"""Virtual playtester, first version (docs/queue.md item -1): a tour through the screens with human-like head motion,
automatic checks taken from the user's past reports, and stereo shots for a review pass.

  playtest.py [--snapshot NAME] [--skip-start] [--seated] [--only title,table,touch,menu,devkeys,vrsettings,chance,shop,narration,fight,death,scenarios]
Stages: title (look-around flicker, ground, where the hero is), title -> table (a quick fade, no campfire), table
(stats on the table, reach, inventory), touch (the hand feel on the test board: a real-hand poke clicks at the fingertip,
the glove rests on a pushed button with the ghost), chance cards (a real-hand poke picks one), the shop (opened by
poking "Buy equipment." with the real hand; no flat backdrop), fight entry by the dev jump (campfire, weapon in hand,
Press A), win, back to the table (a quick fade), death (the death text, back at the side table), exceptions.
Results: PASS/FAIL lines in logs/playtest.log; shots testdata/shots/PT_*; then the AI review of the shots against
docs/playtest-checklist.md (every past user report and decision).
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
LOG = H + "/logs/playtest.log"
GAMELOG = hof2env.GLOG   # (this environment's game log; env 1's = the Steam install's)
GAMECFG = os.path.join(hof2env.G, "BepInEx", "config", "flat2vr.hof2vr.cfg")
RESULTS, SHOTS = [], []
# --seated (2026-10-13, seated mode): the game seated (HOF2VR_SEATED=1) and the simulated head and hands 0.55 m lower (xrsim
# `seat`): the tour's 1.75 m eyes become a seated 1.20 m. Every poke's end point is rated for seated reach (`seatreach tip r`).
SEATED = [False]
REACH = []   # (what, rating line) of every poke's touch point
# the user's habit (2026-10-11 ~22:15, the coverage audit's H9): pointing with the grip squeezed and the index lifted - every
# second poke of the tour is made that way (the grip closes with the index off the trigger: a pointing hand, never a page)
HABIT = {"n": 0, "done": 0}


def log(msg):
    line = time.strftime("%H:%M:%S ") + msg
    print(line, flush=True)
    with open(LOG, "a") as f:
        f.write(line + "\n")


def sim(c):
    subprocess.run(["python3", H + "/scripts/xrsim.py", c], capture_output=True, text=True)


def send(c, t=10):
    try:
        return cmd.send(c, t)
    except SystemExit as e:
        return "ERR " + str(e)


def st():
    for _ in range(5):
        try:
            return cmd.status()
        except Exception:
            time.sleep(0.2)
    return {}


def state():
    return send("state?", 4)


def wait_state(subs, timeout):
    deadline = time.time() + timeout
    while time.time() < deadline:
        s = state()
        if any(x in s for x in subs):
            return s
        time.sleep(1)
    return None


def result(name, ok, evidence):
    RESULTS.append((name, ok))
    log(("PASS " if ok else "FAIL ") + name + " - " + evidence)


def shot(name):
    send("shot PT_" + name)
    time.sleep(1.8)
    SHOTS.append("PT_" + name)


def gamelog_since(n):
    with open(GAMELOG, errors="replace") as f:
        lines = f.read().splitlines()
    return lines[n:], len(lines)


def nlines():
    return gamelog_since(0)[1]


def run(script, *args, timeout=600):
    r = subprocess.run(["bash", H + "/scripts/" + script] + list(args), capture_output=True, text=True, timeout=timeout)
    return r.stdout + r.stderr


def stage_title():
    sim("fov quest2; head 0 1.75 0 0 0 0; hand r 0.18 1.3 -0.32 0 -15 0; hand l -0.18 1.3 -0.32 0 -15 0")
    send("recenter")
    time.sleep(1)
    # 1. flicker while looking round (user 2026-10-11: "flickering when I look around"). The bug's signature: a camera culled
    # twice in one frame (one eye re-culled, drawn without shadows); camlog counts the culls for the look-around
    n_cam = nlines()
    send("camlog 1900")
    out = run("flicker_run.sh", "PT_title", "1900")
    send("camlog 0")
    culls = {}
    for line in gamelog_since(n_cam)[0]:
        m = re.search(r"camlog f(\d+) '([^']+)'", line)
        if m:
            culls[(m.group(1), m.group(2))] = culls.get((m.group(1), m.group(2)), 0) + 1
    doubles = sorted(set(k[1] for k, v in culls.items() if v > 1))
    ndouble = sum(1 for v in culls.values() if v > 1)
    fl = subprocess.run(["python3", H + "/scripts/flashes.py", "PT_title"], capture_output=True, text=True).stdout
    # the user's flicker was one eye at a time; a frame flagged in both eyes at once is a scene change (a pop while
    # turning fast), reported but not the bug
    per = {}
    eye = None
    for line in fl.splitlines():
        m = re.match(r"eye ([LR]):", line)
        if m:
            eye = m.group(1)
            per[eye] = set()
            continue
        m = re.match(r"\s+frame (\d+)", line)
        if m and eye:
            per[eye].add(int(m.group(1)))
    lf, rf = per.get("L", set()), per.get("R", set())
    one = len(lf ^ rf)
    both = len(lf & rf)
    result("title_flicker", ndouble == 0 and one <= 2,
           "%d frames with a camera culled twice (the one-eye flicker's cause) %s; luma: %d one-eye and %d both-eye changed frames over 1900 frames of fast head turns (pops at the view's edge)"
           % (ndouble, doubles[:3] if doubles else "", one, both))
    # 2. where the player stands: beside the hero, facing the fire, level ground all round
    with open(GAMELOG, errors="replace") as f:
        txt = f.read()
    m = re.findall(r"rig: station Title \(beside the hero \(([\d.]+) m, (\d+) deg from the fire\), ([\d.]+) m from the fire", txt)
    if m:
        hd, ang, fd = float(m[-1][0]), int(m[-1][1]), float(m[-1][2])
        result("title_hero_beside", 45 <= ang <= 115 and hd >= 1.0, "hero %.1f m away, %d deg from the fire direction; fire %.1f m ahead" % (hd, ang, fd))
    else:
        result("title_hero_beside", False, "no TitleSpot station line")
    p = send("place")
    mm = re.search(r"station \(([-\d.]+),([-\d.]+),([-\d.]+)\) yaw ([-\d.]+)", p)
    if mm:
        x, y, z, yaw = (float(v) for v in mm.groups())
        worst = 0.0
        for r in (1.0, 2.0):
            for a in range(0, 360, 45):
                gx = x + r * math.sin(math.radians(a + yaw))
                gz = z + r * math.cos(math.radians(a + yaw))
                g = send("ground %.3f %.3f %.3f" % (gx, y + 4, gz))
                gm = re.search(r"ground y=([-\d.]+)", g)
                if gm:
                    worst = max(worst, abs(float(gm.group(1)) - y))
        result("title_ground_level", worst <= 0.15, "largest ground step within 2 m: %.2f m (user: 'all the ground around me ... roughly the same level')" % worst)
    # the title menu on a board within reach (Hands.TitleMenuBoard)
    sim("head 0 1.75 0 0 -12 0")
    time.sleep(1.0)
    b = st().get("boards", "")
    m = re.search(r"title menu \| plates (\d+)", b)
    result("title_menu_board", m is not None and int(m.group(1)) >= 4, b[:150])
    shot("title_board")
    sim("head 0 1.75 0 0 -15 0")
    time.sleep(0.6)
    shot("title_ahead")
    sim("head 0 1.75 0 180 -10 0")
    time.sleep(0.6)
    shot("title_behind")
    sim("head 0 1.75 0 0 0 0")


def stage_table():
    n0 = nlines()
    # as session.py: the simulated hands out of the way, Continue (the Mods screen's Play is X), retried
    sim("hand r 0.2 1.0 -0.3 0 -60 0; hand l -0.2 1.0 -0.3 0 -60 0")
    for k in range(6):
        # the first try by touch (the title menu board), then the laser seam
        if k == 0 and "title menu" in st().get("boards", ""):
            t0 = poke_plate("Continue", settle=0)
            # how soon the view leaves the title after the press (the click lands ~0.95 s into the push path; the mods
            # question, when the game asks it, is answered with X and the time counts from there); before 12 October the
            # title stayed ~3 s (its out animation and the out menu's timer)
            t_press = t0 + 0.95 if t0 else None
            t_leave, t_arrive, places, asked, next_ask = None, None, [], False, 0.0
            while t0 and time.time() - t0 < 25:
                if not asked and t_leave is None and time.time() >= next_ask:
                    next_ask = time.time() + 0.5
                    if send("ui list ModdingDialog").split()[0] != "0":
                        send("press X 6")
                        t_press, asked = time.time(), True
                        places.append("(mods question: X)")
                send("state")
                pl = st().get("place", "")
                if not places or places[-1] != pl:
                    places.append(pl)
                if t_leave is None and ("leaving Title" in pl or pl.startswith("Transition")):
                    t_leave = time.time()
                if t_leave is not None and pl.startswith(("Caravan", "Hub")):
                    t_arrive = time.time()
                    break
                time.sleep(0.08)
            sim("hand r 0.2 1.0 -0.3 0 -60 0; hand l -0.2 1.0 -0.3 0 -60 0")
            if k == 0:
                touched = "select Continue" in st().get("boards", "") or t_leave is not None
                result("title_continue_by_touch", touched, "a real-hand poke on the Continue plate: %s" % ("selected" if touched else st().get("boards", "")[:120]))
                if t0:
                    lv = t_leave - t_press if t_leave else None
                    ar = t_arrive - t_press if t_arrive else None
                    result("title_continue_fades_at_once", lv is not None and lv < 1.0,
                           "the view leaves the title %s after the press; at the table %s after it (places: %s)" % (
                               "%.2f s" % lv if lv is not None else "never", "%.1f s" % ar if ar is not None else "-", " > ".join(places)[:160]))
        else:
            r = send("ui click ContinueButton")
        time.sleep(4)
        if send("ui list ModdingDialog").split()[0] != "0":
            send("press X 6")
        if wait_state(["Profile", "ChallengeMap", "Map", "Attract", "Intro"], 12):
            break
    if not wait_state(["GameState_Map", "ChallengeMap", "Attract"], 120):
        result("title_to_table", False, "never reached the table (%s)" % state())
        return False
    send("gate go")
    time.sleep(5)
    lines, _ = gamelog_since(n0)
    shown = [l for l in lines if "limbo: waiting space on" in l]
    result("quick_hop_title_table", not shown, "waiting space shown: %s" % (shown[-1][-60:] if shown else "no (a short fade)"))
    s = st()
    th = s.get("tableHud", "")
    result("table_stats_on_table", "on the table" in th and "placed 0" not in th, th[:110])
    send("reach PT_table")
    time.sleep(1)
    try:
        with open(H + "/testdata/reach/PT_table.txt") as f:
            rows = sorted(set(round(float(v), 2) for v in re.findall(r"slot \(\d+, \d+\).*?\(ahead ([\d.]+),", f.read())))
        mid = rows[len(rows) // 2] if rows else 9
        result("table_reach_middle", mid <= 0.5, "map rows ahead %s m; middle %.2f m (user: 'reach cards in the middle')" % (rows, mid))
        if SEATED[0]:
            with open(H + "/testdata/reach/PT_table.txt") as f:
                m = re.search(r"seated summary \(touch targets now\): OK (\d+), LEAN (\d+), FAR (\d+), in the lap (\d+)(.*)", f.read())
            result("seated_reach_table", bool(m) and m.group(3) == "0" and m.group(4) == "0",
                   ("the map's next places and the figurine: OK %s, with a lean %s, out of reach %s, in the lap %s%s" % m.groups()) if m else "no seated summary")
    except OSError:
        result("table_reach_middle", False, "no reach file")
    sim("turn 0 0 0 -50 500")
    time.sleep(0.4)
    shot("table_down")
    sim("turn 0 30 -50 -45 500")
    time.sleep(0.4)
    shot("table_downleft_stats")
    # the controls (the game's button bar as buttons): upright, facing the eyes, clear of the table and the choice board
    # (user 2026-10-12: "The button prompts were in weird places at weird angles and collided with the table")
    prompt_check("map")
    sim("turn 30 0 -45 -10 500")
    send("press BACK")
    time.sleep(3)
    shot("inventory")
    # the inventory can be left by touch (user 2026-10-12: "A lot of menus (such as the inventory menu) didn't have back buttons")
    b = st().get("boards", "")
    result("inventory_back_by_touch_offered", "nav:Back" in b, "the inventory's buttons: %s" % ", ".join(re.findall(r"nav:([^\]]*)", b)))
    prompt_check("inventory")
    fonts_check("inventory")
    # a dev jump while a menu is open over the map is refused with a note (bug 2026-10-12: from the character choice a
    # jumped fight hung in Level_In behind the mist)
    r = send("jump fight EL_Ambush_Goblins")
    time.sleep(1)
    result("jump_refused_when_map_busy", not r.startswith("started") and "Level_In" not in state(), "with the inventory open: %s" % r)
    send("press B")
    time.sleep(2)
    return True


def fonts_check(where):
    """The mod's texts shown now (the coverage audit's L8; the user 2026-10-11: no "generic, flat, and boring" text): all in
    the game's fonts, none too small to read (a line under 0.45 deg) or turned more than 70 deg away from the eyes."""
    r = send("fonts")
    m = re.search(r"(\d+) mod labels, (\d+) in the game's fonts .*?\| in view (\d+): the smallest line ([\d.]+) deg .*?the most turned (\d+) deg", r)
    if not m:
        return result("mod_text_" + where, False, r[:200])
    n, game, view, small, turn = int(m.group(1)), int(m.group(2)), int(m.group(3)), float(m.group(4)), int(m.group(5))
    result("mod_text_" + where, n == game and (view == 0 or (small >= 0.45 and turn <= 70)), r[:300])


def prompt_check(where):
    send("state")
    g = send("navgeo")
    m = re.search(r"lowest edge (-?[\d.]+) m above the table top, facing the eyes within (\d+) deg, overlaps the choice board (\w+)", g)
    ok = m is not None and float(m.group(1)) >= 0.10 and int(m.group(2)) <= 30 and m.group(3) == "no"
    result("prompts_clear_" + where, ok, g)


def touch_counts():
    t = st().get("touch", "")
    m = re.search(r"selects (\d+) .*swipes (\d+) steps (\d+) coast (\d+)", t)
    return tuple(int(x) for x in m.groups()) if m else None


def swipe_check():
    """Swipe to scroll (user 2026-10-12: rows with many cards "only scroll with the stick. I should be able to swipe along
    them"): a fingertip comes onto a shop card, then slides 18 cm along the row: the row scrolls (steps), the touch is a
    swipe, never a press (no card bought or selected)."""
    p = send("touchpose Card_EQ 6")
    if "|" not in p:
        result("swipe_scrolls_row", False, "no shop card to touch: %s" % p)
        return
    pos, mv = [x.strip() for x in p.split("|")]
    d = [float(v) for v in mv.split()[2:5]]
    c0 = touch_counts()
    # the hand to its start first: the move to the card is relative to where the hand is
    sim("hand r 0.25 1.2 -0.15 0 -30 0")
    time.sleep(0.4)
    h = send("handto r tip " + pos)
    k = 5.7 / 7.5   # from 6 cm in front to 0.3 cm in front of the face
    sim(h + " 400; wait 200; move r %.4f %.4f %.4f 300; wait 300; move r -0.18 0 0 450; wait 250; move r %.4f %.4f %.4f 300" %
        (d[0] * k, d[1] * k, d[2] * k, -d[0], -d[1], -d[2]))
    time.sleep(3.0)
    c1 = touch_counts()
    ok = c0 is not None and c1 is not None and c1[1] > c0[1] and c1[2] > c0[2] and c1[0] == c0[0]
    result("swipe_scrolls_row", ok, "selects %s -> %s, swipes %s -> %s, row steps %s -> %s, coasting %s -> %s" % (
        c0 and c0[0], c1 and c1[0], c0 and c0[1], c1 and c1[1], c0 and c0[2], c1 and c1[2], c0 and c0[3], c1 and c1[3]))
    sim(REST)


def poke_plate(label, settle=1.5):
    """The simulated hand pokes the choice plate whose text contains `label` (as the player would); returns the time the
    push path started (the click lands about 0.95 s later) or False."""
    b = st().get("boards", "")
    m = re.findall(r"\[(\d+) ([^\]]*)\]", b)
    idx = next((int(i) for i, t in m if label.lower() in t.lower()), None)
    if idx is None:
        return False
    return poke_pose("platepose %d 6 r" % idx, settle)


def poke_pose(pose_cmd, settle=1.5):
    """The simulated right hand comes from the side to 6 cm in front of a button and pushes 1.5 cm through it at 0.25 m/s
    (`pose_cmd` answers "x y z | move r dx dy dz", as `platepose` and `vrset pose` do). Every second poke with the grip
    squeezed and the index lifted (HABIT)."""
    HABIT["n"] += 1
    grip = HABIT["n"] % 2 == 0
    # (the finger rests on the trigger long enough for a frame to see it: 40 ms was shorter than a frame now and then on the
    # test GPU profile - until a touch had ever been seen, every grip poke counted as a page, and the seated tour of 06:27
    # paged its VR Settings poke: "fingers: R reports touch trigger" came only after it)
    sim("hand r 0.25 1.2 -0.15 0 -30 0" + ("; ctouch r trigger 1; wait 150; ctouch r trigger 0; squeeze r 1" if grip else ""))
    if grip:
        HABIT["done"] += 1
    time.sleep(0.4)
    try:
        return _poke_pose(pose_cmd, settle)
    finally:
        if grip:
            sim("squeeze r 0")


def _poke_pose(pose_cmd, settle):
    r = send(pose_cmd)
    if "|" not in r:
        return False
    pos, press = [x.strip() for x in r.split("|")]
    h = send("handto r tip " + pos)
    p = press.split()
    back = "move r %.4f %.4f %.4f 300" % (-float(p[2]), -float(p[3]), -float(p[4]))
    t0 = time.time()
    if SEATED[0]:
        sim(h + " 400; wait 300; " + press + " 300")
        REACH.append((pose_cmd, send("seatreach tip r")))
        sim("wait 150; " + back)
    else:
        sim(h + " 400; wait 300; " + press + " 300; wait 150; " + back)
    time.sleep(settle)
    return t0


def press_card(name):
    """The simulated hand pokes a 3D card / tile (touchpose: 6 cm in front of its face, 1.5 cm through)."""
    r = send("touchpose %s 6 r" % name)
    if "|" not in r:
        return False
    pos, press = [x.strip() for x in r.split("|")]
    h = send("handto r tip %s" % pos)
    if not h.startswith("move"):
        return False
    sim(h + " 400")
    time.sleep(0.5)
    p = press.split()
    back = "move r %.4f %.4f %.4f 300" % (-float(p[2]), -float(p[3]), -float(p[4]))
    if SEATED[0]:
        sim("%s 300" % press)
        REACH.append(("card " + name, send("seatreach tip r")))
        sim("wait 150; %s" % back)
    else:
        sim("%s 300; wait 150; %s" % (press, back))
    time.sleep(1.2)
    return True


REST = "hand r 0.2 1.0 -0.3 0 -60 0; hand l -0.2 1.0 -0.3 0 -60 0"


def stage_touch():
    """The hand feel (docs/hand-feel-design.md Phase 1) on the mod's test board at the table."""
    import handfeel_test as hf
    sim(REST + "; head 0 1.75 0 0 0 0")
    send("feel reset")
    if not send("tboard show 3").startswith("shown"):
        result("touch_board", False, "no test board")
        return
    time.sleep(0.8)
    p0 = hf.presses()
    hf.poke(2, 0.3)
    time.sleep(1.1)
    m = re.search(r"last press ([-\d.]+) mm in at ([\d.]+) m/s", st().get("tboard", ""))
    depth = float(m.group(1)) if m else -1
    result("touch_click_at_fingertip", hf.presses() == p0 + 1 and 0 <= depth <= 3.5 + 1 + 6, "a 0.3 m/s poke clicked at %.1f mm (button click point 3.5 mm)" % depth)
    sim("head 0 1.75 0 0 -32 0")
    time.sleep(0.4)
    hf.at(2, 3.0)
    shot("touch_approach")
    n, u = hf.at(2, 4.0)
    sim(hf.mv([-n[k] * 0.07 for k in range(3)], 350))
    time.sleep(1.5)
    f = st().get("feel", "")
    shot("touch_pushed3cm")
    mm = re.search(r"R: d (?:-|[-\d.]+cm \w+) filter [\d.]+ assist [\d.]+ stop ([\d.]+) mm.*? ghost ([\d.]+)", f)
    stop, ghost = (float(mm.group(1)), float(mm.group(2))) if mm else (-1, -1)
    result("touch_glove_rests", 18 <= stop <= 30 and ghost > 0, "real fingertip 3 cm past the button: glove held %.0f mm back (on the pushed button), ghost %.2f" % (stop, ghost))
    sim(hf.mv([n[k] * 0.15 for k in range(3)], 300) + "; " + REST + "; head 0 1.75 0 0 0 0")
    send("tboard hide")
    time.sleep(0.6)


def stage_chance():
    """The chance cards: the game's own task (`mg chance`), a card picked by a real-hand poke."""
    sim(REST + "; head 0 1.75 0 0 0 0")
    r = send("scen here Choose_a_card")   # the Scenarios catalog's steps (one source of truth with the Dev menu)
    if not r.startswith("running"):
        result("chance_cards", False, r)
        return
    time.sleep(1.0)
    picked, tries = False, 0
    deadline = time.time() + 40
    shot_done = False
    while time.time() < deadline and not picked:
        if st().get("chance", "-").startswith("running"):
            if not shot_done:
                sim("head 0 1.75 0 0 -30 0")
                time.sleep(0.5)
                shot("chance_cards")
                send("rtshot PT_chance_cards_rt")   # the 2D screen too (the coverage audit's T1)
                shot_done = True
                sim("head 0 1.75 0 0 0 0")
            t0 = st().get("touch", "")
            s0 = int(re.search(r"selects (\d+)", t0).group(1)) if re.search(r"selects (\d+)", t0) else 0
            press_card("CardChoiceContainer/Card_CN")
            tries += 1
            time.sleep(1.2)
            t1 = st().get("touch", "")
            s1 = int(re.search(r"selects (\d+)", t1).group(1)) if re.search(r"selects (\d+)", t1) else 0
            picked = s1 == s0 + 1
            sim(REST)
        time.sleep(1.0)
    result("chance_card_by_touch", picked, "%d pokes; %s | %s" % (tries, st().get("chance"), st().get("touch", "")[:80]))
    for _ in range(20):
        if "done" in st().get("mg", "") or not st().get("chance", "-").startswith("running"):
            break
        send("press A 6")
        time.sleep(1.5)
    time.sleep(3)


def stage_shop():
    """The shop: the dev jump starts the game's shop encounter; the real hand pokes 'Buy equipment.'."""
    sim(REST + "; head 0 1.75 0 0 0 0")
    n0 = nlines()
    send("scen here Shop")   # the Scenarios catalog's steps (one source of truth with the Dev menu)
    ok = False
    for _ in range(15):
        time.sleep(1)
        if "Buy equipment" in st().get("boards", ""):
            ok = True
            break
    if not ok:
        result("shop_open_by_touch", False, "no shop plates: %s" % st().get("boards", "")[:100])
        return
    sim("head 0 1.75 0 0 -25 0")
    time.sleep(0.6)
    shot("shop_choices")
    send("rtshot PT_shop_choices_rt")   # the 2D screen too (the coverage audit's T1)
    prompt_check("encounter")
    sim("head 0 1.75 0 0 0 0")
    poke_plate("Buy equipment")
    time.sleep(2.5)
    menu = st().get("menu")
    result("shop_open_by_touch", menu == "Shop", "after poking 'Buy equipment.': menu %s" % menu)
    sim(REST + "; head 0 1.75 0 0 -25 0")
    time.sleep(0.8)
    shot("shop")
    send("rtshot PT_shop_rt")   # the 2D screen too (the coverage audit's T1)
    lines, _ = gamelog_since(n0)
    hid = [l for l in lines if "themed wash made transparent" in l]
    result("shop_no_flat_backdrop", bool(hid), hid[-1].split("] ")[-1] if hid else "the shop's green wash was not made transparent (user: no flat rectangles in the room)")
    swipe_check()
    sim("head 0 1.75 0 0 0 0")
    send("press B")
    time.sleep(2.5)
    if "Leave" in st().get("boards", ""):
        poke_plate("Leave")
    for _ in range(10):
        if st().get("menu") in ("Map", None) and "plates 0" in st().get("boards", "plates 0"):
            break
        send("press B")
        time.sleep(1.5)
    time.sleep(2)


def equip_counts():
    """(asked, with plates, without, equipped at once) from the mod's `equipAsk` status (Diag/EquipAsk.cs)."""
    m = re.match(r"(\d+) asked, (\d+) with plates, (\d+) WITHOUT, (\d+) equipped at once", st().get("equipAsk", ""))
    return tuple(int(x) for x in m.groups()) if m else (0, 0, 0, 0)


def answer_equip_by_touch(timeout=15):
    """Waits for the equip question's plates and pokes one by hand: No when offered (keep what is worn), else Equip /
    Reserve. Returns the word poked, or None."""
    t0 = time.time()
    while time.time() - t0 < timeout:
        navs = [n.strip() for n in re.findall(r"\[\d+ nav:([^\]]*)\]", st().get("boards", ""))]
        for want in ("No", "Equip", "Reserve", "Store"):
            if any(n.lower() == want.lower() for n in navs):
                sim("head 0 1.75 0 0 0 0")
                poke_plate("nav:" + want)
                time.sleep(1.5)
                return want
        time.sleep(0.8)
    return None


def stage_equip():
    """Earned gear (round 5 item 6; user 2026-10-12: "When I earned gear, I had no way to choose whether or not to equip
    it"): the ways the game hands over equipment - dealt directly, a reward card (an encounter's or a chance card's
    outcome: the game's Equipment+1), a choice of three, a shop purchase - each asks with plates, answered by a real-hand
    poke. The mod notes every question and whether the plates offered its words (status `equipAsk`)."""
    sim(REST + "; head 0 1.75 0 0 -15 0")
    a0 = equip_counts()
    answers = []
    for label in ("Gain_a_weapon", "Random_equipment"):
        r = send("scen here " + label)
        if not r.startswith("running"):
            answers.append("%s: %s" % (label, r[:40]))
            continue
        ans = answer_equip_by_touch(20)
        answers.append("%s: %s" % (label, ans or "no plates"))
        finish_encounter("", 30)
    # a choice of three: the card picked by hand, Continue, then the question
    r = send("scen here Choose_1_of_3")
    if r.startswith("running"):
        sim("head 0 1.75 0 0 -20 0")
        # the game's card ("choose one of three") first, with its Continue; then the three cards are dealt
        if wait_boards(lambda b: "nav:Continue" in b, 10) is not None:
            poke_plate("nav:Continue")
        t0 = time.time()
        while time.time() - t0 < 15 and "|" not in send("touchpose Card_EQ 6 r"):
            time.sleep(1)
        picked = press_card("Card_EQ")
        time.sleep(2)
        cont = wait_boards(lambda b: "nav:Continue" in b, 6) is not None and poke_plate("nav:Continue")
        ans = answer_equip_by_touch(20)
        answers.append("Choose_1_of_3: %s" % (ans or "no plates (picked %s, continue %s)" % (bool(picked), bool(cont))))
        finish_encounter("", 30)
    # the shop: enough gold, Buy equipment, a card by hand, its Buy plate, then the question
    send("stat gold 800")
    r = send("scen here Shop")
    if r.startswith("running") and wait_boards(lambda b: "Buy equipment" in b, 20):
        sim("head 0 1.75 0 0 0 0")
        poke_plate("Buy equipment")
        time.sleep(2.5)
        sim("head 0 1.75 0 0 -20 0")
        bought = None
        # a card whose slot is taken (buying it must ask; with the slot free the game equips it at once, as the flat game)
        occ = send("equipask occupied").split()
        targets = [x for x in occ if x != "none"][:3] or ["Card_EQ"]
        for name in targets:
            press_card(name)
            time.sleep(1.5)
            navs = [n.strip() for n in re.findall(r"\[\d+ nav:([^\]]*)\]", st().get("boards", ""))]
            buy = next((n for n in navs if n.lower() not in ("back", "inspect", "no", "yes")), None)
            if buy:
                poke_plate("nav:" + buy)
                bought = buy
                break
            send("press B 6")
            time.sleep(1.5)
        ans = answer_equip_by_touch(20) if bought else None
        answers.append("Shop: %s" % ("%s %s, then %s" % (bought, name, ans or "no plates") if bought else "no card to buy (%s)" % " ".join(occ)))
        time.sleep(2)
        for _ in range(8):
            if "Leave" in st().get("boards", ""):
                poke_plate("Leave")
                break
            send("press B 6")
            time.sleep(1.5)
        finish_encounter("Leave", 30)
    a1 = equip_counts()
    asked, withp, without = a1[0] - a0[0], a1[1] - a0[1], a1[2] - a0[2]
    poked = sum(1 for x in answers if x.split(": ", 1)[-1].split(", then ")[-1] in ("No", "Equip", "Reserve", "Store"))
    ways = re.search(r"ways: (.*?) \| ", st().get("equipAsk", ""))
    result("equip_question_by_touch_every_way", without == 0 and withp >= 4 and poked >= 4,
           "%d questions, %d with plates, %d without; answered by hand: %s | ways: %s" % (asked, withp, without, "; ".join(answers), ways.group(1)[:300] if ways else "-"))


LAST_JUMP = ""


def jump_when_ready(what, timeout=60):
    """A dev jump, once the map is ready: after a won fight the game shows the fight's result page first, and jumps are
    refused until it is closed (DevJump.MapReady) - the page is closed by its first plate (or A) and the jump tried again."""
    t0 = time.time()
    global LAST_JUMP
    while time.time() - t0 < timeout:
        r = send("jump " + what)
        LAST_JUMP = r
        if r.startswith("started"):
            return True
        if "paused" in r:
            # a pause menu left open by a stage before: closed as a player does (its Resume by touch, else the menu button)
            if not send("ptouch Resume").startswith("tap"):
                send("press START 6")
        elif "is open: close it first" in r:
            if not send("ptouch nav:Back").startswith("tap"):
                send("press B 6")
        elif "map is ready" in r or "not yet" in r:
            # an equip question (a reward after the last fight): No - the next fight keeps the same weapon and shield (the
            # campfire's kept copies are checked against them); else the page's first option or its Continue. Every answer
            # by touching a plate found by its label, never a button (13:14 RC tour: an A meant for a page reached the map
            # and walked the hero into another fight; the jump was refused while it loaded)
            answer_page()
        time.sleep(2.5)
    return False


def mark_die():
    """The dice's marking phase as a player does it: the hand up over the table, then a fingertip poke on a die (press_card:
    rated for seated reach in a seated tour), die after die until one is marked (2026-10-16: the laser's click on D6 (6)
    came from the resting hand - seated, under the table top - and met a map card, so seated dice were never marked)."""
    sim("hand r 0.2 %.2f -0.3 0 -60 0" % (1.5 if SEATED[0] else 1.0))
    time.sleep(0.5)
    for name in ["D6 (%d)" % i for i in range(1, 10)] + ["D6"]:
        if not press_card("PlayerDiceRoller/Container/" + name):
            continue
        d = st().get("dice", "") or ""
        log("      dice: %s touched -> %s" % (name, d[:24]))
        if d.startswith("Marking") and "marked=0" not in d:
            return True
    return False


def answer_page():
    """Whatever the table waits for, as a player answers it - by touching a plate found by its label (never a button that
    could reach the map): an equip question No, a page's first option, a choice's Select or a card, Continue; the dice
    finished; the inventory closed."""
    s0 = st()
    b = s0.get("boards", "").split(" | touch")[0]
    menu = s0.get("menu") or ""
    if "Dice" in menu:
        d = s0.get("dice", "") or ""
        if d.startswith("Marking"):
            if "marked=0" in d:
                mark_die()
            send("press X 6")
        elif "nav:Continue" in b:
            send("ptouch nav:Continue")
        else:
            send("dice throw 0 0.5 2.0")   # an encounter's dice: thrown (A does nothing there)
    elif menu == "Inventory":
        send("ptouch nav:Back")
    elif "nav:No" in b:
        send("ptouch nav:No")
    elif menu == "Choice" and "nav:Select" in b and not re.search(r"\[\d+ (?!nav:)", b):
        send("ptouch nav:Select")
    elif menu == "Choice" and not any(k in b for k in ("nav:Continue", "nav:Back", "nav:Select")):
        send("touch CardChoiceContainer/Card_ r tap")
    elif re.search(r"\[\d+ (?!nav:)", b):
        send("ptouch opt")
    elif "nav:Continue" in b:
        send("ptouch nav:Continue")


def stage_death():
    """Death in a fight: the death text, then back at the side table facing the map."""
    if "GameState_Map" not in state():
        result("death", False, "not on the map (%s)" % state())
        return
    sim(REST + "; head 0 1.75 0 0 0 0")
    send("gate auto 1")
    n_in = nlines()
    if not jump_when_ready("fight EL_Ambush_Goblins") and not any(k in state() for k in ("Level_In", "Level_Play")):
        # (a fight that began anyway - an encounter's - serves as well: the death happens in it)
        result("death", False, "the jump was refused for 60 s (%s; the last answer: %s; menu %s)" % (state(), LAST_JUMP[:90], st().get("menu")))
        return
    for _ in range(15):
        time.sleep(3)
        if "Level_Play" in state():
            break
        # the fight's page at the table (Continue) and the campfire's A; an equip question is answered No (the second
        # fight keeps the first one's weapon and shield: the campfire's kept copies are checked against them)
        if "Level_In" in state():
            send("press A 6")   # the campfire's A (the arena waits for it), the enemies' description
        else:
            answer_page()       # the fight's page at the table, by touch
    # the second fight of the session: at its campfire the hands hold the weapon and shield kept from the first (the arena,
    # and the hero with it, stays asleep until A since round 5)
    lines, _ = gamelog_since(n_in)
    if any("limbo: waiting space on (Plain, state GameState_Level_In)" in l for l in lines):
        kept = [l for l in lines if "of the last fight in the waiting space's hands" in l]
        result("fight_weapon_kept_at_campfire", bool(kept), (kept[0].split("] ")[-1][:160] if kept else "no weapon at the second fight's campfire"))
    else:
        # item 30: straight in from the card (no campfire): the weapon is in the hands by the fight's reveal
        body = [l.split("] ", 1)[-1] for l in lines]
        rel = next((i for i, b in enumerate(body) if "fight flow: straight in - the fight is revealed" in b), len(body))
        held = [b for b in body[:rel] if b.startswith("CombatVR: holding")]
        result("fight_weapon_at_reveal", bool(held) and rel < len(body), "; ".join(h[:70] for h in held[:2]) or "no weapon held before the reveal")
    if "Level_Play" not in state():
        result("death", False, "never in the fight (%s)" % state())
        return
    time.sleep(2)
    n_die = nlines()
    r = send("die")
    shot_done, died_state, dealer_shot = False, None, False
    dim = None
    t_map = None
    t0 = time.time()
    while time.time() - t0 < 120:
        time.sleep(0.5)
        s = state()
        if died_state is None and "Level_Play" not in s:
            died_state = s.strip()
            time.sleep(2.5)   # the death text comes up (and the game's red bleed overlay with it)
            sim("head 0 1.75 0 0 0 0")
            send("rtshot PT_death_rt")   # the 2D screen first: the stray damage number faded within ~3 s
            shot("death")
            dim = rt_edge_cover("PT_death_rt")
            shot_done = True
        if died_state is not None and not dealer_shot:
            send("state")   # a fresh status.json
            if st().get("deathText") == "dealer":
                # back at the table with the death text still up: it stands above the Dealer, not on his face
                sim("head 0 1.75 0 0 -15 0")
                time.sleep(0.3)
                shot("after_death_text")
                sim("head 0 1.75 0 0 0 0")
                dealer_shot = True
        if "Hub" in s or "ChallengeMap" in s:
            break
        if died_state is not None and "GameState_Map" in s:
            # back on the dungeon map (2026-10-13: the game came back here with the death text still above the Dealer, and the
            # check ran before it went): wait until its summary is logged (it goes at the end-of-run screens), at most 45 s -
            # never A here (on the map A walks the hero on); a Continue plate is touched by its label
            if t_map is None:
                t_map = time.time()
                a_presses = 0
            if any("the death text at the table:" in l for l in gamelog_since(n_die)[0]) or time.time() - t_map > 60:
                break
            s1 = st()
            if "nav:Continue" in s1.get("boards", ""):
                send("ptouch nav:Continue")
            elif s1.get("deathText") == "dealer" and time.time() - t_map > 8 + 6 * a_presses and a_presses < 4:
                # the death text above the Dealer waits for the player (13:00 RC tour: it stood 57 s, nothing pressed):
                # A, as a player continues (the run is over: the map takes no move then)
                send("press A 6")
                a_presses += 1
            time.sleep(1.0)
            continue
        if died_state is not None:
            send("press A 6")
            time.sleep(1.0)
    # (user 2026-10-11: "You Died" world-fixed, without a dim box) the 2D screen's edges stay clear while the death text shows
    if dim is None:
        result("death_no_dim_box", False, "no capture of the 2D screen during the death text")
    else:
        # (and nothing else stray at the edges: the killing blow's damage number stayed in a corner, a red "7")
        result("death_no_dim_box", dim[0] < 0.02 and dim[2] <= 40, "the 2D screen's edges during the death text: %.1f %% covered, mean alpha %.2f, %d opaque "
               "pixels (the dim box was 100 %%, 0.57; the stray '7' ~150 px)" % (dim[0] * 100, dim[1], dim[2]))
    time.sleep(4)
    p = send("place")
    sim("head 0 1.75 0 0 -15 0")
    time.sleep(0.6)
    shot("after_death")
    sim("head 0 1.75 0 0 0 0")
    # back on the dungeon map at the card table, or (the run lost) on the challenge map at the side table; never outside
    result("death_back_at_table", died_state is not None and any(k in state() for k in ("GameState_Map", "ChallengeMap", "Hub")) and "Outside" not in p
           and ("Caravan station" in p or "Hub station" in p),
           "died (%s); now %s; %s" % (died_state, state(), p[:120]))
    # the death text back at the card table (it stays ~2 s into GameState_Post): above the Dealer, never over his face on
    # the big screen (the mod logs a summary when it goes)
    lines, _ = gamelog_since(n_die)
    summ = [l.split("] ")[-1] for l in lines if "the death text at the table:" in l]
    m = re.search(r"(\d+) frames above the Dealer.*?(\d+) frames on the big screen", summ[-1]) if summ else None
    if not summ:
        result("death_text_clear_of_dealer", "Hub station" in p, "the death text never showed at the card table (%s)" % p[:60])
    else:
        result("death_text_clear_of_dealer", bool(m) and int(m.group(2)) == 0 and int(m.group(1)) > 0, summ[-1][:160])


def rt_edge_cover(name):
    """(share, mean alpha, opaque pixels) of the 2D UI texture's outer band (12 % of each side) that something covers (alpha > 0.08):
    on the panel anything there is a dark or coloured box round the middle."""
    try:
        from PIL import Image
        import numpy as np
    except ImportError:
        return None
    path = H + "/testdata/shots/" + name + "_ui.png"
    for _ in range(10):
        if os.path.exists(path) and time.time() - os.path.getmtime(path) < 30:
            break
        time.sleep(0.3)
    a = np.array(Image.open(path))[:, :, 3].astype(float) / 255.0
    h, w = a.shape
    bx, by = int(w * 0.12), int(h * 0.12)
    band = np.concatenate([a[:by, :].ravel(), a[-by:, :].ravel(), a[by:-by, :bx].ravel(), a[by:-by, -bx:].ravel()])
    return float((band > 0.08).mean()), float(band.mean()), int((band > 0.5).sum())


def boards_now():
    send("state")   # a fresh status.json
    return st().get("boards", "")


def wait_boards(pred, timeout):
    t0 = time.time()
    while time.time() - t0 < timeout:
        b = boards_now()
        if pred(b):
            return b
        time.sleep(0.4)
    return None


def wait_plates_still(timeout, still=1.0):
    """The board's plates unchanged for `still` seconds (their labels and count); the board then, else None."""
    t0 = time.time()
    last, since = None, time.time()
    while time.time() - t0 < timeout:
        b = boards_now()
        m = re.search(r"plates \d+(?: \[[^\]]*\])*", b)
        key = m.group(0) if m else b[:200]
        if key != last:
            last, since = key, time.time()
        elif time.time() - since >= still:
            return b
        time.sleep(0.25)
    return None


def stage_menu():
    """Menus by touch: the pause menu on the menu board within reach; Settings, Back and Resume by a real-hand poke."""
    sim(REST + "; head 0 1.75 0 0 0 0")
    send("press START 6")
    b = wait_boards(lambda b: "menu board pause" in b and "Resume" in b, 10)
    g = send("boards")
    bc = re.search(r"board centre .*?: (-?[\d.]+) ahead (-?[\d.]+) below", g)
    result("menu_board_within_reach", b is not None and bc is not None and 0.35 <= float(bc.group(1)) <= 0.6,
           "pause menu on the menu board %s" % (bc.group(0) if bc else (b or boards_now())[:120]))
    if b is None:
        return
    sim("head 0 1.75 0 0 -15 0")
    time.sleep(0.4)
    shot("menu_pause")
    sim("head 0 1.75 0 0 0 0")
    poke_plate("Settings")
    b = wait_boards(lambda b: re.search(r"\[\d+ Back\]", b), 8)
    # (the settings page's first opening shows its plates for every tab - 17, Back as plate 16 - for a few seconds, then its
    # 6; a poke aimed at the first layout's Back met nothing once the board had changed: the full tours of 2026-10-18 failed
    # here, slower frames making it last longer. A player presses what is there: the plates are poked once they stand still)
    b = wait_plates_still(8) or b
    if b:
        sim("head 0 1.75 0 0 -15 0")
        time.sleep(0.4)
        shot("menu_settings")
        sim("head 0 1.75 0 0 0 0")
    poke_plate("Back")
    b2 = wait_boards(lambda b: re.search(r"\[\d+ Resume\]", b), 8)
    poke_plate("Resume")
    b3 = wait_boards(lambda b: "menu board" not in b, 8)
    result("menu_by_touch", b is not None and b2 is not None and b3 is not None,
           "Settings %s, Back %s, Resume %s" % ("opened" if b else "no", "back" if b2 else "no", "closed the menu" if b3 else "no"))
    sim(REST)


def devkeys_now():
    send("state")   # a fresh status.json
    s = st()
    return s.get("bugMark", ""), s.get("devJump", ""), s.get("devChord", "")


def stage_devkeys():
    """The dev gestures on B + Y (user 2026-10-12 evening: both thumbsticks open WiVRn's own window, so the old
    both-sticks bug mark could not be used). In the pause menu: B + Y held 0.7 s marks a bug and the game never sees the B
    (the pause menu stays open); B + Y held 1.7 s opens the jump menu (no mark), B closes it and the game still sees no B;
    then B alone reaches the game (late by at most 0.1 s): the pause menu closes."""
    sim(REST + "; head 0 1.75 0 0 0 0")
    send("press START 6")
    b = wait_boards(lambda b: "menu board pause" in b and "Resume" in b, 10)
    if b is None:
        result("devkeys", False, "the pause menu did not open: %s" % boards_now()[:120])
        return
    mk = lambda: int((re.search(r"(\d+) marks", devkeys_now()[0]) or re.search("(0)", "0")).group(1))
    m0 = mk()
    sim("hold r b 1; hold l b 1; wait 700; hold r b 0; hold l b 0")
    time.sleep(1.5)
    m1 = mk()
    open1 = "menu board pause" in boards_now()
    result("bugmark_chord", m1 == m0 + 1 and open1, "B + Y 0.7 s: marks %d -> %d, the pause menu %s; %s" %
           (m0, m1, "still open (no B reached the game)" if open1 else "CLOSED (the game saw B)", devkeys_now()[2]))
    sim("hold r b 1; hold l b 1; wait 1700")
    time.sleep(0.3)
    j1 = devkeys_now()[1]
    time.sleep(0.8)
    vrset_was = vrset_now()
    sim("hold r b 0; hold l b 0; wait 400; btn r b 150")
    time.sleep(1.0)
    j2 = devkeys_now()[1]
    m2 = mk()
    open2 = "menu board pause" in boards_now()
    # with the Scenarios dev menu on (the user's settings), B + Y opens the VR settings page on its Dev tab instead of the
    # jump card (round 5 item 2); B closes either
    scen_on = cfg_value("Scenarios").lower() == "true"
    if scen_on:
        dev_opened = "page Dev" in (vrset_was or "")
        result("jumpmenu_chord", dev_opened and m2 == m1 and open2,
               "B + Y 1.7 s (Scenarios on): VR settings '%s', marks %d -> %d, after B the pause menu %s" %
               ((vrset_was or "-")[:30], m1, m2, "still open" if open2 else "CLOSED (the game saw B)"))
    else:
        result("jumpmenu_chord", j1.startswith("open") and j2.startswith("closed") and m2 == m1 and open2,
               "B + Y 1.7 s: jump menu '%s', after B '%s', marks %d -> %d, the pause menu %s" %
               (j1[:20], j2[:20], m1, m2, "still open" if open2 else "CLOSED (the game saw B)"))
    sim("btn r b 150")
    gone = wait_boards(lambda b: "menu board pause" not in b, 6)
    result("b_alone_reaches_game", gone is not None, "B alone %s; %s" % ("closed the pause menu" if gone else "did nothing", devkeys_now()[2]))
    if gone is None:
        send("press START 6")


def scen_now():
    send("state")
    return st().get("scenarios", "")


def stage_scenarios():
    """The Scenarios menu (Dev.Scenarios, user 2026-10-12 ~22:10): B + Y held opens the pause menu on its Dev tab; a scenario
    picked by a real-hand poke runs in the dev save (the game resets to the title with it); a fight from the catalog; Leave
    brings the test's own save back at the title - byte-identical (sha1), and the user's real save too (savecheck)."""
    import hashlib
    prof = os.path.join(hof2env.TESTDATA, "remote", "player_data.profile")
    send("cfg Scenarios true")
    h0 = file_sha1(prof)
    sim(REST + "; head 0 1.75 0 0 0 0")
    sim("hold r b 1; hold l b 1; wait 1700; hold r b 0; hold l b 0")
    v = wait_vrset(lambda v: v.startswith("page Dev"), 8)
    result("scenarios_menu_by_chord", v is not None, "B + Y 1.7 s: %s" % (v or vrset_now())[:90])
    if v is None:
        return
    sim("head 0 1.75 0 0 -12 0")
    time.sleep(0.5)
    shot("scenarios_menu")
    sim("head 0 1.75 0 0 0 0")
    poke_vrset("Dev/Table")
    time.sleep(1.0)
    poke_vrset("Table/Gain_life")
    t0 = time.time()
    done = None
    while time.time() - t0 < 120:
        sc = scen_now()
        if "Gain life: " in sc and "idle" in sc:
            done = sc
            break
        time.sleep(2)
    in_dev = done is not None and "dev Dev" in done
    result("scenario_by_touch_in_dev_save", in_dev and "Gain life: done" in done,
           "after %.0f s: %s" % (time.time() - t0, (done or scen_now())[:160]))
    time.sleep(1.5)
    shot("scenario_gain_life")
    # a fight from the catalog, through the campfire to Level_Play
    send("scen run Bandits")   # its @confirm presses the encounter card's Continue; the campfire's gate goes by itself in tests
    s, t0 = None, time.time()
    while time.time() - t0 < 120:
        cur = state()
        if "Level_Play" in cur:
            s = cur
            break
        if "Level_In" in cur and "waiting" not in send("gate", 4):
            send("press A 6")   # the game's enemy showcase waits for A
        time.sleep(2)
    result("scenario_fight", s is not None, "Bandits from the catalog: %s" % (s or state()))
    # Leave: back to the test's own save at the title
    send("scen leave")
    s = wait_state(["StartScreen"], 60)
    time.sleep(4)
    h1 = file_sha1(prof)
    sc = scen_now()
    sv = subprocess.run(["bash", H + "/scripts/savecheck.sh"], capture_output=True, text=True)
    result("scenarios_leave_save_untouched", s is not None and h0 == h1 and "dev Real" in sc and sv.returncode == 0,
           "title %s; the test's own save sha1 %s -> %s; %s; %s" % ("yes" if s else "no", h0, h1, sc.split("|")[4].strip()[:40] if sc.count("|") > 4 else sc[:60],
                                                            (sv.stdout + sv.stderr).strip()))


def vrset_now():
    send("state")   # a fresh status.json
    return st().get("vrsettings", "")


def wait_vrset(pred, timeout):
    t0 = time.time()
    while time.time() - t0 < timeout:
        v = vrset_now()
        if pred(v):
            return v
        time.sleep(0.4)
    return None


def poke_vrset(key):
    """The simulated hand pokes the VR settings page's button `key` ("Back", "Hands", "Combat/Classic", "Walk_speed/+")."""
    return poke_pose("vrset pose %s 6 r" % key)


def cfg_value(key):
    r = send("cfg " + key)
    return r.split("=", 1)[1].split()[0] if "=" in r else r


def file_sha1(path):
    import hashlib
    try:
        with open(path, "rb") as f:
            return hashlib.sha1(f.read()).hexdigest()[:10]
    except OSError:
        return "missing"


def stage_vrsettings():
    """The VR settings page (12 October): the "VR Settings" button under the pause menu's list on the menu board opens a page
    of the mod's settings within reach; all by real-hand pokes: a choice and a number change live (a test run never writes
    the settings file), the tabs, Back to the pause menu, Resume."""
    h0 = file_sha1(GAMECFG)
    sim(REST + "; head 0 1.75 0 0 0 0")
    send("press START 6")
    b = wait_boards(lambda b: "menu board pause" in b and "Resume" in b, 10)
    if not b:
        # (14:24 RC tour: the pause menu was still open from the stage before - the first START closed it)
        send("press START 6")
        b = wait_boards(lambda b: "menu board pause" in b and "Resume" in b, 10)
    v = wait_vrset(lambda v: v.startswith("entry"), 8) if b else None
    g = send("vrset geo VR_Settings") if v else "-"
    result("vrsettings_button_on_pause_board", v is not None, "the pause menu board %s; %s" % ("shown" if b else "missing", g))
    if v is None:
        send("press START 6")
        return
    poke_vrset("VR_Settings")
    v = wait_vrset(lambda v: v.startswith("page"), 8)
    if v and not v.startswith("page Fights"):
        # the page opens on the tab used last (the Dev tab after the devkeys stage's B + Y): its Fights tab
        poke_vrset("Fights")
    v = wait_vrset(lambda v: v.startswith("page Fights"), 8)
    geo = send("vrset geo Combat/Classic") if v else "-"
    m = re.search(r"\| (-?[\d.]+) ahead (-?[\d.]+) below", geo)
    reach = m is not None and 0.35 <= float(m.group(1)) <= 0.65 and 0.0 <= float(m.group(2)) <= 0.45
    result("vrsettings_page_by_touch", v is not None and reach, "page %s; a row's button %s; %s" % (
        (v or vrset_now())[:60], m.group(0) if m else geo[:80], send("vrset size")))
    if v is None:
        send("press START 6")
        return
    sim("head 0 1.75 0 0 -20 0")
    time.sleep(0.5)
    shot("vrsettings_fights")
    fonts_check("vrsettings")
    sim("head 0 1.75 0 0 0 0")
    # live changes by touch: a choice (Combat: Classic, and back) and a number (Walk speed + and -)
    poke_vrset("Combat/Classic")
    m1, phys1 = cfg_value("Mode"), send("pcombat?")
    poke_vrset("Combat/Physical")
    m2 = cfg_value("Mode")
    w0 = cfg_value("WalkSpeed")
    poke_vrset("Walk_speed/+")
    w1 = cfg_value("WalkSpeed")
    poke_vrset("Walk_speed/-")
    w2 = cfg_value("WalkSpeed")
    lit = vrset_now()
    ok = m1 == "Classic" and m2 == "Physical" and abs(float(w1 or 0) - float(w0 or 0) - 0.2) < 0.01 and w2 == w0 and "Combat/Physical*" in lit
    result("vrsettings_change_live", ok, "Combat -> %s (%s) -> %s; Walk speed %s -> %s -> %s; lit: %s" % (
        m1, phys1[:40], m2, w0, w1, w2, "Combat/Physical*" in lit))
    # the tabs
    seen = []
    for t in ("Hands", "Turning", "Comfort", "Voice"):
        poke_vrset(t)
        v = wait_vrset(lambda v, t=t: v.startswith("page " + t), 6)
        if v:
            seen.append(t)
            sim("head 0 1.75 0 0 -20 0")
            time.sleep(0.4)
            shot("vrsettings_" + t.lower())
            sim("head 0 1.75 0 0 0 0")
    result("vrsettings_tabs", len(seen) == 4, "tabs opened by touch: %s" % ", ".join(seen))
    # the Voice tab (the narration): a live change by touch, for this run only
    n1 = send("narr")
    poke_vrset("Narration/On")
    n2 = send("narr")
    poke_vrset("Narration/Off")
    n3 = send("narr")
    result("vrsettings_voice_tab", "enabled False" in n1 and "enabled True" in n2 and "enabled False" in n3,
           "Narration Off -> On -> Off: %s / %s / %s" % tuple(re.search(r"enabled \w+", x).group(0) if re.search(r"enabled \w+", x) else "-" for x in (n1, n2, n3)))
    # the Voice tab's 3D sound row (VR/Hrtf.cs, docs/hrtf-plan.md): live by touch, for this run only
    a0 = cfg_value("Hrtf")
    poke_vrset("3D_sound_(HRTF)/On")
    a1, s1 = cfg_value("Hrtf"), send("hrtf")
    poke_vrset("3D_sound_(HRTF)/Off")
    a2, s2 = cfg_value("Hrtf"), send("hrtf")
    if a0 == "true":
        poke_vrset("3D_sound_(HRTF)/On")
    result("vrsettings_hrtf_row", a1 == "true" and a2 == "false" and not s2.startswith("ON"),
           "3D sound On -> %s (%s) -> Off -> %s (%s)" % (a1, s1[:40], a2, s2[:40]))
    # Back to the pause menu, then Resume
    poke_vrset("Back")
    b2 = wait_boards(lambda b: "menu board pause" in b and "Resume" in b, 8)
    poke_plate("Resume") if b2 else send("press START 6")
    b3 = wait_boards(lambda b: "menu board" not in b, 8)
    v3 = vrset_now()
    h1 = file_sha1(GAMECFG)
    result("vrsettings_back_and_resume", b2 is not None and b3 is not None and v3.startswith("closed"),
           "Back -> %s, Resume -> %s, page %s" % ("the pause board" if b2 else "no", "closed the menu" if b3 else "no", v3[:20]))
    result("vrsettings_test_run_writes_nothing", h0 == h1, "settings file sha1 %s before, %s after" % (h0, h1))
    sim(REST)


def b1_touches(f):
    """The fingertip touches FightCard counted (its status: '... starts S offers O touches T buttons B ...')."""
    m = re.search(r"touches (\d+)", f or "")
    return int(m.group(1)) if m else 0


def stage_fight():
    if "GameState_Map" not in state():
        result("fight_entry", False, "not on the map (%s)" % state())
        return
    sim(REST + "; head 0 1.75 0 0 0 0")   # hands down (a stage before may have left a hand raised at a plate)
    send("gate auto 0")
    n0 = nlines()
    send("jump fight EL_Ambush_Goblins")
    for _ in range(12):
        time.sleep(3)
        s = state()
        if "Level_In" in s or "Level_Play" in s:
            break
        answer_page()   # the fight's page at the table (its option, Continue), by touch
    # B1 (user 2026-10-10, built 2026-10-13): the player stays at the table for the Dealer's call to battle, then touches the
    # glowing encounter card that comes to them (src/HoF2VR/Game/FightCard.cs; scripts/b1_test.py tests it in depth)
    b1, places = None, []
    for _ in range(50):
        send("state")
        s = st()
        f = s.get("fightCard", "")
        pl = s.get("place", "")
        if not places or places[-1] != pl:
            places.append(pl)
        if f.startswith("offered"):
            b1 = f
            break
        if not f.startswith("dealing"):
            break
        time.sleep(0.3)
    if b1 is not None or any(p.startswith("Caravan") for p in places):
        result("fight_b1_stays_at_table", b1 is not None and all(p.startswith("Caravan") for p in places),
               "places in Level_In before the touch: %s; the card: %s" % (" > ".join(places), (b1 or st().get("fightCard", ""))[:130]))
    if b1 is not None:
        time.sleep(1.2)   # its glide toward the player
        sim("head 0 1.75 0 0 -25 0")
        time.sleep(0.4)
        shot("b1_card")
        sim("head 0 1.75 0 0 0 0")
        t0 = b1_touches(b1)
        poke_pose("fightcard pose 6 r", settle=1.0)
        send("state")
        f = st().get("fightCard", "")
        # (the counter: by the time the status is read the card may be gone already - "off (the waiting space is up)")
        t1 = b1_touches(f)
        result("fight_b1_card_touched", t1 > t0 or ("fingertip" in f and not f.startswith("offered")),
               "after a real-hand poke on the card: touches %d -> %d | %s" % (t0, t1, f[:150]))
        if f.startswith("offered"):
            send("fightcard go")
    time.sleep(6)
    lines, _ = gamelog_since(n0)
    space = any("limbo: waiting space on (Plain, state GameState_Level_In)" in l for l in lines)
    touched = [l.split("] ", 1)[-1] for l in lines if "fight card: touched (" in l]
    direct = bool(touched) and "straight into the fight" in touched[-1]
    if direct:
        # item 30 (the user, 2026-10-18): the card touched once its fight had loaded (the simulator loads within its glide) -
        # straight into the fight: no campfire, the arena woken in the place change's dark (scripts/b1_test.py in depth)
        act = [l.split("] ", 1)[-1] for l in lines if "the arena activates now, in the dark" in l]
        result("fight_straight_in", not space and bool(act), "%s | waiting space: %s | %s" % (touched[-1][:100], "shown" if space else "no", act[0][:90] if act else "no activation in the dark"))
        sim("head 0 1.75 0 0 -20 0")
        time.sleep(0.5)
        shot("fight_arrival")
        sim("head 0 1.75 0 0 0 0")
    else:
        result("fight_campfire", space, "the campfire on the way into the fight: %s" % ("yes" if space else "no"))
        sim("head 0 1.75 0 0 -20 0")
        time.sleep(0.5)
        shot("space_ahead")
        sim("turn 0 42 -20 -8 500")
        time.sleep(0.4)
        shot("space_controls_card")
        # the campfire before A (user 2026-10-12: enemy noises and fight music there; a blip): the arena waits, asleep
        send("state")
        ff = st().get("fightflow", "")
        result("fight_arena_waits_for_a", ff.startswith("arena held") and "ducked True" in ff,
               "at the campfire before A: %s" % ff[:160])
        sim("head 0 1.75 0 0 0 0")
        send("gate go")
    # the fight's description (the game's showcase) stays in one place in the world, also when the head turns
    fixed, pos0, pos1 = False, None, None
    for _ in range(8):
        time.sleep(1.5)
        send("state")
        ff = st().get("fightflow", "")
        m = re.search(r"showcase fixes (\d+) panel \(([-\d., ]+)\)", ff)
        if m and int(m.group(1)) > 0 and "Level_In" in state():
            pos0 = m.group(2)
            sim("turn 0 35 0 0 600")
            time.sleep(1.0)
            send("state")
            m1 = re.search(r"panel \(([-\d., ]+)\)", st().get("fightflow", ""))
            pos1 = m1.group(1) if m1 else None
            fixed = True
            shot("fight_showcase_turned")
            sim("head 0 1.75 0 0 0 0")
            break
        if "Level_Play" in state():
            break
    if fixed:
        result("fight_description_world_fixed", pos0 == pos1, "the panel at (%s), after a 35 deg head turn at (%s)" % (pos0, pos1))
    for _ in range(4):
        time.sleep(3)
        if "Level_Play" in state():
            break
        send("press A 6")
    send("state")
    ff = st().get("fightflow", "")
    m = re.search(r"stalls >= 40 ms (\d+) \(worst (\d+) ms\)", ff)
    if direct:
        # (straight in: the long frames of the way in - from the touch until the reveal's fade-in is over - fall while the view
        # is dark, never in plain view; the tour's own eye captures stall frames by themselves: not counted)
        lines, _ = gamelog_since(n0)
        body = [l.split("] ", 1)[-1] for l in lines]
        def clk(x):
            mm = re.search(r"(\d\d):(\d\d):(\d\d\.\d+)", x)
            return int(mm.group(1)) * 3600 + int(mm.group(2)) * 60 + float(mm.group(3)) if mm else None
        it = next((i for i, b in enumerate(body) if "fight card: touched (" in b), 0)
        rel = next((b for b in body[it:] if b.startswith("fade release 'place'")), "")
        mr = re.search(r"at (\d\d:\d\d:\d\d\.\d+)", rel)
        t_end = clk(mr.group(1)) + 0.5 if mr else 1e9
        caps = [clk(b) for b in body if b.startswith("captured eyes")]
        stalls = [b for b in body[it:] if "perf stall" in b and "view " in b and clk(b) is not None and clk(b) <= t_end and
                  not any(c is not None and 0 <= clk(b) - c < 0.6 for c in caps)]
        seen = [l for l in stalls if re.search(r"view (open|space|fading 0\.[0-8])", l)]
        result("fight_way_in_no_stall", not seen and "activations" in ff, "%d frames >= 40 ms on the way in, %d of them in view: %s" % (
            len(stalls), len(seen), " / ".join(re.sub(r" \| hold.*", "", x)[:110] for x in seen[:3]) or "-"))
    else:
        result("fight_campfire_no_stall", m is not None and int(m.group(1)) == 0 and "activations" in ff,
               "frames >= 40 ms while the campfire was in view: %s" % (m.group(0) if m else ff[:120]))
    # paused in the fight: the menu board within reach, Resume by touch
    if "Level_Play" in state():
        sim(REST)
        send("press START 6")
        b = wait_boards(lambda b: "menu board pause" in b and "Resume" in b, 10)
        if b:
            time.sleep(0.5)
            shot("fight_pause")
        # the VR settings page in the fight: the enemy guard as rings (2026-10-18: Shell / Rings replaced the pips), live once the
        # fight goes on
        style0 = cfg_value("EnemyStatus")
        vs = None
        if b and wait_vrset(lambda v: v.startswith("entry"), 6):
            poke_vrset("VR_Settings")
            if wait_vrset(lambda v: v.startswith("page"), 6):
                poke_vrset("Looks")   # (the enemy health's look moved to the Looks tab, 2026-10-14)
                vs = wait_vrset(lambda v: v.startswith("page Looks"), 6)
                poke_vrset("Enemy_guard/Rings")
                vs = wait_vrset(lambda v: "Enemy_guard/Rings*".replace("_", " ") in v, 4) if vs else None
                poke_vrset("Back")
                wait_boards(lambda b: "menu board pause" in b and "Resume" in b, 8)
        poke_plate("Resume")
        b2 = wait_boards(lambda b: "menu board" not in b, 8)
        result("fight_pause_by_touch", b is not None and b2 is not None and "Level_Play" in state(),
               "pause menu on the board: %s; Resume by touch: %s; %s" % ("yes" if b else "no", "closed" if b2 else "no", state()))
        time.sleep(1.5)
        bars = send("bars?")
        result("fight_vrsettings_live", vs is not None and "style Rings" in bars.split("|")[0] and not bars.startswith("0 shown") and " 0 rings" not in bars.split("|")[0],
               "Enemy guard %s -> Rings on the VR settings page in the fight: %s; after Resume: %s" % (
                   style0, "lit" if vs else "no", bars.split("|")[0].strip()))
        sim("head 0 1.75 0 0 0 0")
        time.sleep(0.4)
        shot("fight_bars")
        send("cfg EnemyStatus " + (style0 if style0 in ("Shell", "Rings") else "Shell"))
        sim(REST)
    # the win: the fight music stops, the table's music plays until the player leaves (user 2026-10-12)
    send("killall")
    time.sleep(4)
    send("state")
    ff = st().get("fightflow", "")
    result("fight_calm_after_win", "after the win" in ff, "after the last kill: %s" % (re.search(r"music ([^|]*)", ff).group(1).strip() if "music" in ff else ff[:100]))
    n1 = nlines()
    # leave as the player does: the portal touched (the seam's win when no portal came)
    for _ in range(10):
        if "card (" in st().get("fightExit", ""):
            break
        time.sleep(1)
    how = "the portal touched"
    if "card (" in st().get("fightExit", ""):
        send("exitcard touch")
    else:
        how = "jump win (no portal)"
        send("jump win")
    ok = wait_state(["GameState_Map"], 90)
    time.sleep(3)
    lines, _ = gamelog_since(n1)
    shown = [l for l in lines if "limbo: waiting space on" in l]
    result("quick_hop_fight_table", bool(ok) and not shown, "back at the table (%s): %s; waiting space: %s" % (how, bool(ok), "shown" if shown else "no (a short fade)"))
    # the exit's long frames (the user's log: 290-340 ms) fall while the view is black or misted, never in plain view
    stalls = [l.split("] ", 1)[-1] for l in lines if "perf stall" in l and "view " in l]
    seen = [l for l in stalls if re.search(r"view (open|fading 0\.[0-8])", l)]
    worst = max((int(re.search(r"busy (\d+)", l).group(1)) for l in stalls), default=0)
    result("fight_exit_stalls_hidden", not seen, "%d frames >= 40 ms on the way back (worst %d ms), %d of them in view: %s" % (
        len(stalls), worst, len(seen), " / ".join(re.sub(r" \| hold.*", "", x)[:110] for x in seen[:3]) or "views " + ", ".join(sorted(set(re.search(r"view (\w+)", l).group(1) for l in stalls)))))


NARR_DIR = H + "/testdata/narration/clips"
NARR_WORK = "/home/bara/Projects/flat2vr/HandOfFate2/work/narration"
NARR_P0 = "308075f20466e94d"   # Forgotten Dreams, page 0 ("Townsfolk gather around a well...")


def narr(t=6):
    return send("narr", t)


def narr_num(s, pat, default=-1.0):
    m = re.search(pat, s or "")
    return float(m.group(1)) if m else default


def map_ready():
    """The dev jump's own test: it answers 'no encounter like' only when the map is ready for a jump."""
    return send("jump encounter zz_ready_probe").startswith("no encounter like")


def finish_encounter(first="", timeout=60):
    """Ends the open encounter: the plate whose label contains `first` (else the first plate, else A) until the map is
    ready again; nothing is clicked once it is."""
    t0 = time.time()
    while time.time() - t0 < timeout:
        if map_ready():
            return True
        b = st().get("boards", "")
        plates = re.findall(r"\[(\d+) ([^\]]*)\]", b)
        i = next((n for n, t in plates if first and first.lower() in t.lower()), plates[0][0] if plates else None)
        if i is None or not send("boards click " + i).startswith("clicking"):
            send("press A 6")
        time.sleep(2.0)
    return map_ready()


def stage_narration():
    """The narration (12 October; switched on for this run only, placeholder clips from scripts/narr_testclips.py): the
    hash and motion parity with the pipeline, a page's clip plays from the Dealer (3D, his mixer group, his lip flap),
    his head nods within the design's limits, the same page is not read twice, a choice cuts the clip, his own lines are
    re-voiced inside the game's playback (subtitle and gestures kept) or muted, and switching it off restores them."""
    if not os.path.isdir(NARR_DIR + "/Normal"):
        result("narration_clips", False, "no placeholder clips in %s (run scripts/narr_testclips.py)" % NARR_DIR)
        return
    sim(REST + "; head 0 1.75 0 0 0 0")
    send("narr reset")
    r = send("narr test " + NARR_DIR)
    hs = send("narr selftest " + NARR_WORK + "/hash_vectors.tsv", 20)
    result("narration_hash_parity", "307/307 match" in hs, hs[:200] + " | " + r[:120])
    # the whole corpus (21,212 texts; scripts/narr_testclips.py writes the file) when it is there
    if os.path.isfile(H + "/testdata/narration/hash_all.tsv"):
        ha = send("narr selftest " + H + "/testdata/narration/hash_all.tsv", 60)
        m = re.search(r"(\d+)/(\d+) match", ha)
        result("narration_hash_parity_corpus", m is not None and m.group(1) == m.group(2) and int(m.group(2)) > 20000, ha[:200])
    mt = send("narr motiontest %s/Normal/%s.motion %s/Normal/%s.curves.tsv" % (NARR_DIR, NARR_P0, NARR_DIR, NARR_P0), 20)
    result("narration_motion_parity", "PASS" in mt, mt)
    # the encounter: the Dealer's activation line of the card (re-voiced placeholder) first, then page 0's clip
    n0 = nlines()
    jumped = jump_when_ready("encounter Card_EN_ForgottenDreams")
    s, t0 = "", time.time()
    while time.time() - t0 < 30:
        s = narr()
        if s.startswith("Playing " + NARR_P0):
            break
        time.sleep(0.4)
    playing = s.startswith("Playing " + NARR_P0)
    lines, _ = gamelog_since(n0)
    waits = [l.split("] ")[-1] for l in lines if "narration: page " in l or "narration: plays" in l or "DealerLines:" in l]
    result("narration_page_plays", playing, "jump %s; after %.1f s: %s | %s" % (jumped, time.time() - t0, s[:140], " / ".join(waits[-4:])[:400]))
    if not playing:
        return
    sim("head 0 1.75 0 0 -8 0")
    time.sleep(3.0)
    s = narr()
    head = narr_num(s, r"from head ([\d.]+)")
    blend = narr_num(s, r"blend ([\d.]+)")
    grp = re.search(r"group (\S+)", s)
    result("narration_from_the_dealer", 0 <= head <= 0.3 and blend > 0.99 and grp is not None and grp.group(1) == "Dealer",
           "source %.3f u from his head bone, blend %.1f, mixer group %s | %s" % (head, blend, grp.group(1) if grp else "-", send("voice")[:160]))
    rms, flap = narr_num(s, r"rms [\d.]+ max ([\d.]+)"), narr_num(s, r"flap [\d.]+ max ([\d.]+)")
    frames, vrms = narr_num(s, r"frames (\d+) voice"), narr_num(s, r"voice rms max ([\d.]+)")
    # his VoiceFlap reads the reading's source while it is audible; the loudness itself only exists with sound on (a silent
    # test run sets AudioListener.volume 0 and FMOD then gives no output data - his own lines read 0 there too)
    result("narration_lip_flap", frames > 0 and (rms <= 0.0 or flap > 0.0),
           "his VoiceFlap read the reading's source in %d frames; output rms max %.3f (his own voice's %.3f: %s), flap max %.2f" % (
               frames, rms, vrms, "silent test run" if rms <= 0.0 and vrms <= 0.0 else "sound on", flap))
    shot("narration_dealer")
    dm = send("dealer motion")
    mm = re.search(r"max ([\d.]+)/([\d.]+)/([\d.]+)", dm)
    mp, mr, my = (float(mm.group(1)), float(mm.group(2)), float(mm.group(3))) if mm else (-1.0, -1.0, -1.0)
    result("narration_head_motion", 0.3 <= mp <= 4.0 and mr <= 3.0 and my <= 2.0 and "src narration" in dm,
           "max pitch %.2f roll %.2f yaw %.2f deg (limits 4 / 3 / 2) | %s" % (mp, mr, my, dm[:300]))
    bake = send("dealer rig test")
    time.sleep(0.5)
    bk = send("dealer motion")
    result("narration_head_bone_moves_mesh", "bake MOVES" in bk, re.sub(r".*\| bake ", "", bk)[:300])
    # the same page again (the panel shows it once more) is not read twice
    t_before = narr_num(narr(), r" t ([\d.]+)/")
    rp = send("narr repage")
    s = narr()
    result("narration_not_twice", "not read twice" in rp and s.startswith("Playing " + NARR_P0) and narr_num(s, r" t ([\d.]+)/") >= t_before,
           "%s; then %s" % (rp, s[:60]))
    # a choice cuts the reading (0.12 s fade)
    s0 = narr()
    send("boards click 1")
    time.sleep(1.5)
    s = narr()
    ms = narr_num(s, r"last stop 'a choice' (-?\d+) ms")
    # a 0.12 s fade; the simulator draws 20-60 frames a second, so the stop lands up to one frame later
    result("narration_stops_on_choice", 0 <= ms <= 200, "before: %s | after the choice: %s" % (s0[:50], re.sub(r" \| rms.*", "", s)[:220]))
    # his own line while she reads: it is re-voiced (our clip in the game's line, same subtitle, the gestures kept) and the
    # reading pauses for it, then resumes
    for _ in range(20):
        if narr().startswith("Playing"):
            break
        time.sleep(0.25)
    subs0 = send("narr subs")
    send("narr subs 1")
    vp = send("narr vo play VO_DYN_PLAYDICE_1")
    seen_line, seen_motion = "", ""
    for _ in range(12):
        time.sleep(0.25)
        v = send("narr vo")
        if "playing hof2vr:vo:VO_DYN_PLAYDICE_1" in v:
            seen_line = v
            seen_motion = send("dealer motion")
            break
    info = send("narr vo info VO_DYN_PLAYDICE_1")
    m = re.search(r"gestures (\d+)/(\d+)", info)
    time.sleep(1.0)
    scales = narr_num(send("narr vo"), r"subtitle scales (\d+)")
    result("narration_his_line_revoiced", bool(seen_line) and m is not None and m.group(1) == m.group(2) and scales >= 1,
           "%s | %s | %s | subtitle timings stretched %d | %s" % (vp[:80], seen_line[:120] or send("narr vo")[:160], info[:200], scales, seen_motion[:60]))
    time.sleep(3.5)
    s = narr()
    result("narration_pauses_for_his_line", narr_num(s, r"pauses (\d+)") >= 1 and narr_num(s, r"resumes (\d+)") >= 1, s[:200])
    # a line without a clip is muted: a silent clip as long as the original, the subtitle stays
    vp = send("narr vo play VO_DYN_LOSEDICE_1")
    seen = ""
    for _ in range(12):
        time.sleep(0.25)
        v = send("narr vo")
        if "playing hof2vr_silent_" in v:
            seen = v
            break
    result("narration_line_without_clip_muted", bool(seen), "%s | %s" % (vp[:80], seen[:160] or send("narr vo")[:200]))
    if "off" in subs0:
        send("narr subs 0")
    # a line whose clip is named by its transcript's hash (not by its name); then, with no clip folder at all, Replace
    # leaves his lines as they are (only Mute silences them)
    def line_now(name, want, secs=4.0):
        t0, v = time.time(), ""
        while time.time() - t0 < secs:
            time.sleep(0.25)
            v = send("narr vo")
            m = re.search(r"\| voice playing (\S+)", v)
            if m and want(m.group(1)):
                return m.group(1)
        return ""
    time.sleep(2.0)
    send("narr vo play VO_DYN_WINDICE_1")
    by_hash = line_now("VO_DYN_WINDICE_1", lambda c: c == "hof2vr:vo:VO_DYN_WINDICE_1")
    result("narration_line_by_transcript_hash", bool(by_hash), "VO_DYN_WINDICE_1 (only 997c952b25d5690d.ogg, its transcript's hash, exists): %s" % (
        by_hash or send("narr vo")[:200]))
    time.sleep(2.5)
    send("narr set ClipDir /nonexistent/hof2vr-no-clips")
    send("narr vo play VO_DYN_WINDICE_1")
    orig = line_now("VO_DYN_WINDICE_1", lambda c: not c.startswith("hof2vr"))
    send("narr set ClipDir " + NARR_DIR)
    result("narration_no_clip_folder_keeps_his_lines", bool(orig), "Replace without any clip folder: the game plays '%s' | %s" % (
        orig or "-", send("narr vo")[:160]))
    time.sleep(2.0)
    time.sleep(2.5)
    sim("head 0 1.75 0 0 -8 0")
    time.sleep(0.5)
    shot("narration_next_page")
    # the pace: the same text's clip from the Fast folder is shorter (158 -> 180 words a minute)
    lens = {}
    for pace in ("Normal", "Fast"):
        send("narr set Pace " + pace)
        send("narr say 9451a19fddef81e0")
        for _ in range(20):
            time.sleep(0.25)
            s = narr()
            if s.startswith("Playing 9451a19fddef81e0"):
                lens[pace] = narr_num(s, r" t [\d.]+/([\d.]+)")
                break
    send("narr set Pace Normal")
    send("narr stop")
    k = lens.get("Fast", 0) / lens["Normal"] if lens.get("Normal") else 0
    result("narration_pace", abs(k - 158.0 / 180.0) < 0.03, "page 1's clip: Normal %.2f s, Fast %.2f s (ratio %.3f, want %.3f)" % (
        lens.get("Normal", 0), lens.get("Fast", 0), k, 158.0 / 180.0))
    # finish the encounter (the first choice until the map is ready again), still reading
    done1 = finish_encounter()
    # a page without a clip (the shop has none here): silent, its hash logged once - also when it is opened again
    n0, m0 = nlines(), narr_num(narr(), r"misses (\d+)")
    jumped = jump_when_ready("encounter Card_EN_Shoppe")
    m1, t1 = m0, time.time()
    while time.time() - t1 < 40 and m1 <= m0:
        time.sleep(1)
        m1 = narr_num(narr(), r"misses (\d+)")
    # the same page left and opened again (as after a choice): a second miss, still one log line
    rp = send("narr repage again")
    m2 = narr_num(narr(), r"misses (\d+)")
    logged = []
    for _ in range(12):    # BepInEx writes its log file a moment later
        time.sleep(0.5)
        lines, _ = gamelog_since(n0)
        logged = [l.split("] ")[-1] for l in lines if "narration: no clip for" in l]
        if logged:
            time.sleep(1.0)
            lines, _ = gamelog_since(n0)
            logged = [l.split("] ")[-1] for l in lines if "narration: no clip for" in l]
            break
    hashes = [re.search(r"no clip for (\w+)", l).group(1) for l in logged]
    result("narration_missing_clip_silent_logged_once", m1 > m0 and m2 > m1 and len(hashes) == len(set(hashes)) and len(hashes) >= 1 and not narr().startswith("Playing"),
           "the first encounter ended: %s; jump %s; misses %d -> %d -> %d (opened again: %s); log lines: %s" % (
               done1, jumped, m0, m1, m2, rp, " / ".join(logged)[:300]))
    done2 = finish_encounter("leave")
    if not done2:
        result("narration_back_to_the_map", False, "the map is not ready after the shop: %s" % send("jump encounter zz_ready_probe")[:120])
    # off: his lines are the originals again
    send("narr set Enabled false")
    time.sleep(0.6)
    v = send("narr vo")
    result("narration_off_restores_his_lines", "in memory 0" in v and narr_num(v, r"restores (\d+)") >= 1, v[:200])
    sim(REST)
    time.sleep(2)


def main():
    a = sys.argv[1:]
    snap = a[a.index("--snapshot") + 1] if "--snapshot" in a else "magician_mid"
    SEATED[0] = "--seated" in a
    if SEATED[0]:
        os.environ["HOF2VR_SEATED"] = "1"
    open(LOG, "a").close()
    log("==== playtest tour (snapshot %s)%s" % (snap, " SEATED (eyes 1.20 m)" if SEATED[0] else ""))
    if "--skip-start" not in a:
        subprocess.run(["python3", H + "/scripts/session.py", "start", "--snapshot", snap], capture_output=True, text=True, timeout=4200)
    sim("seat %s" % ("0.55" if SEATED[0] else "0"))
    # (the reply starts with the posture - "standing | ... seated eyes - ..." also holds the word: a tour after a standing run
    # stayed standing, 2026-10-18: no seated summary, the controls under the table top)
    if SEATED[0]:
        # (chosen again every time: the seated eyes are taken from the head when Seated is chosen - or, when the setting is
        # already Seated at boot, at the run's first recentre, with the session's 1.60 m head lowered by the seat to 1.05 m:
        # the tables then 0.15 m too low for the tour's own seated eyes, 1.75 - 0.55 = 1.20 m - "in the lap", 2026-10-18)
        # (Seated chosen again while already seated changes nothing: Standing first)
        if send("posture").strip().startswith("seated"):
            send("posture standing")
            time.sleep(2.5)
        sim("head 0 1.75 0 0 0 0")
        time.sleep(0.5)
        send("posture seated")
        time.sleep(2.5)
    only = a[a.index("--only") + 1].split(",") if "--only" in a else None

    def want(x):
        return only is None or x in only
    if want("title"):
        stage_title()
    # (--only with a session already at the dungeon map - e.g. --skip-start after session.py start --to table: the stages run
    # from there, the title's stages skipped)
    already = only is not None and not want("table") and "GameState_Map" in state()
    at_table = True if already else stage_table() if (want("table") or only is not None) else False
    if at_table:
        # the narration needs the dungeon map (encounter jumps): after the shop, before the fight and the death
        for name, fn in (("touch", stage_touch), ("menu", stage_menu), ("devkeys", stage_devkeys), ("vrsettings", stage_vrsettings), ("chance", stage_chance), ("shop", stage_shop), ("equip", stage_equip),
                         ("narration", stage_narration), ("fight", stage_fight), ("death", stage_death), ("scenarios", stage_scenarios)):
            if want(name):
                try:
                    fn()
                except Exception as e:  # noqa: BLE001
                    result(name, False, "error: %s" % e)
    # nothing at the Dealer's table came too close during the whole tour (round 5 item 5: the proximity watchdog, Diag/
    # Proximity.cs - within 0.45 m of the eyes or into the body's 0.22 m cylinder; the touch rows are listed, not counted)
    send("state")
    pr = st().get("proximity", "")
    m = re.search(r"violations (\d+) \(first frame (\d+)\) \| closest (.*)", pr)
    if m:
        send("prox list")
        with open(GAMELOG, errors="replace") as f:
            plines = f.read().splitlines()
        bad = []
        for i in range(len(plines) - 1, -1, -1):
            if "] proximity: " in plines[i]:
                bad = [l.strip() for l in plines[i + 1:i + 41] if l.startswith("  ") and l.strip().startswith(("BAD", "FIRST"))]
                break
        result("table_nothing_too_close", int(m.group(1)) == 0, "violations %s (first frame %s); closest %s%s" % (
            m.group(1), m.group(2), m.group(3)[:120], (" | " + " / ".join(b[:110] for b in bad[:3])) if bad else ""))
    # every screen of the tour could be left (or continued) by touch, not only with a controller button (user 2026-10-12:
    # "Several menus didn't have ways to back out of them", "no option to continue except pressing A")
    with open(GAMELOG, errors="replace") as f:
        notouch = [l.split("] ", 1)[-1].strip() for l in f if "and no touch plates" in l]
    result("every_screen_touchable", not notouch, "; ".join(notouch)[:300] or "no screen without touch plates")
    with open(GAMELOG, errors="replace") as f:
        exc = sum(1 for l in f if "Exception" in l)
    # the Unity player log too (the game's own exceptions land there, not in BepInEx's log)
    plogs = sorted((f for f in os.listdir(H + "/logs") if f.startswith("player-") and f.endswith(".log")), key=lambda f: os.path.getmtime(H + "/logs/" + f))
    pexc = 0
    if plogs:
        with open(H + "/logs/" + plogs[-1], errors="replace") as f:
            pexc = sum(1 for l in f if "Exception:" in l)
    result("no_exceptions", exc == 0 and pexc == 0, "%d exception lines in the mod's log, %d in the game's player log (%s)" % (exc, pexc, plogs[-1] if plogs else "-"))
    send("state")
    g = st().get("grips", "")
    m = re.search(r"pointing (\d+) page (\d+)", g)
    result("touch_with_grip_held", HABIT["done"] > 0 and m is not None and int(m.group(1)) >= HABIT["done"],
           "%d of the tour's %d pokes made with the grip squeezed and the index lifted (the user's habit): %s" % (HABIT["done"], HABIT["n"], g))
    if SEATED[0]:
        # every touch of the tour within a seated player's reach (ReachProbe.SeatedRate: OK or LEAN, never FAR or in the lap)
        bad = [(w, r) for w, r in REACH if not r.startswith(("OK", "LEAN")) or "LAP" in r.split()[0]]
        lean = sum(1 for _, r in REACH if r.startswith("LEAN"))
        result("seated_touch_within_reach", REACH and not bad, "%d touches, %d OK, %d with a lean, %d out of reach or in the lap%s" % (
            len(REACH), len(REACH) - lean - len(bad), lean, len(bad), (": " + " / ".join("%s -> %s" % (w, r[:70]) for w, r in bad[:4])) if bad else ""))
        for w, r in REACH:
            log("      reach %-40s %s" % (w[:40], r[:110]))
        sim("seat 0")
    ok = sum(1 for _, v in RESULTS if v)
    log("==== %d/%d PASS; shots to review: %s" % (ok, len(RESULTS), " ".join(SHOTS)))


if __name__ == "__main__":
    main()
