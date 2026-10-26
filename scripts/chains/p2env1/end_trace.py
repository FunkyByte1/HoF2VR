#!/usr/bin/env python3
"""The end of a challenge (`wincoins`: won with its token), seated or standing, with KeepOut's trace on the equipment cards that
orbit the player at the end, and both-eye pictures of the end coins and rewards (2026-10-18).
Usage: end_trace.py [--seated] [TAG]   -> captures/fixes-p2/<TAG>_*.jpg, the game log's kotrace / PROXIMITY lines"""
import os, sys, time
H = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..")
sys.path.insert(0, H)
seated = "--seated" in sys.argv
tag = next((a for a in sys.argv[1:] if not a.startswith("--")), "end_seated" if seated else "end_standing")
sys.argv = ["playtest.py", "--skip-start"]
import playtest as T  # noqa: E402
import pt17  # noqa: E402
pt17.OUT = os.path.join(H, "..", "captures", "fixes-p2")

T.sim("seat %s" % ("0.55" if seated else "0"))
if seated:
    if T.send("posture").strip().startswith("seated"):
        T.send("posture standing"); time.sleep(2.5)
    T.sim("head 0 1.75 0 0 0 0"); time.sleep(0.5)
    T.send("posture seated"); time.sleep(4.0)
else:
    if T.send("posture").strip().startswith("seated"):
        T.send("posture standing"); time.sleep(3.0)
T.sim(T.REST + "; head 0 1.75 0 0 -20 0")
print("posture:", T.send("posture")[:90], flush=True)
print("trace:", T.send("kotrace Shield|Axe|Blade|Sword|Armour|deck|title 9000"), flush=True)
print("gate:", T.send("gate auto 1.5"), flush=True)
print("win:", T.send("wincoins"), flush=True)
t0 = time.time()
shots, last, last_a = 0, "", 0.0
while time.time() - t0 < 75:
    time.sleep(1.5)
    s = T.state()
    if s.split(" for ")[0] != last:
        last = s.split(" for ")[0]
        print("%5.1f s %s | menu %s" % (time.time() - t0, s, T.st().get("menu")), flush=True)
    if shots < 14 and time.time() - t0 > 1.0 + shots * 2.5:
        T.sim("head 0 1.75 0 0 %d 0" % (-20 if shots % 2 == 0 else 5))
        time.sleep(0.2)
        print("  ", pt17.shot("%s_%02d" % (tag, shots)), flush=True)
        shots += 1
    if "ChallengeMap" in s or "Hub" in T.st().get("place", ""):
        break
    if time.time() - t0 > 6 and time.time() - last_a > 4.0:
        last_a = time.time()
        T.send("press A 6")   # (Open the token, take the rewards, Continue)
T.send("state")
print("proximity:", T.st().get("proximity", "")[:300], flush=True)
