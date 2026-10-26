#!/usr/bin/env python3
"""menu_by_touch after the inventory (2026-10-18): open the inventory by its plate, close it with B, then report the inventory
layers / screen state and run the tour's menu stage (the full tours failed it; alone it passed)."""
import os, sys, time
H = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..")
sys.path.insert(0, H)
import cmd as C  # noqa: E402
import p2fix as P  # noqa: E402

def show(tag):
    s = C.status()
    print(tag, "| menu", s.get("menu"), "| invLayers", s.get("invLayers"), "|", C.send("sget VRUI InventoryScreen", 5),
          "| inv state", C.send("sget InventoryLayers Active", 5), flush=True)

show("start")
P.inventory("menu_after_inv")
show("open")
C.send("press B 6", 5)
time.sleep(3)
show("after B")
time.sleep(3)
show("+3 s")
sys.argv = ["playtest.py", "--skip-start", "--only", "menu,devkeys"] + sys.argv[1:]
import playtest  # noqa: E402
playtest.main()
show("end")
