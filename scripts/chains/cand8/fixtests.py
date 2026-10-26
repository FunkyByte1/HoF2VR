#!/usr/bin/env python3
"""Candidate 8: the playtest fixes' checks without a test script of their own (env 1), from a session at the map:
  slider_handcheck  the hand check's Tilt slider dragged by a simulated fingertip (the fit follows)
  slider_vrsettings the VR settings page's first number row on the Turning tab dragged (the setting follows)
  t6_fan            the stat fan put on the map's middle steps off it ("the stat cards moved")
Prints PASS / FAIL lines (as the other tests) and "N/M passed"."""
import re
import sys
import time

import os
H = os.path.realpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", ".."))   # this worktree's (its env's game)
sys.path.insert(0, H + "/scripts")
sys.path.insert(0, H + "/scripts/chains")
import cmd as C  # noqa: E402
import pt17  # noqa: E402
from pt17_tests2 import drag, st, gamelog  # noqa: E402

RES = []


def result(name, ok, ev):
    RES.append((name, ok))
    print("%s %s %s - %s" % (time.strftime("%H:%M:%S"), "PASS" if ok else "FAIL", name, ev[:300]), flush=True)


def slider_handcheck():
    C.send("handcheck start", 5)
    time.sleep(2.0)
    m = re.search(r"tilt (-?[\d.]+)", C.send("handcheck state", 5))
    now = float(m.group(1)) if m else 20.0
    target = now + 10 if now < 30 else now - 10
    r = drag("handcheck slider", 1, now, target)
    fit = re.search(r"fit tilt (-?[\d.]+)", C.send("handcheck state", 5))
    got = float(fit.group(1)) if fit else None
    result("slider_handcheck", got is not None and abs(got - target) <= 1.0 and "drags" in r, "tilt %s -> %s (wanted %s) | %s" % (now, got, target, r))
    C.send("handcheck close", 5)
    time.sleep(1.0)


def slider_vrsettings():
    C.send("press START 6", 5)
    time.sleep(2.0)
    C.send("vrset open Turning", 5)
    time.sleep(1.5)
    for i in range(1, 10):
        r = C.send("vrslider %d 0 0.8" % i, 5)
        m = re.search(r"\| (.+?) (-?[\d.]+) drags", r)
        if not m:
            continue
        name, now = m.group(1), float(m.group(2))
        target = now * 0.5 if now > 0 else 1.0
        r2 = drag("vrslider", i, now, target)
        vs = st().get("vrsettings", "")
        v = re.search(r"%s=(-?[\d.]+)" % re.escape(name), vs)
        got = float(v.group(1)) if v else None
        result("slider_vrsettings", got is not None and got < now, "%s %s -> %s | %s" % (name, now, got, r2))
        break
    else:
        result("slider_vrsettings", False, "no number row with a slider on the Turning tab: " + st().get("vrsettings", "")[:200])
    C.send("vrset close", 5)
    time.sleep(1.0)
    C.send("press START 6", 5)
    time.sleep(2.0)


def t6_fan():
    n0 = gamelog().count("stat cards moved")
    C.send("tablehud -0.10 0.50 0.06 0", 5)
    time.sleep(2.5)
    moved = [l for l in gamelog().splitlines() if "stat cards moved" in l]
    result("t6_fan", len(moved) > n0, (moved[-1][-70:] if moved else "never moved") + " | " + st().get("tableHud", "")[:120])
    C.send("tablehud -0.56 0.26 0.06 0", 5)
    time.sleep(1.0)


def main():
    for f in (slider_handcheck, slider_vrsettings, t6_fan):
        try:
            f()
        except Exception as e:
            result(f.__name__, False, "exception: %s" % e)
    ok = sum(1 for _, o in RES if o)
    print("%s fixtests: %d/%d passed" % (time.strftime("%H:%M:%S"), ok, len(RES)), flush=True)


if __name__ == "__main__":
    main()
