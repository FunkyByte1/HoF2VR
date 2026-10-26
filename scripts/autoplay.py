#!/usr/bin/env python3
"""Keep playing with simulated VR input: fights with motion gestures, table phases with A / laser,
dice grabbed and thrown by hand (every third roll with the trigger draw instead), map moves by a
fingertip poke on the tile (the laser if that fails), a `reach` probe at the first visit of each menu.

  autoplay.py <minutes> [--shots]

Logs every game-state change to logs/autoplay.log. If the state and the clickable targets stop
changing for 90 s it saves screenshots (testdata/shots/stuck_*) and tries B, then A.
"""
import math
import os
import random
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cmd  # noqa: E402

import hof2env  # noqa: E402  (this worktree's test environment, docs/two-environments.md)

H = hof2env.H
LOG = H + "/logs/autoplay.log"
dice_games = 0


def sim(c):
    subprocess.run(["python3", H + "/scripts/xrsim.py", c], check=False)


def alive():
    return hof2env.game_alive()   # this environment's test instance


def log(msg):
    line = time.strftime("%H:%M:%S ") + msg
    print(line, flush=True)
    with open(LOG, "a") as f:
        f.write(line + "\n")


def send(c, t=4):
    try:
        return cmd.send(c, t)
    except SystemExit:
        return ""


def hybrid_throw():
    """Grab the dice with the right hand (palm onto them, grip), shake, throw forward and let go."""
    r = send("handto r palm dice", 3)
    if not r.startswith("move"):
        log("dice: handto failed (%s)" % r)
        return False
    sim(r + " 400; wait 150; hold r grip 1; wait 400")
    time.sleep(1.2)
    if "held=R" not in cmd.status().get("dice", ""):
        sim("hold r grip 0")
        log("dice: grab failed (%s)" % cmd.status().get("dice", ""))
        return False
    log("dice: grabbed - shake and throw")
    sim("path r 0 0.06 0 0 -0.12 0 0 0.12 0 0 -0.06 0 400; fling r 0 0.08 -0.45 220 0.8; hold r grip 0")
    return True


def touch_tile(tile):
    """Put the right fingertip 6 cm above the tile, push 1.5 cm in and back (the simulated hand
    reaches the far map as a leaning player would). True if the game took it (the hero walks)."""
    r = send("touchpose MapLayout/%s 6 r" % tile, 3)
    if "|" not in r:
        return False
    pos, press = [x.strip() for x in r.split("|")]
    h = send("handto r tip " + pos, 3)
    if not h.startswith("move"):
        return False
    before = cmd.status().get("touch", "")
    p = press.split()
    back = "move r %.4f %.4f %.4f 300" % (-float(p[2]), -float(p[3]), -float(p[4]))
    sim(h + " 400; wait 300; " + press + " 300; wait 150; " + back)
    time.sleep(1.5)
    after = cmd.status().get("touch", "")
    sim("hand r 0.25 1.3 -0.35 0 0 0")
    sel = lambda t: int(re.search(r"selects (\d+)", t).group(1)) if re.search(r"selects (\d+)", t) else -1
    return sel(after) > sel(before)


def poke_plate(n):
    """Encounter choices on the near board (TableBoards): the simulated right fingertip goes 6 cm in front of
    plate n, pushes 1.5 cm through it and comes back. True if the plate took it (a select)."""
    r = send("platepose %d 6 r" % n, 3)
    if "|" not in r:
        return False
    pos, press = [x.strip() for x in r.split("|")]
    h = send("handto r tip " + pos, 3)
    if not h.startswith("move"):
        return False
    m0 = re.search(r"selects (\d+)", cmd.status().get("boards", ""))
    p = press.split()
    back = "move r %.4f %.4f %.4f 300" % (-float(p[2]), -float(p[3]), -float(p[4]))
    sim(h + " 400; wait 300; " + press + " 300; wait 150; " + back)
    time.sleep(1.5)
    m1 = re.search(r"selects (\d+)", cmd.status().get("boards", ""))
    sim("hand r 0.25 1.3 -0.35 0 0 0")
    return bool(m0 and m1 and int(m1.group(1)) > int(m0.group(1)))


def fight_step(last_turn):
    r = send("tele?", 2)
    if "COUNTER" in r:
        sim("move l 0 0.05 -0.4 110; wait 100; move l 0 -0.05 0.4 300")
        return last_turn
    if time.time() - last_turn > 4:
        sim("stick r 1 0 350")
        last_turn = time.time()
    sim("swing r right 130; wait 120")
    return last_turn


def main():
    minutes = float(sys.argv[1]) if len(sys.argv) > 1 else 10
    shots = "--shots" in sys.argv
    sim("hand r 0.25 1.3 -0.35 0 0 0; hand l -0.25 1.25 -0.35 0 0 0; wait 200")
    send("god on")
    t0 = time.time()
    last_state, since = None, time.time()
    last_sig, sig_since = None, time.time()
    last_turn = 0
    last_map_move = 0
    last_menu = None
    menu_since = time.time()
    # round 2 (2026-10-17): a poked plate that opened the Inventory (focus_test's walk to a fight: two stalls; 16 Oct 08:05:
    # 12 minutes) - B left it, and the next A pressed the same selection again. Left at once now, and that plate is not
    # poked again in this encounter; the next move there is another plate's poke, not A
    last_plate = None
    no_plates = set()
    poke_next = False
    last_shop = 0
    shot_menus = set()
    reached = set()
    touched = {}
    global dice_games
    visited = []
    while time.time() - t0 < minutes * 60:
        if not alive():
            log("GAME NOT RUNNING (crash?)")
            return 2
        st = {}
        try:
            st = cmd.status()
        except Exception:
            pass
        s = st.get("state", "")
        if s != last_state:
            log("state %s -> %s" % (last_state, s))
            if s not in visited:
                visited.append(s)
            if shots and s:
                send("shot2 auto_%s" % s.replace("GameState_", ""))
            last_state, since = s, time.time()
        if "Level_Play" in s:
            # PlayerFightExit: after the win, walk into the glowing exit card
            if st.get("fightExit", "-").startswith("pending") and "card" in st.get("fightExit", ""):
                r = send("exitcard info", 3)
                m = re.search(r"bearing (-?\d+)", r)
                if m:
                    b = math.radians(float(m.group(1)))
                    log("exit card: %s -> walk" % r)
                    sim("stick l %.2f %.2f 600" % (math.sin(b), math.cos(b)))
                    time.sleep(0.7)
                    continue
            last_turn = fight_step(last_turn)
            continue
        menu = st.get("menu", "")
        if menu != last_menu:
            log("menu %s" % menu)
            if menu not in ("Encounter", "Inventory"):
                no_plates.clear()
            last_menu = menu
            menu_since = time.time()
            if not menu.startswith("Shop") and menu != "Choice":
                touched.clear()
            key = menu.replace(" / ", "+").replace(" ", "")
            if key and key not in reached and key != "-":
                reached.add(key)
                time.sleep(1.0)
                send("reach auto_%s" % key)   # interaction reach probe (testdata/reach/)
            if shots and key and key not in shot_menus:
                shot_menus.add(key)
                time.sleep(1.5)   # let the screen settle
                send("shot menu_%s" % key)
                send("rtshot menu_%s" % key)
        if menu == "Inventory" and time.time() - menu_since > 1.5:
            log("inventory: leave with B (plate %s opened it)" % last_plate)
            if last_plate is not None:
                no_plates.add(last_plate)
            poke_next = True
            send("press B 6")
            time.sleep(2)
            continue
        # presented cards (equipment choices, shop stock, the zoomed shop card): pick by fingertip
        if menu in ("Choice", "Shop", "Shop_Examine") and touched.get(menu, 0) < 2 and time.time() - since > 4:
            touched[menu] = touched.get(menu, 0) + 1
            target = "ZoomContainer/Card_" if menu == "Shop_Examine" else "CardChoiceContainer/Card_"
            r = send("touch %s r tap" % target, 3)
            time.sleep(2.5)
            after = cmd.status().get("menu", "")
            log("touch %s in %s -> %s | %s" % (r, menu, after, cmd.status().get("touch", "")))
            continue
        if menu.startswith("Shop") and time.time() - since > 10 and time.time() - last_shop > 8:
            log("shop: leave with B")
            send("press B 6"); time.sleep(2)
            if "Examine" in menu:
                send("press B 6"); time.sleep(2)
            last_shop = time.time()
            continue
        if "Deckbuilder_SelectMode" in menu and "Dialogue" not in menu and time.time() - since > 6:
            log("deck builder: Recommend (Y) -> Fill (A) -> Start (X)")
            send("press Y 6"); time.sleep(3)
            send("press A 6"); time.sleep(3)
            send("press X 6"); time.sleep(6)
            continue
        # encounter choices on the near board: poke one (every other time; A below otherwise)
        pm = re.search(r"plates (\d+)", st.get("boards", ""))
        free = [i for i in range(1, int(pm.group(1)) + 1) if i not in no_plates] if pm else []
        if menu == "Encounter" and free and time.time() - since > 3 and (poke_next or random.random() < 0.5):
            n = random.choice(free)
            last_plate, poke_next = n, False
            ok = poke_plate(n)
            log("encounter: poke plate %d of %s -> %s" % (n, pm.group(1), "selected" if ok else "no select (A next)"))
            if ok:
                time.sleep(2)
                continue
        if st.get("dice", "-").startswith("Catapault"):
            dice_games += 1
            if dice_games % 3 == 0 or not hybrid_throw():
                log("dice: trigger draw + release")
                sim("hold r trigger 1; wait 900; hold r trigger 0; wait 200")
            sim("hand r 0.25 1.3 -0.35 0 0 0")
            time.sleep(4)
            continue
        # the map: A only confirms the current tile; a move needs an adjacent tile picked (by laser)
        if s == "GameState_Map" and time.time() - since > 12 and time.time() - last_map_move > 12:
            m = send("map", 3)
            tiles = re.findall(r"\(\d+, \d+\)", m.split("movable", 1)[1]) if "movable" in m else []
            if tiles and "Main: valid=True group=Map" not in send("selection", 3):
                # an encounter panel / dialogue still holds the selection: let the A presses below clear it
                tiles = []
            if tiles:
                t = random.choice(tiles)
                if touch_tile(t):
                    log("map: %s -> fingertip poke %s" % (m, t))
                else:
                    log("map: %s -> laser click %s" % (m, t))
                    send("world click MapLayout/" + t, 3)
                last_map_move = time.time()
                time.sleep(3)
                continue
        sig = s + "|" + send("ui list", 3) + "|" + send("world list", 3)
        if sig != last_sig:
            last_sig, sig_since = sig, time.time()
        elif time.time() - sig_since > 90:
            tag = time.strftime("%H%M%S")
            log("STUCK in %s for 90s -> shots stuck_%s, trying B then A" % (s, tag))
            send("rtshot stuck_%s" % tag)
            send("shot2 stuck_%s" % tag)
            send("press B 6")
            time.sleep(2)
            sig_since = time.time()
        # same state for a long time: also try the nav-bar buttons (e.g. customisation's "X Play")
        stale = time.time() - since
        if stale > 60 and int(stale) % 20 < 3:
            for b in ("B", "X", "Y"):
                send("press %s 6" % b)
                time.sleep(1.5)
        send("press A 6")
        time.sleep(2.5)
    log("autoplay done; states visited: " + ", ".join(visited))
    return 0


if __name__ == "__main__":
    sys.exit(main())
