#!/usr/bin/env python3
"""The bug hunt of 2026-10-18 (captures/audit-2026-10-18/core-table.md): its fixes that a test at the Dealer's table can show.

  audit_fixes_test.py   (from a session at the table: session.py start --snapshot magician_mid --to table)

 7  a controller whose state can't be read lets go: the left grip held (xrsim squeeze), `pad l lost 900` -> let go after
    0.25 s, held again once it can be read
 10 the hidden glove-fit gesture (both grips + the right stick): at the table nothing; in the hand check, after the grips'
    dwell, the fit changes (and is undone the same way)
 11 the hand check's glove fit is kept when the check closes by itself (a recentre), not only on Back / Next
 12 a fade-snap that starts while a snap turn's quick blink fades in keeps its own timing (0.18 s in, its end marked)
Results: PASS/FAIL in logs/audit_fixes_test.log.
"""
import os
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import playtest as pt  # noqa: E402

pt.LOG = pt.H + "/logs/audit_fixes_test.log"
log, sim, send, st, result = pt.log, pt.sim, pt.send, pt.st, pt.result


def grip(side):
    send("state")
    b = st().get("buttons", "")
    part = b.split("|")[0 if side == "l" else 1] if "|" in b else ""
    m = re.search(r"grip=([\d.]+)", part)
    return float(m.group(1)) if m else -1.0


def body(n0):
    lines, _ = pt.gamelog_since(n0)
    return [l.split("] ", 1)[-1] for l in lines]


def wait_lines(n0, sub, count=1, timeout=4.0):
    """The mod's log lines since n0 that contain `sub`, waiting up to `timeout` for `count` of them (the log is written a moment
    after the lines happen)."""
    t0 = time.time()
    while True:
        got = [b for b in body(n0) if sub in b]
        if len(got) >= count or time.time() - t0 > timeout:
            return got
        time.sleep(0.4)


def lost_controller():
    sim("squeeze l 1")
    time.sleep(0.6)
    g0 = grip("l")
    n0 = pt.nlines()
    r = send("pad l lost 900")
    time.sleep(0.55)
    g1 = grip("l")
    time.sleep(0.9)
    g2 = grip("l")
    sim("squeeze l 0")
    let = wait_lines(n0, "controller's state can't be read", 1, 3.0)
    result("f7_lost_controller_lets_go", g0 >= 0.9 and g1 == 0.0 and g2 >= 0.9 and bool(let),
           "left grip held %.1f, unreadable %.1f, readable again %.1f | %s | %s" % (g0, g1, g2, r[:60], let[0][:100] if let else "no let-go line"))


def fit_gesture():
    sim(pt.REST + "; head 0 1.75 0 0 0 0")
    time.sleep(0.5)
    n0 = pt.nlines()
    # at the table: two fists and the right stick (as when holding a card in each hand and nudging the stick)
    sim("squeeze l 1; squeeze r 1")
    time.sleep(1.0)
    sim("stick r 0 1 1000")
    time.sleep(1.3)
    sim("squeeze l 0; squeeze r 0")
    time.sleep(0.6)
    at_table = wait_lines(n0, "glove fit adjusted in VR", 1, 2.5)
    result("f10_fit_gesture_not_at_table", not at_table, at_table[0][:120] if at_table else "both grips + the right stick at the table: the fit unchanged")
    # in the hand check: the grips held still first (the dwell), then the stick
    send("handcheck start")
    time.sleep(1.5)
    n1 = pt.nlines()
    sim("squeeze l 1; squeeze r 1")
    time.sleep(1.0)
    sim("stick r 0 1 1000")
    time.sleep(1.3)
    sim("squeeze l 0; squeeze r 0")
    time.sleep(0.6)
    fit = wait_lines(n1, "glove fit adjusted in VR", 1, 4.0)
    # (undone: the stick the other way as long)
    sim("squeeze l 1; squeeze r 1")
    time.sleep(1.0)
    sim("stick r 0 -1 1000")
    time.sleep(1.3)
    sim("squeeze l 0; squeeze r 0")
    time.sleep(0.6)
    back = wait_lines(n1, "glove fit adjusted in VR", 2, 4.0)
    result("f10_fit_gesture_in_hand_check", bool(fit), "%s | undone: %s" % (fit[0][:110] if fit else "the gesture did nothing in the hand check", back[-1][:80] if len(back) > 1 else "-"))


def hand_check_close_saves():
    send("handcheck start")
    time.sleep(1.5)
    n0 = pt.nlines()
    send("handcheck tap 1")   # one step of the first axis
    time.sleep(1.2)
    send("recenter")          # the check closes by itself (the place's version changed)
    wait_lines(n0, "handcheck: closed (", 1, 4.0)
    b = body(n0)
    saved = [i for i, x in enumerate(b) if x.startswith("handcheck: glove fit")]
    closed = [i for i, x in enumerate(b) if x.startswith("handcheck: closed (")]
    result("f11_fit_kept_when_check_closes", bool(saved) and bool(closed) and saved[0] <= closed[0],
           "%s | %s" % (b[saved[0]][:100] if saved else "no fit saved", b[closed[0]][:60] if closed else "not closed"))
    # (the step undone: its opposite, then the seam's close - saved again, this run only)
    send("handcheck start")
    time.sleep(1.5)
    send("handcheck tap 2")
    time.sleep(1.2)
    send("handcheck close")
    time.sleep(0.5)


def blink_then_snap():
    time.sleep(2.5)   # (no fade in the last 2 s: the plain timing applies)
    n0 = pt.nlines()
    send("fadetest blinksnap")
    time.sleep(1.0)
    t = wait_lines(n0, "fadetest blinksnap:", 1, 4.0)
    m = re.search(r"fade-in took ([\d.]+) s", t[0]) if t else None
    took = float(m.group(1)) if m else -1
    result("f12_snap_after_blink_own_timing", took >= 0.15 and "its end marked" in t[0] if t else False, t[0][:200] if t else "no result line")


def main():
    open(pt.LOG, "a").close()
    log("==== audit fixes test")
    for name, fn in (("f7", lost_controller), ("f10", fit_gesture), ("f11", hand_check_close_saves), ("f12", blink_then_snap)):
        try:
            fn()
        except Exception as e:  # noqa: BLE001
            result(name, False, "error: %s" % e)
    with open(pt.GAMELOG, errors="replace") as f:
        exc = sum(1 for l in f if "Exception" in l)
    result("audit_no_exceptions", exc == 0, "%d exception lines in the mod's log" % exc)
    ok = sum(1 for _, v in pt.RESULTS if v)
    log("==== %d/%d PASS" % (ok, len(pt.RESULTS)))
    sys.exit(0 if ok == len(pt.RESULTS) else 1)


if __name__ == "__main__":
    main()
