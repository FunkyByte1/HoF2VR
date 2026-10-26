#!/usr/bin/env python3
"""Checks for the user's 2026-10-11 morning feedback (title menu, table spot, reach, story text, choice board).

  boards_test.py title         at the title: menu inside the panel, unchanged when the head turns toward it
  boards_test.py table         at the card table (dungeon map): spot, hidden furniture, map pull, reach
  boards_test.py encounter     from the map of snapshot magician_mid: walks onto the 'A Debt Repaid' tile (2, 0)
                               and plays it through the plates: hover/swipe/slow press nothing, the laser
                               continues, the simulated hand pokes 'Accept.', A (the pad) continues
  boards_test.py menu          (at the table) the pause menu on the menu board within reach; by touch: Settings, a toggle
                               and back, an Audio slider pressed and slid (then put back), Back, Resume
  boards_test.py titlemenu     (at the title) Settings from the title board on the menu board; Back by touch
  boards_test.py dialogue      (at the table) the wheel's nudge question: its answers on the choice board; one by touch
Results: PASS/FAIL lines on stdout and in logs/boards_test.log. Shots: testdata/shots/BT_*.
Start a session first, e.g.:  python3 scripts/session.py start --snapshot magician_mid  (title)
                              python3 scripts/session.py start --snapshot magician_mid --to table
"""
import math
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cmd  # noqa: E402

import hof2env  # noqa: E402  (this worktree's test environment, docs/two-environments.md)

H = hof2env.H
LOG = H + "/logs/boards_test.log"
RESULTS = []
EYE = 1.75   # the user's eye height (their sessions: 1.71-1.80 m)


def log(msg):
    line = time.strftime("%H:%M:%S ") + msg
    print(line, flush=True)
    with open(LOG, "a") as f:
        f.write(line + "\n")


def sim(c):
    r = subprocess.run(["python3", H + "/scripts/xrsim.py", c], capture_output=True, text=True)
    if r.returncode != 0:
        log("  xrsim error: " + (r.stdout + r.stderr).strip())
    return r.stdout.strip()


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


def wait(pred, timeout, step=0.25):
    deadline = time.time() + timeout
    while time.time() < deadline:
        s = st()
        try:
            if pred(s):
                return s
        except Exception:
            pass
        time.sleep(step)
    return None


def result(name, ok, evidence):
    RESULTS.append((name, ok))
    log(("PASS " if ok else "FAIL ") + name + " - " + evidence)


def head(yaw=0.0, pitch=0.0):
    """Head turned about a neck pivot 0.10 m behind and below the eyes (the eyes move as in real life)."""
    off = (0.0, 0.10, -0.10)
    p, y = math.radians(pitch), math.radians(yaw)
    y1 = off[1] * math.cos(p) - off[2] * math.sin(p)
    z1 = off[1] * math.sin(p) + off[2] * math.cos(p)
    x2, z2 = z1 * math.sin(y), z1 * math.cos(y)
    sim("head %.4f %.4f %.4f %g %g 0" % (x2, EYE - off[1] + y1, -off[2] + z2, yaw, pitch))


def gamelog():
    with open(hof2env.GLOG, errors="replace") as f:
        return f.read()


def rects(key):
    """uiwhere -> {path tail: (x0, y0, x1, y1)} from the newest listing in the log."""
    n0 = gamelog().count("uiwhere '%s'" % key)
    send("uiwhere %s 80" % key)
    for _ in range(30):   # the log file is written with a delay
        time.sleep(0.2)
        text = gamelog()
        if text.count("uiwhere '%s'" % key) > n0 and text.rstrip().endswith("logged") or text.count("[cmd]") and text.rfind("uiwhere %s 80 => logged" % key) > text.rfind("uiwhere '%s'" % key):
            break
    i = text.rfind("uiwhere '%s'" % key)
    out = {}
    for line in text[i:].splitlines()[1:]:
        if not line.startswith("  "):
            if out:
                break
            continue
        m = re.match(r"\s+(\S.*?) \[(\w+)\].*rt=\((-?\d+),(-?\d+)\)-\((-?\d+),(-?\d+)\).*visible=(\w+)", line)
        if m:
            out[m.group(1).split("StartScreen/")[-1]] = (int(m.group(3)), int(m.group(4)), int(m.group(5)), int(m.group(6)), m.group(7) == "True")
    return out


def shot(name):
    send("shot BT_" + name)
    time.sleep(1.5)


def test_title():
    head(0, 0)
    time.sleep(1)
    a = rects("ButtonGridContainer")
    shot("title_straight")
    head(-22, -12)   # turned toward the menu (right and down), the eyes move with the head
    time.sleep(1)
    b = rects("ButtonGridContainer")
    shot("title_menu")
    head(0, 0)
    labels = {k: v for k, v in a.items() if k.endswith("/Label")}
    xmax = max(v[2] for v in labels.values()) if labels else -1
    xmin = min(v[0] for v in labels.values()) if labels else -1
    result("title_menu_inside", bool(labels) and xmin > 60 and xmax < 1920 - 120,
           "%d labels, x %d..%d (panel 0..1920; the hover arrows need ~80 px beside them)" % (len(labels), xmin, xmax))
    moved = [k for k in labels if k in b and b[k][:4] != labels[k][:4]]
    result("title_menu_still", bool(labels) and not moved, "labels moved with the head: %s" % (moved or "none"))
    g = rects("StartScreenGoblin")
    over = []
    for k, v in g.items():
        if not v[4]:
            continue
        for lk, lv in labels.items():
            if v[0] < lv[2] and lv[0] < v[2] and v[1] < lv[3] and lv[1] < v[3]:
                over.append((k.split("/")[-1], lk.split("/")[-2]))
    result("title_goblin_clear", not over, "goblin parts over menu labels: %s" % (over or "none"))


def test_table():
    s = st()
    tc = s.get("tableComfort", "")
    result("table_hidden", "DeckBuilder_Board (1)" in tc, tc)
    m = re.search(r"map pull ([\d.]+) m", tc)
    pull = float(m.group(1)) if m else -1
    send("reach BT_table")
    time.sleep(0.5)
    with open(H + "/testdata/reach/BT_table.txt") as f:
        reach = f.read()
    edge = re.search(r"near edge \(straight ahead\).*horizontal ([\d.]+) m", reach)
    rows = [float(x) for x in re.findall(r"slot \(\d+, \d+\).*?\(ahead ([\d.]+),", reach)]
    below = re.search(r"slot .*?below ([\d.]+),", reach)
    b = float(below.group(1)) if below else 0.76
    near = min(rows) if rows else 9
    chest = math.hypot(near, b - 0.30)
    result("table_edge", edge is not None and 0.12 <= float(edge.group(1)) <= 0.24, "table edge %s m ahead of the eyes" % (edge.group(1) if edge else "?"))
    result("table_map_reach", rows and chest <= 0.66,
           "map pulled %.2f m; nearest row %.2f m ahead, %.2f m below the eyes -> %.2f m from the chest (0.30 m below the eyes); rows %s"
           % (pull, near, b, chest, sorted(set(round(r, 2) for r in rows))))
    head(0, 0)
    shot("table_straight")
    head(0, -55)
    shot("table_down")
    head(30, -50)
    shot("table_downleft")
    head(0, 0)


def boards():
    return st().get("boards", "")


def plates():
    m = re.search(r"plates (\d+)", boards())
    return int(m.group(1)) if m else 0


def opt_plates(b=None):
    """The board's own plates, not the controls column's (`nav:` plates: the game's bottom bar as buttons, shown at the
    map and during encounters since 2026-10-13 - this test predates them)."""
    return sum(1 for i, t in re.findall(r"\[(\d+) ([^\]]*)\]", b if b is not None else boards()) if not t.startswith("nav:"))


def counts():
    b = boards()
    m = re.search(r"hovers (\d+) selects (\d+) refused (\d+)", b)
    return tuple(int(x) for x in m.groups()) if m else (0, 0, 0)


def test_encounter():
    rest = "hand r 0.2 1.0 -0.3 0 -60 0; hand l -0.2 1.0 -0.3 0 -60 0; trig r 0; touch r 0"
    sim(rest)
    head(0, 0)
    if not wait(lambda s: s.get("state") == "GameState_Map" and s.get("menu") == "Map", 30):
        result("enc_start", False, "not on the map: %s / %s" % (st().get("state"), st().get("menu")))
        return
    send("world click MapLayout/(2, 0)")
    # (the encounter's own plates or its story: the controls column's nav plates are up at the map already)
    if not wait(lambda s: opt_plates(s.get("boards", "")) > 0 or s.get("boards", "").startswith("story"), 40):
        result("enc_start", False, "no plates: %s" % boards())
        return
    time.sleep(1.5)
    b = send("boards")
    log("  " + b)
    m = re.search(r"story centre .*?= ([\d.]+) m, (-?\d+) deg right, (-?\d+) deg up", b)
    result("enc_story", m is not None and int(m.group(2)) > 0 and -2 <= int(m.group(3)) <= 20,
           "story text %s" % (m.group(0) if m else "missing"))
    bc = re.search(r"board centre .*?: ([\d.]+) ahead ([\d.]+) below", b)
    result("enc_board_reach", bc is not None and float(bc.group(1)) < 0.55 and float(bc.group(2)) < 0.5,
           "board %s" % (bc.group(0) if bc else "missing"))
    head(0, -30)
    shot("enc_page1")
    head(0, 0)
    # page 1 (a continue arrow): touching without a proper press does nothing
    for mode in ("hover", "swipe", "slow"):
        c0 = counts()
        send("ptouch 1 " + mode)
        time.sleep(2.6)
        c1 = counts()
        result("enc_no_press_" + mode, c1[1] == c0[1] and opt_plates() == 1, "%s: selects %d -> %d, the board's plates %d" % (mode, c0[1], c1[1], opt_plates()))
    # the laser on the plate continues (the real pointer path from the controller)
    send("boards click 1")
    ok = wait(lambda s: "Accept" in s.get("boards", ""), 10)
    result("enc_laser_click", ok is not None, "after the laser click: %s" % boards())
    if not ok:
        return
    time.sleep(1.0)
    head(0, -30)
    shot("enc_page2")
    head(0, 0)
    # page 2: the simulated hand pokes "Accept." (fingertip through the plate)
    sim("hand r 0.25 1.2 -0.15 0 -30 0")
    time.sleep(0.5)
    r = send("platepose 1 6 r")
    if "|" not in r:
        result("enc_poke_accept", False, "platepose: " + r)
        return
    pos, press = [x.strip() for x in r.split("|")]
    h = send("handto r tip " + pos)
    c0 = counts()
    p = press.split()
    back = "move r %.4f %.4f %.4f 300" % (-float(p[2]), -float(p[3]), -float(p[4]))
    sim(h + " 400; wait 300; " + press + " 300; wait 150; " + back)
    time.sleep(1.5)
    c1 = counts()
    result("enc_poke_accept", c1[1] == c0[1] + 1 and "select Accept" in boards(), "hand poke: selects %d -> %d, %s" % (c0[1], c1[1], boards()))
    sim(rest)
    # page 3: the pad's A still continues
    if wait(lambda s: re.search(r"\[1 1\]", s.get("boards", "")) and opt_plates(s.get("boards", "")) == 1, 15):
        time.sleep(1.0)
        head(0, -20)
        shot("enc_page3")
        head(0, 0)
        send("press A 6")
        ok = wait(lambda s: opt_plates(s.get("boards", "")) == 0 or s.get("menu") != "Encounter", 15)
        result("enc_pad_A", ok is not None, "after A: %s / menu %s" % (boards(), st().get("menu")))
    else:
        result("enc_pad_A", False, "page 3 not shown: %s" % boards())


# ---- menus by touch (TableBoards.Menu.cs) --------------------------------------------------------------------------------------
REST = "hand r 0.25 1.0 -0.2 0 -60 0; hand l -0.25 1.0 -0.2 0 -60 0; trig r 0; touch r 0"


def plate_index(label):
    for i, t in re.findall(r"\[(\d+) ([^\]]*)\]", boards()):
        t = re.sub(r" =[\d.]+| \*$", "", t).strip()
        if label.lower() in t.lower():
            return int(i)
    return None


def bar_plates():
    return [(int(i), float(v)) for i, v in re.findall(r"\[(\d+) [^\]]*? =([\d.]+)", boards())]


def poke(idx, slide=None):
    """The simulated hand pokes plate idx: from 6 cm in front, 1.5 cm through (as a player does), optionally sliding
    `slide` = (dx metres along the room's x, ms) while it holds the plate down, then back out."""
    if idx is None:
        return False
    sim("hand r 0.25 1.2 -0.15 0 -30 0")
    time.sleep(0.4)
    r = send("platepose %d 6 r" % idx)
    if "|" not in r:
        return False
    pos, press = [x.strip() for x in r.split("|")]
    h = send("handto r tip " + pos)
    p = press.split()
    back = "move r %.4f %.4f %.4f 300" % (-float(p[2]), -float(p[3]), -float(p[4]))
    seq = h + " 400; wait 300; " + press + " 300; wait 150; "
    if slide:
        seq += "move r %.4f 0 0 %d; wait 150; " % (slide[0], slide[1])
    sim(seq + back)
    time.sleep(1.2)
    return True


def test_menu():
    """The pause menu and its settings on the menu board, by touch."""
    sim(REST)
    head(0, 0)
    send("press START 6")
    ok = wait(lambda s: "menu board pause" in s.get("boards", ""), 10)
    result("menu_pause_board", ok is not None and plate_index("Resume") is not None and plate_index("Settings") is not None, boards()[:220])
    if not ok:
        return
    g = send("boards")
    bc = re.search(r"board centre .*?: (-?[\d.]+) ahead (-?[\d.]+) below (-?[\d.]+) right", g)
    result("menu_board_reach", bc is not None and 0.35 <= float(bc.group(1)) <= 0.6 and 0.0 <= float(bc.group(2)) <= 0.3,
           "board %s (within reach: 0.35-0.6 m ahead, a little below the eyes)" % (bc.group(0) if bc else g[:160]))
    head(0, -15)
    shot("menu_pause")
    head(0, 0)
    c0 = counts()
    poke(plate_index("Settings"))
    ok = wait(lambda s: re.search(r"\[\d+ Back\]", s.get("boards", "")), 8)
    result("menu_settings_by_touch", ok is not None and counts()[1] == c0[1] + 1, "after poking Settings: %s" % boards()[:260])
    if not ok:
        return
    time.sleep(0.8)
    head(0, -15)
    shot("menu_settings")
    head(0, 0)
    i = plate_index("No Blood")
    if i is not None:
        v0 = send("boards val %d" % i).split("= ")[-1]
        poke(i)
        v1 = send("boards val %d" % plate_index("No Blood")).split("= ")[-1]
        poke(plate_index("No Blood"))
        v2 = send("boards val %d" % plate_index("No Blood")).split("= ")[-1]
        result("menu_toggle_by_touch", v0 != v1 and v2 == v0, "No Blood: %s -> %s -> %s (two pokes)" % (v0, v1, v2))
    else:
        result("menu_toggle_by_touch", False, "no 'No Blood' plate: %s" % boards()[:200])
    poke(plate_index("Audio"))
    ok = wait(lambda s: re.search(r"\[\d+ [^\]]*? =[\d.]+", s.get("boards", "")), 8)
    bars = bar_plates()
    if not ok or not bars:
        result("menu_slider_drag", False, "no slider after poking Audio: %s" % boards()[:240])
    else:
        time.sleep(0.6)
        head(0, -15)
        shot("menu_audio")
        head(0, 0)
        i, v0 = bars[0]

        def bar(idx, v=None):
            m = re.search(r"bar ([\d.]+)", send("boards bar %d" % idx + ("" if v is None else " %.3f" % v)))
            return float(m.group(1)) if m else -1.0
        v0 = bar(i)
        bar(i, 0.2)
        time.sleep(0.3)
        poke(i, slide=(0.05, 500))
        v1 = bar(i)
        bar(i, v0)   # the setting as it was
        time.sleep(0.3)
        v2 = bar(i)
        result("menu_slider_drag", v1 > 0.55 and abs(v2 - v0) < 0.011, "slider %d: set to 0.20, pressed at its middle and slid 5 cm right -> %.2f; put back %.2f -> %.2f"
               % (i, v1, v0, v2))
    poke(plate_index("Back"))
    ok = wait(lambda s: re.search(r"\[\d+ Resume\]", s.get("boards", "")), 8)
    result("menu_back_by_touch", ok is not None, "after poking Back: %s" % boards()[:200])
    # a question box over the pause menu: Forfeit -> "finish the encounter first" -> OK by touch
    poke(plate_index("Forfeit"))
    ok = wait(lambda s: "menu board dialogue" in s.get("boards", ""), 8)
    if ok:
        time.sleep(0.6)
        head(0, -15)
        shot("menu_dialogue")
        head(0, 0)
    # never confirm a forfeit: the cancelling answer when there is one, else the notice's OK
    labels = [t for _, t in re.findall(r"\[(\d+) ([^\]]*)\]", boards())]
    cancel = next((t for t in labels if re.search(r"cancel|\bno\b|back", t, re.I)), None)
    if cancel is None and len(labels) == 1:
        cancel = labels[0]
    log("  question answers: %s -> %s" % (labels, cancel))
    if cancel is not None:
        poke(plate_index(cancel))
    ok2 = wait(lambda s: re.search(r"\[\d+ Resume\]", s.get("boards", "")), 8)
    result("menu_dialogue_by_touch", ok is not None and ok2 is not None, "Forfeit -> %s; OK -> %s" % ("a question on the board" if ok else "no question",
                                                                                                  "back on the pause menu" if ok2 else boards()[:160]))
    poke(plate_index("Resume"))
    ok = wait(lambda s: "menu board" not in s.get("boards", ""), 8)
    result("menu_resume_by_touch", ok is not None, "after poking Resume: %s" % boards()[:160])
    sim(REST)


def test_dialogue():
    """A question box at the card table (not paused): its answers on the choice board, picked by touch. The box is one of the
    game's own (the `boards dialogue` seam shows the pause menu's "finish the encounter first" notice through Dialogue.Show,
    as the wheel shows its nudge question)."""
    sim(REST)
    head(0, 0)
    if not wait(lambda s: s.get("state") == "GameState_Map" and s.get("menu") == "Map", 30):
        result("dialogue_start", False, "not on the map: %s / %s" % (st().get("state"), st().get("menu")))
        return
    r = send("boards dialogue")
    ok = wait(lambda s: "table dialogue" in s.get("boards", ""), 8)
    result("dialogue_board", ok is not None and plates() >= 1, "%s: the answers on the choice board: %s" % (r, boards()[:220]))
    if not ok:
        return
    time.sleep(0.8)
    head(0, -25)
    shot("dialogue_board")
    head(0, 0)
    c0 = counts()
    poke(1)
    ok = wait(lambda s: "table dialogue" not in s.get("boards", ""), 6)
    result("dialogue_by_touch", ok is not None and counts()[1] == c0[1] + 1, "poking the answer: the box %s, selects %d -> %d"
           % ("closed" if ok else "still open", c0[1], counts()[1]))
    sim(REST)

def test_titlemenu():
    """At the title: Settings from the title board opens the settings on the menu board; Back by touch returns."""
    sim(REST)
    head(0, 0)
    ok = wait(lambda s: "title menu" in s.get("boards", "") and "Settings" in s.get("boards", ""), 40)
    if not ok:
        result("title_settings_board", False, "no title board: %s" % boards()[:160])
        return
    poke(plate_index("Settings"))
    ok = wait(lambda s: "menu board " in s.get("boards", ""), 10)   # (the title opens its settings as the pause menu)
    result("title_settings_board", ok is not None and plate_index("Back") is not None, "after poking Settings at the title: %s" % boards()[:220])
    if ok:
        time.sleep(0.6)
        head(0, -10)
        shot("title_settings")
        head(0, 0)
    poke(plate_index("Back"))
    ok = wait(lambda s: "| title menu" in s.get("boards", ""), 10)
    result("title_back_by_touch", ok is not None, "after poking Back: %s" % boards()[:160])
    sim(REST)

def main():
    tests = sys.argv[1:] or ["title"]
    log("=== boards_test %s" % " ".join(tests))
    for t in tests:
        {"title": test_title, "table": test_table, "encounter": test_encounter, "menu": test_menu, "dialogue": test_dialogue,
         "titlemenu": test_titlemenu}[t]()
    fails = [n for n, ok in RESULTS if not ok]
    log("=== %d checks, %d failed%s" % (len(RESULTS), len(fails), (": " + ", ".join(fails)) if fails else ""))
    sys.exit(1 if fails else 0)


if __name__ == "__main__":
    main()
