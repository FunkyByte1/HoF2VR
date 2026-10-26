#!/usr/bin/env python3
"""Checks for the hand feel, Phase 1 (docs/hand-feel-design.md §4): the simulated right hand pokes the mod's test board
(`tboard`: three buttons at arm's length, the same press code as the encounter's choices) with xrsim moves.

  handfeel_test.py [test ...]      default: all
Tests:
  speeds      straight pokes at 0.1 0.3 0.6 1.0 1.4 m/s: a click each, at the fingertip (click depth <= 3.5 mm + 1 mm + a frame)
  angles      0.6 m/s pokes 0/30/50/70 degrees off straight: clicks up to 50, none at 70
  noaccident  a swipe across 5 mm in, a hover, a creep (0.02 m/s): no click
  careful     a careful press (0.08 m/s): a click
  assist      a poke 8 mm beside button 1's edge: Normal clicks (pull <= 12 mm), Off doesn't
  calm        fingertip held 1 cm in front of a button's edge with tracking noise: few hover changes (control: Off)
  stop        tracked tip 3 cm past a button: drawn finger held on it + ghost; 7 cm: let go
  preshape    the glove starts pointing 12 cm in front of a button and points fully from 6 cm
  release     after a click, backing out gives a release cue; the button springs back
  budget      30 s of pokes: at most 12 vibration calls a second per hand
  shots       stereo shots: approach (shadow, glow), contact, pushed past (ghost)
  latepose    the gloves drawn with the render-time controller pose: equal at rest, one frame ahead of Unity's at 0.5 m/s, and
              the next frame's Unity pose is this frame's late one (`lateprobe`)
  tabletop    (at the card table) 3 cm into the table from above: the glove's lowest point on the top, ghost, a knock; 9 cm: let
              go; reaching under the top from the side: not stopped
Run from a test session at the table or title (python3 scripts/session.py start --snapshot magician_mid --to table).
Results: PASS/FAIL lines on stdout and in logs/handfeel_test.log; shots testdata/shots/HF_*.
"""
import math
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cmd  # noqa: E402
import hof2env  # noqa: E402

H = hof2env.H
LOG = H + "/logs/handfeel_test.log"
GAMELOG = hof2env.GLOG
RESULTS = []
REST = "hand r 0.25 1.2 -0.15 0 -30 0; hand l -0.25 1.0 -0.2 0 -60 0; trig r 0; touch r 0; trig l 0; touch l 0"


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


def result(name, ok, evidence):
    RESULTS.append((name, ok))
    log(("PASS " if ok else "FAIL ") + name + " - " + evidence)


def nlines():
    with open(GAMELOG, errors="replace") as f:
        return sum(1 for _ in f)


def lines_since(n):
    with open(GAMELOG, errors="replace") as f:
        return f.read().splitlines()[n:]


def presses():
    m = re.search(r"pressed (\d+)", st().get("tboard", ""))
    return int(m.group(1)) if m else 0


def feel():
    return st().get("feel", "")


def vec(s):
    return [float(x) for x in s.split()]


def geo(i):
    r = send("tboard geo %d" % i)
    m = re.search(r"centre ([-\d. ]+) normal ([-\d. ]+) right ([-\d. ]+) size ([-\d. ]+) scale ([-\d.]+)", r)
    if not m:
        raise RuntimeError("tboard geo: " + r)
    return vec(m.group(1)), vec(m.group(2)), vec(m.group(3)), vec(m.group(4)), float(m.group(5))


def mv(d, ms):
    return "move r %.4f %.4f %.4f %d" % (d[0], d[1], d[2], max(1, int(ms)))


def at(i, cm, lateral=0.0, up=0.0):
    """Bring the fingertip `cm` in front of button i's centre (+ lateral metres along its right axis, + up metres)."""
    c, n, u, size, s = geo(i)
    p = send("tboard pose %d %.2f" % (i, cm)).split("|")[0].strip()
    h = send("handto r tip " + p)
    if not h.startswith("move"):
        raise RuntimeError("handto: " + h)
    seq = h + " 350; wait 120"
    if lateral or up:
        # up: the plate's own up axis = n x right (xrsim frame)
        upv = [n[1] * u[2] - n[2] * u[1], n[2] * u[0] - n[0] * u[2], n[0] * u[1] - n[1] * u[0]]
        d = [u[k] * lateral + upv[k] * up for k in range(3)]
        seq += "; " + mv(d, 150) + "; wait 100"
    sim(seq)
    time.sleep(0.75)
    return n, u


def poke(i, speed, angle=0.0, lateral=0.0, start=0.06, through=0.015, back=True):
    """A straight (or angled) poke at button i: from `start` in front to `through` past the face at `speed` m/s."""
    n, u = at(i, start * 100.0, lateral - start * math.tan(math.radians(angle)))
    dist = start + through
    t = math.tan(math.radians(angle))
    d = [-n[k] * dist + u[k] * dist * t for k in range(3)]
    length = math.sqrt(sum(x * x for x in d))
    seq = mv(d, length / speed * 1000.0) + "; wait 150"
    if back:
        seq += "; " + mv([n[k] * 0.1 for k in range(3)], 250)
    sim(seq)
    time.sleep(length / speed + 0.7)


def press_lines(n0):
    return [l for l in lines_since(n0) if "tboard: R pressed plate" in l]


def setup():
    sim(REST + "; head 0 1.75 0 0 0 0")
    send("feel reset")
    send("cfg Preset Normal")
    send("cfg StopAtSurfaces true")
    r = send("tboard show 3")
    time.sleep(0.8)
    log("  board: " + r + " | " + send("tboard geo 2"))
    return r.startswith("shown")


def t_speeds():
    ok_all, ev = True, []
    for v in (0.1, 0.3, 0.6, 1.0, 1.4):
        p0, n0 = presses(), nlines()
        poke(2, v)
        p1 = presses()
        time.sleep(1.1)   # status.json is written once a second
        m = re.search(r"last press ([-\d.]+) mm in at ([\d.]+) m/s in", st().get("tboard", ""))
        depth = float(m.group(1)) if m else -1.0
        # ClickAt + 1 mm + one frame of motion (xrsim's steps make the real speed differ from the asked one: the larger)
        vm = max(v, float(m.group(2))) if m else v
        limit = 3.5 + 1.0 + vm * 1000.0 / 60.0
        ok = p1 == p0 + 1 and 0 <= depth <= limit
        ok_all &= ok
        ev.append("%.1f m/s: %s at %.1f mm (limit %.1f)" % (v, "click" if p1 > p0 else "NO click", depth, limit))
    result("speeds", ok_all, "; ".join(ev))


def t_angles():
    ev, ok_all = [], True
    send("tboard show 1")   # one button: a slanted path must not cross a neighbour
    time.sleep(0.5)
    for a in (0, 30, 50, 70):
        p0 = presses()
        poke(1, 0.6, angle=a)
        clicked = presses() > p0
        want = a <= 50
        ok_all &= clicked == want
        ev.append("%d deg: %s" % (a, "click" if clicked else "none"))
    send("tboard show 3")
    time.sleep(0.5)
    result("angles", ok_all, "; ".join(ev) + " (want clicks up to 50)")


def t_noaccident():
    ev, ok_all = [], True
    send("tboard show 1")   # the swipe starts beside the button: no neighbour there
    time.sleep(0.5)
    for mode in ("swipe", "hover", "slow"):
        p0 = presses()
        send("tboard %s 1" % mode)
        time.sleep(2.8)
        ok = presses() == p0
        ok_all &= ok
        ev.append("%s: %s" % (mode, "none" if ok else "CLICKED"))
    send("tboard show 3")
    time.sleep(0.5)
    # the real hand creeping in at 0.02 m/s
    p0 = presses()
    n, u = at(2, 2.0)
    sim(mv([-n[k] * 0.035 for k in range(3)], 1750) + "; wait 150; " + mv([n[k] * 0.1 for k in range(3)], 250))
    time.sleep(2.6)
    ok = presses() == p0
    ok_all &= ok
    ev.append("hand creep 0.02 m/s: %s" % ("none" if ok else "CLICKED"))
    result("noaccident", ok_all, "; ".join(ev))


def t_careful():
    p0 = presses()
    poke(2, 0.08, start=0.03)
    result("careful", presses() == p0 + 1, "a careful 0.08 m/s press: %s" % ("click" if presses() > p0 else "none"))


def t_assist():
    c, n, u, size, s = geo(1)
    off = size[0] / 2 + 0.008     # 8 mm beside the left button's outer edge (its left end)
    ev, ok_all = [], True
    for preset, want in (("Normal", True), ("Off", False)):
        send("cfg Preset " + preset)
        send("feel reset")
        p0 = presses()
        poke(1, 0.3, lateral=-off)
        clicked = presses() > p0
        m = re.search(r"max stop [\d.]+ assist ([\d.]+)", feel())
        pull = float(m.group(1)) if m else -1
        ok = clicked == want and (preset == "Off" or pull <= 12.05)
        ok_all &= ok
        ev.append("%s: %s, pull %.1f mm" % (preset, "click" if clicked else "none", pull))
    send("cfg Preset Normal")
    result("assist", ok_all, "; ".join(ev) + " (8 mm beside the edge: Normal clicks with <= 12 mm pull, Off doesn't)")


def hovers():
    m = re.search(r"hovers (\d+)", st().get("tboard", ""))
    return int(m.group(1)) if m else 0


def t_calm():
    c, n, u, size, s = geo(2)
    edge = size[0] / 2 + 0.002   # right at button 2's right edge (+2 mm, inside the 4 mm margin)
    ev = []
    counts = {}
    for preset in ("Normal", "Off"):
        send("cfg Preset " + preset)
        at(2, 5.0, edge)
        sim("noise on 1.0 0.25")
        sim(mv([-n[k] * 0.04 for k in range(3)], 300))   # down to 1 cm in front
        time.sleep(1.4)
        h0 = hovers()
        time.sleep(3.0)
        counts[preset] = hovers() - h0
        sim("noise off; " + mv([n[k] * 0.08 for k in range(3)], 250))
        time.sleep(1.2)
        ev.append("%s: %d hover starts" % (preset, counts[preset]))
    send("cfg Preset Normal")
    result("calm", counts["Normal"] <= 1, "; ".join(ev) + " in 3 s, fingertip 1 cm in front of the edge, 1 mm / 0.25 deg tracking noise (3x the simulator's worn-headset default; want <= 1 with Normal)")


def t_stop():
    send("feel reset")
    n, u = at(2, 4.0)
    p0 = presses()
    # 3 cm past the face, held
    sim(mv([-n[k] * 0.07 for k in range(3)], 350))
    time.sleep(1.5)
    f3 = feel()
    send("feel probe")
    send("shot HF_stop3cm")
    time.sleep(1.5)
    m = re.search(r"R: d (-|[-\d.]+cm \w+) filter [\d.]+ assist [\d.]+ stop ([\d.]+) mm.*? ghost ([\d.]+)", f3)
    stop3 = float(m.group(2)) if m else -1
    ghost3 = float(m.group(3)) if m else -1
    # 7 cm past: let go
    sim(mv([-n[k] * 0.04 for k in range(3)], 300))
    time.sleep(1.6)   # status.json is written once a second
    f7 = feel()
    m7 = re.search(r"R: d (-|[-\d.]+cm \w+) filter [\d.]+ assist [\d.]+ stop ([\d.]+) mm", f7)
    stop7 = float(m7.group(2)) if m7 else -1
    lg = re.search(r"letgos (\d+)", f7)
    sim(mv([n[k] * 0.15 for k in range(3)], 400))
    time.sleep(1.0)
    ok = 18 <= stop3 <= 30 and ghost3 > 0 and stop7 < 2 and lg and int(lg.group(1)) >= 1
    result("stop", ok, "3 cm past: drawn held %.1f mm back (want ~24 = 30 - 6 mm travel), ghost %.2f; 7 cm past: held %.1f mm, let-gos %s; clicks +%d"
           % (stop3, ghost3, stop7, lg.group(1) if lg else "?", presses() - p0))


def t_preshape():
    vals = {}
    for cm in (15, 9, 5):
        at(2, cm)
        time.sleep(0.4)
        m = re.search(r"R: .*? point ([\d.]+)", feel())
        vals[cm] = float(m.group(1)) if m else -1
    ok = vals[15] < 0.05 and 0.05 < vals[9] < 0.95 and vals[5] > 0.95
    result("preshape", ok, "point weight at 15 / 9 / 5 cm: %.2f / %.2f / %.2f" % (vals[15], vals[9], vals[5]))


def t_release():
    m0 = re.search(r"releases (\d+)", st().get("tboard", ""))
    r0 = int(m0.group(1)) if m0 else 0
    poke(3, 0.3)
    time.sleep(0.5)
    t = st().get("tboard", "")
    m1 = re.search(r"releases (\d+)", t)
    r1 = int(m1.group(1)) if m1 else 0
    pm = re.search(r"travel ([\d.,]+)", t)
    flat = pm and all(float(x) < 0.3 for x in pm.group(1).split(","))
    result("release", r1 == r0 + 1 and bool(flat), "release cues %d -> %d; travel now %s mm" % (r0, r1, pm.group(1) if pm else "?"))


def t_budget():
    send("feel reset")
    t0 = time.time()
    k = 0
    while time.time() - t0 < 30:
        poke(1 + k % 3, 0.6, start=0.04)
        k += 1
    h = st().get("haptics", "")
    m = re.search(r"max/s L (\d+) R (\d+)", h)
    mx = max(int(m.group(1)), int(m.group(2))) if m else 99
    perf = [l for l in lines_since(max(0, nlines() - 400)) if "haptics: a vibration request took" in l]
    result("budget", mx <= 12, "%d pokes in 30 s; at most %d calls in a second per hand; slow calls logged: %d" % (k, mx, len(perf)))


def t_shots():
    sim("head 0 1.75 0 0 -32 0")
    time.sleep(0.4)
    at(2, 3.0)
    send("shot HF_approach")
    time.sleep(1.5)
    n, u = at(2, 0.4)
    send("shot HF_contact")
    time.sleep(1.5)
    sim(mv([-n[k] * 0.034 for k in range(3)], 300))
    time.sleep(0.6)
    send("shot HF_pushed3cm")
    time.sleep(1.5)
    send("cfg StopAtSurfaces false")
    time.sleep(0.3)
    send("shot HF_pushed3cm_nostop")
    time.sleep(1.5)
    send("cfg StopAtSurfaces true")
    sim(mv([n[k] * 0.15 for k in range(3)], 300) + "; head 0 1.75 0 0 0 0")
    time.sleep(0.6)
    result("shots", True, "testdata/shots/HF_approach, HF_contact, HF_pushed3cm, HF_pushed3cm_nostop, HF_stop3cm (_eyes.png) for review")


# ---- Phase 1, the rest: the render-time pose (§3.2) and the table top (§3.3) ------------------------------------------------
def probe_rows(n0, timeout=6):
    """The rows of the next `lateprobe` dump after log line n0: frame late lx ly lz rx ry rz gx gy gz ang hx hy hz ox oy oz."""
    deadline = time.time() + timeout
    while time.time() < deadline:
        ls = lines_since(n0)
        for i, l in enumerate(ls):
            if "lateprobe:" in l:
                rows = []
                for r in ls[i + 1:]:
                    p = r.split()
                    if len(p) == 18 and p[0].isdigit():
                        rows.append([float(x) for x in p])
                    elif rows:
                        break
                if rows:
                    return rows
        time.sleep(0.3)
    return []


def t_latepose():
    """The gloves are drawn with the controllers' render-time pose, a frame fresher than Unity's (§3.2)."""
    sim(REST + "; hand r 0.2 1.3 -0.35 0 -30 0")
    time.sleep(0.6)
    n0 = nlines()
    send("lateprobe 30")
    time.sleep(1.2)
    rest = probe_rows(n0)
    n1 = nlines()
    send("lateprobe 50")
    sim("move r 0 0 -0.4 800")   # 0.5 m/s
    time.sleep(1.2)
    mov = probe_rows(n1)

    def d(a, b):
        return math.dist(a, b) * 1000.0
    if not rest or not mov:
        result("latepose", False, "no probe rows (rest %d, moving %d)" % (len(rest), len(mov)))
        return
    late_all = all(r[1] == 1 for r in rest + mov)
    rest_d = max(d(r[2:5], r[5:8]) for r in rest)
    heads = [d(r[12:15], r[15:18]) for r in rest + mov if not math.isnan(r[15])]
    head_d = max(heads) if heads else -1
    glove_d = max(d(r[8:11], r[5:8]) for r in rest + mov)
    moving = [r for r in mov if d(r[2:5], r[5:8]) > 0.5]
    lead = sum(d(r[2:5], r[5:8]) for r in moving) / len(moving) if moving else 0.0
    steps = [d(a[2:5], b[2:5]) for a, b in zip(mov, mov[1:]) if b[0] == a[0] + 1 and d(a[2:5], b[2:5]) > 0.5]
    step = sum(steps) / len(steps) if steps else 0.0
    pairs = [(a, b) for a, b in zip(mov, mov[1:]) if b[0] == a[0] + 1]
    same = sum(1 for a, b in pairs if d(a[5:8], b[2:5]) < 1.0)
    ok = late_all and rest_d < 1.0 and 0 <= head_d < 1.0 and glove_d < 0.5 and moving and 0.6 * step <= lead <= 1.6 * step and same >= 0.8 * len(pairs)
    result("latepose", ok, "every frame late %s; at rest late-latched %.2f mm; runtime head vs Unity's late head %.2f mm; moving 0.5 m/s: the glove "
           "%.1f mm ahead of Unity's pose (one frame of motion = %.1f mm), drawn at the late pose within %.2f mm; next frame's Unity pose = this "
           "frame's late pose in %d of %d" % (late_all, rest_d, head_d, lead, step, glove_d, same, len(pairs)))


def tabletop():
    send("state")
    return st().get("tableTop", "")


def tt_side(s, side="R"):
    m = re.search(side + r" (contact|armed|-)( \(off the table\))? top ([-\w.]+) low ([-\d.]+) drawn ([-\d.]+)", s)
    if not m:
        return None
    return m.group(1), m.group(2) is None, float(m.group(3)), float(m.group(4)), float(m.group(5))


def t_tabletop():
    """The card table's top stops the glove (§3.3): from above, 3 cm in -> its lowest point rests on the top, the ghost shows,
    a knock; 9 cm in -> let go; a hand that reaches under the top from the side (not from above) is not stopped."""
    if "Caravan" not in send("place"):
        result("tabletop", False, "not at the card table (%s)" % send("place")[:60])
        return
    send("feel reset")
    sim(REST + "; hand r 0.1 1.15 -0.45 0 -40 0")
    time.sleep(0.8)
    a = tt_side(tabletop())
    if not a or a[0] != "armed" or not a[1]:
        result("tabletop", False, "the hand is not armed above the table: %s" % tabletop()[:160])
        return
    top, low = a[2], a[3]
    log("  tabletop: armed, the glove's lowest point %.4f, the top %.4f: the push %.3f m in 0.4 s (%.2f m/s; a knock from 0.25)"
        % (low, top, low - top + 0.03, (low - top + 0.03) / 0.4))
    k0 = re.search(r"knocks (\d+)", tabletop())
    k0 = int(k0.group(1)) if k0 else 0
    sim("move r 0 %.4f 0 400" % -(low - top + 0.03))   # 3 cm into the table at ~0.36 m/s
    time.sleep(1.3)
    b = tt_side(tabletop())
    f3 = feel()
    g = re.search(r"R: .*? ghost ([\d.]+)", f3)
    ghost = float(g.group(1)) if g else -1
    k1 = re.search(r"knocks (\d+)", tabletop())
    k1 = int(k1.group(1)) if k1 else 0
    lc = re.search(r"last (.*)$", tabletop())
    log("  tabletop: the contact: %s" % (lc.group(1)[:80] if lc else "-"))
    sim("move r 0 -0.06 0 300")                          # 9 cm in: let go
    time.sleep(1.3)
    c = tt_side(tabletop())
    sim("move r 0 0.3 0 500")                            # above again
    time.sleep(0.6)
    # under the top from the side: off the table near the body, down, then forward under it
    sim("hand r 0.1 %.4f -0.02 0 -40 0" % (1.15 - (low - top) - 0.03))
    time.sleep(0.5)
    d0 = tt_side(tabletop())
    sim("move r 0 0 -0.43 600")
    time.sleep(1.3)
    e = tt_side(tabletop())
    sim(REST)
    rest_ok = b and b[0] == "contact" and abs(b[4] - top) < 0.002 and ghost > 0
    letgo_ok = c and abs(c[4] - c[3]) < 0.002
    side_ok = d0 and not d0[1] and e and e[1] and e[0] != "contact" and abs(e[4] - e[3]) < 0.002 and e[3] < top
    ok = bool(rest_ok and letgo_ok and side_ok and k1 == k0 + 1)
    result("tabletop", ok, "top %.4f: 3 cm in -> %s, drawn lowest point %.1f mm from the top, ghost %.2f, knocks +%d; 9 cm in -> drawn %.1f mm from the real "
           "hand (let go); from the side under the top -> %s, drawn %.1f mm from the real hand"
           % (top, b[0] if b else "?", (b[4] - top) * 1000 if b else -1, ghost, k1 - k0, (c[4] - c[3]) * 1000 if c else -1,
              (e[0] + ("" if e[1] else " off")) if e else "?", (e[4] - e[3]) * 1000 if e else -1))


TESTS = {"speeds": t_speeds, "angles": t_angles, "noaccident": t_noaccident, "careful": t_careful, "assist": t_assist,
         "calm": t_calm, "stop": t_stop, "preshape": t_preshape, "release": t_release, "budget": t_budget, "shots": t_shots,
         "latepose": t_latepose, "tabletop": t_tabletop}


def main():
    names = sys.argv[1:] or list(TESTS)
    log("=== handfeel_test %s" % " ".join(names))
    if not setup():
        result("setup", False, "no test board")
        sys.exit(1)
    for n in names:
        try:
            TESTS[n]()
        except Exception as e:  # noqa: BLE001
            result(n, False, "error: %s" % e)
        sim(REST)
        time.sleep(0.3)
    send("tboard hide")
    fails = [n for n, ok in RESULTS if not ok]
    log("=== %d checks, %d failed%s" % (len(RESULTS), len(fails), (": " + ", ".join(fails)) if fails else ""))
    sys.exit(1 if fails else 0)


if __name__ == "__main__":
    main()
