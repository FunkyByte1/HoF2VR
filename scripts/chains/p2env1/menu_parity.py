#!/usr/bin/env python3
"""menu_by_touch with the poke habit in both phases (2026-10-18): the tour pokes every second plate with the grip squeezed,
so the stages before the menu decide which way Settings / Back / Resume are poked. Runs the menu stage with the counter even,
then odd, and logs the settings page's plates."""
import os, sys, time
H = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..")
sys.path.insert(0, H)
sys.argv = ["playtest.py", "--skip-start"]
import playtest as T  # noqa: E402

for start in (0, 1, 0, 1):
    T.HABIT["n"] = start
    T.send("press START 6")
    time.sleep(2.0)
    T.poke_plate("Settings")
    time.sleep(2.5)
    print("start %d settings page: %s" % (start, T.boards_now()[:700]), flush=True)
    T.send("press B 6"); time.sleep(1.5); T.send("press B 6"); time.sleep(1.5)
    T.sim(T.REST)
    time.sleep(1.0)
    T.stage_menu()
    T.send("state"); time.sleep(1.0)
    print("start %d done: %s" % (start, T.boards_now()[:120]), flush=True)
    if "menu board" in T.boards_now():
        T.send("press B 6"); time.sleep(1.0); T.send("press B 6"); time.sleep(1.0)
