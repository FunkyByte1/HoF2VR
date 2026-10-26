#!/usr/bin/env python3
"""Candidate 9's picture walk, the places out of fights (env 1; from cand8/walk.py). At each place the head walks
(headwalk.py: both eyes at rest, turned 30 deg left / right, leaned 20 cm forward / right; cards, boards and labels checked
to stay put), plus a few still views. Both eyes -> captures/cand9/w<NN>_<place>_<pose>.jpg.

  walk.py table      from the title of a session (magician_mid): the title, the dungeon map, a story and its choice, the
                     dice, choose-a-card, the pendulum, the wheel, a reward (choose 1 of 3), a shop, the inventory, the
                     call to battle and the campfire, the end coins
  walk.py hub        from the caravan (a magician session at the table): the challenge map, its deck builder (select,
                     build), the challenge's opening at the table
  walk.py adventure  from the challenge map: the Adventure tab, an adventure's deck builder (AdventureDeckBuilder)
"""
import os
import sys
import time

H = "/home/bara/Projects/flat2vr/HandOfFate2"
sys.path.insert(0, H + "/scripts")
sys.path.insert(0, H + "/scripts/chains")
sys.path.insert(0, H + "/scripts/chains/cand9")
import cmd as C  # noqa: E402
import pt17  # noqa: E402
import headwalk as HW  # noqa: E402

log = HW.log
send = HW.send
MAP = "Scale/Map/MapLayout"


def status():
    try:
        return C.status()
    except Exception:
        return {}


def scen(label, until=None, timeout=90):
    log("scenario " + label + ": " + send("scen run " + label, 10))
    t0 = time.time()
    while time.time() - t0 < timeout:
        time.sleep(2)
        s = status()
        sc = s.get("scenarios", "")
        if "(" + label.split("/")[-1] + ": done)" in sc and (until is None or until in (s.get("menu", "") + " " + s.get("state", ""))):
            return s
    log("  timed out: " + str(status().get("scenarios", ""))[:120])
    return status()


def still(name, yaw, pitch, part=MAP, reach=False):
    """One still view (yaw / pitch from the line to PART, + = left); reach: both hands out over the table as a player's
    reaching for a card (else down at the sides)."""
    b = HW.base_yaw(part) if part else None
    if reach:
        HW.hands(b or 0.0, 0.18, 1.05, 0.38, -35)
    else:
        HW.hands(b or 0.0)
    HW.pose((b or 0.0) + yaw, 0, pitch)
    time.sleep(0.8)
    log("  " + pt17.shot(name).split(" -> ")[-1])


def hw(tag, pitch, part=MAP, yaw=0.0):
    try:
        HW.walk(tag, part, yaw, pitch)
    except Exception as e:
        log("HEADWALK %s ERROR %r" % (tag, e))


def wait_menu(part, timeout=30):
    t0 = time.time()
    while time.time() - t0 < timeout:
        s = status()
        if part in s.get("menu", "") or part in s.get("state", ""):
            return True
        time.sleep(1.0)
    return False


def wait_status(key, prefix, timeout):
    t0 = time.time()
    while time.time() - t0 < timeout:
        if status().get(key, "").startswith(prefix):
            return True
        time.sleep(0.5)
    log("  %s not %s after %d s: %s / mg %s" % (key, prefix, timeout, status().get(key, ""), status().get("mg", "")))
    return False


def to_map(n=6):
    for _ in range(n):
        if "GameState_Map" in send("state?", 3) and status().get("menu", "") == "Map":
            return True
        pt17.sim("btn r a 150")
        time.sleep(2.5)
    return False


def table():
    log("cand9 walk (table) begins: " + send("state?", 3)[:60])
    pt17.sim(pt17.REST)
    # the title: the light at the feet, the plant on the right gone
    hw("w01_title", -5, None)
    # what stands near the player at the title (the trial walk's turn left showed a man at the left edge of the view)
    HW.pose(30.0, 0, -5)
    time.sleep(0.8)
    log("  title, turned left 30: near the eyes: " + send("eyenear 1.5", 5)[:900])
    HW.pose(0.0, 0, -5)
    still("w01_title_feet", 0, -60, None)
    still("w01_title_right", -70, -25, None)
    # the dungeon map, nothing happening (the stat fan, the figurine, the controls column)
    scen("Table/Gain food", None, 60)
    time.sleep(3)
    to_map()
    hw("w02_map", -35)
    still("w02_map_hands_reaching", 0, -45, MAP, True)
    # a story and its choice
    scen("Table/Dialogue", "Encounter")
    time.sleep(2)
    hw("w03_story", -20)
    still("w03_story_dealer", 0, 5)
    for _ in range(3):
        send("press A", 5)
        time.sleep(2)
        if "Choice" in status().get("menu", ""):
            break
    hw("w03_choice", -25)
    send("press A", 5)
    time.sleep(3)
    to_map()
    # the dice with a re-roll: thrown, a die marked, the Re-roll plate
    scen("Table/Gain gold")
    time.sleep(2)
    to_map()
    send("mg dice 99 0 0", 8)
    t0 = time.time()
    while time.time() - t0 < 40 and not status().get("dice", "").startswith("Catapault"):
        time.sleep(1)
    still("w04_dice_waiting", 0, -40)
    send("dice throw 0 0.3 1.8 r game", 8)
    t0 = time.time()
    while time.time() - t0 < 25 and not status().get("dice", "").startswith("Marking"):
        time.sleep(1)
    time.sleep(1.5)
    send("world click PlayerDiceRoller/Container/D6", 8)
    time.sleep(1.5)
    log("  reroll: " + status().get("reroll", "")[:120])
    hw("w04_dice_marked", -45)
    send("reroll press", 8)
    time.sleep(3)
    send("dice throw 0 0.2 1.5 r game", 8)
    time.sleep(6)
    import interact_test as T
    T.finish_task()
    to_map()
    # the pendulum (stopped by a grip after the walk)
    scen("Games/Pendulum")
    wait_status("pendulum", "swinging", 25)
    hw("w05_pendulum", -15)
    send("stop r grip", 5)
    T.finish_task()
    time.sleep(2)
    to_map()
    # choose a card (picked by a tap after the walk)
    scen("Games/Choose a card")
    wait_status("chance", "running", 40)
    time.sleep(1.0)
    hw("w06_choose_card", -25)
    log("  chance pick: " + str(T.touch_seam("CardChoiceContainer/Card_CN", "tap", 0.3)))
    T.finish_task()
    time.sleep(2)
    to_map()
    # the wheel (stopped by a grip after the walk)
    scen("Games/Wheel")
    wait_status("wheel", "Looping", 40)
    time.sleep(1.0)
    hw("w07_wheel", -20)
    send("stop r grip", 5)
    T.finish_task(60)
    time.sleep(2)
    to_map()
    # a reward the game shows you: choose 1 of 3 (the spawn rule: brought up at 0.53 m or more, then put)
    scen("Table/Choose 1 of 3")
    time.sleep(4)
    hw("w08_reward_choose3", -20)
    for _ in range(4):
        send("press A", 5)
        time.sleep(2)
    to_map()
    # a shop
    scen("Table/Shop")
    time.sleep(4)
    hw("w09_shop", -20)
    still("w09_shop_down", 0, -40)
    send("press B", 5)
    time.sleep(2)
    for _ in range(3):
        send("press A", 5)
        time.sleep(2)
    to_map()
    # the inventory (cards lower, tabs and their list higher and in front: P2-4); a scenario first: its @map gets back to
    # the dungeon map whatever a mini-game's outcome started (the trial walk's wheel outcome had started a fight)
    scen("Table/Gain food", None, 60)
    time.sleep(3)
    to_map()
    log("  inventory: " + send("ptouch nav:Inventory", 5))
    if not wait_menu("Inventory", 12):
        send("press L 6", 5)
        wait_menu("Inventory", 10)
    time.sleep(2.5)
    send("invgeo", 10)
    hw("w10_inventory", -15)
    still("w10_inventory_down", 0, -30)
    send("press B", 5)
    time.sleep(2)
    to_map()
    # the call to battle and the campfire (the gate waits for A: auto off)
    send("gate auto 0", 5)
    log("scenario Places/Campfire (fight): " + send("scen run Places/Campfire (fight)", 10))
    t0 = time.time()
    while time.time() - t0 < 150 and "Level_In" not in send("state?", 3):
        time.sleep(0.5)
    for k in range(4):
        b = HW.base_yaw(MAP)
        HW.pose(b or 0.0, 0, -10)
        log("  " + pt17.shot("w11_call_to_battle_%d" % k).split(" -> ")[-1])
        time.sleep(0.3)
    t0 = time.time()
    while time.time() - t0 < 90 and not ("READY" in status().get("board", "") and "Into battle" in status().get("board", "")):
        time.sleep(1)
    time.sleep(1.5)
    log("  board: " + status().get("board", "")[:100])
    hw("w12_campfire", 0, None)
    send("gate auto 1.5", 5)
    t0 = time.time()
    while time.time() - t0 < 90 and "Level_Play" not in send("state?", 3):
        time.sleep(1)
    import indicator_shots
    log("  leave the fight: " + str(indicator_shots.leave()))
    time.sleep(15)
    for _ in range(6):
        if "GameState_Map" in send("state?", 3):
            break
        pt17.sim("btn r a 150")
        time.sleep(3)
    # the end coins
    send("scen run Table/End coins", 10)
    time.sleep(6)
    hw("w13_end_coins", -15)
    log("cand9 walk (table) done")


def hub():
    log("cand9 walk (hub) begins: %s / %s" % (send("state?", 3)[:50], status().get("menu")))
    pt17.sim(pt17.REST)
    if "Attract" in status().get("state", ""):
        hw("w20_caravan_attract", -15, None)
        for _ in range(3):
            send("press A 6", 5)
            if wait_menu("ChallengeMap", 25):
                break
        time.sleep(4)
    log("  " + status().get("state", "") + " / " + status().get("menu", ""))
    hw("w21_challenge_map", -40, "Map_Pivot/MapBoard")
    still("w21_challenge_map_left", 40, -25, None)
    # a pin pressed from above opens its deck builder (P2-8; A alone needs a pin chosen first)
    import p2fix
    p2fix.LOG = HW.DLOG
    pt17.OUT = HW.OUT   # (p2fix sets its own folder on import)
    if p2fix.pin("w22_pin"):
        send("press A 6", 5)
    if not wait_menu("Deckbuilder", 20):
        log("  no deck builder: " + status().get("menu", ""))
        return
    time.sleep(3)
    log("  deck builder: " + status().get("menu", "") + " | " + status().get("deckrows", "")[:200])
    hw("w22_deckbuilder_select", -45, "Map_Pivot/MapBoard")
    send("press Y 6", 5)
    time.sleep(3)
    send("press A 6", 5)
    time.sleep(3)
    log("  after Recommend + A: " + status().get("menu", "") + " | " + status().get("deckrows", "")[:200])
    hw("w23_deckbuilder_build", -45, "Map_Pivot/MapBoard")
    log("cand9 walk (hub) done")


def adventure():
    """From the challenge map: the Adventure tab, the first adventure, its deck builder at the table."""
    log("cand9 walk (adventure) begins: %s / %s" % (send("state?", 3)[:50], status().get("menu")))
    for _ in range(4):
        if "ChallengeMap" in status().get("menu", ""):
            break
        send("press B 6", 5)
        time.sleep(2.5)
    log("  boards: " + send("boards", 5)[:300])
    log("  the Adventure plate: " + send("ptouch Adventure", 5))
    time.sleep(4)
    log("  " + status().get("state", "") + " / " + status().get("menu", "") + " | " + send("boards", 5)[:300])
    hw("w24_adventure_map", -40, "Map_Pivot/MapBoard")
    send("press A 6", 5)
    time.sleep(4)
    log("  after A: " + status().get("state", "") + " / " + status().get("menu", "") + " | " + status().get("deckrows", "")[:200])
    hw("w25_adventure_next", -40, "Map_Pivot/MapBoard")
    log("cand9 walk (adventure) done")


if __name__ == "__main__":
    a = sys.argv[1:] or ["table"]
    for part in a:
        {"table": table, "hub": hub, "adventure": adventure}[part]()
