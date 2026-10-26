#!/usr/bin/env python3
"""Leave the current fight and settle on the map (combat test chains: no reboot, so the one-game slot stays ours).

  leave_fight.py [timeout_s]

In a fight: god mode, enemies killed (`killall`), the way out (the portal's A hold); then A through the post-fight
encounter, dialogue and loot until the primary menu is the map. Exit 0 on the map, 1 otherwise.
"""
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cmd  # noqa: E402
import hof2env  # noqa: E402

H = hof2env.H


def send(c, t=8):
    try:
        return cmd.send(c, t)
    except SystemExit as e:
        return "ERR " + str(e)


def menu():
    try:
        return cmd.status().get("menu") or ""
    except Exception:  # noqa: BLE001
        return ""


def main():
    timeout = float(sys.argv[1]) if len(sys.argv) > 1 else 300
    t0 = time.time()
    while time.time() - t0 < timeout:
        st = send("state?", 4)
        m = menu()
        if "GameState_Map" in st and m in ("Map", ""):
            time.sleep(1.0)
            if menu() in ("Map", ""):
                print("on the map after %.0f s" % (time.time() - t0))
                return 0
        if "Level_In" in st and send("gate", 4).startswith("gate waiting"):
            # a new encounter's waiting space: through its gate (A on the controller), then end that fight too
            subprocess.run(["python3", H + "/scripts/xrsim.py", "btn r a 150"], capture_output=True, text=True)
            time.sleep(2.0)
            continue
        if "Level_Play" in st:
            send("god on")
            send("tough off")
            send("killall")
            time.sleep(3)
            subprocess.run(["python3", H + "/scripts/xrsim.py", "btn r a 2500"], capture_output=True, text=True)
            time.sleep(3)
            continue
        send("press A 6")
        time.sleep(2.0)
    print("not on the map after %.0f s (%s, menu %s)" % (timeout, send("state?", 4), menu()))
    return 1


if __name__ == "__main__":
    sys.exit(main())
