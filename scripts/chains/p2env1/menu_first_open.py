#!/usr/bin/env python3
"""The pause menu's Settings page at its first opening in a session, in pictures (2026-10-18): opened by touch, both-eye shots
at +0.3 / +1 / +2.5 s, a fingertip brought to 2 cm in front of Back (the hover moves off General and its options close), then
Back pressed. Each step logs the board's status (its layout line). -> captures/fixes-p2/menu_first_open_*.jpg"""
import os, re, sys, time
H = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..")
sys.path.insert(0, H)
sys.argv = ["playtest.py", "--skip-start"]
import playtest as T  # noqa: E402
import pt17  # noqa: E402
pt17.OUT = os.path.join(H, "..", "captures", "fixes-p2")

def step(name):
    b = T.boards_now()
    m = re.search(r"menu board [^|]*", b)
    pl = re.search(r"plates \d+", b)
    print("%s | %s | %s" % (name, m.group(0) if m else "no menu board", pl.group(0) if pl else "-"), flush=True)
    T.sim("head 0 1.75 0 0 -12 0")
    time.sleep(0.3)
    print("   ", pt17.shot("menu_first_open_" + name.split(" ")[0]), flush=True)
    T.sim("head 0 1.75 0 0 0 0")

T.sim(T.REST + "; head 0 1.75 0 0 0 0")
T.send("press START 6")
T.wait_boards(lambda b: "menu board pause" in b and "Resume" in b, 10)
step("0_pause")
T.poke_plate("Settings")
t0 = time.time()
for name, at in (("1_settings_0.3s", 0.3), ("2_settings_1s", 1.0), ("3_settings_2.5s", 2.5)):
    time.sleep(max(0.0, at - (time.time() - t0)))
    step(name)
# one continuous poke at Back as the first layout numbers it (the tour's way): to 6 cm in front, in to 2 cm - its hover
# moves the game's hover off General, the options close and the plates are numbered again - then through it
b = T.boards_now()
idx = next((int(i) for i, t in re.findall(r"\[(\d+) ([^\]]*)\]", b) if t == "Back"), None)
r = T.send("platepose %d 6 r" % idx)
pos, mv = [x.strip() for x in r.split("|")]
d = [float(v) for v in mv.split()[2:5]]
T.sim("hand r 0.25 1.2 -0.15 0 -30 0")
time.sleep(0.4)
T.sim(T.send("handto r tip " + pos) + " 500")
time.sleep(0.8)
T.sim("move r %.4f %.4f %.4f 300" % (d[0] * 4 / 7.5, d[1] * 4 / 7.5, d[2] * 4 / 7.5))
time.sleep(1.2)
step("4_finger_2cm_from_back (was plate %d)" % idx)
T.sim("move r %.4f %.4f %.4f 250" % (d[0] * 3.5 / 7.5, d[1] * 3.5 / 7.5, d[2] * 3.5 / 7.5))
time.sleep(1.5)
step("5_pushed_through")
T.sim(T.REST)
print("RESULT back_pressed %s" % ("PASS" if "Resume" in T.boards_now() else "FAIL"), flush=True)
T.send("press B 6"); time.sleep(1.0)
