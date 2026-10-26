#!/usr/bin/env python3
"""The Dealer notices you (feature B; src/HoF2VR/Narration/DealerGaze.cs): his head follows the player.

  dealer_gaze.py [--start] [--targets | --dice]

--targets (2026-10-13, the coverage audit's I3): from the dungeon map, the things he looks at - the wheel and the dice as their
games start (and the dice while they roll), the hero's figurine carried over the next place, a reward appearing (the end
coins: they end the run, so last). Each: the gaze's target (status dealerGaze) names it within a few seconds.

At the card table (session.py start --snapshot magician_mid --to table with --start): the simulated head stands at five
spots (left, middle, right, crouched, tall) looking at the Dealer's face; after 1.5 s the gaze's offset (status
dealerGaze) and a shot of his face. Checks: he turns toward the player's side (the yaw changes sign between left and
right, within 30 deg), up / down within 15 deg, and with [Table] DealerGaze = false the offset springs back to 0.
The frame strip: captures/dealer-gaze-2026-10-13/index.html (the left eye, cropped round his head).
"""
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cmd  # noqa: E402
import hof2env  # noqa: E402

H = hof2env.H
OUT = os.path.join(H, "captures", "dealer-gaze-2026-10-13")
SHOTS = os.path.join(hof2env.TESTDATA, "shots")
LOG = H + "/logs/dealer_gaze.log"
REST = "hand r 0.2 1.0 -0.3 0 -60 0; hand l -0.2 1.0 -0.3 0 -60 0"
SPOTS = [("left", -0.30, 1.75, 0.0), ("middle", 0.0, 1.75, 0.0), ("right", 0.30, 1.75, 0.0), ("low", 0.0, 1.45, 0.0), ("tall", 0.0, 1.95, 0.0)]
RESULTS = []


def log(msg):
    line = time.strftime("%H:%M:%S ") + msg
    print(line, flush=True)
    with open(LOG, "a") as f:
        f.write(line + "\n")


def result(name, ok, ev):
    RESULTS.append((name, ok))
    log("%s %s - %s" % ("PASS" if ok else "FAIL", name, ev))


def send(c, t=10):
    try:
        return cmd.send(c, t)
    except SystemExit as e:
        return "ERR " + str(e)


def sim(c):
    subprocess.run(["python3", H + "/scripts/xrsim.py", c], capture_output=True, text=True)


def gaze():
    send("state")
    try:
        g = cmd.status().get("dealerGaze", "")
    except Exception:
        g = ""
    m = re.search(r"yaw (-?[\d.]+) pitch (-?[\d.]+)", g)
    return (float(m.group(1)), float(m.group(2)), g) if m else (None, None, g)


def crop(name):
    from PIL import Image
    src = os.path.join(SHOTS, name + "_eyes.png")
    if not os.path.exists(src):
        return None
    im = Image.open(src)
    w, h = im.size
    left = im.crop((0, 0, w // 2, h))
    # round the middle (the head is aimed at)
    cw, ch = left.size
    box = (int(cw * 0.25), int(ch * 0.2), int(cw * 0.75), int(ch * 0.8))
    left.crop(box).save(os.path.join(OUT, name + ".png"))
    return name + ".png"


def wait_target(word, secs):
    """Polls the gaze's target for `secs`; returns the first status naming `word`, else the last one."""
    t0, last = time.time(), ""
    while time.time() - t0 < secs:
        send("state")
        try:
            last = cmd.status().get("dealerGaze", "")
        except Exception:
            last = ""
        if ("on: " + word) in last:
            return True, last
        time.sleep(0.15)
    return False, last


def targets(dice=False):
    """The things on the table he looks at (the user 2026-10-12 22:45): the wheel, the hero's figurine moving, a reward
    appearing (the end coins: last, they end the run); with dice=True only the dice (as their game starts and while they
    roll) - run last in a session: the harness's die marking can leave the dice game open."""
    import table_sweep as ts
    send("gate auto 1.5")
    sim(REST + "; head 0 1.75 0 0 0 0")
    if not ts.map_ready(30, act=True):
        log("not on the dungeon map: %s" % ts.state())
    for label, word, kind, secs in ((("Dice", "the dice", "dice", 14),) if dice else (("Wheel", "the wheel", "stop", 14),)):
        r = send("scen here " + label)
        ok, g = wait_target(word, 4.0)
        result("gaze_target_" + label.lower(), r.startswith("running") and ok, "%s: %s | %s" % (label, r[:40], g[:120]))
        if label == "Dice":
            # thrown (once the dice wait for a throw): he follows them while they roll
            r2 = ""
            for _ in range(20):
                send("state")
                d = cmd.status().get("dice", "") or ""
                if d.startswith(("Catapault", "Ready")):
                    r2 = send("dice throw 0 0.5 2.0")
                    break
                time.sleep(0.4)
            ok2, g2 = wait_target("the dice rolling", 3.0)
            g2 = "throw: %s | %s" % (r2[:40], g2)
            result("gaze_target_dice_rolling", ok2, g2[:140])
        ts.answer(kind, time.time() + secs)
        ts.map_ready(40, act=True)
    if dice:
        return
    # the figurine: carried over the next place and set down (FigurineGrab; as interact_test.py figurine)
    import interact_test as it
    send("cfg GrabThrow true")
    w = send("fig where")
    m = re.search(r"hero top (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) \| to (.+?) top (-?[\d.]+) (-?[\d.]+) (-?[\d.]+)", w)
    if m:
        hero_above = "%s %.4f %s" % (m.group(1), float(m.group(2)) + 0.04, m.group(3))
        tile_above = "%s %.4f %s" % (m.group(5), float(m.group(6)) + 0.10, m.group(7))
        sim("ctouch r trigger 1")
        it.hand_to("r", "palm", hero_above, ms=400)
        time.sleep(0.4)
        sim("squeeze r 1")
        time.sleep(0.6)
        it.hand_to("r", "palm", tile_above, ms=900)
        ok, g = wait_target("the hero's figurine", 2.0)
        sim("squeeze r 0")
        time.sleep(0.5)
        if not ok:
            ok, g = wait_target("the hero's figurine", 4.0)   # the game's own walk after the set-down
        result("gaze_target_figurine", ok, g[:140])
        sim(REST + "; head 0 1.75 0 0 0 0")
        ts.map_ready(90, act=True)
    else:
        result("gaze_target_figurine", False, "no figurine or no place to go: " + w[:120])
    # a reward appearing: the end of the challenge's coins (they end the run)
    r = send("scen here End_coins")
    ok, g = wait_target("a reward", 20.0)
    result("gaze_target_reward", r.startswith("running") and ok, "End coins: %s | %s" % (r[:40], g[:120]))


def main():
    a = sys.argv[1:]
    os.makedirs(OUT, exist_ok=True)
    open(LOG, "a").close()
    log("==== dealer gaze" + (" (targets)" if "--targets" in a else ""))
    if "--start" in a:
        subprocess.run(["python3", H + "/scripts/session.py", "start", "--snapshot", "magician_mid", "--to", "table"], capture_output=True, text=True, timeout=4200)
    if "--targets" in a or "--dice" in a:
        send("cfg DealerGaze true")
        send("cfg Scenarios true")
        targets(dice="--dice" in a)
        fails = [n for n, ok in RESULTS if not ok]
        log("==== %d/%d PASS%s" % (len(RESULTS) - len(fails), len(RESULTS), (" - FAIL: " + ", ".join(fails)) if fails else ""))
        sys.exit(1 if fails else 0)
    send("cfg DealerGaze true")
    sim(REST + "; head 0 1.75 0 0 0 0")
    time.sleep(1.0)
    rows = []
    for name, x, y, z in SPOTS:
        h = send("headlook Dealer - Face from %.2f %.2f %.2f" % (x, y, z))
        if not h.startswith("head"):
            h = "head %.2f %.2f %.2f 0 0 0" % (x, y, z)
        sim(h)
        time.sleep(2.0)
        yv, pv, g = gaze()
        tag = "DG_" + name
        send("shot " + tag)
        time.sleep(2.0)
        rows.append((name, yv, pv, crop(tag), g))
        log("%s: yaw %s pitch %s | %s" % (name, yv, pv, g[:140]))
    by = {r[0]: r for r in rows}
    yl, yr = by["left"][1], by["right"][1]
    result("gaze_follows_sideways", yl is not None and yr is not None and yl * yr < 0 and abs(yl) <= 30.5 and abs(yr) <= 30.5 and abs(yl - yr) > 4,
           "the player 0.3 m left: yaw %s; right: %s (opposite signs, within 30 deg)" % (yl, yr))
    pl, pt = by["low"][2], by["tall"][2]
    result("gaze_follows_up_down", pl is not None and pt is not None and abs(pl) <= 15.5 and abs(pt) <= 15.5 and pl != pt,
           "crouched: pitch %s; tall: %s (within 15 deg)" % (pl, pt))
    send("cfg DealerGaze false")
    time.sleep(2.5)
    yo, po, g = gaze()
    result("gaze_off_springs_back", yo is not None and abs(yo) < 1.0 and abs(po) < 1.0, "DealerGaze = false: yaw %s pitch %s" % (yo, po))
    send("cfg DealerGaze true")
    sim(REST + "; head 0 1.75 0 0 0 0")
    figs = "".join('<figure><img src="%s"><figcaption><b>%s</b><br>his head %s deg sideways, %s deg down</figcaption></figure>' % (
        f, n, "%.1f" % yv if yv is not None else "-", "%.1f" % pv if pv is not None else "-") for n, yv, pv, f, _ in rows if f)
    html = """<!doctype html><html><head><meta charset="utf-8"><title>The Dealer notices you (2026-10-13)</title>
<style>body{background:#14110d;color:#e8dcc4;font-family:Georgia,serif;margin:24px}h1{color:#f0c879}
.row{display:flex;gap:10px;flex-wrap:wrap}figure{margin:0;width:19%%;min-width:200px}img{width:100%%;border:1px solid #6b5432}
figcaption{font-size:14px;margin-top:4px}code{color:#f0c879}</style></head><body>
<h1>The Dealer notices you</h1>
<p>Your note (12 October): "I'd like to try it." The player stood at five spots at the table (0.3 m to the left, the
middle, 0.3 m to the right, crouched, tall), looking at him; each picture 2 s later. His head turns toward you within
30 degrees to the side and 15 up or down, about half a second behind, on top of his own idle movement; now and then he
glances at the board or at what you touch. <code>[Table] DealerGaze</code> (on).</p>
<div class="row">%s</div>
<p style="margin-top:30px;font-size:13px">scripts/dealer_gaze.py in the simulator, %s. Left eye.</p></body></html>""" % (figs, time.strftime("%Y-%m-%d %H:%M"))
    with open(os.path.join(OUT, "index.html"), "w") as f:
        f.write(html)
    fails = [n for n, ok in RESULTS if not ok]
    log("==== %d checks, %d failed%s; page %s" % (len(RESULTS), len(fails), (": " + ", ".join(fails)) if fails else "", os.path.join(OUT, "index.html")))
    sys.exit(1 if fails else 0)


if __name__ == "__main__":
    main()
