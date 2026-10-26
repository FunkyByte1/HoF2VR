#!/usr/bin/env python3
"""Checks for the one-minute hand check (src/HoF2VR/VR/HandCheck.cs), at the title of a test session.

  handcheck_test.py
Steps: the title offer (allowed in this test run by `handcheck auto`), Start, the glove page, the fingertip page (the
simulated hands' fingertips 1.2 cm apart -> measured), the vibration ladder (Nothing -> 3 ms, Felt it -> floor 3 ms),
the feel page (a locked button refuses, Done), Close; then the dev jump menu (both sticks held, trigger) starts it
again. Nothing may be written to the player's settings file (test run). Shots: testdata/shots/HC_*.
Results: PASS/FAIL lines on stdout and in logs/handcheck_test.log.
"""
import hashlib
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cmd  # noqa: E402
import hof2env  # noqa: E402

H = hof2env.H
LOG = H + "/logs/handcheck_test.log"
CFG = os.path.join(hof2env.G, "BepInEx", "config", "flat2vr.hof2vr.cfg")
RESULTS = []


def log(msg):
    line = time.strftime("%H:%M:%S ") + msg
    print(line, flush=True)
    with open(LOG, "a") as f:
        f.write(line + "\n")


def sim(c):
    subprocess.run(["python3", H + "/scripts/xrsim.py", c], capture_output=True, text=True)


def send(c, t=8):
    try:
        return cmd.send(c, t)
    except SystemExit as e:
        return "ERR " + str(e)


def st():
    for _ in range(5):
        try:
            return cmd.status()
        except Exception:
            time.sleep(0.2)
    return {}


def hc():
    return st().get("handcheck", "")


def page():
    return hc().split(" |")[0].strip()


def result(name, ok, evidence):
    RESULTS.append((name, ok))
    log(("PASS " if ok else "FAIL ") + name + " - " + evidence)


def tap(i, wait=1.6):
    send("handcheck tap %d" % i)
    time.sleep(wait)


def wait_page(p, timeout=6):
    t0 = time.time()
    while time.time() - t0 < timeout:
        if page() == p:
            return True
        time.sleep(0.4)
    return False


def shot(name):
    send("shot " + name)
    time.sleep(1.6)


def cfg_hash():
    with open(CFG, "rb") as f:
        return hashlib.sha1(f.read()).hexdigest()[:10]


def main():
    log("=== handcheck_test")
    h0 = cfg_hash()
    sim("head 0 1.75 0 0 -20 0; hand r 0.22 1.25 -0.25 0 -30 0; hand l -0.22 1.25 -0.25 0 -30 0; trig r 0; touch r 0; trig l 0; touch l 0")
    send("handcheck close")
    send("handcheck auto")
    result("offer_at_title", wait_page("Offer", 8), "page %s (state %s)" % (page(), st().get("state")))
    shot("HC_offer")
    tap(1)
    result("start", wait_page("Fit"), "after Start: %s" % page())
    shot("HC_fit")
    # both fingertips 1.2 cm apart, 15 cm in front of the first button: the gap shows on the fit page as a guide
    p = send("handcheck pose 1 15").split("|")[0].split()
    x, y, z = (float(v) for v in p)
    ml = send("handto l tip %.4f %.4f %.4f" % (x - 0.006, y, z))
    mr = send("handto r tip %.4f %.4f %.4f" % (x + 0.006, y, z))
    sim(ml + " 300; " + mr + " 300")
    time.sleep(2.5)
    m = re.search(r"gap ([\d.]+) cm", hc())
    gap = float(m.group(1)) if m else -1
    result("tips_measured", m is not None and 0.6 <= gap <= 2.0, "gap %s cm (hands placed 1.2 cm apart in the world = %.1f cm real at this scale)" % (m.group(1) if m else "-", 1.2))
    shot("HC_tips")
    sim("hand r 0.22 1.25 -0.25 0 -30 0; hand l -0.22 1.25 -0.25 0 -30 0")
    time.sleep(0.8)
    # the six ways of the fit (the user: "an option to rotate hands along the other axes, which I need"): one + each
    def fit():
        m = re.search(r"fit tilt ([-\d.]+)\D+turn ([-\d.]+)\D+roll ([-\d.]+)\D+up ([-\d.]+) cm, forward ([-\d.]+) cm, side ([-\d.]+) cm", hc())
        return [float(v) for v in m.groups()] if m else None
    f0 = fit()
    for b in (2, 4, 6, 8, 10, 12):
        tap(b, 1.8)
    f1 = fit()
    want = [1, 1, 1, 0.25, 0.25, 0.25]
    ok = f0 is not None and f1 is not None and all(abs((f1[i] - f0[i]) - want[i]) < 0.02 for i in range(6))
    result("fit_six_ways", ok, "fit %s -> %s" % (f0, f1))
    shot("HC_fit_moved")
    tap(14, 1.8)   # Reset
    f2 = fit()
    result("fit_reset", f2 is not None and abs(f2[0] - 20) < 0.05 and abs(f2[1]) < 0.05 and abs(f2[2]) < 0.05 and abs(f2[3] - 1.75) < 0.02 and abs(f2[4] - 2.5) < 0.02 and abs(f2[5]) < 0.02,
           "after Reset: %s" % (f2,))
    tap(13)   # Back: closes from the first page
    result("fit_back_closes", wait_page("Off"), "after Back: %s" % page())
    send("handcheck start")
    wait_page("Fit")
    tap(15)   # Next
    result("buzz_page", wait_page("Buzz"), "after Next: %s" % page())
    time.sleep(1.6)
    h1 = st().get("haptics", "")
    s0 = re.search(r"strength ([\d.]+)", hc())
    tap(4, 1.8)   # Weaker
    s1 = re.search(r"strength ([\d.]+)", hc())
    result("vibration_weaker", s0 and s1 and abs(float(s0.group(1)) - float(s1.group(1)) - 0.25) < 0.01, "touch vibrations %s -> %s" % (s0.group(1) if s0 else "-", s1.group(1) if s1 else "-"))
    tap(5, 1.8)   # Stronger (back)
    tap(2)   # Nothing -> 3 ms
    time.sleep(1.0)
    tap(1)   # Felt it
    ok = wait_page("Feel")
    fl = re.search(r"floor ([\d.]+) ms", hc())
    result("buzz_floor", ok and fl is not None and fl.group(1) == "3", "page %s, floor %s ms; haptics before: %s" % (page(), fl.group(1) if fl else "-", h1[:60]))
    shot("HC_feel")
    r0 = re.search(r"refused (\d+)", hc())
    tap(2)   # the locked button
    r1 = re.search(r"refused (\d+)", hc())
    result("locked_refuses", r0 and r1 and int(r1.group(1)) == int(r0.group(1)) + 1, "refusals %s -> %s" % (r0.group(1) if r0 else "-", r1.group(1) if r1 else "-"))
    tap(3)
    result("done_page", wait_page("Done"), "after Done: %s" % page())
    shot("HC_done")
    tap(1)
    result("closed", wait_page("Off"), "after Close: %s" % page())
    # the dev jump menu: B + Y held (DevChord), then a trigger pull
    sim("hold l b 1; hold r b 1; wait 1700; hold l b 0; hold r b 0; wait 300; btn r trigger 150")
    time.sleep(0.5)
    result("jump_menu_starts_it", wait_page("Fit", 5), "after B + Y + trigger: %s" % page())
    send("handcheck close")
    h2 = cfg_hash()
    result("nothing_saved_in_test", h0 == h2, "settings file sha1 %s -> %s" % (h0, h2))
    fails = [n for n, ok in RESULTS if not ok]
    log("=== %d checks, %d failed%s" % (len(RESULTS), len(fails), (": " + ", ".join(fails)) if fails else ""))
    sys.exit(1 if fails else 0)


if __name__ == "__main__":
    main()
