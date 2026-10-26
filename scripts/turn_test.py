#!/usr/bin/env python3
"""Turning and the comfort tunnel in a fight (the coverage audit's C4, 2026-10-13: not re-checked since 10 October although
Comfort.cs and CombatVR.cs changed): snap angle, smooth speed, the snap blink, the turn and walk vignettes.

  turn_test.py [--no-fixture]

In a live fight (a goblin ambush by the dev jump from the dungeon map, god mode, the enemies killed so nothing pushes the
view), the simulated right / left stick (xrsim) and the per-frame turn log (`turnlog`, Diag/TurnLog.cs: the view's yaw,
the tunnel's strength and cause, the fade, the quick blinks). Each setting for this run only (`cfg`):
  smooth_120 / smooth_60   SnapTurn false, SmoothTurnSpeed 120 / 60: the yaw's rate while the stick is held = the speed
                           (within 8 %), no step larger than the rate allows
  turn_vignette            TurnVignette 0.5: the tunnel closes to 0.5 x SmoothStep(rate / 90) while turning (cause turn),
                           and opens after; TurnVignette 0: no tunnel
  snap_30 / snap_45        SnapTurn true, SnapTurnAngle 30 / 45: one step of exactly that per flick, both ways; a stick
                           held out repeats it every 0.3 s (the design since 10 October)
  snap_blink               SnapTurnBlink true: one quick blink per snap (the fade dark at the step); false: none
  walk_vignette            WalkVignette 0.4: walking with the left stick closes the tunnel to ~0.4 (cause walk)
Results: PASS/FAIL in logs/turn_test.log.
"""
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import playtest as pt  # noqa: E402

H = pt.H
pt.LOG = H + "/logs/turn_test.log"
log, sim, send, st, result = pt.log, pt.sim, pt.send, pt.st, pt.result


def rows():
    send("turnlog dump", 15)
    out = []
    with open(pt.hof2env.TESTDATA + "/probes/turnlog.tsv") as f:
        f.readline()
        for line in f:
            p = line.rstrip("\n").split("\t")
            if len(p) < 11:
                continue
            out.append(dict(t=float(p[0]), rx=float(p[1]), lx=float(p[3]), ly=float(p[4]), yaw=float(p[5]), vig=float(p[6]), cause=p[7],
                            fade=float(p[8]), blinks=int(p[9]), snaps=int(p[10])))
    return out


def unwrap(ys):
    out, off = [], 0.0
    for i, y in enumerate(ys):
        if i and y - ys[i - 1] > 180:
            off -= 360
        elif i and y - ys[i - 1] < -180:
            off += 360
        out.append(y + off)
    return out


def record(cmds, secs):
    send("turnlog on")
    time.sleep(0.3)
    sim(cmds)
    time.sleep(secs)
    send("turnlog off")
    return rows()


def smooth(speed):
    send("cfg SnapTurn false")
    send("cfg SmoothTurnSpeed %d" % speed)
    send("cfg TurnVignette 0.5")
    r = record("stick r 1 0 1500; wait 600", 2.6)
    held = [w for w in r if w["rx"] > 0.95 and w["yaw"] == w["yaw"]]
    if len(held) < 10:
        return result("smooth_%d" % speed, False, "too few frames with the stick held (%d)" % len(held))
    ys = unwrap([w["yaw"] for w in held])
    rate = (ys[-1] - ys[0]) / max(1e-3, held[-1]["t"] - held[0]["t"])
    steps = [abs(ys[i + 1] - ys[i]) / max(1e-4, held[i + 1]["t"] - held[i]["t"]) for i in range(len(ys) - 1)]
    result("smooth_%d" % speed, abs(rate - speed) <= 0.08 * speed and max(steps) <= speed * 1.6,
           "held %.2f s: %.1f deg/s (setting %d); the fastest frame %.0f deg/s" % (held[-1]["t"] - held[0]["t"], rate, speed, max(steps)))
    return r, rate


def main():
    a = sys.argv[1:]
    open(pt.LOG, "a").close()
    log("==== turn test")
    sim("seat 0; " + pt.REST + "; head 0 1.75 0 0 0 0")
    if "Level_Play" not in pt.state():
        send("gate auto 1")
        send("cfg Scenarios true")
        for _ in range(3):
            if pt.map_ready():
                break
            pt.answer_page()
            time.sleep(2)
        send("jump fight EL_Ambush_Goblins")
        for _ in range(40):
            time.sleep(1.5)
            s = pt.state()
            if "Level_Play" in s:
                break
            if "Level_In" in s:
                send("gate go")
                if "Showcase" in (st().get("menu") or ""):
                    sim("btn r a 150")
            else:
                pt.answer_page()
    if "Level_Play" not in pt.state():
        result("fight", False, "never in a fight: %s" % pt.state())
        sys.exit(1)
    send("god on")
    send("killall")
    time.sleep(4)
    # smooth turning at two speeds; the turn vignette
    r120 = smooth(120)
    if r120:
        r, rate = r120
        turning = [w for w in r if w["rx"] > 0.95]
        peak = max((w["vig"] for w in turning), default=0.0)
        want = 0.5 * min(1.0, rate / 90.0) ** 2 * (3 - 2 * min(1.0, rate / 90.0))
        causes = set(w["cause"] for w in turning if w["vig"] > 0.05)
        after = [w for w in r if w["t"] > (turning[-1]["t"] + 0.5 if turning else 0)]
        result("turn_vignette", abs(peak - want) <= 0.05 and causes <= {"turn"} and (not after or after[-1]["vig"] < 0.05),
               "turning at %.0f deg/s: the tunnel %.2f (want %.2f), causes %s; 0.5 s after the stick: %.2f" % (rate, peak, want, sorted(causes), after[-1]["vig"] if after else -1))
    smooth(60)
    send("cfg TurnVignette 0")
    send("cfg SmoothTurnSpeed 120")
    r = record("stick r 1 0 1000; wait 400", 1.8)
    peak0 = max((w["vig"] for w in r if w["rx"] > 0.95), default=0.0)
    result("turn_vignette_off", peak0 < 0.02, "TurnVignette 0: the tunnel's peak while turning %.3f" % peak0)
    send("cfg TurnVignette 0.5")
    # snap turns, both ways, with and without the blink
    send("cfg SnapTurn true")
    for angle in (30, 45):
        send("cfg SnapTurnAngle %d" % angle)
        send("cfg SnapTurnBlink false")
        # (a snap per push; a stick held out repeats it every 0.3 s - the design since 10 October: "hold to keep turning")
        r = record("stick r 1 0 180; wait 500; stick r -1 0 180; wait 500; stick r 1 0 1000; wait 400", 3.2)
        ys = unwrap([w["yaw"] for w in r if w["yaw"] == w["yaw"]])
        ts = [r[i + 1]["t"] for i in range(len(ys) - 1) if abs(ys[i + 1] - ys[i]) > 1.0]
        jumps = [round(ys[i + 1] - ys[i], 1) for i in range(len(ys) - 1) if abs(ys[i + 1] - ys[i]) > 1.0]
        held = [round(ts[i + 1] - ts[i], 2) for i in range(2, len(ts) - 1)]
        snaps = r[-1]["snaps"] - r[0]["snaps"] if r else 0
        want = [float(angle), float(-angle)] + [float(angle)] * 4
        result("snap_%d" % angle, jumps == want and snaps == 6 and all(0.27 <= h <= 0.4 for h in held),
               "two flicks then the stick held 1.0 s: yaw steps %s (want %s), snap count +%d, the held repeats %s s apart" % (jumps, want, snaps, held))
    send("cfg SnapTurnBlink true")
    r = record("stick r 1 0 180; wait 500; stick r -1 0 180; wait 500", 1.6)
    blinks = r[-1]["blinks"] - r[0]["blinks"] if r else 0
    dark_at_step = 0
    ys = [w["yaw"] for w in r]
    for i in range(1, len(r)):
        if abs(((ys[i] - ys[i - 1]) + 180) % 360 - 180) > 1.0 and max(w["fade"] for w in r[max(0, i - 2):i + 2]) >= 0.99:
            dark_at_step += 1
    result("snap_blink", blinks == 2 and dark_at_step == 2, "SnapTurnBlink on: %d quick blinks for 2 snaps, the view dark at %d of the steps" % (blinks, dark_at_step))
    send("cfg SnapTurnBlink false")
    r = record("stick r 1 0 180; wait 500", 0.9)
    blinks = r[-1]["blinks"] - r[0]["blinks"] if r else 0
    result("snap_no_blink", blinks == 0, "SnapTurnBlink off: %d quick blinks" % blinks)
    send("cfg SnapTurn false")
    # walking with the left stick: the walk tunnel
    send("cfg WalkVignette 0.4")
    r = record("stick l 0 1 1500; wait 600", 2.4)
    walking = [w for w in r if w["ly"] > 0.95]
    peak = max((w["vig"] for w in walking), default=0.0)
    causes = set(w["cause"] for w in walking if w["vig"] > 0.05)
    result("walk_vignette", 0.33 <= peak <= 0.42 and causes <= {"walk"}, "walking: the tunnel %.2f (WalkVignette 0.4), causes %s" % (peak, sorted(causes)))
    for k in ("SnapTurn false", "SnapTurnAngle 30", "SnapTurnBlink false", "SmoothTurnSpeed 120", "TurnVignette 0.5", "WalkVignette 0.4"):
        send("cfg " + k)
    send("god off")
    ok = sum(1 for _, v in pt.RESULTS if v)
    log("==== %d/%d PASS" % (ok, len(pt.RESULTS)))
    sys.exit(0 if ok == len(pt.RESULTS) else 1)


if __name__ == "__main__":
    main()
