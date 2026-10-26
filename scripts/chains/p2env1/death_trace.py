#!/usr/bin/env python3
"""The tour's death stage (a goblin fight, `die`, the end of the challenge: its cards orbit the player), seated or standing, with
KeepOut's trace on the equipment cards; pictures of the orbit (2026-10-18). Usage: death_trace.py [--seated] [TAG]"""
import os, sys, threading, time
H = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..")
sys.path.insert(0, H)
seated = "--seated" in sys.argv
tag = next((a for a in sys.argv[1:] if not a.startswith("--")), "orbit_seated" if seated else "orbit_standing")
sys.argv = ["playtest.py", "--skip-start"] + (["--seated"] if seated else [])
import playtest as T  # noqa: E402
import pt17  # noqa: E402
pt17.OUT = os.path.join(H, "..", "captures", "fixes-p2")
T.SEATED[0] = seated
T.sim("seat %s" % ("0.55" if seated else "0"))
if T.send("posture").strip().startswith("seated"):
    T.send("posture standing"); time.sleep(2.5)
if seated:
    T.sim("head 0 1.75 0 0 0 0"); time.sleep(0.5)
    T.send("posture seated"); time.sleep(4.0)
print("posture:", T.send("posture")[:80], flush=True)
print("trace:", T.send("kotrace Shield|Axe|Blade|Sword|Armour 99000"), flush=True)
# pictures while the end of the challenge plays (after the death)
def pics():
    t_post = None
    n = 0
    while n < 8:
        time.sleep(0.5)
        s = T.state()
        if "Post" in s and t_post is None:
            t_post = time.time()
        if t_post is not None and time.time() - t_post > 1.0 + n * 1.2:
            T.sim("head 0 1.75 0 0 %d 0" % (-25 if n % 2 == 0 else 0))
            print("  ", pt17.shot("%s_%02d" % (tag, n)), flush=True)
            n += 1
        if t_post is None and n == 0 and time.time() - t0 > 200:
            return
t0 = time.time()
th = threading.Thread(target=pics, daemon=True)
th.start()
T.stage_death()
th.join(timeout=30)
T.send("state")
print("proximity:", T.st().get("proximity", "")[:300], flush=True)
