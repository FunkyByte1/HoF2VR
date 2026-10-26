#!/usr/bin/env python3
"""Playtest 3 (env 1), run A at the dungeon map of a magician_mid session: both-eye pictures (captures/pt3/a_*.jpg) of the
shop with an option hovered (item 12), a gained weapon zoomed with the controls column (27), the map with the stat cards (23),
the pendulum in front (24), then the call to battle (14, 25, 28; reveal.py's way). Leaves the waiting space up."""
import os
import subprocess
import sys
import time

H = "/home/bara/Projects/flat2vr/HandOfFate2"
sys.path.insert(0, H + "/scripts")
os.environ.setdefault("PT17_OUT", H + "/captures/pt3")
import cmd as C  # noqa: E402
import pt17  # noqa: E402


def st():
    try:
        return C.status()
    except Exception:
        return {}


def log(m):
    print(time.strftime("%H:%M:%S ") + m, flush=True)


def shot(name, yaw=0, pitch=-20, freeze=False):
    pt17.sim(pt17.REST)
    pt17.look(yaw, pitch)
    time.sleep(0.7)
    if freeze:
        C.send("timescale 0", 5)
        time.sleep(0.3)
    log("  " + pt17.shot(name).split(" -> ")[-1])
    if freeze:
        C.send("timescale 1", 5)


def to_map(n=12):
    for _ in range(n):
        s = st()
        if s.get("state") == "GameState_Map" and s.get("menu") == "Map":
            return True
        if "Limbo" in str(s.get("limbo", "")) or "waiting" in str(s.get("gate", "")):
            C.send("gate go", 5)
        C.send("press A 6", 5)
        time.sleep(3)
    return False


def wait(pred, t):
    t0 = time.time()
    while time.time() - t0 < t:
        s = st()
        if pred(s):
            return s
        time.sleep(0.5)
    return None


def main():
    C.send("cfg GrabThrow true", 5)
    log("start: %s / %s" % (st().get("state"), st().get("menu")))
    # 12: the shop, the long option hovered
    log("shop: " + C.send("scen run Table/Shop", 10))
    wait(lambda s: "Buy equipment" in str(s.get("boards", "")), 40)
    log("  " + C.send("ptouch 2 hover", 5))
    time.sleep(1.0)
    log("  plates: " + " ".join(x for x in C.send("boards", 5).split(" | ") if x.startswith("plate ")) [:400])
    shot("a_shop_hover2", 0, -20)
    log("  " + C.send("ptouch 1", 5))
    time.sleep(3)
    log("  back to the map: %s" % to_map())
    # 27: a gained weapon, zoomed, and the controls column
    log("gain: " + C.send("scen run Table/Gain a weapon", 10))
    time.sleep(7)
    log("  boards: " + C.send("boards", 5)[:300])
    shot("a_gain_weapon", 0, -15)
    shot("a_gain_weapon_right", -25, -25)
    log("  " + C.send("ptouch nav:No", 5))
    time.sleep(3)
    log("  back to the map: %s" % to_map())
    # 23: the stat cards beside the map
    shot("a_map_stats", 15, -55)
    shot("a_map_rest", 0, -45)
    # 24: the pendulum in front
    log("pendulum: " + C.send("scen run Games/Pendulum", 10))
    s = wait(lambda s: "in front (1.00)" in str(s.get("metronome", "")) or "waiting" in str(s.get("gate", "")), 60)
    if s is not None and "in front (1.00)" not in str(s.get("metronome", "")):
        C.send("gate go", 5)
        wait(lambda s: "in front (1.00)" in str(s.get("metronome", "")), 30)
    time.sleep(1.0)
    log("  metronome: " + str(st().get("metronome", ""))[:160])
    shot("a_pendulum", 0, -15, freeze=True)
    shot("a_pendulum_down", 0, -30, freeze=True)
    log("  " + C.send("stop pendulum", 5))
    time.sleep(4)
    log("  back to the map: %s" % to_map())
    # 14, 25, 28: the call to battle
    r = subprocess.run(["python3", H + "/scripts/chains/pt3/reveal.py", "a", "jump"], capture_output=True, text=True, timeout=200)
    log("reveal: " + " | ".join(l.strip() for l in r.stdout.splitlines()[-4:]))
    log("dealpath: " + C.send("dealpath", 5))


if __name__ == "__main__":
    main()
