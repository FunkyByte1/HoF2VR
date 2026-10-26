#!/usr/bin/env python3
"""Playtest 3 (env 1), run B from the caravan of a magician session: the challenge map (items 2, 7, 8: the symbols' row, the
section plates, the mode menu), the head walk there, then the challenge started by touching Start while leaning out to the
right and staying leaned 3 s (item 20: the recentre must take where the player stood). Pictures captures/pt3/b_*.jpg."""
import os
import re
import subprocess
import sys
import time

H = "/home/bara/Projects/flat2vr/HandOfFate2"
sys.path.insert(0, H + "/scripts")
os.environ["PT17_OUT"] = H + "/captures/pt3"
import cmd as C  # noqa: E402
import pt17  # noqa: E402
import p2fix as P  # noqa: E402
pt17.OUT = H + "/captures/pt3"   # (p2fix sets its own folder)

GL = os.path.expanduser("~/.local/share/Steam/steamapps/common/Hand of Fate 2/BepInEx/LogOutput.log")


def st():
    try:
        return C.status()
    except Exception:
        return {}


def log(m):
    print(time.strftime("%H:%M:%S ") + m, flush=True)


def shot(name, yaw=0, pitch=-30):
    pt17.sim(pt17.REST)
    pt17.look(yaw, pitch)
    time.sleep(0.8)
    log("  " + pt17.shot(name).split(" -> ")[-1])


def last(pat):
    out = ""
    with open(GL, errors="replace") as f:
        for l in f:
            if re.search(pat, l):
                out = l.strip()
    return out


def main():
    for _ in range(3):
        time.sleep(2)
        C.send("press A 6", 5)
        if P.wait_menu("ChallengeMap", 25):
            break
    time.sleep(4)
    log("at %s / %s" % (st().get("state"), st().get("menu")))
    log("markers: " + C.send("markers", 5))
    log("boards: " + C.send("boards", 5)[:260])
    log("station: " + last(r"rig: station caravan side table")[30:200])
    log("spot: " + last(r"place: (the )?side table spot")[30:220])
    shot("b_map_rest", 0, -30)
    shot("b_map_up", 0, -10)
    shot("b_map_menu_left", 30, -40)
    r = subprocess.run(["python3", H + "/scripts/chains/cand9/headwalk.py", "b_hw_map", "--part", "Map_Pivot/MapBoard", "--pitch", "-35"],
                       capture_output=True, text=True, timeout=400, env=dict(os.environ, CAND9_PICS=H + "/captures/pt3"))
    log("headwalk: " + " ".join(l for l in r.stdout.splitlines() if "HEADWALK" in l)[:400])
    # 20: start the challenge by touching Start while leaned out to the right, and stay leaned
    C.send("press A 6", 5)
    time.sleep(5)
    C.send("press Y 6", 5)
    time.sleep(3)
    log("  " + C.send("ptouch Fill", 5))
    time.sleep(4)
    pt17.sim("head 0.16 1.64 0.0 -29 -40 0")
    time.sleep(1.5)
    log("  " + C.send("ptouch nav:Start", 5))
    time.sleep(3.0)
    pt17.sim("head 0 1.66 0 0 -35 0")
    time.sleep(10)
    log("recentre: " + last(r"recenter: neutral head")[30:330])
    log("at %s / %s, head %s" % (st().get("state"), st().get("menu"), st().get("head")))
    shot("b_setup_rest", 0, -35)




def pendulum():
    """(item 24) the pendulum in front: its parts (static batches) and a picture."""
    log("pendulum: " + C.send("scen run Games/Pendulum", 10))
    t0 = time.time()
    while time.time() - t0 < 70:
        s = st()
        if "in front (1.00)" in str(s.get("metronome", "")):
            break
        if "waiting" in str(s.get("gate", "")) or "on" in str(s.get("limboShown", "")):
            C.send("gate go", 5)
        time.sleep(1)
    time.sleep(1)
    log("  pendgeo: " + C.send("pendgeo", 8))
    C.send("timescale 0", 5)
    shot("b_pendulum", 0, -15)
    shot("b_pendulum_close", 0, -12)
    C.send("timescale 1", 5)
    log("  " + C.send("stop pendulum", 5))


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "pendulum":
        pendulum()
    else:
        main()
        pendulum()
